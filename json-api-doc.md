# JSON API文档

## API

如果以服务器模式启动BBDownT，BBDownT会在本地启动一个http server，该服务器有以下API：

默认监听地址为`http://127.0.0.1:23333`。监听本机地址时默认不需要Token；监听`0.0.0.0`或其他非本机地址时会启用API Token。Token可以通过`--api-token`或`BBDownT.config`配置，未配置时启动会自动生成。

需要Token时，请在请求头中携带以下任意一种：

```text
Authorization: Bearer <token>
X-BBDownT-Token: <token>
```

跨源访问：服务器对任意来源开放CORS，便于其他网页或工具在请求头里携带Token调用 `/add-task`、`/get-tasks` 等接口。
未启用Token时（只监听本机地址）：

- 只接受用本机地址访问：请求的 `Host` 必须是回环地址（`127.0.0.0/8`、`[::1]`）、`localhost` 或 `*.localhost`，否则所有接口都返回 `403`。
  这可以挡住 DNS rebinding（把攻击者的域名解析到 `127.0.0.1`，网页与本服务看起来“同源”）。
  本机反向代理保留了原始 `Host` 又确实不想设置Token时，可用 `--server-allowed-hosts` 指定允许的域名；更推荐设置Token。
- 网页前端专用的接口 `/ui/*`、`/parse`、`/files`、`/history` 和取消排队的 `/remove-pending` 只接受同源请求：浏览器从其他网页（包括本机其他端口上的网页）发起的请求
  及其CORS预检都返回 `403`，防止其他网站读取B站账号和大会员状态、借用登录解析视频，读取、删除已下载的文件和下载历史，或清空下载队列。

同源的判断：浏览器带了 `Sec-Fetch-Site` 时只按它判断（`same-origin`、`none` 为同源，`same-site`、`cross-site` 为跨源），
所以经过会改写 `Host` 的反向代理（如 nginx 默认的 `proxy_set_header Host $proxy_host`）访问也不会误判；
没有这个请求头时（较老的浏览器）比较 `Origin` 与 `Host` 或 `X-Forwarded-Host`。curl、脚本等不带这些请求头的客户端不受影响，
`/add-task` 等其他接口仍允许跨源调用（未启用Token时，本机浏览器里打开的网页也能添加任务、读取任务列表、移除已完成的任务记录，这是为了兼容油猴脚本等工具的原有设计；
`/remove-pending` 是新接口，没有旧工具依赖它，所以只接受同源请求）。
启用Token时，在请求头里携带Token的跨源请求照常可用；只靠网页前端登录Cookie鉴权的请求必须同源，否则返回 `401`。

### 获取任务列表

```http
GET /get-tasks/
```

| 项目 | 内容 |
| ---- | ---- |
| 说明 | 获取所有任务的列表，包括正在运行的任务和已完成的任务 |
| 返回 | JSON格式的`DownloadTaskCollection` |

### 获取正在运行的任务列表

```http
GET /get-tasks/running
```

| 项目 | 内容 |
| ---- | ---- |
| 说明 | 获取所有正在运行的任务的列表 |
| 返回 | JSON格式的`List<DownloadTask>`，正在运行的任务列表 |

### 获取等待中的任务列表

```http
GET /get-tasks/pending
```

| 项目 | 内容 |
| ---- | ---- |
| 说明 | 获取尚未开始执行的任务列表 |
| 返回 | JSON格式的`List<DownloadTask>` |

### 获取已完成的任务列表

```http
GET /get-tasks/finished
```

| 项目 | 内容 |
| ---- | ---- |
| 说明 | 获取所有已完成的任务的列表 |
| 返回 | JSON格式的`List<DownloadTask>`，已完成的任务列表 |

### 获取特定任务

```http
GET /get-tasks/{id}
```

| 项目 | 内容 |
| ---- | ---- |
| 说明 | 获取特定任务的详细信息，优先使用提交时返回的TaskId，也兼容已解析出的AID |
| 参数 | `{id}`：任务TaskId或视频AID |
| 返回 | 找到时返回JSON格式的`DownloadTask`，未找到时返回404 Not Found |

### 添加任务

```http
POST /add-task
```

| 项目 | 内容 |
| ---- | ---- |
| 说明 | 向任务队列中添加新任务 |
| Body | JSON格式的任务信息，需要符合`ServeRequestOptions`数据结构；通常只需要填写`Url`字段 |
| 返回 | `202 Accepted`：已成功加入队列，并返回`{ "TaskId": "..." }` |
| 返回 | `429 Too Many Requests`：任务队列已满 |
| 返回 | `400 Bad Request`：请求无效，并附带错误消息 |
| 返回 | `401 Unauthorized`：未通过API鉴权 |

服务器默认限制通过API传入部分选项：

