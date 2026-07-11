# JSON API文档

## API

如果以服务器模式启动BBDownT，BBDownT会在本地启动一个http server，该服务器有以下API：

默认监听地址为`http://127.0.0.1:23333`。监听本机地址时默认不需要Token；监听`0.0.0.0`或其他非本机地址时会启用API Token。Token可以通过`--api-token`或`BBDownT.config`配置，未配置时启动会自动生成。

需要Token时，请在请求头中携带以下任意一种：

```text
Authorization: Bearer <token>
X-BBDownT-Token: <token>
```

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
| `--cookie-allowed-domains <domains>` | 设置允许携带Cookie的域名列表，用逗号分隔 |
| `--allow-insecure-tls` | 允许忽略TLS证书错误 |
| `--max-grpc-message-mb <num>` | 设置gRPC响应最大解压大小，默认64MiB |

## 数据结构

### `DownloadTask` 数据结构
`DownloadTask` 数据结构表示一个下载任务的信息。

**属性：**
- `TaskId` `<string>`: 提交任务时立即生成的稳定唯一标识；AID尚未解析或解析失败时也可用。
- `Aid` `<string>`: 视频解析出的Aid，用作正在下载中的任务的唯一标识符，已完成任务中允许重复存在
- `Url` `<string>`: 下载任务请求时的URL，不一定需要完整的URL，命令行支持的`av|bv|BV|ep|ss`都可以在这里使用。
- `TaskCreateTime` `<long>`: 任务创建时间，Unix时间戳，精确到秒，本机时区。
- `Title` `<string?>`: 视频的标题。
- `Pic` `<string?>`: 视频的封面图片链接。
- `VideoPubTime` `<long?>`: 视频发布时间，Unix时间戳，精确到秒。
- `TaskFinishTime` `<long?>`: 任务完成时间，Unix时间戳，精确到秒，本机时区。
- `Progress` `<double>`: 任务的下载进度，为0-1区间范围的小数。
- `DownloadSpeed` `<double>`: 下载速度，单位为Byte/s。下载中时为最后一次更新的实时速度，下载完成后为平均速度。
- `TotalDownloadedBytes` `<double>`: 总下载字节(Byte)数，完成后的数字比实际文件偏小。
- `IsSuccessful` `<bool>`: 标识任务是否成功完成。
- `Error` `<string?>`: 失败原因；敏感值会被遮盖，成功或尚未失败时为null。

### `DownloadTaskCollection` 数据结构
`DownloadTaskCollection` 数据结构包含等待、正在运行和已完成三个列表。

**属性：**
- `Pending` `<List<DownloadTask>>`: 尚未开始执行的任务列表。
- `Running` `<List<DownloadTask>>`: 包含正在运行的任务的列表，每个元素都是`DownloadTask`数据结构。
- `Finished` `<List<DownloadTask>>`: 包含已完成的任务的列表，每个元素都是`DownloadTask`数据结构。

### `ServeRequestOptions` 数据结构

参考[BBDownT/Model/ServeRequestOptions.cs](./BBDownT/Model/ServeRequestOptions.cs)和[BBDownT/MyOption.cs](./BBDownT/MyOption.cs)。属性和命令行参数基本对应，相应的值填写命令行会使用的值即可。这个结构会随着版本变化，请参考对应版本的文件。

## 注意事项

- 由于BBDownT的下载进度回报频率所限，`TotalDownloadedBytes`会比实际下载的文件略低，大概会少等效于1秒下载速度的文件体积，如果文件本身就非常小那这个数字偏差会较大。
- BBDownT目前内部机制没有太好的方法取消单个下载任务，因此目前任务提交以后只能等任务失败或者完成。
- 服务器任务会进入内存队列，程序退出后队列不会保留。
- callback 默认超时为10秒，失败不改变下载任务本身的成功状态，也不会阻塞后续下载任务。

## 使用例

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
