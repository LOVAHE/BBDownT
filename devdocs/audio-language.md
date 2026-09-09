# 配音语言选择

实现日期：2026-09-06。用户选择先实现平台配音语言选择，暂不实现并行下载。

## 功能与入口

- CLI/config：`--audio-language <code>`；`-info` 在音视频流列表之后显示“可选配音语言：”、语言代码、标题、AI 标记和默认/当前版本。默认行仅在真实终端中显示绿色，重定向输出保持纯文本；默认版本来自未指定语言时的接口响应。
- HTTP 任务：可选字符串 `AudioLanguage`，由 `ServeRequestOptions : MyOption` 与原 source-generation 上下文绑定。
- 现有 `--language` / JSON `Language` 仍只设置封装语言标签，不选择实际音轨。
- 一次一个完整语言代码，不区分大小写；采用服务端返回的 canonical code 发请求。没有语言族匹配、自动翻译或新增交互菜单。
- 只支持 WEB/DASH。显式配音与 TV/APP/INTL 或仅字幕/封面/弹幕模式冲突，在任务执行前拒绝。

## 协议与选择流程

1. 不指定语言：沿用原请求次数和默认版本，只附加解析可用语言元数据。
2. 指定语言：先使用现有解析器获取可选语言，精确匹配 `language.items[].lang`。
3. 使用匹配到的代码重新获取完整播放结果，传入 `cur_language`；保持原 `fnval`、质量补请求和签名路径。
4. 每次选定语言的响应，包括最高画质补请求，必须包含一致的 `cur_language`，否则在合并轨道前抛出 `AudioLanguageUnavailableException`。
5. 选定结果整体替代默认结果，不拼接默认版本的视频或音频。接口若返回 DURL 分段合并流，CLI 在文件下载前拒绝。
6. 指定版本不可用属于确定性失败，单 P 重试层不重试该异常；其他异常保留原重试方式。

元数据来源是播放响应 root 的 `cur_language` 与 `language.items`，不是单个 DASH audio 条目的 codec/码率。
`production_type == 2` 才标 AI。使用服务端标题，不推断第一个或非 AI 项必然是原声。

参考了公开接口的字段与调用方式，未复制第三方项目代码：

- [PiliPlus 语言请求与切换](https://github.com/bggRGjQaUbCoE/PiliPlus/blob/4d66b7b638c9cb9d533ffe95f23a56b822af90e2/lib/pages/video/controller.dart#L781)
- [PiliPlus 语言响应字段](https://github.com/bggRGjQaUbCoE/PiliPlus/blob/4d66b7b638c9cb9d533ffe95f23a56b822af90e2/lib/models/video/play/url.dart#L145)
- [Bilibili-Evolved 的 cur_language 请求](https://github.com/the1812/Bilibili-Evolved/blob/9535fa6cade09148b817334e1e940ab1052759c5/registry/lib/components/video/download/apis/dash.ts#L119)

## 代码所有权

| 模块 | 职责 |
| --- | --- |
| `BBDownT.Core/Entity/AudioLanguageInfo.cs` | 语言元数据与明确的不可用异常。 |
| `BBDownT.Core/AudioLanguageMapper.cs` | 解析字段、去重、逐响应语言确认。 |
| `BBDownT.Core/Parser.cs` | 将请求语言带入所有质量请求，保留原公开 ExtractTracksAsync 签名并新增 overload。 |
| `BBDownT/AudioLanguageSelection.cs` | 选项校验、选择/整体换流、信息展示、版本文件名及 aid 归档规则。 |
| `BBDownT/Program.cs` | 真实下载接线；显式选择在任何文件创建/下载之前执行。 |

## 文件与归档

- 显式语言输出及主音视频临时文件附加 `.audio-<lowercase-code>`；同代码不同大小写共享同一命名。
- 显式 audio-only 版本在检查已有文件前确定 `.m4a`，避免同语言 MP4 误命中。
- 原来的归档文件仅保存 AID，不能区分语言。显式配音不读写此归档，不改变旧格式；常规混流模式通过独立输出文件复用，skip-mux 仍按原始流流程处理。
- 不指定语言时，命名、归档和缓存流程保持原行为。
- 不自动改字幕选择或封装语言标签。字幕依然由现有字幕选项控制。

## 验证

新增 51 项离线用例，涵盖：字段映射、缺失元数据、每轮质量返回语言校验、URL 编码/签名前传参、非 WEB 拒绝、精确匹配/大小写、整套结果替换、分段流拒绝、默认/当前语言展示、输出/临时命名、aid-only 归档隔离、CLI/config/API/batch 传递。

```bash
env DOTNET_PROCESSOR_COUNT=1 DOTNET_GCHeapHardLimit=0x20000000 MSBUILDDISABLENODEREUSE=1 \
  dotnet test BBDownT.sln -c Release --no-restore --nologo \
  -m:1 -nr:false -p:UseSharedCompilation=false
git diff --check
```

2026-09-09 提交前验证：381 项 Release 测试通过，0 失败、0 跳过；编译成功。330 项基线包含分 P 调度解耦。

验证限制：未使用真实账号或线上配音视频做下载；采用公开协议字段与合成响应，不能证明每个账号/视频都可返回该功能。
URL 构造及多次质量响应分别通过测试；公共 ExtractTracksAsync 闭包向全部质量请求传递语言的组合接线经源码复核，尚无真实 HTTP 端到端测试。

维护交接：通过，独立子代理复核未发现剩余阻断。配音选择逻辑有独立入口与测试；默认行为和历史接口签名保留。后续若支持其他取流协议、多版本批量保存或交互语言选择，应先定义相应的失败、缓存和字幕策略。