- 默认不允许传入`Aria2cArgs`，如需使用请启动时配置`--server-allow-aria2c-args`。
- 默认不允许自定义`WorkDir`，也不允许绝对路径或包含`..`的输出路径；如需使用请启动时配置`--server-allow-custom-output`。
- 默认不允许自定义`Host`、`EpHost`、`TvHost`、`UposHost`或开启`AllowPcdn`；如需使用请启动时配置`--server-allow-custom-network-hosts`。

入队前的检查：

- `SelectPage` 只检查写法（如 `1`、`1,3-5`、`LAST`、`ALL`），写法错误返回 `400`；此时还不知道分P数，是否超出分P数在任务执行时检查。
- `DownloadAll` 只接受 UP 主空间投稿链接（`space.bilibili.com/数字`），其他链接返回 `400`，提示使用网页界面上的名称「UP主空间链接：下载全部投稿」并注明字段名。
- 返回 `400` 时服务器日志会记录原因、请求里与默认值不同的字段名，以及去掉查询参数的链接；不记录 Cookie、Token 等字段的值。

精确指定音视频流：`VideoStream`（画质代码[:编码]，如 `120:HEVC`）和 `AudioStream`（音频流ID，如 `30280`），
取值见 `/parse` 返回的 `Videos[].Key`、`Audios[].Id`，或命令行 `-info` 每条流末尾的 `(-vs ...)`、`(-as ...)`。
多P视频中某个分P没有指定的流时，按 `DfnPriority`/`EncodingPriority` 回退并在日志中警告；实际下载的流记录在任务的 `Streams` 字段。
格式错误返回 `400`。

配音语言可通过可选字符串 `AudioLanguage` 指定，完整匹配 `-info` 列出的代码，不区分大小写。
例如下面请求选择平台提供的 `en-US` 配音版本，并仅下载音频：

```json
{
  "Url": "BVxxxx",
  "AudioLanguage": "en-US",
  "AudioOnly": true
}
```

`en-US` 仅为示例，需视频实际提供对应版本。此选项只支持默认 WEB/DASH 模式；
无效代码格式、与 TV/APP/INTL 或仅字幕/封面/弹幕模式冲突会在入队前返回 `400`。
视频未提供该语言、返回版本不符或仅有分段合并流时，已入队任务会记录失败。
显式选择的输出文件加入 `.audio-en-us` 等语言后缀；若同时设置 `SaveArchivesToFile`，
此次不读写仅按 aid 的归档；常规混流模式使用带后缀的输出文件检查是否已下载，
`SkipMux: true` 仍走已有原始流下载流程。
不设置 `AudioLanguage` 时保持默认取流行为。`Language` 仍仅用于写封装的语言标签，字幕另行选择。
`OnlyShowInfo: true` 会在服务器控制台列出配音语言，不新增任务 JSON 的语言列表字段。

字幕选择沿用 `SubOnly`、`SkipAi`、`OnlyShowInfo`，新增可选字符串 `SubtitleLanguage` 和
`AiSubtitlePolicy`，语义与同名命令行参数一致。显式策略优先于 `SkipAi`；未指定策略时，
`SkipAi` 默认 `true`。无效语言格式或策略返回 `400`。

```json
{
  "Url": "BVxxxx",
  "SubOnly": true,
  "SubtitleLanguage": "zh,en",
  "AiSubtitlePolicy": "prefer-human"
}
```

`OnlyShowInfo: true` 配合 `SubOnly: true` 仅将字幕列表输出到服务器控制台，不下载正文。
列表不是新增的JSON返回字段；服务器任务使用语言/策略参数选择字幕，不需要交互输入。
下载任务传入 `Interactive: true` 时返回 `400`，避免队列等待控制台输入；信息模式不会交互。

空间投稿批量下载请求示例：

```json
{
  "Url": "https://space.bilibili.com/123456",
  "DownloadAll": true,
  "DelayPerVideo": 15
}
```

`DownloadAll` 默认为 `false`，此时只导出 TXT 并成功完成任务；`DelayPerVideo` 默认为 `10` 秒，
允许范围为 `0` 到 `2147483`，超过范围返回 `400`。`DownloadAll` 仅接受 UP 主空间投稿链接。
清单写入后才开始逐条处理，`OnlyShowInfo: true` 时只导出。整个批次占一个任务，保留 UP 主标题
和空间 AID；`SavePaths` 包含 TXT 与已产生的媒体/附属文件路径。任一视频失败时仍处理后续视频，
最后将整体 `IsSuccessful` 设为 `false`；已完成文件保留，回调在整个批次结束后发送一次。
批量处理仍在现有串行队列中执行，进度/速度沿用当前媒体的显示，视频序号和失败详情输出到日志。

### 解析预览

```http
POST /parse
```

