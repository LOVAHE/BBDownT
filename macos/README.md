# BBDownT.app（macOS 应用）

把 BBDownT 的服务器模式和网页前端打包成一个双击就能用的 macOS 应用：打开后是一个独立窗口，里面就是网页版界面；
后台服务只在本机（127.0.0.1）监听，每次启动生成随机访问令牌，只交给这个窗口使用。关闭窗口或退出应用时后台服务一起停止，
有下载在进行时会先确认。

- 下载目录：`~/Downloads/BBDownT`
- 登录信息、配置、下载历史：`~/Library/Application Support/BBDownT`
- 日志：`~/Library/Logs/BBDownT/engine.log`
- 合并音视频需要 ffmpeg：`brew install ffmpeg`（应用会在 `/opt/homebrew/bin` 和 `/usr/local/bin` 里找）

## 下载

- 正式版本：Releases 里的 `BBDownT_macOS_universal.zip`（同时支持 Apple 芯片和 Intel，macOS 13 及以上）。
- 每次推送的构建：Actions → 「macOS App」→ 某次运行 → Artifacts 里的 `BBDownT-macOS-app`（需要登录 GitHub）。
  下载后先解压出 `BBDownT_macOS.zip`，再解压得到 `BBDownT.app`。

## 第一次打开

应用只做了本地签名（ad-hoc），没有经过苹果公证，从网上下载后 macOS 会拦下：

1. 把 `BBDownT.app` 拖进「应用程序」，双击打开，出现“无法验证”提示时点「完成」。
2. 打开「系统设置 → 隐私与安全性」，在下方找到 BBDownT，点「仍要打开」，再确认一次。

也可以在终端去掉下载带来的隔离标记：

```bash
xattr -dr com.apple.quarantine /Applications/BBDownT.app
```

第一次下载或查看文件时，系统会询问是否允许访问「下载」文件夹，点「允许」。
由于是本地签名，每装一个新版本都会再问一次；没点之前应用会停在「正在启动」。

## 自己打包

```bash
macos/build.sh --install          # 编译 Apple 芯片版并安装到 /Applications
macos/build.sh --universal --zip  # 编译通用版并生成 macos/build/BBDownT_macOS.zip
```

本机没有 .NET 9 SDK 时，脚本会临时下载到临时目录，构建完删除。
GitHub 上的「macOS App」工作流和「Build Latest」都调用这个脚本：

```bash
macos/build.sh --engine <osx-arm64 的 BBDownT> --engine-x64 <osx-x64 的 BBDownT> --zip --out <目录>
```
