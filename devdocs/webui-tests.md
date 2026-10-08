# 网页前端的链接规则测试

网页前端（`BBDownT/WebUi/index.html`）在浏览器里判断输入的是哪类链接：从分享文本中提取链接、
判断是否是UP主空间链接、是否自动解析、两个输入是否是同一个视频等。这些规则有一部分与服务端相同
（如 `SpaceBatchDownload.IsSpaceUrl`，以及下载历史记录链接时去掉分享跟踪参数和登录凭证参数的 `DownloadHistory.CleanUrl`，
对应前端的 `cleanLink`），改动任一侧时两边都要检查。

- 对照用例：`BBDownT.Tests/TestData/link-rules.json`。
- 前端函数放在 `index.html` 的 `// link-rules:begin` 和 `// link-rules:end` 之间，只能是不依赖页面的纯函数。
- 前端检查脚本：`BBDownT.Tests/WebUi/check-link-rules.mjs`，抽出上面那一段，用对照用例逐条检查。

运行：

```bash
node BBDownT.Tests/WebUi/check-link-rules.mjs   # 只检查前端，需要 Node.js 18 或更新版本
dotnet test BBDownT.Tests                        # C# 测试会用同一份用例检查服务端，装有 node 时也会运行上面的脚本
```

新增或修改规则时，先在 `link-rules.json` 里补上用例，再改前端或服务端的实现。

## 按上次的选择自动选中流

解析结果里点选的视频流、音频流保存在本机，之后每次解析按 `index.html` 里 `// stream-pref:begin` 到
`// stream-pref:end` 之间的纯函数（`matchVideoPref`、`matchAudioPref`、`guestParse`）自动选中：
完全相同的流；同画质的其他编码（按「视频编码优先」）；更低、最接近的一档；只有更高的画质时不选。
检查脚本 `BBDownT.Tests/WebUi/check-stream-pref.mjs` 抽出这一段，用脚本里的用例逐条检查：

```bash
node BBDownT.Tests/WebUi/check-stream-pref.mjs
```

`dotnet test` 装有 node 时也会运行它（`WebUiLinkRulesTests`）。