| 项目 | 内容 |
| ---- | ---- |
| 说明 | 执行与下载任务相同的解析，列出可选的视频流、音频流、分P、字幕，以及画质受限的原因；不下载、不写下载目录、不进入下载队列，可与正在执行的下载同时进行（首次带登录解析时可能在数据目录生成 `BBDownT.web.json`，与下载任务相同） |
| Body | 与 `/add-task` 相同的 `ServeRequestOptions`，另可加 `Page`（从1开始的分P序号，指定列出哪个分P的流） |
| 返回 | `200 OK`：JSON格式的 `ParseResponse` |
| 返回 | `400 Bad Request`：请求无效（如无法识别的链接或编号、不是B站的网址，分P写法错误或超出分P数时会给出分P数和写法示例），附带错误消息 |
| 返回 | `422 Unprocessable Entity`：UP主空间、收藏夹（包括旧版 `/medialist/detail/ml…` 链接）、合集、系列等列表链接（解析预览不会逐页读取整个列表），附带说明 |
| 返回 | `429 Too Many Requests`：同时最多进行 2 个解析，名额已满且排队 5 秒仍未空出 |
| 返回 | `504 Gateway Timeout`：45 秒内没有完成；`502 Bad Gateway`：B站接口出错或返回了无法识别的内容，附带简短说明（异常原文只写进服务器日志） |

- 只使用请求里显式给出的 `Cookie`/`AccessToken`，或数据目录中保存的登录（`BBDownT.data`、`BBDownTTV.data`、`BBDownTApp.data`）；
  不借用其他任务正在使用的Cookie，也不刷新或改写登录文件（Cookie需要刷新时由下一个下载任务处理）。
- 不支持会改写进程级设置的 `UserAgent`、`Area` 和自定义 Host（返回 `400`），解析时也不沿用其他任务设置过的 `Area`/Host；`DownloadAll`、`Interactive`、`CallBackWebHook` 等只影响下载的选项会被忽略。
- `http(s)` 链接只接受 `bilibili.com`、`bilibili.tv`、`biliintl.com` 及其子域和 `b23.tv`、`bili.im` 短链，服务器不会代为请求其他网址。
- 超时或客户端断开后，正在进行的B站请求会立即取消，名额随之释放。
- 番剧和课程的整季信息只需一次请求，会列出全部分P（最多1000个），但音视频流只来自一个分P。
- APP/TV 接口不使用网页扫码登录。没有对应的 access_token 时按未登录身份解析：APP 通常最高 480P，且一次只返回一种编码；TV 拿不到 1080P 及以上画质（未登录通常最高 720P）。

`ParseResponse` 主要字段：

| 字段 | 说明 |
| --- | --- |
| `Api` / `RequestedApi` | 实际使用的接口和请求的接口（互动视频不支持TV，会回退到WEB） |
| `Account` | `WebLoggedIn`、`WebUserName`、`IsVip`、`VipLabel`：网页登录状态；`TvTokenSaved`、`AppTokenSaved`：本机是否保存了TV/APP登录凭证；`ApiAuthenticated`：本次所用接口是否带登录凭证 |
| `Hints` | 提示列表，`Level` 为 `warn`/`info`，`Code` 如 `app-no-token`、`tv-no-token`、`not-logged-in`、`cookie-invalid`、`need-vip`、`app-one-codec`、`progressive`、`low-quality` |
| `Kind` | `video`、`bangumi`、`cheese` 或 `list`（多个视频组成的列表） |
| `Pages` / `PageCount` / `StreamsPage` | 分P列表、分P总数、流列表所属的分P |
| `DownloadPages` | 不指定分P时任务会下载的分P序号，`null` 表示全部 |
| `Videos` | 视频流：`Key`（如 `120:HEVC`，认不出编码时只有画质代码如 `80`；可作为 `VideoStream` 提交）、`Quality`、`Codec`、`Resolution`、`Fps`、`BandwidthKbps`、`Size`（`SizeIsEstimate` 为 `true` 时按时长和码率估算） |
| `Audios` | 音频流：`Id`（可作为 `AudioStream` 提交）、`Label`、`Codec`、`BandwidthKbps`、`Size`、`Kind`（`normal`/`dolby`/`hires`） |
| `DefaultVideo` / `DefaultAudio` | 不指定流时任务会选择的流 |
| `AcceptQualities` | 视频声明的画质档位；`Available` 为 `false` 的档位附带 `NeedLogin`/`NeedVip` |
| `AudioLanguages` / `Subtitles` | 可选配音和字幕 |
| `IsPreviewOnly` / `Progressive` | 只有试看片段；返回的是合并流（只能下载最高画质） |

### 移除已完成的任务

```http
DELETE /remove-finished
```

| 项目 | 内容 |
| ---- | ---- |
| 说明 | 移除所有已完成的任务 |
| 返回 | 200 OK |

### 移除已完成但失败的任务

```http
DELETE /remove-finished/failed
```

| 项目 | 内容 |
| ---- | ---- |
| 说明 | 移除所有已完成但是失败(`IsSuccessful == false`)的任务 |
| 返回 | 200 OK |

### 移除特定已完成的任务

```http
DELETE /remove-finished/{id}
```

| 项目 | 内容 |
| ---- | ---- |
| 说明 | 移除特定已完成的任务，根据TaskId或视频AID |
| 参数 | `{id}`：TaskId或视频AID |
| 返回 | 无论是否能找到对应ID的任务，均返回200 OK |

