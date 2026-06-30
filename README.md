[![img](https://img.shields.io/github/stars/LOVAHE/BBDownT?label=%E7%82%B9%E8%B5%9E)](https://github.com/LOVAHE/BBDownT)  [![img](https://img.shields.io/github/last-commit/LOVAHE/BBDownT?label=%E6%9C%80%E8%BF%91%E6%8F%90%E4%BA%A4)](https://github.com/LOVAHE/BBDownT/commits)  [![img](https://img.shields.io/github/release/LOVAHE/BBDownT?label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC)](https://github.com/LOVAHE/BBDownT/releases)  [![img](https://img.shields.io/github/license/LOVAHE/BBDownT?label=%E8%AE%B8%E5%8F%AF%E8%AF%81)](https://github.com/LOVAHE/BBDownT)  [![Build Latest](https://github.com/LOVAHE/BBDownT/actions/workflows/build_latest.yml/badge.svg)](https://github.com/LOVAHE/BBDownT/actions/workflows/build_latest.yml)

> 本项目仅供个人学习、研究和非商业性用途。使用本工具时，需自行确保遵守相关法律法规，特别是与版权相关的法律条款。开发者不对因使用本工具而产生的任何版权纠纷或法律责任承担责任。请谨慎使用，并仅在有合法授权的情况下使用相关内容。

# BBDownT
一个命令行式哔哩哔哩下载器. Bilibili Downloader.

本项目接手自 [BBDown](https://github.com/nilaoda/BBDown)，后续项目名称、命令和文档均以 `BBDownT` 为准。

# 注意
本软件混流时需要外部程序：

* 普通视频：[ffmpeg](https://www.gyan.dev/ffmpeg/builds/) ，或 [mp4box](https://gpac.wp.imt.fr/downloads/)
* 杜比视界：ffmpeg5.0以上或新版mp4box.

# 快速开始
本软件已经以 [Dotnet Tool](https://www.nuget.org/packages/BBDownT/) 形式发布。

如果你本地有dotnet环境，使用如下命令即可安装使用
```
dotnet tool install --global BBDownT
```

如果需要更新BBDownT，使用如下命令
```
dotnet tool update --global BBDownT
```

# 下载
自动构建产物：https://github.com/LOVAHE/BBDownT/actions/workflows/build_latest.yml

Release页面：https://github.com/LOVAHE/BBDownT/releases

如果Release页面暂无对应版本，请以 Build Latest 工作流产物为准。

# 开始使用
目前命令行参数支持情况
```
Description:
  BBDownT是一个免费且便捷高效的哔哩哔哩下载/解析软件.

Usage:
  BBDownT <url> [command] [options]

Arguments:
  <url>  视频地址 或 av|bv|BV|ep|ss

Options:
  -tv, --use-tv-api                              使用TV端解析模式
  -app, --use-app-api                            使用APP端解析模式
  -intl, --use-intl-api                          使用国际版(东南亚视频)解析模式
  --use-mp4box                                   使用MP4Box来混流
  -e, --encoding-priority <encoding-priority>    视频及音频编码的选择优先级, 用逗号分割 例: "hevc,av1,avc,flac,eac3,m4a"
  -q, --dfn-priority <dfn-priority>              画质优先级,用逗号分隔 例: "8K 超高清, 1080P 高码率, HDR 真彩, 杜比视界"
  -info, --only-show-info                        仅解析而不进行下载
  --show-all                                     展示所有分P标题
  -aria2, --use-aria2c                           调用aria2c进行下载(你需要自行准备好二进制可执行文件)
  -ia, --interactive                             交互式选择清晰度
  -hs, --hide-streams                            不要显示所有可用音视频流
  -mt, --multi-thread                            使用多线程下载(默认开启)
  --video-only                                   仅下载视频
  --audio-only                                   仅下载音频
  --danmaku-only                                 仅下载弹幕
  --sub-only                                     仅下载字幕
  --cover-only                                   仅下载封面
  --debug                                        输出调试日志
  --skip-mux                                     跳过混流步骤
  --skip-subtitle                                跳过字幕下载
  --skip-cover                                   跳过封面下载
  --force-http                                   下载音视频时强制使用HTTP协议替换HTTPS(默认开启)
  -dd, --download-danmaku                        下载弹幕
  -ddf, --download-danmaku-formats <formats>     指定需下载的弹幕格式, 用逗号分隔, 可选 xml/ass, 默认: "xml,ass"
  --skip-ai                                      跳过AI字幕下载(默认开启)
  --video-ascending                              视频升序(最小体积优先)
  --audio-ascending                              音频升序(最小体积优先)
  --allow-pcdn                                   不替换PCDN域名, 仅在正常情况与--upos-host均无法下载时使用
  -F, --file-pattern <file-pattern>              使用内置变量自定义单P存储文件名:
  
                                                 <videoTitle>: 视频主标题
                                                 <pageNumber>: 视频分P序号
                                                 <pageNumberWithZero>: 视频分P序号(前缀补零)
                                                 <pageTitle>: 视频分P标题
                                                 <bvid>: 视频BV号
                                                 <aid>: 视频aid
                                                 <cid>: 视频cid
                                                 <dfn>: 视频清晰度
                                                 <res>: 视频分辨率
                                                 <fps>: 视频帧率
                                                 <videoCodecs>: 视频编码
                                                 <videoBandwidth>: 视频码率
                                                 <audioCodecs>: 音频编码
                                                 <audioBandwidth>: 音频码率
                                                 <ownerName>: 上传者名称
                                                 <ownerMid>: 上传者mid
                                                 <publishDate>: 收藏夹/番剧/合集发布时间
                                                 <videoDate>: 视频发布时间(分p视频发布时间与<publishDate>相同)
                                                 <apiType>: API类型(TV/APP/INTL/WEB)
  
                                                 默认为: <videoTitle>
  -M, --multi-file-pattern <multi-file-pattern>  使用内置变量自定义多P存储文件名:
  
                                                 默认为: <videoTitle>/[P<pageNumberWithZero>]<pageTitle>
  -p, --select-page <select-page>                选择指定分p或分p范围: (-p 8 或 -p 1,2 或 -p 3-5 或 -p ALL 或 -p LAST 或 -p 3,5,LATEST)
  --language <language>                          设置混流的音频语言(代码), 如chi, jpn等
  -ua, --user-agent <user-agent>                 指定user-agent, 否则使用随机user-agent
  -c, --cookie <cookie>                          设置字符串cookie用以下载网页接口的会员内容
  -token, --access-token <access-token>          设置access_token用以下载TV/APP接口的会员内容
  --aria2c-args <aria2c-args>                    调用aria2c的附加参数(默认参数包含"-x16 -s16 -j16 -k 5M", 使用时注意字符串转义)
  --work-dir <work-dir>                          设置程序的工作目录
  --ffmpeg-path <ffmpeg-path>                    设置ffmpeg的路径
  --mp4box-path <mp4box-path>                    设置mp4box的路径
  --aria2c-path <aria2c-path>                    设置aria2c的路径
  --upos-host <upos-host>                        自定义upos服务器
  --force-replace-host                           强制替换下载服务器host(默认开启)
  --save-archives-to-file                        将下载过的视频记录到本地文件中, 用于后续跳过下载同个视频
  --delay-per-page <delay-per-page>              设置下载合集分P之间的下载间隔时间(单位: 秒, 默认无间隔)
  --host <host>                                  指定BiliPlus host(使用BiliPlus需要access_token, 不需要cookie, 解析服务器能够获取你账号的大部分权限!)
  --ep-host <ep-host>                            指定BiliPlus EP host(用于代理api.bilibili.com/pgc/view/web/season, 大部分解析服务器不支持代理该接口)
  --tv-host <tv-host>                            自定义tv端接口请求Host(用于代理api.snm0516.aisee.tv)
  --area <area>                                  (hk|tw|th) 使用BiliPlus时必选, 指定BiliPlus area
  --config-file <config-file>                    读取指定的BBDownT本地配置文件(默认为: BBDownT.config)
  --api-token <api-token>                        服务器API鉴权Token，监听非本机地址且未配置时会自动生成
  --version                                      Show version information
  -?, -h, --help                                 Show help and usage information


Commands:
  login    通过APP扫描二维码以登录您的WEB账号
  logintv  通过APP扫描二维码以登录您的TV账号
  serve    以服务器模式运行
```

# 功能
- [x] 番剧下载(Web|TV|App)
- [x] 课程下载(Web)
- [x] 普通内容下载(Web|TV|App)
- [x] 合集/列表/收藏夹/个人空间解析
- [x] 多分P自动下载
- [x] 选择指定分P进行下载
- [x] 选择指定清晰度进行下载
- [x] 下载外挂字幕并转换为srt格式
- [x] 自动合并音频+视频流+字幕流+**章节信息**`(使用ffmpeg或mp4box)`
- [x] 单独下载视频/音频/字幕
- [x] 二维码登录账号
- [x] 多线程下载
- [x] 支持调用aria2c下载
- [x] 支持AVC/HEVC/AV1编码
- [x] **支持8K/HDR/杜比视界/杜比全景声下载**
- [x] 自定义存储文件名

# TODO
- [ ] 自动刷新cookie
- [ ] 支持更多自定义选项

# 使用教程

<details>
<summary>配置文件 (NEW)</summary> 

---

在`1.4.9`或更高版本中，BBDownT支持读取本地配置文件以简化命令行的手动输入。

如果没有指定`--config-file`，则默认读取程序同目录下的`BBDownT.config`文件；若指定该参数，则读取对应文件。

一个典型的配置文件:
```config
#本文件是BBDownT程序的配置文件
#以#开头的都会被程序忽略
#然后剩余非空白内容程序逐行读取，对于一个选项，其参数应当在下一行出现

#例如下面将设置输出文件名格式
--file-pattern
<videoTitle>[<dfn>]

--multi-file-pattern
<videoTitle>/[P<pageNumberWithZero>]<pageTitle>[<dfn>]

#下面设置下载多个分P时，每个分P的下载间隔为2秒
--delay-per-page
2

#开启弹幕下载功能
--download-danmaku
```

</details>

<details>
<summary>自定义输出文件名格式 (NEW)</summary> 

---

在`1.4.9`或更高版本中，BBDownT支持自定义合并时的文件名组成。
|  代码   | 含义  |
|  ----  | ----  |
`<videoTitle>`|视频主标题
`<pageNumber>`|视频分P序号
`<pageNumberWithZero>`|视频分P序号(前缀补零)
`<pageTitle>`|视频分P标题
`<bvid>`|视频BV号
`<aid>`|视频aid
`<cid>`|视频cid
`<dfn>`|视频清晰度
`<res>`|视频分辨率
`<fps>`|视频帧率
`<videoCodecs>`|视频编码
`<videoBandwidth>`|视频码率
`<audioCodecs>`|音频编码
`<audioBandwidth>`|音频码率
`<ownerName>`|上传者名称(下载番剧时，该值为"")
`<ownerMid>`|上传者mid(下载番剧时，该值为"")
`<publishDate>`|发布时间(yyyy-MM-dd_HH-mm-ss)
`<apiType>`|API类型（TV/APP/INTL/WEB）

</details>

<details>
<summary>WEB/TV鉴权</summary>  

---
  
扫码登录网页账号：
```
BBDownT login
```
然后按照提示操作

扫码登录云视听小电视账号：
```
BBDownT logintv
```
然后按照提示操作
 
*PS: 如果登录报错`The type initializer for 'Gdip' threw an exception`，通常是运行环境缺少图形或二维码相关依赖，请按当前系统补齐依赖后重试*

手动加载网页cookie：
```
BBDownT -c "SESSDATA=******" "https://www.bilibili.com/video/BV1qt4y1X7TW"
```
手动加载云视听小电视token：
```
BBDownT -tv -token "******" "https://www.bilibili.com/video/BV1qt4y1X7TW"
```

</details>

<details>
<summary>APP鉴权</summary>  

---

> TV登录产生的`access_token`也可以给APP接口使用。可复制`BBDownTTV.data`到`BBDownTApp.data`使程序自动读取.

目前程序无法自动获取鉴权信息，推荐通过**抓包**来获取.

在请求Header中寻找键为`authorization`的项，其值形为`identify_v1 5227************1`，其中的`5227************1`就是token(access_key)

获取后手动通过`-token`命令加载, 或写入`BBDownTApp.data`使程序自动读取.
  
```
BBDownT -app -token "******" "https://www.bilibili.com/video/BV1qt4y1X7TW"
```

</details>

<details>
<summary>常用命令</summary>  

---

下载普通视频：
```
BBDownT "https://www.bilibili.com/video/BV1qt4y1X7TW"
```
使用TV接口下载(粉丝量大的UP主基本上是无水印片源)：
```
BBDownT -tv "https://www.bilibili.com/video/BV1qt4y1X7TW"
```
当分P过多时，默认会隐藏展示全部的分P信息，你可以使用如下命令来显示所有每一个分P。
```
BBDownT --show-all "https://www.bilibili.com/video/BV1At41167aj"
```
选择下载某些分P的三种情况：
* 单个分P：10
```
BBDownT "https://www.bilibili.com/video/BV1At41167aj?p=10"
BBDownT -p 10 "https://www.bilibili.com/video/BV1At41167aj"
```
* 多个分P：1,2,10
```
BBDownT -p 1,2,10 "https://www.bilibili.com/video/BV1At41167aj"
```
* 范围分P：1-10
```
BBDownT -p 1-10 "https://www.bilibili.com/video/BV1At41167aj"
```
下载番剧全集：
```
BBDownT -p ALL "https://www.bilibili.com/bangumi/play/ss33073"
```

</details>

<details>
<summary>API服务器</summary>

启动服务器：

```shell
BBDownT serve
BBDownT serve -l http://127.0.0.1:12450
BBDownT serve -l http://0.0.0.0:12450
```

默认监听 `http://127.0.0.1:23333`，仅本机访问时不强制鉴权。如果监听 `0.0.0.0` 或其他非本机地址，API会启用Token鉴权；可以通过 `--api-token` 或 `BBDownT.config` 配置固定Token，未配置时启动会自动生成随机Token。请求时可使用 `Authorization: Bearer <token>` 或 `X-BBDownT-Token: <token>`。

```shell
BBDownT serve -l http://0.0.0.0:12450 --api-token your-token
```

服务器模式默认按安全配置运行：

* `/add-task` 只负责加入内存队列，成功入队返回 `202 Accepted`；队列满返回 `429 Too Many Requests`。
* 默认单任务执行，最多排队100个任务，避免短时间大量请求同时启动下载进程。
* 默认不允许远程任务传入 `Aria2cArgs`，避免对外开放服务时把aria2c附加参数暴露给远程调用方。
* 默认不允许远程任务自定义 `WorkDir`，也不允许绝对路径或包含 `..` 的输出路径。
* 默认不允许远程任务自定义 `Host`、`EpHost`、`TvHost`、`UposHost` 或放开PCDN；如需远程使用BiliPlus或自定义upos，需要显式开启。
* 默认只向B站页面/API域名、官方媒体CDN域名、字幕/静态资源域名发送Cookie，避免跳转或非相关地址拿到Cookie。
* 通过 `--host`、`--ep-host`、`--tv-host`、`--upos-host` 配置的域名会自动加入Cookie允许列表，不需要在 `--cookie-allowed-domains` 里重复填写。
* 默认校验TLS证书；如需抓包代理或调试自签证书，需要显式关闭。

需要兼容旧行为时，可以按需显式开启：

```shell
BBDownT serve --server-allow-aria2c-args --server-allow-custom-output --server-allow-custom-network-hosts
```

常用服务器安全配置：

| 配置 | 默认值 | 说明 |
| ---- | ---- | ---- |
| `--api-token <token>` | 本机监听为空；非本机监听自动生成 | 固定API访问Token，可写入配置文件 |
| `--server-max-queue <num>` | `100` | 最大排队任务数 |
| `--server-download-root <path>` | 当前工作目录 | 默认下载根目录 |
| `--server-allow-aria2c-args` | 关闭 | 允许API请求传入 `Aria2cArgs`，只建议在可信网络使用 |
| `--server-allow-custom-output` | 关闭 | 允许API请求自定义 `WorkDir`、绝对路径或上级目录输出 |
| `--server-allow-custom-network-hosts` | 关闭 | 允许API请求自定义 `Host`、`EpHost`、`TvHost`、`UposHost` 或放开PCDN |
| `--cookie-allowed-domains <domains>` | `bilibili.com,bilibili.tv,biliintl.com,bilivideo.com,bilivideo.cn,hdslb.com,biliapi.net` | 允许发送Cookie的基础域名，支持子域名；`--host`、`--ep-host`、`--tv-host`、`--upos-host` 会自动合并进去 |
| `--allow-insecure-tls` | 关闭 | 关闭TLS证书校验，仅建议调试代理时使用 |
| `--max-grpc-message-mb <num>` | `64` | 限制gRPC/gzip消息解包后的最大大小 |

这些配置都可以写进 `BBDownT.config`。带参数的选项按“选项一行、值一行”写入；布尔开关单独一行即可：

```config
--api-token
your-token

--server-max-queue
100

--server-download-root
./downloads

--cookie-allowed-domains
bilibili.com,bilibili.tv,biliintl.com,bilivideo.com,bilivideo.cn,hdslb.com,biliapi.net

--max-grpc-message-mb
64
```

API服务器不支持HTTPS配置，如果有需要请自行使用nginx等反向代理进行配置

</details>

# 演示
![1](https://user-images.githubusercontent.com/20772925/88686407-a2001480-d129-11ea-8aac-97a0c71af115.gif)

下载完毕后在当前目录查看MP4文件：

![2](https://user-images.githubusercontent.com/20772925/88478901-5e1cdc00-cf7e-11ea-97c1-154b9226564e.png)

# 致谢

* https://github.com/codebude/QRCoder
* https://github.com/icsharpcode/SharpZipLib
* https://github.com/protocolbuffers/protobuf
* https://github.com/grpc/grpc
* https://github.com/dotnet/command-line-api
* https://github.com/SocialSisterYi/bilibili-API-collect
* https://github.com/SeeFlowerX/bilibili-grpc-api
* https://github.com/FFmpeg/FFmpeg
* https://github.com/gpac/gpac
* https://github.com/aria2/aria2
