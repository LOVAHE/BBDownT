// BBDownT 的 macOS 外壳：启动本机 BBDownT 服务（只监听 127.0.0.1），用独立窗口显示它的网页界面。
// 每次启动生成随机访问令牌，只写进本窗口的 cookie；关闭窗口或退出应用时停止服务。
import Cocoa
import WebKit

let preferredPort = 38123
let fm = FileManager.default
let home = fm.homeDirectoryForCurrentUser
let downloadRoot = home.appendingPathComponent("Downloads/BBDownT", isDirectory: true)
let dataDir = home.appendingPathComponent("Library/Application Support/BBDownT", isDirectory: true)
let logDir = home.appendingPathComponent("Library/Logs/BBDownT", isDirectory: true)
let engineLog = logDir.appendingPathComponent("engine.log")
let pidFile = dataDir.appendingPathComponent("engine.pid")
let cookieName = "bbdownt_token"   // 与服务端 SessionCookieName 一致

// 网页里的「退出网页登录」在本机版没有意义（令牌由外壳管理），隐藏掉
let hideCSS = "#logoutBtn{display:none!important}"

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, WKNavigationDelegate, WKUIDelegate, WKDownloadDelegate {
    var window: NSWindow!
    var webView: WKWebView!
    var engine: Process?
    var port = preferredPort
    var token = ""
    var quitting = false
    var quitConfirmed = false
    var baseURL: URL { URL(string: "http://127.0.0.1:\(port)/")! }

    // MARK: 生命周期

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMenu()
        for dir in [downloadRoot, dataDir, logDir] {
            try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        }
        token = randomToken()
        killStaleEngine()
        port = isPortFree(preferredPort) ? preferredPort : freePort()
        setupWindow()
        showMessage("正在启动 BBDownT…", detail: nil)
        do {
            try startEngine()
        } catch {
            showMessage("后台服务启动失败", detail: error.localizedDescription)
            return
        }
        installCookie { self.waitForServer(attempt: 0) }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    /// 关窗口也走退出流程：有下载在进行时可以取消，窗口保留
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        NSApp.terminate(nil)
        return false
    }

    /// 有下载正在进行或排队时，先确认再退出（退出会中断下载；下次同画质再下会接着用已下的分片）
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard !quitConfirmed, engine?.isRunning == true else { return .terminateNow }
        var req = URLRequest(url: baseURL.appendingPathComponent("get-tasks/"))
        req.timeoutInterval = 1.5
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        URLSession.shared.dataTask(with: req) { data, _, _ in
            var active = 0
            if let data, let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                active = ((obj["Running"] as? [Any])?.count ?? 0) + ((obj["Pending"] as? [Any])?.count ?? 0)
            }
            DispatchQueue.main.async {
                guard active > 0 else { NSApp.reply(toApplicationShouldTerminate: true); return }
                let a = NSAlert()
                a.alertStyle = .warning
                a.messageText = "还有 \(active) 个下载没完成，确定退出吗？"
                a.informativeText = "退出会中断下载。下次用同样的画质重新下载同一个视频，会接着用已经下好的部分。"
                a.addButton(withTitle: "继续下载")
                a.addButton(withTitle: "退出")
                self.window.makeKeyAndOrderFront(nil)
                NSApp.activate(ignoringOtherApps: true)
                let quit = a.runModal() == .alertSecondButtonReturn
                if quit { self.quitConfirmed = true }
                NSApp.reply(toApplicationShouldTerminate: quit)
            }
        }.resume()
        return .terminateLater
    }

    func applicationWillTerminate(_ notification: Notification) {
        quitting = true
        stopEngine()
    }

    // MARK: 后台服务

    func startEngine() throws {
        let exe = Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/bbdownt-engine")
        let p = Process()
        p.executableURL = exe
        p.arguments = ["serve", "-l", "http://127.0.0.1:\(port)", "--server-download-root", downloadRoot.path]
        var env = ProcessInfo.processInfo.environment
        // Homebrew 的 ffmpeg 在 /opt/homebrew/bin；从访达启动的应用 PATH 里没有它
        env["PATH"] = "/opt/homebrew/bin:/usr/local/bin:" + (env["PATH"] ?? "/usr/bin:/bin:/usr/sbin:/sbin")
        env["BBDOWNT_DATA_DIR"] = dataDir.path
        env["BBDOWNT_API_TOKEN"] = token
        p.environment = env
        p.currentDirectoryURL = downloadRoot
        fm.createFile(atPath: engineLog.path, contents: nil)
        let log = try FileHandle(forWritingTo: engineLog)
        p.standardOutput = log
        p.standardError = log
        p.terminationHandler = { [weak self] proc in
            DispatchQueue.main.async { self?.engineExited(proc.terminationStatus) }
        }
        try p.run()
        engine = p
        try? String(p.processIdentifier).write(to: pidFile, atomically: true, encoding: .utf8)
    }

    func stopEngine() {
        guard let p = engine, p.isRunning else { try? fm.removeItem(at: pidFile); return }
        p.terminate()
        let deadline = Date().addingTimeInterval(3)
        while p.isRunning && Date() < deadline { usleep(50_000) }
        if p.isRunning { kill(p.processIdentifier, SIGKILL) }
        try? fm.removeItem(at: pidFile)
    }

    func engineExited(_ status: Int32) {
        if quitting { return }
        showMessage("后台服务意外退出（代码 \(status)）", detail: "日志：\(engineLog.path)\n重新打开应用即可恢复。")
    }

    /// 上次异常退出时可能留下服务进程占着端口，只结束确认是本应用服务的那个
    func killStaleEngine() {
        guard let text = try? String(contentsOf: pidFile, encoding: .utf8),
              let pid = Int32(text.trimmingCharacters(in: .whitespacesAndNewlines)), pid > 1 else { return }
        let ps = Process()
        ps.executableURL = URL(fileURLWithPath: "/bin/ps")
        ps.arguments = ["-p", String(pid), "-o", "comm="]
        let out = Pipe()
        ps.standardOutput = out
        try? ps.run()
        ps.waitUntilExit()
        let comm = String(data: out.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        if comm.contains("bbdownt-engine") { kill(pid, SIGTERM); usleep(500_000) }
        try? fm.removeItem(at: pidFile)
    }

    func installCookie(_ done: @escaping () -> Void) {
        let props: [HTTPCookiePropertyKey: Any] = [.name: cookieName, .value: token, .domain: "127.0.0.1", .path: "/"]
        guard let cookie = HTTPCookie(properties: props) else { done(); return }
        webView.configuration.websiteDataStore.httpCookieStore.setCookie(cookie, completionHandler: done)
    }

    func waitForServer(attempt: Int) {
        var req = URLRequest(url: baseURL.appendingPathComponent("ui/status"))
        req.timeoutInterval = 1
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        URLSession.shared.dataTask(with: req) { _, resp, _ in
            DispatchQueue.main.async {
                if (resp as? HTTPURLResponse)?.statusCode == 200 {
                    self.webView.load(URLRequest(url: self.baseURL))
                } else if attempt < 120, self.engine?.isRunning == true {
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { self.waitForServer(attempt: attempt + 1) }
                } else if self.engine?.isRunning == true {
                    self.showMessage("后台服务没有响应", detail: "日志：\(engineLog.path)")
                }
            }
        }.resume()
    }

    // MARK: 窗口与菜单

    func setupWindow() {
        let config = WKWebViewConfiguration()
        config.websiteDataStore = .default()
        let script = WKUserScript(
            source: "var s=document.createElement('style');s.textContent='\(hideCSS)';document.documentElement.appendChild(s);",
            injectionTime: .atDocumentEnd, forMainFrameOnly: true)
        config.userContentController.addUserScript(script)
        webView = WKWebView(frame: .zero, configuration: config)
        webView.navigationDelegate = self
        webView.uiDelegate = self
        if #available(macOS 13.3, *) { webView.isInspectable = true }

        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1120, height: 780),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered, defer: false)
        window.title = "BBDownT"
        window.delegate = self
        window.minSize = NSSize(width: 420, height: 480)
        window.contentView = webView
        window.center()
        window.setFrameAutosaveName("BBDownTMainWindow")
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func showMessage(_ title: String, detail: String?) {
        let esc = { (s: String) in s.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;") }
        let d = detail.map { "<p>\(esc($0).replacingOccurrences(of: "\n", with: "<br>"))</p>" } ?? ""
        let html = """
        <!doctype html><meta charset="utf-8"><style>
        :root{color-scheme:light dark}body{margin:0;height:100vh;display:grid;place-items:center;
        font:15px -apple-system,BlinkMacSystemFont,"PingFang SC",sans-serif;color:CanvasText;background:Canvas}
        div{text-align:center;max-width:32em;padding:0 24px}h1{font-size:18px;font-weight:600}p{opacity:.7;line-height:1.6;word-break:break-all}
        </style><div><h1>\(esc(title))</h1>\(d)</div>
        """
        webView.loadHTMLString(html, baseURL: nil)
    }

    func buildMenu() {
        let main = NSMenu()
        func add(_ title: String, _ items: [NSMenuItem]) {
            let item = NSMenuItem()
            let menu = NSMenu(title: title)
            items.forEach(menu.addItem)
            item.submenu = menu
            main.addItem(item)
        }
        func mi(_ t: String, _ a: Selector?, _ k: String, _ mods: NSEvent.ModifierFlags = .command) -> NSMenuItem {
            let i = NSMenuItem(title: t, action: a, keyEquivalent: k)
            i.keyEquivalentModifierMask = mods
            return i
        }
        add("BBDownT", [
            mi("关于 BBDownT", #selector(NSApplication.orderFrontStandardAboutPanel(_:)), ""),
            .separator(),
            mi("隐藏 BBDownT", #selector(NSApplication.hide(_:)), "h"),
            .separator(),
            mi("退出 BBDownT", #selector(NSApplication.terminate(_:)), "q"),
        ])
        add("编辑", [
            mi("撤销", Selector(("undo:")), "z"),
            mi("重做", Selector(("redo:")), "z", [.command, .shift]),
            .separator(),
            mi("剪切", #selector(NSText.cut(_:)), "x"),
            mi("拷贝", #selector(NSText.copy(_:)), "c"),
            mi("粘贴", #selector(NSText.paste(_:)), "v"),
            mi("全选", #selector(NSText.selectAll(_:)), "a"),
        ])
        let reload = mi("重新载入", #selector(reloadPage), "r")
        let folder = mi("打开下载文件夹", #selector(openDownloads), "d", [.command, .shift])
        let logs = mi("打开日志", #selector(openLog), "")
        [reload, folder, logs].forEach { $0.target = self }
        add("显示", [reload, .separator(), folder, logs])
        add("窗口", [
            mi("最小化", #selector(NSWindow.performMiniaturize(_:)), "m"),
            mi("关闭", #selector(NSWindow.performClose(_:)), "w"),
        ])
        NSApp.mainMenu = main
    }

    @objc func reloadPage() { webView.load(URLRequest(url: baseURL)) }
    @objc func openDownloads() { NSWorkspace.shared.open(downloadRoot) }
    @objc func openLog() { NSWorkspace.shared.activateFileViewerSelecting([engineLog]) }

    // MARK: 链接、下载、对话框

    /// 网页里的文件路径是相对下载根目录的；只接受落在根目录内、且存在的文件
    func localFile(_ relative: String) -> URL? {
        let url = (relative.hasPrefix("/") ? URL(fileURLWithPath: relative) : downloadRoot.appendingPathComponent(relative))
            .standardizedFileURL
        let root = downloadRoot.standardizedFileURL.path + "/"
        guard url.path.hasPrefix(root), fm.fileExists(atPath: url.path) else { return nil }
        return url
    }

    /// 任务输出链接只带任务编号，按文件名在下载目录里找（同名时取最新的）
    func findDownloaded(named name: String) -> URL? {
        guard let e = fm.enumerator(at: downloadRoot, includingPropertiesForKeys: [.contentModificationDateKey]) else { return nil }
        var best: (URL, Date)?
        for case let url as URL in e where url.lastPathComponent == name {
            let d = (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            if best == nil || d > best!.1 { best = (url, d) }
        }
        return best?.0
    }

    func isLocalServer(_ url: URL) -> Bool { url.host == "127.0.0.1" && url.port == port }

    /// 文件链接：在线播放 → 用默认播放器打开本地文件；下载到本机 → 在访达中选中（文件本来就在本机）
    func handleFileLink(_ url: URL) -> Bool {
        guard isLocalServer(url), url.path == "/files/download" else { return false }
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        guard let rel = items.first(where: { $0.name == "path" })?.value, let file = localFile(rel) else { return false }
        if items.first(where: { $0.name == "inline" })?.value == "1" {
            NSWorkspace.shared.open(file)
        } else {
            NSWorkspace.shared.activateFileViewerSelecting([file])
        }
        return true
    }

    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = action.request.url else { return decisionHandler(.allow) }
        if handleFileLink(url) { return decisionHandler(.cancel) }
        if !isLocalServer(url), ["http", "https"].contains(url.scheme ?? ""), action.navigationType == .linkActivated {
            NSWorkspace.shared.open(url)   // 外部链接（如B站页面）交给默认浏览器
            return decisionHandler(.cancel)
        }
        decisionHandler(action.shouldPerformDownload ? .download : .allow)
    }

    func webView(_ webView: WKWebView, decidePolicyFor response: WKNavigationResponse,
                 decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void) {
        let disposition = (response.response as? HTTPURLResponse)?.value(forHTTPHeaderField: "Content-Disposition") ?? ""
        decisionHandler(!response.canShowMIMEType || disposition.lowercased().hasPrefix("attachment") ? .download : .allow)
    }

    func webView(_ webView: WKWebView, navigationAction: WKNavigationAction, didBecome download: WKDownload) {
        download.delegate = self
    }

    func webView(_ webView: WKWebView, navigationResponse: WKNavigationResponse, didBecome download: WKDownload) {
        download.delegate = self
    }

    func download(_ download: WKDownload, decideDestinationUsing response: URLResponse, suggestedFilename: String,
                  completionHandler: @escaping (URL?) -> Void) {
        if let existing = findDownloaded(named: suggestedFilename) {
            completionHandler(nil)   // 已在本机下载目录里，不再复制一份
            NSWorkspace.shared.activateFileViewerSelecting([existing])
            return
        }
        let dir = home.appendingPathComponent("Downloads", isDirectory: true)
        var dest = dir.appendingPathComponent(suggestedFilename)
        var n = 1
        while fm.fileExists(atPath: dest.path) {
            let ext = (suggestedFilename as NSString).pathExtension
            let stem = (suggestedFilename as NSString).deletingPathExtension
            dest = dir.appendingPathComponent(ext.isEmpty ? "\(stem) \(n)" : "\(stem) \(n).\(ext)")
            n += 1
        }
        completionHandler(dest)
    }

    // target="_blank" 的链接（在线播放）
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for action: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let url = action.request.url, !handleFileLink(url) {
            if isLocalServer(url) { webView.load(action.request) } else { NSWorkspace.shared.open(url) }
        }
        return nil
    }

    func webView(_ webView: WKWebView, runJavaScriptAlertPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping () -> Void) {
        let a = NSAlert()
        a.messageText = message
        a.addButton(withTitle: "好")
        a.beginSheetModal(for: window) { _ in completionHandler() }
    }

    func webView(_ webView: WKWebView, runJavaScriptConfirmPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping (Bool) -> Void) {
        let a = NSAlert()
        a.messageText = message
        a.addButton(withTitle: "确定")
        a.addButton(withTitle: "取消")
        a.beginSheetModal(for: window) { completionHandler($0 == .alertFirstButtonReturn) }
    }

    /// 令牌 cookie 万一被网页清掉（例如服务端返回删除 cookie），补回去并刷新
    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        guard let url = webView.url, isLocalServer(url) else { return }
        webView.configuration.websiteDataStore.httpCookieStore.getAllCookies { cookies in
            if !cookies.contains(where: { $0.name == cookieName && $0.value == self.token }) {
                self.installCookie { webView.reload() }
            }
        }
    }
}

// MARK: 工具

func randomToken() -> String {
    var bytes = [UInt8](repeating: 0, count: 32)
    _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
    return bytes.map { String(format: "%02x", $0) }.joined()
}

func loopbackAddress(_ port: Int) -> sockaddr_in {
    var addr = sockaddr_in()
    addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
    addr.sin_family = sa_family_t(AF_INET)
    addr.sin_port = in_port_t(UInt16(port).bigEndian)
    addr.sin_addr.s_addr = inet_addr("127.0.0.1")
    return addr
}

func isPortFree(_ port: Int) -> Bool {
    let fd = socket(AF_INET, SOCK_STREAM, 0)
    guard fd >= 0 else { return false }
    defer { close(fd) }
    var addr = loopbackAddress(port)
    return withUnsafePointer(to: &addr) {
        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
    } == 0
}

func freePort() -> Int {
    let fd = socket(AF_INET, SOCK_STREAM, 0)
    guard fd >= 0 else { return preferredPort + 1 }
    defer { close(fd) }
    var addr = loopbackAddress(0)
    var len = socklen_t(MemoryLayout<sockaddr_in>.size)
    _ = withUnsafePointer(to: &addr) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, len) } }
    _ = withUnsafeMutablePointer(to: &addr) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &len) } }
    return Int(UInt16(bigEndian: addr.sin_port))
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