### 取消排队中的任务

```http
DELETE /remove-pending/{id}
```

| 项目 | 内容 |
| ---- | ---- |
| 说明 | 从队列中移除一个还没开始的任务（`/get-tasks/pending` 里的任务）。被取消的任务不会执行，不写下载历史，也不改动任何文件。未启用Token时只接受同源请求（见上文「跨源访问」） |
| 参数 | `{id}`：TaskId（排队中的任务还没有视频AID） |
| 返回 | `200 OK`：已取消 |
| 返回 | `409 Conflict`：任务正在下载，附带消息「任务已开始，无法取消」；任务已结束（还在已完成列表里），附带消息「任务已结束，无法取消」 |
| 返回 | `404 Not Found`：找不到这个排队中的任务（如已被取消） |

取消与队列开始执行任务互斥：同一时刻队列开始执行的任务，取消要么在开始之前生效（任务不会执行），要么返回 `409`，
不会出现返回成功而任务仍被执行的情况。正在下载的任务目前不能取消。

### 取消全部排队中的任务

```http
DELETE /remove-pending
```

| 项目 | 内容 |
| ---- | ---- |
| 说明 | 取消全部还没开始的任务；正在下载的任务不受影响。未启用Token时只接受同源请求 |
| 返回 | `200 OK`：JSON `{ "Removed": 2 }`，`Removed` 为取消的任务数 |

### 网页前端相关接口

服务器根路径 `/` 提供网页前端，部署方法见 [DEPLOY.md](./DEPLOY.md)。网页使用以下接口：

| 接口 | 说明 |
| ---- | ---- |
| `POST /ui/session` | Body `{ "Token": "..." }`，校验成功后写入 HttpOnly Cookie，之后的请求可用该Cookie代替请求头鉴权；无需鉴权 |
| `DELETE /ui/session` | 清除上述Cookie；无需鉴权 |
| `GET /ui/status` | 返回版本、是否需要鉴权、B站登录状态、昵称和大会员状态（`BiliVip`、`BiliVipLabel`），以及本机是否保存了TV/APP登录凭证（`TvTokenSaved`、`AppTokenSaved`） |
| `POST /ui/bili-login` | 生成B站扫码登录二维码，返回 `{ "Key": "...", "QrCode": "data:image/png;base64,..." }` |
| `GET /ui/bili-login/{key}` | 查询扫码状态，`State` 为 `waiting`/`scanned`/`expired`/`success`；成功后Cookie保存到数据目录并立即生效 |
| `DELETE /ui/bili-login` | 退出B站账号（删除保存的Cookie） |
| `GET /files/` | 列出下载根目录下的文件（相对路径 `Path`、大小 `Size`、修改时间 `ModifiedTime`；属于某条下载历史的文件另有视频标题 `Title`，否则为 null） |
| `GET /files/download?path=<相对路径>` | 下载文件，支持断点续传；加 `&inline=1` 可在浏览器中直接播放 |
| `GET /files/download?task=<TaskId>&index=<序号>` | 下载任务 `SavePaths` 中的第N个文件 |
| `DELETE /files/?path=<相对路径>` | 删除文件；文件在正在下载的临时工作文件夹里时返回409 |
| `GET /files/groups` | 按视频分组列出下载根目录下的文件，返回 `FileGroupList`（见下文「已下载文件分组」）；`/files/` 保持不变 |
| `DELETE /files/groups?id=<组Id>[&count=<FileCount>&bytes=<TotalBytes>&mtime=<ModifiedTime>]` | 删除一整组文件（只删这一组列出的文件；未完成的下载连同临时文件夹一起删除），返回 `{ "Deleted": 删除的文件数, "Failed": 未能删除的文件数 }`；找不到这一组时返回404，正在下载时返回409。带上列表里的 `FileCount`、`TotalBytes`、`ModifiedTime` 时，服务器重新计算的值不一致（列表加载后这一组有了变化）也返回409，网页据此提示刷新后再删 |
| `GET /history/?q=<搜索词>&offset=<起始>&limit=<条数>` | 下载历史，最新的在前，返回 `DownloadHistoryList`；`q` 按标题、UP主、BV号等过滤（空格分隔的多个词须全部匹配），`limit` 默认50、最大500 |
| `DELETE /history/{id}` | 删除一条历史记录（不删除文件）；找不到时返回404 |
| `DELETE /history/` | 清空全部历史记录（不删除文件） |

文件接口只能访问下载根目录内的文件，并且不会返回数据目录中的登录、配置文件和下载历史文件，也不会返回未完成下载的说明文件 `.bbdownt-task.json`。

#### 已下载文件分组

`GET /files/groups` 把文件按视频分组（网页「已下载文件」标签用它，标签上的数字是组数）。规则依次为：

