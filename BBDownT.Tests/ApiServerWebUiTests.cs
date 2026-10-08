using Microsoft.AspNetCore.Http;

namespace BBDownT.Tests;

public class ApiServerWebUiTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bbdownt-webui-" + Guid.NewGuid().ToString("N"));

    public ApiServerWebUiTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "sub"));
    }

    public void Dispose()
    {
        Directory.Delete(root, true);
    }

    private BBDownTApiServer CreateServer() => new(new BBDownTServerOptions { DownloadRoot = root });

    [Theory]
    [InlineData("video.mp4")]
    [InlineData("sub/视频.mp4")]
    [InlineData("sub/../video.mp4")]
    public void ResolveDownloadPath_AcceptsPathsInsideRoot(string relativePath)
    {
        var fullPath = CreateServer().ResolveDownloadPath(relativePath);

        Assert.Equal(Path.GetFullPath(Path.Combine(root, relativePath)), fullPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../secret.txt")]
    [InlineData("sub/../../secret.txt")]
    [InlineData("/etc/passwd")]
    public void ResolveDownloadPath_RejectsPathsOutsideRoot(string? relativePath)
    {
        Assert.Null(CreateServer().ResolveDownloadPath(relativePath));
    }

    [Fact]
    public void ResolveDownloadPath_RejectsSiblingDirectoryWithSamePrefix()
    {
        Assert.Null(CreateServer().ResolveDownloadPath("../" + Path.GetFileName(root) + "-other/video.mp4"));
    }

    [Fact]
    public void ListDownloadedFiles_ReturnsRelativePathsNewestFirst()
    {
        var older = Path.Combine(root, "sub", "a.mp4");
        var newer = Path.Combine(root, "b.m4a");
        File.WriteAllText(older, "a");
        File.WriteAllText(newer, "bb");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));

        var files = CreateServer().ListDownloadedFiles();

        Assert.Equal(["b.m4a", "sub/a.mp4"], files.Select(f => f.Path));
        Assert.Equal(2, files[0].Size);
    }

    [Theory]
    [InlineData("BBDownT.data", true)]
    [InlineData("BBDownTTV.data", true)]
    [InlineData("BBDownTApp.data", true)]
    [InlineData("BBDownTIntl.data", true)]
    [InlineData("BBDownT.data.writing-0123456789abcdef0123456789abcdef", true)]
    [InlineData("BBDownTIntl.data.writing-0123456789abcdef0123456789abcdef", true)]
    [InlineData("BBDownT.config", true)]
    [InlineData("BBDownT.archives", true)]
    [InlineData("BBDownT.web.json", true)]
    [InlineData("BBDown.data", true)]
    [InlineData("engine.pid", true)]
    [InlineData("BBDownT 教程.mp4", false)]
    [InlineData("video.mp4", false)]
    public void IsProtectedFile_ProtectsCredentialAndConfigFilesInAppDirectory(string fileName, bool expected)
    {
        Assert.Equal(expected, BBDownTApiServer.IsProtectedFile(Path.Combine(Program.APP_DIR, fileName)));
    }

    [Fact]
    public void IsProtectedFile_AllowsSameNameOutsideAppDirectory()
    {
        Assert.False(BBDownTApiServer.IsProtectedFile(Path.Combine(root, "BBDownT.data")));
    }

    [Theory]
    [InlineData("GET", "/", true)]
    [InlineData("GET", "/index.html", true)]
    [InlineData("POST", "/ui/session", true)]
    [InlineData("DELETE", "/ui/session", true)]
    [InlineData("GET", "/ui/status", false)]
    [InlineData("POST", "/", false)]
    [InlineData("GET", "/get-tasks/", false)]
    [InlineData("GET", "/files/download", false)]
    [InlineData("POST", "/ui/bili-login", false)]
    public void IsPublicPath_OnlyExemptsPageAndSessionEndpoint(string method, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.Equal(expected, BBDownTApiServer.IsPublicPath(context.Request));
    }

    private static string IndexHtml()
    {
        using var stream = typeof(BBDownTApiServer).Assembly.GetManifestResourceStream("BBDownT.WebUi.index.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void WebUi_DownloadAllCheckboxStartsDisabledAndIsNotPersisted()
    {
        var html = IndexHtml();

        Assert.Contains("<input type=\"checkbox\" id=\"downloadAll\" disabled>", html);
        Assert.DoesNotContain("id=\"downloadAll\" data-save", html);
        // 只对与服务端规则相同的空间链接发送
        Assert.Contains("if ($('downloadAll').checked && isSpaceUrl(url))", html);
    }

    [Fact]
    public void WebUi_ApiSelectorIsVisibleAndSavedAppOrTvWithoutTokenIsResetToWeb()
    {
        var html = IndexHtml();
        var advanced = html.IndexOf("<details class=\"adv\">", StringComparison.Ordinal);

        Assert.True(html.IndexOf("<select id=\"api\"", StringComparison.Ordinal) < advanced);
        Assert.Contains("function migrateSavedApi()", html);
        Assert.Contains("v === 'app' && !status.AppTokenSaved", html);
        Assert.Contains("v === 'tv' && !status.TvTokenSaved", html);
        Assert.Contains("通常最高 480P，且一次只返回一种编码", html);
        Assert.Contains("拿不到 1080P 及以上画质（未登录通常最高 720P）", html);
        Assert.DoesNotContain("拿不到大会员画质", html);
        // 只迁移一次：之后用户自己选的 TV/APP 保留
        Assert.Contains("store.get('opt.apiMigratedV2') === '1'", html);
    }

    [Fact]
    public void WebUi_AutoParseIsDebouncedCachedAndOnlyPinsWhatTheUserPicked()
    {
        var html = IndexHtml();

        Assert.Contains("const PARSE_DEBOUNCE_MS = 400;", html);
        Assert.Contains("const PARSE_CACHE_MS = 60000;", html);
        Assert.Contains("if (!pinnable(link) || !effectivePick()) return body;", html);
    }

    [Fact]
    public void WebUi_FilesTabShowsOneCollapsibleGroupPerVideo()
    {
        var html = IndexHtml();

        // 分组接口；标签上的数字是视频(组)数
        Assert.Contains("await api('/files/groups')", html);
        Assert.Contains("$('fileCount').textContent = fileGroups.length ? String(fileGroups.length) : '';", html);
        // 按组保留卡片、只改变化的部分(封面不闪)
        Assert.Contains("const fileGroupHtml = new WeakMap();", html);
        Assert.Contains("if (last.cover !== parts.cover) { coverEl.innerHTML = parts.cover; last.cover = parts.cover; }", html);
        // 可展开的组默认收起，展开状态按组 Id 保存；搜索时匹配的组自动展开
        Assert.Contains("const groupExpandable = (g) => g.FileCount > 1 || (g.Clips || []).length > 0;", html);
        Assert.Contains("return kw ? !closedInSearch.has(g.Id) : openGroups.has(g.Id);", html);
        Assert.Contains("aria-expanded=\"${open}\"", html);
        // 徽标与操作
        Assert.Contains("未完成${g.ClipCount ? `·${g.ClipCount} 个分片` : ''}", html);
        Assert.Contains("<span class=\"badge done\">完成</span>", html);
        Assert.Contains("继续下载", html);
        Assert.Contains("删除整组", html);
        // 删除整组时带上确认时看到的文件数、大小和修改时间；失败后按钮恢复可用
        Assert.Contains("`/files/groups?id=${encodeURIComponent(g.Id)}&count=${g.FileCount}&bytes=${g.TotalBytes}&mtime=${g.ModifiedTime}`, { method: 'DELETE' }", html);
        Assert.Contains("} finally {\n        // 删除失败时列表可能没有变化、不会重绘，按钮要恢复可用\n        t.disabled = false;", html);
        // 未完成的下载里的文件不能单独删除
        Assert.Contains("body: expandable ? (g.Files || []).map((f) => fileRow(f, incomplete)).join('') + clipsHtml(g) : '',", html);
        Assert.Contains("${inWork ? '' : `<button class=\"icon-btn\" data-del=", html);
        // 搜索时不匹配的卡片只隐藏，「没有匹配」放在单独的元素里
        Assert.Contains("el.hidden = !match;", html);
        Assert.Contains("<div class=\"card empty\" id=\"fileEmpty\" hidden></div>", html);
        Assert.Contains("$('fileNote').hidden = !filesTruncated;", html);
        Assert.Contains("toast('已加入下载队列：' + g.Title);", html);
        Assert.Contains("await api('/add-task', { method: 'POST', body: g.Request });", html);
        // 分片列表：序号、大小、已完成/部分
        Assert.Contains("${c.Complete ? '已完成' : '部分'}", html);
        // 窄屏时操作按钮换行
        Assert.Contains(".fg-actions { flex-basis: 100%; justify-content: flex-start; }", html);
    }

    [Fact]
    public void WebUi_PendingTasksCanBeCancelled_WithoutBreakingKeyedRendering()
    {
        var html = IndexHtml();

        // 排队中的任务卡片上有「取消排队」，正在下载的没有
        Assert.Contains(": state === 'pending'\n        ? `<button class=\"btn small\" data-cancel=\"${esc(t.TaskId)}\" type=\"button\" title=\"从队列中移除这个任务，不会下载\">取消排队</button>`", html);
        Assert.Contains("await api('/remove-pending/' + encodeURIComponent(cancel.dataset.cancel), { method: 'DELETE' });", html);
        // 409：任务恰好开始了或已结束，显示服务器的消息后刷新列表；只有失败时按钮恢复可用(卡片内容没变时不会重建)，
        // 成功时保持禁用直到卡片移除，避免再发一次取消
        Assert.Contains("toast(err.status === 409 ? err.message || '任务已开始，无法取消'", html);
        Assert.Contains("} catch (err) {\n        // 卡片按内容是否变化重绘：失败时内容不变、按钮不会被重建，要自己恢复可用\n        cancel.disabled = false;", html);
        Assert.DoesNotContain("} finally {\n        cancel.disabled = false;", html);
        // 清空排队
        Assert.Contains("<button class=\"btn small\" id=\"clearPending\" type=\"button\"", html);
        Assert.Contains("await api('/remove-pending/', { method: 'DELETE' });", html);
        Assert.Contains("$('clearPending').hidden = activeTab !== 'tasks' || pendingCount < 2;", html);
        // 窄屏时「取消排队」换到标题下一行
        Assert.Contains(".task-head:has([data-cancel]) .task-actions { flex-basis: 100%; }", html);
        // 卡片仍按任务保留，只改变化的部分(封面不闪)
        Assert.Contains("const taskCardHtml = new WeakMap();", html);
        Assert.Contains("if (last.cover !== card.cover) { el.firstElementChild.innerHTML = card.cover; last.cover = card.cover; }", html);
    }

    [Fact]
    public void WebUi_RemembersTheQualitySettingAndTheLastPickedStreams()
    {
        var html = IndexHtml();

        // 「画质」「视频编码优先」下拉框本来就保存在本机；「解析接口」的一次性迁移保留
        Assert.Contains("<select id=\"quality\" data-save>", html);
        Assert.Contains("<select id=\"encoding\" data-save>", html);
        Assert.Contains("store.get('opt.apiMigratedV2') === '1'", html);
        // 点选的视频流、音频流保存在本机；取消时忘掉
        Assert.Contains("const PREF_KEY = 'pref.streams';", html);
        Assert.Contains("savePref('video', v ? { Qn: v.Id, Quality: v.Quality, Codec: v.Codec } : null);", html);
        Assert.Contains("savePref('audio', a ? { Id: a.Id, Label: a.Label, Kind: a.Kind } : null);", html);
        Assert.Contains("rememberPick(part);", html);
        // 每次解析按上次的选择自动选中(规则见 stream-pref，node 检查)；未登录、继续下载旧文件夹时不自动选择
        Assert.Contains("applyStreamPref(res);", html);
        Assert.Contains("prefState = pickByPref(r, pick, loadPref(), prefHold(r), codecOrder($('encoding').value));", html);
        Assert.Contains("let m = hold ? null : matchVideoPref(pref.video, r.Videos, order);", html);
        Assert.Contains("return isResumeParse(r) ? 'resume' : guestParse(r) ? 'guest' : '';", html);
        Assert.Contains("if (m) { p.video = m.video.Key; p.autoVideo = m.how; }", html);
        Assert.Contains("上次选择 ${videoPrefLabel(v.pref)} 不可用，已选 ${curV.Quality} ${curV.Codec}", html);
        Assert.Contains("当前按未登录身份解析，可选画质受限，没有自动选用上次选择的", html);
        Assert.Contains("${how === 'exact' ? '上次选择' : '按上次选择'}", html);
        // 自动选中的流与手动点选一样在下载请求里指定(pinnedRequest 只看 pick.video/pick.audio)
        Assert.Contains("if (v && wantsVideo() && !parsed.Progressive) {\n      body.VideoStream = v.Key;", html);
        Assert.Contains("const effectivePick = () => !!((pick.video && wantsVideo() && parsed && !parsed.Progressive)", html);
        // 改「画质」或「视频编码优先」时忘掉视频流的选择，并取消选中的视频流(手动点选的也取消)
        Assert.Contains("['quality', 'encoding'].forEach((id) => $(id).addEventListener('change', () => {", html);
        Assert.Contains("    if (last) savePref('video', null);\n    pick.video = null;\n    pick.autoVideo = null;", html);
        Assert.True(html.IndexOf("['quality', 'encoding'].forEach((id) => $(id).addEventListener('change'", StringComparison.Ordinal)
            < html.IndexOf("['quality', 'content', 'encoding', 'subtitle', 'page'].forEach((id) => $(id).addEventListener('change', onParseOptionChange));", StringComparison.Ordinal));
    }

    [Fact]
    public void WebUi_WaitsForThePendingParseBeforeSubmitting_SoTheLastPickIsUsed()
    {
        var html = IndexHtml();

        // 记住了上次的选择、第一个链接的解析还在防抖或进行中时，先等解析完成再生成下载请求
        Assert.Contains("if (parseTimer) doParse({ auto: true });", html);
        Assert.Contains("await ctl.done;", html);
        var wait = html.IndexOf("waited = await settleParse(links);", StringComparison.Ordinal);
        var add = html.IndexOf("await api('/add-task', { method: 'POST', body: pinnedRequest(link) });", StringComparison.Ordinal);
        Assert.True(wait >= 0 && wait < add);
        // doParse 无论成功、失败还是被取消都会完成 ctl.done，等待不会卡住
        Assert.Contains("      syncParseState();\n      settle();\n    }\n  }\n  $('parseBtn')", html);
        // 正在提交(含等待解析)时忽略回车和结果区的「下载」：requestSubmit 不看按钮是否禁用
        Assert.Contains("if (submitting) return;", html);
    }

    [Fact]
    public void WebUi_DoesNotApplyOrRememberTheLastPick_WhenResumingAnOldFolderOrParsingAsGuest()
    {
        var html = IndexHtml();

        // 继续下载旧文件夹(填入链接并解析)：记下这个视频，解析它时视频流和音频流都不自动选择
        Assert.Contains("resumeTarget = { aid: g.Aid || null, link: linkKey(link) };", html);
        Assert.Contains("继续下载时不自动选用上次的选择", html);
        // 未登录或继续下载时点选的流不记作上次的选择
        Assert.Contains("    if (prefHold(parsed)) return;\n", html);
        // 取消按上次的选择换选的流(codec/lower)：只是这个视频不选，上次的选择保留
        Assert.Contains("pick[part === 'video' ? 'skipVideoPref' : 'skipAudioPref'] = true;", html);
    }

    [Fact]
    public void WebUi_ParsesMultipleLinksOneByOne_AndSubmitsEachRowWithItsOwnStreams()
    {
        var html = IndexHtml();

        // 多个链接的结果单独放在 #batch 里，不影响单个链接的结果区
        Assert.Contains("<section class=\"card result batch\" id=\"batch\" aria-label=\"多个链接的解析结果\" hidden></section>", html);
        Assert.Contains("if (syncBatch(links)) { syncParseState(); return; }", html);
        // 逐个解析：一次只有一个请求、两次请求之间有间隔、与单个链接共用缓存；输入变了时取消对不上的请求
        Assert.Contains("const BATCH_GAP_MS = 400;", html);
        Assert.Contains("if (batch.ctl || batch.halted) return;", html);
        Assert.Contains("const wait = batch.lastAt + BATCH_GAP_MS - Date.now();", html);
        Assert.Contains("const hit = !row.force && cacheGet(row.reqKey);", html);
        Assert.Contains("if (ctl && !(batch.rows.includes(ctl.row) && ctl.row.reqKey === ctl.reqKey)) { ctl.abort(); batch.ctl = null; }", html);
        // 每行按上次的选择自动选中，规则与单个链接相同(pickByPref)，未登录等情况不自动选择
        Assert.Contains("row.prefState = pickByPref(res, p, loadPref(), prefHold(res), codecOrder($('encoding').value));", html);
        Assert.Contains("列表链接，按原方式下载", html);
        Assert.Contains("超过 ${BATCH_MAX} 个，不解析，按「画质」等设置下载", html);
        Assert.Contains("共 ${r.PageCount} 个分P，按上方「分P」设置下载", html);
        // 展开后复用单个链接的流表格，点选只用于这一行
        Assert.Contains("+ streamSecs(r, row.pick) + '<div class=\"hint\">在这里点选的流只用于这个视频，不会记作上次的选择。</div>'", html);
        Assert.Contains("${hints}<div class=\"hintbar info\" id=\"prefHint\" hidden><span></span></div>${streamSecs(r, pick)}", html);
        // 按行保留元素(封面不闪)
        Assert.Contains("const batchItemHtml = new WeakMap();", html);
        Assert.Contains("if (last.cover !== parts.cover) { el.querySelector('.cover').innerHTML = parts.cover; last.cover = parts.cover; }", html);
        // 全部下载：先等还在解析的(有上次的选择时，最多 90 秒)，再逐行按这一行的流提交
        Assert.Contains("const BATCH_WAIT_MS = 90000;", html);
        Assert.Contains("if (batch.rows.length) return submitBatch(links, ignored);", html);
        var wait = html.IndexOf("wait = await settleBatch();", StringComparison.Ordinal);
        var add = html.IndexOf("await api('/add-task', { method: 'POST', body: batchRowRequest(buildRequest(row.link), row, wants) });", StringComparison.Ordinal);
        Assert.True(wait >= 0 && wait < add);
        Assert.Contains("submitWait = `等待解析 ${prog.done}/${prog.total}…`;", html);
        Assert.Contains("$('submitText').textContent = submitWait || label;", html);
        Assert.Contains("`已添加 ${ok} 个，${errors.length} 个失败（原因见输入框下方）`", html);
        Assert.Contains("还没解析完，按「画质」等设置下载，没有用上次选择的流", html);
        // 改「画质」或「视频编码优先」时各行选中的视频流也取消
        Assert.Contains("const n = clearBatchVideoPicks();", html);
    }

    [Fact]
    public void ResolveAppDir_UsesDataDirectoryWhenConfigured()
    {
        var dataDir = Path.Combine(root, "data");

        Assert.Equal(dataDir, Program.ResolveAppDir(dataDir, "/opt/bbdownt"));
        Assert.True(Directory.Exists(dataDir));
        Assert.Equal("/opt/bbdownt", Program.ResolveAppDir(" ", "/opt/bbdownt"));
    }
}