1. **未完成的下载**：下载时的临时工作文件夹 `<aid>/` 里不属于下载历史的文件为一组，`Status` 为 `incomplete`。
   临时工作文件夹指有说明文件 `.bbdownt-task.json` 的子文件夹，或者文件夹名就是 av 号、里面有以这个 av 号命名的工作文件：
   分片 `00000_<aid>.P<n>.<cid>.vclip/.aclip`、单线程下载的临时文件 `<aid>….tmp`（含校验用的 `.verify.tmp`）及它们的 `.resume` 续传状态，
   或合并中断留下的隐藏暂存文件 `.<aid>.P<n>.<cid>.<guid>.partial.mp4`。合并好的轨道旁的 `<轨道>.resume` 不算（只下载不混流时它和轨道就是输出，
   归入轨道所在的组）。别的下载工具留下的 `foo.resume` 之类不算，普通文件夹不会被当成未完成的下载。
   国际站的临时工作文件夹是 `intl_<epid>/`，靠说明文件识别。
   分片单独列在 `Clips` 里（序号、大小、是否完整），其他文件（封面、合并好的轨道、隐藏的暂存文件等）在 `Files` 里。
2. **下载历史记录过的文件**：按视频（BV号/av号/ep号）分组，同一视频的多次下载合为一组，标题、封面、完成时间取最新的记录；
   同一文件夹里同名的字幕、弹幕、封面（如 `视频.mp4` 的 `视频.zh-CN.srt`、`视频.xml`、`视频.jpg`）也归入这一组。
   多P文件夹里有下载记录的文件都来自同一个视频时，文件夹里其他以分P序号开头的音视频（之前的版本下载的分P）和与它们同名的附属文件也归入这一组；
   文件夹里别的文件（没有下载记录的其他视频、说明文字等）不并入。
3. **其余文件**：多P文件夹（至少两个音视频文件以 `[P01]` 之类的分P序号开头）里这些分P音视频和与它们同名的附属文件一组，标题为文件夹名；
   其他文件按文件名（去掉扩展名）分组，同名的附属文件归入同一组，标题为文件名。

下载开始时，下载流程在临时工作文件夹里写入 `.bbdownt-task.json`（权限 `0600`），记下标题、UP主、封面、av号/BV号/cid、分P，
以及继续下载用的请求（字段与 `/add-task` 相同，同 `DownloadHistoryEntry.Request`，不含Cookie、Token等登录信息）；分P完成、文件夹里不再有分片等未完成的工作时随文件夹删除。
旧版本留下的文件夹没有这个文件：列出时按av号查询B站公开的视频信息接口（不带Cookie，只查一次，结果缓存在内存并补写进文件夹的说明文件，`Source` 为 `lookup`，没有 `Request`）；
新开始的查询最多等1.5秒，超时或查询失败时标题显示为「未完成的下载（av号）」，查询中时 `TitlePending` 为 `true`，稍后再请求即可拿到标题。
B站明确返回视频不存在（`-404`）或稿件不可见（`62002`）时 `Unavailable` 为 `true`，不给出 `Url`、`Bvid`，也不写说明文件。
只查询、补写以 av 号命名的临时工作文件夹。

**正在下载**：下载流程开始下载一个视频前登记它要用到的临时工作文件夹，结束（成功、失败或出错）后注销；
登记中的文件夹，或者有正在运行的任务就是这个 av 号时，`Active` 为 `true`，整组和其中的单个文件都不能删除（409）。不再按文件的修改时间估计。

删除整组只删除这一组列出的文件：每个文件都必须在下载根目录内（解析符号链接后也是），不能是受保护的文件，也不能在数据目录里；
未完成的下载还删除临时工作文件夹里的说明文件和合并中断留下的隐藏暂存文件（它们计入 `FileCount`、`TotalBytes` 和返回的 `Deleted`）。
删除后为空的文件夹（只剩 `.DS_Store` 之类系统文件的也算）一并删除，下载根目录本身不删除。只能用 `GET /files/groups` 返回的 `Id` 删除，不接受路径。

**断点续传**由下载引擎负责：分片、单线程临时文件和合并好的轨道旁的 `.resume` 是 JSON 续传状态（资源标识、范围、已下载长度及其 SHA-256、
ETag/Last-Modified）。再次下载同一视频（网页「继续下载」重新提交说明文件里的请求）时，引擎按续传状态校验并沿用已下载的数据，
重新解析后地址签名变化也能认出是同一个媒体文件；沿用的字节不计入下载速度。

#### 下载历史

任务结束时（成功或失败）在数据目录的 `history.json` 中记录一条下载历史，服务重启、「清除已完成」或移除任务后仍保留：

- 普通任务记一条；UP主空间批量下载（`DownloadAll`）清单里的每个视频各记一条（解析失败的也记），投稿清单TXT不算在某个视频里；
  只导出清单、或在解析前就失败的任务按整个任务记一条。只看信息（`OnlyShowInfo`）的任务和 `/parse` 解析预览不记录。
- 最多保留最新的2000条；写入时先写临时文件再改名替换，写到一半退出留下的临时文件在下次启动时清理。
  文件损坏时备份为 `history.json.corrupt-<时间>` 后从空记录开始。非 Windows 系统上文件权限为 `0600`（只有本用户可读写）。
- 文件被别的进程改过时（如命令行 `serve` 和 App 共用数据目录）会在下次读写前重新读取，但两个进程同时写入时仍可能丢失其中一方的修改，
  不建议多个服务共用一个数据目录。
- 文件路径相对下载根目录，`Exists` 在每次查询时检查；不在下载根目录内的输出文件（自定义 `WorkDir` 时）不记录。
- `Request` 是重新下载用的请求，字段与 `/add-task` 相同，可直接提交；只保存链接、解析接口、下载内容、指定的流、画质/编码优先级、
  分P、配音、字幕/弹幕/封面、文件名格式，以及UP主空间链接的 `DownloadAll`、`DelayPerVideo` 等选项，
  不保存Cookie、Token、UserAgent、工作目录、自定义Host等；链接里的 `access_key`、`access_token` 等登录凭证参数直接删掉。

环境变量：

- `BBDOWNT_API_TOKEN`：未通过 `--api-token` 或配置文件指定时，使用此值作为API Token。
- `BBDOWNT_DATA_DIR`：登录信息、配置文件、下载归档和下载历史（`history.json`）的保存目录，默认为程序所在目录。

## 服务器配置

以下选项只在`serve`模式下使用，也可以写入`BBDownT.config`：

| 配置 | 说明 |
| ---- | ---- |
| `--api-token <token>` | 指定API Token |
| `--server-max-queue <num>` | 设置等待队列最大长度，默认100；不包含正在执行的任务 |
| `--server-max-finished <num>` | 最多保留的已完成任务数，默认1000 |
| `--server-finished-retention-hours <hours>` | 已完成任务最长保留时间，默认24小时 |
| `--server-download-root <path>` | 设置服务器下载根目录，默认使用当前工作目录 |
| `--server-allow-aria2c-args` | 允许API任务传入aria2c附加参数 |
| `--server-allow-custom-output` | 允许API任务自定义工作目录和输出路径 |
| `--server-allow-custom-network-hosts` | 允许API任务自定义解析和下载相关Host |
| `--server-allow-private-callbacks` | 允许回调本机、内网或保留地址；默认拒绝 |
| `--server-allowed-hosts <hosts>` | 未启用API Token时，除本机地址外还允许用来访问的域名，用逗号分隔（如本机反向代理保留了原始 `Host`）；默认只接受本机地址 |
| `--cookie-allowed-domains <domains>` | 设置允许携带Cookie的域名列表，用逗号分隔 |
| `--allow-insecure-tls` | 允许忽略TLS证书错误 |
| `--max-grpc-message-mb <num>` | 设置gRPC响应最大解压大小，默认64MiB |

## 数据结构

### `DownloadTask` 数据结构
`DownloadTask` 数据结构表示一个下载任务的信息。

**属性：**
- `TaskId` `<string>`: 提交任务时立即生成的稳定唯一标识；AID尚未解析或解析失败时也可用。
- `Aid` `<string>`: 解析出的输入标识，普通视频为AID，空间投稿任务为`mid:UID`；区分任务请使用`TaskId`。
- `Url` `<string>`: 下载任务请求时的URL，不一定需要完整的URL，命令行支持的`av|bv|BV|ep|ss`都可以在这里使用。
- `TaskCreateTime` `<long>`: 任务创建时间，Unix时间戳，精确到秒，本机时区。
- `Title` `<string?>`: 视频的标题。
- `Pic` `<string?>`: 视频的封面图片链接。
- `VideoPubTime` `<long?>`: 视频发布时间，Unix时间戳，精确到秒。
- `TaskFinishTime` `<long?>`: 任务完成时间，Unix时间戳，精确到秒，本机时区。
- `Progress` `<double>`: 任务的下载进度，为0-1区间范围的小数。
- `DownloadSpeed` `<double>`: 下载速度，单位为Byte/s。下载中时为最后一次更新的实时速度，下载完成后为平均速度。
- `TotalDownloadedBytes` `<double>`: 总下载字节(Byte)数，即本次实际下载的各个音视频文件大小之和；续传时沿用的已下载字节不计入。
- `IsSuccessful` `<bool>`: 标识任务是否成功完成。
- `Error` `<string?>`: 失败原因；敏感值会被遮盖，成功或尚未失败时为null。
- `SavePaths` `<List<string>>`: 已记录的输出文件绝对路径，包括空间投稿导出的TXT清单及下载产生的媒体或附属文件。
- `Streams` `<List<string>>`: 每个分P实际下载的音视频流，如 `P1 · WEB · 4K 超清 3840x2160 HEVC · 192K M4A`；同一分P重试时覆盖旧记录。

### `DownloadTaskCollection` 数据结构
`DownloadTaskCollection` 数据结构包含等待、正在运行和已完成三个列表。

**属性：**
- `Pending` `<List<DownloadTask>>`: 尚未开始执行的任务列表。
- `Running` `<List<DownloadTask>>`: 包含正在运行的任务的列表，每个元素都是`DownloadTask`数据结构。
- `Finished` `<List<DownloadTask>>`: 包含已完成的任务的列表，每个元素都是`DownloadTask`数据结构。

### `DownloadHistoryList` 数据结构
`GET /history/` 的返回。

**属性：**
- `Total` `<int>`: 全部历史记录数。
- `Matched` `<int>`: 符合搜索词 `q` 的记录数（没有 `q` 时等于 `Total`）。
- `Offset` `<int>`、`Limit` `<int>`: 实际使用的分页参数。
- `Items` `<List<DownloadHistoryEntry>>`: 本页记录，最新的在前。

### `DownloadHistoryEntry` 数据结构
一条下载历史，对应一个视频的一次下载。

**属性：**
- `Id` `<string>`: 记录ID，用于 `DELETE /history/{id}`。
- `TaskId` `<string>`: 所属任务的 `TaskId`；UP主空间批量下载的各条记录属于同一个任务。
- `FinishedAt` `<long>`: 结束时间，Unix时间戳，精确到秒。
- `Url` `<string>`: 提交的链接或编号，已去掉 `spm_id_from`、`vd_source`、`share_source` 等分享跟踪参数和 `access_key`、`access_token` 等登录凭证参数。
- `PageUrl` `<string?>`: 在B站打开的页面；输入是链接时就是 `Url`，否则按BV号、EP号拼出，认不出时为null。
- `Kind` `<string?>`: `video`（普通视频）、`bangumi`（番剧）、`cheese`（课程）、`list`（收藏夹、合集等）、`space`（UP主投稿清单）；未知时为null。
- `Aid`、`Bvid`、`Ep` `<string?>`: 能识别时的AV号（数字）、BV号、EP号。
- `Title` `<string?>`: 视频标题；解析前就失败时为null。
- `Owner` `<string?>`: UP主昵称。
- `Pic` `<string?>`: 封面图片链接。
- `Api` `<string?>`: 实际使用的解析接口：`WEB`、`TV`、`APP` 或 `INTL`。
- `Pages` `<List<{ Index, Title }>>`: 实际下载完成（含已存在而跳过）的分P。
- `Streams` `<List<string>>`: 每个分P实际下载的音视频流，与 `DownloadTask.Streams` 相同。
- `StreamTags` `<List<string>>`: 画质、编码、音质标签（去重），如 `["4K 超清", "HEVC", "192K"]`。
- `Files` `<List<{ Path, Size, Exists }>>`: 输出文件：相对下载根目录的路径、记录时的大小，以及查询时文件是否仍存在。
- `TotalBytes` `<long>`: 输出文件大小之和。
- `Success` `<bool>`: 是否成功。
- `Skipped` `<bool>`: 每个分P输出的 MP4 都已存在，这次没有重新下载（仍算成功）；此时 `Streams`、`StreamTags` 为空，`Files`、`TotalBytes` 是已有文件的。
- `Error` `<string?>`: 失败原因（常见系统错误换成中文说明，下载根目录下的路径换成相对路径，敏感值已遮盖，最长300字）；成功时为null。
- `Request` `<object>`: 重新下载用的请求，可直接 `POST /add-task`；只包含与默认值不同的字段。

### `FileGroupList` 数据结构
`GET /files/groups` 的返回。

**属性：**
- `Total` `<int>`: 组数。
- `TotalFiles` `<int>`: 列出的文件数（含分片和 `.resume` 续传状态，不含隐藏的说明文件和暂存文件）。
- `Truncated` `<bool>`: 文件超过2万个、只统计了最新的一部分时为 `true`。
- `Groups` `<List<FileGroup>>`: 各组，最近更新（或完成）的在前。

### `FileGroup` 数据结构
一个视频的文件。

**属性：**
- `Id` `<string>`: 组的标识，用于 `DELETE /files/groups?id=`：`h:<BV号等>`（下载历史）、`d:<文件夹>`（多P文件夹）、`s:<文件夹/文件名>`（同名文件）、`w:<文件夹>`（未完成的下载）。
- `Status` `<string>`: `complete`（已完成）或 `incomplete`（未完成的下载）。
- `Source` `<string>`: 分组依据：`history`、`folder`、`file`、`work`。
- `Title` `<string>`: 视频标题；没有记录时为文件夹名、文件名或「未完成的下载（av号）」。
- `TitlePending` `<bool>`: 正在按av号查询标题。
- `Unavailable` `<bool>`: 未完成的下载：按av号查询时B站返回视频不存在或不可见，不能继续下载（`Url`、`Bvid` 为null）。
- `Pic` `<string?>`: 封面图片链接；`CoverFile` `<string?>`: 组里的本地图片文件（如下载时保存的封面）。
- `Owner`、`Aid`、`Bvid`、`PageUrl` `<string?>`: UP主、av号、BV号、B站页面链接（能识别时）。
- `Url` `<string?>`: 重新解析用的链接或编号。
- `FinishedAt` `<long?>`: 下载历史记录的完成时间；`ModifiedTime` `<long>`: 组内文件最新的修改时间。均为Unix时间戳（秒）。
- `TotalBytes` `<long>`: 删除整组时会删除的全部文件的大小之和（未完成的下载含分片、续传状态、说明文件和隐藏的暂存文件）。
- `FileCount` `<int>`: 删除整组时会删除的文件数。已完成的组就是 `Files` 的个数；未完成的下载还包括分片、`.resume` 续传状态、说明文件和隐藏的暂存文件。
- `MainFile` `<string?>`: 主视频（没有视频时为音频）的相对路径，用于播放和打开位置；未完成的下载为null。
- `Folder` `<string?>`: 多P文件夹或临时工作文件夹的相对路径。
- `Files` `<List<{ Path, Size, ModifiedTime, Title }>>`: 组内的文件（未完成的下载不含分片和 `.resume`），音视频在前；`Title` 恒为null（标题在组上）。
  未完成的下载还列出合并中断留下的隐藏暂存文件（`.<轨道>.<guid>.partial.mp4`）。
- `ClipCount`、`CompleteClipCount` `<int>`: 分片数、其中已下载完整的分片数。
- `Clips` `<List<{ Path, Track, Index, Page, Size, Complete }>>`: 未完成的下载的分片：`Track` 为 `video`/`audio`，`Page` 为能认出的分P序号，
  `Complete` 表示这一段已下载完整：分片的 `.resume` 续传状态记下已完整，且本地文件长度与记下的一致；没有或读不出续传状态时算未完成。
- `Active` `<bool>`: 正在下载（下载流程登记了这个临时工作文件夹，或者有正在运行的任务就是这个av号）；此时整组和其中的文件都不能删除。
- `Request` `<object?>`: 未完成的下载：继续下载用的请求，可直接 `POST /add-task`（没有记下时为null，网页改为填入链接并解析）；
  已完成的下载：下载历史里的重新下载请求。

### `ServeRequestOptions` 数据结构

参考[BBDownT/Model/ServeRequestOptions.cs](./BBDownT/Model/ServeRequestOptions.cs)和[BBDownT/MyOption.cs](./BBDownT/MyOption.cs)。属性和命令行参数基本对应，相应的值填写命令行会使用的值即可。这个结构会随着版本变化，请参考对应版本的文件。

空间投稿批量下载相关字段：

| 字段 | 类型 | 默认值 | 对应命令行参数与作用 |
| --- | --- | --- | --- |
| `DownloadAll` | `bool` | `false` | `--download-all`：空间TXT导出完成后按清单串行下载 |
| `DelayPerVideo` | `int` | `10` | `--delay-per-video`：视频任务间隔秒数，允许0到2147483 |

`DelayPerVideo` 与现有的分P间隔 `DelayPerPage` 分别生效；首项之前、末项之后不等待。

## 注意事项

- `TotalDownloadedBytes` 按每秒一次的测速累计，下载结束时会补上最后不足1秒的部分，因此等于这次实际下载的字节数；续传时沿用的已下载字节不计入下载量和下载速度。
- BBDownT目前内部机制没有太好的方法取消单个下载任务，因此目前任务提交以后只能等任务失败或者完成。
- 服务器任务会进入内存队列，程序退出后队列不会保留；下载历史保存在数据目录的 `history.json`，不受影响。
- callback 默认超时为10秒，失败不改变下载任务本身的成功状态，也不会阻塞后续下载任务。

## 使用例

#### 按空间清单批量下载

```shell
curl -X POST -H 'Content-Type: application/json' -d '{ "Url": "https://space.bilibili.com/123456", "DownloadAll": true, "DelayPerVideo": 15 }' http://localhost:23333/add-task
```

省略 `DownloadAll` 或设为 `false` 时只导出TXT。需要鉴权的服务仍须携带API Token。

#### 先解析再指定画质下载

```shell
curl -X POST -H 'Content-Type: application/json' -d '{ "Url": "BV1qt4y1X7TW" }' http://localhost:23333/parse
curl -X POST -H 'Content-Type: application/json' -d '{ "Url": "BV1qt4y1X7TW", "VideoStream": "120:HEVC", "AudioStream": "30280" }' http://localhost:23333/add-task
```

#### 用BV号添加任务

```shell
curl -X POST -H 'Content-Type: application/json' -d '{ "Url": "BV1qt4y1X7TW" }' http://localhost:23333/add-task
```

#### 携带API Token添加任务

```shell
curl -X POST -H 'Content-Type: application/json' -H 'Authorization: Bearer <token>' -d '{ "Url": "BV1qt4y1X7TW" }' http://localhost:23333/add-task
```

#### 使用相对路径输出

```shell
curl -X POST -H 'Content-Type: application/json' -d '{ "Url": "BV1qt4y1X7TW", "FilePattern": "downloads/<videoTitle>[<dfn>]" }' http://localhost:23333/add-task
```
