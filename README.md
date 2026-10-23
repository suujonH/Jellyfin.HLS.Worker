# Jellyfin HLS Worker

为 Jellyfin 预编码 HLS 的 Worker。它向服务器领取任务，用 ffmpeg 把视频编码成多个档位的 HLS，上传到自己的存储根（S3、SFTP 或本机目录），再报告给服务器；服务器校验后发布，播放时直接交付这些成果。

Worker 还能把本机文件**添加到媒体库**：视频放到 S3，并在媒体库目录中写入 `.strm` 和它的 TrueCache 条目；也可以直接放进媒体库目录。添加时可以同时编码部分或全部档位。

需要带 HLS 任务中心的 Jellyfin 服务器（协议版本 1），以及一个管理员 API Key。

## 包内容

发布包依赖框架，需要先安装 **.NET 10 运行时**和 **ASP.NET Core 10 运行时**（TrueCache 工具运行服务器的代码，需要后者）。提供 `win-x64`、`linux-x64`、`linux-arm64` 三种。

- Windows：从 [.NET 10 下载页](https://dotnet.microsoft.com/download/dotnet/10.0) 安装「.NET Runtime」和「ASP.NET Core Runtime」。
- Linux：安装发行版或 Microsoft 软件源中的 `aspnetcore-runtime-10.0` 包，它会同时装上 .NET 运行时。
- 运行时要装在系统位置：systemd 服务不读取用户的 `DOTNET_ROOT`。用 `dotnet --list-runtimes` 确认已有 `Microsoft.NETCore.App 10.*` 和 `Microsoft.AspNetCore.App 10.*`。

| 文件 | 说明 |
| --- | --- |
| `jellyfin-hls-worker` | 命令行 Worker：`init`、`check`、`run`、`add` |
| `jellyfin-hls-worker-gui` | 桌面程序：运行、检查、添加媒体、编辑配置 |
| `truecache/` | `Jellyfin.TrueCache.Tool`，添加媒体到 S3 时生成 TrueCache 条目 |
| `install.sh`、`jellyfin-hls-worker.service` | Linux 下安装为 systemd 服务（仅 Linux 包） |

- **TrueCache 工具**随 Worker 一起发布。配置中的 `TrueCacheTool` 保持默认值 `Jellyfin.TrueCache.Tool` 时，Worker 使用包内 `truecache/` 中的工具；包内没有时，从 PATH 查找。也可以填路径，指定另一个工具。
- 工具运行的是服务器自己的探测和提取代码，在 Linux 上需要 libicu（`libicu` 或 `icu-libs` 包）。Jellyfin 服务器所在的机器通常已经装有。
- Windows 打出的 `.tar.gz` 不带可执行权限。手动运行前先执行：
  ```sh
  chmod +x jellyfin-hls-worker jellyfin-hls-worker-gui truecache/Jellyfin.TrueCache.Tool
  ```
  用 `install.sh` 安装时会自动设置。

另外需要 ffmpeg 和 ffprobe。建议使用 [jellyfin-ffmpeg](https://github.com/jellyfin/jellyfin-ffmpeg)：HDR 色调映射需要的 `zscale` 和各硬件编码器它都带有。

## 快速开始

```sh
jellyfin-hls-worker init --config worker.json    # 生成配置模板
# 填写 Server、ApiKey、Connections 和 Hls
jellyfin-hls-worker check --config worker.json   # 测试编码器、服务器、各连接和 HLS 根，不领取任务
jellyfin-hls-worker run --config worker.json     # 常驻领取并处理任务
```

- `run --once`：没有可领取的任务、进行中的任务也都结束后退出。
- API Key 也可以由环境变量 `JELLYFIN_HLS_API_KEY` 提供，优先于配置文件。
- Ctrl+C 或 SIGTERM 会停止进行中的任务，报告给服务器后退出。

退出码：

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功 |
| 1 | 有任务失败，或服务器不可用 |
| 2 | 参数或配置错误 |
| 130 | 被停止 |

## 配置

配置文件是一个 JSON 文件，字段名不区分大小写，不认识的字段会报错。相对路径相对于配置文件所在的目录。

### 基本字段

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `Server` | `http://localhost:8096` | 服务器地址；经反向代理时带上路径前缀 |
| `ApiKey` | — | 管理员 API Key |
| `Name` | 机器名 | 报告给服务器的 Worker 名称 |
| `Ffmpeg`、`Ffprobe` | `ffmpeg`、`ffprobe` | 只写名称时从 PATH 查找，带路径分隔符的视为路径 |
| `TrueCacheTool` | `Jellyfin.TrueCache.Tool` | 见上文「包内容」 |
| `WorkDirectory` | `work` | 输入缓存、任务目录和本地数据库所在的目录 |
| `Encoder` | `libx264` | 可选 `h264_nvenc`、`h264_qsv`、`h264_amf`、`h264_vaapi` |
| `HardwareDevice` | — | 硬件编码器的设备：VAAPI 填渲染节点（如 `/dev/dri/renderD128`，必填），NVENC 填 GPU 序号 |
| `Threads` | `0` | ffmpeg 的线程数，0 由 ffmpeg 决定 |
| `Concurrency` | `1` | 同时下载输入、识别和编码的任务数 |
| `UploadConcurrency` | `1` | 同时上传的任务数；编完的任务等待上传时，下一个任务可以开始编码 |
| `PollSeconds` | `10` | 没有领到任务时，两次领取之间的秒数 |
| `ControlTimeoutSeconds` | `30` | 下载以外请求的超时秒数 |
| `InputCacheGiB` | `20` | 输入缓存的容量；0 表示任务完成后立即删除输入 |
| `KeepFailedJobs` | `false` | 失败任务的目录是否保留，便于排查 |

### 连接（`Connections`）

每条连接有 `Name`（1–64 个字符，供其他设置引用）和 `Type`。

| `Type` | 用途 | 字段 |
| --- | --- | --- |
| `S3` | S3 兼容的桶 | `Endpoint`、`Region`（默认 `us-east-1`）、`Bucket`（服务器所知的桶名）、`AccessKey`、`SecretKey` |
| `Sftp` | SSH 服务器上的目录 | `Host`、`Port`（默认 22）、`User`、`Password` 或 `PrivateKeyPath`（可带 `PrivateKeyPassphrase`）、`HostKey`、`RemoteRoot`、`ServerRoot` |
| `Local` | 本机目录，例如挂载的共享 | `LocalRoot`、`ServerRoot` |

- **`ServerRoot`**：`RemoteRoot` 或 `LocalRoot` 在 Jellyfin 服务器上的绝对路径，例如 `/mnt/hls` 或 `D:\HLS`。媒体库目录和 HLS 根都由它映射。
  - SFTP 连接必须填写。
  - 本机连接与服务器在同一台机器上时可以留空。
- **`HostKey`**：主机密钥的 SHA-256 指纹，格式与 `ssh-keygen -lf` 的输出相同。留空时接受任何密钥，并在日志中记下。
- SFTP 用户写入的文件要能被 Jellyfin 读取：使用与 Jellyfin 相同的用户，或同组并让组可读。

### HLS 根（`Hls`）

```json
"Hls": { "Connection": "hls", "Path": "media" }
```

- 编码成果写到这条连接下的 `Path` 目录。它就是 Worker 报告给服务器的根，S3 为 `s3://桶/路径`，其他类型为服务器上的路径。
- `check` 会在根中写删一个测试对象，再请服务器校验这个根。
- Worker 在根中写入标记文件 `.jellyfin-hls`。
- 本机或 SFTP 的根不能与媒体库目录互相包含，也不能在服务器的数据目录下。

### 媒体库的默认去向（`Libraries`）

```json
"Libraries": [ { "Id": "<媒体库 ItemId>", "Connection": "s3", "Prefix": "movies" } ]
```

- `Connection`：添加到这个媒体库的视频默认上传到哪条 S3 连接。留空表示直接放进媒体库目录。
- `Prefix`：对象在桶中的前缀。
- 没有列出的媒体库，视频直接放进媒体库目录。
- 桌面程序中添加媒体时勾选「记住为这个媒体库的默认去处」，会写入这一项。

## 添加媒体

```sh
jellyfin-hls-worker add --config worker.json --library 电影 [--into 子目录] \
    [--connection <S3 连接> | --direct] [--encode 1080,720 | --encode all] <文件或目录>...
```

- `--library` 可以填媒体库名称，也可以填 Id。
- 媒体库目录要经 SFTP 或本机连接到达，即某条连接的 `ServerRoot`（本机连接留空时为 `LocalRoot`）包含该媒体库的路径。放到 S3 时，`.strm` 和条目也经这条连接写入。
- 目录连同目录名一起添加，`--into` 指定放在媒体库下的哪个子目录。
- **S3**：对象上传到 `{前缀}/{相对路径}/{文件名}`。媒体库目录中写入：
  - `{文件名去掉扩展名}.strm`：与视频同名的外挂字幕和图片因此归属于它；
  - 旁边 `__true_cache__/` 中的条目。
  - 先写条目，最后写 `.strm`，所以扫描读到 `.strm` 时条目已经完整。
  - 服务器还没有这个桶的访问权限时，Worker 用自己的凭据为服务器建一条。
- **直接放进媒体库目录**：文件先以临时名写入，再改名。
- 字幕、图片等其他文件原样复制到媒体库目录。
- 两个文件会得到同一个名字时，在写入任何东西之前拒绝。
- `--encode`：随添加编码这些档位，`all` 为该媒体库的全部生效档位。其余档位在扫描入库后由服务器安排。
- 一批结束后，Worker 请服务器扫描该媒体库。
- **本地数据库**：Worker 在 `WorkDirectory` 中记下添加过的媒体。之后领到同一个文件的任务时，直接读本地文件，不再下载。所以添加后不要移动或修改本地文件，否则 Worker 会改为下载。

## 桌面程序

`jellyfin-hls-worker-gui [--config worker.json]` 有三个页面：

- **运行**：开始、停止和检查，并显示日志。
- **添加媒体**：选择或拖入文件，选择媒体库、去向和档位。
- **设置**：编辑配置文件。

不带 `--config` 时，配置文件在用户的应用数据目录下：

- Windows：`%AppData%\Jellyfin.HLS.Worker\worker.json`
- Linux：`~/.config/Jellyfin.HLS.Worker/worker.json`

运行中保存的配置，在停止后重新开始时生效。

## 部署为 systemd 服务（Linux）

在解压后的包目录中执行：

```sh
sudo sh ./install.sh
```

`install.sh` 会做以下事情：

- 先启动一次 Worker，确认本机的 .NET 10 运行时可用；不可用时不做任何改动就退出。
- 安装到 `/opt/jellyfin-hls-worker`，并创建系统用户 `jellyfin-hls`。该用户若存在 `video`、`render` 组则加入，供硬件编码使用。
- 工作目录为 `/var/lib/jellyfin-hls-worker`。
- 第一次安装时在 `/etc/jellyfin-hls-worker/worker.json` 生成配置，权限为 640，只有 root 和 `jellyfin-hls` 能读。
- 再次执行即升级：保留配置，并重启正在运行的服务。

填好配置后，检查并启用：

```sh
sudo -u jellyfin-hls /opt/jellyfin-hls-worker/jellyfin-hls-worker check --config /etc/jellyfin-hls-worker/worker.json
sudo systemctl enable --now jellyfin-hls-worker
journalctl -u jellyfin-hls-worker -f
```

- API Key 也可以写在 `/etc/jellyfin-hls-worker/environment` 中，格式为 `JELLYFIN_HLS_API_KEY=...`。
- 配置错误（退出码 2）时，服务不会反复重启；其他失败 30 秒后重启。

## 安全

- 配置文件中有管理员 API Key 和各连接的凭据，请只让运行 Worker 的用户可读。
- 生成 TrueCache 条目时，Worker 以 `--api-key` 参数把 API Key 传给 TrueCache 工具。工具运行期间，**本机其他用户可以在进程列表中看到它**。在多人共用的机器上添加媒体到 S3 时请注意这一点。

## 从源码构建

需要 .NET 10 SDK。

```sh
dotnet build -c Release
dotnet test -c Release
```

S3 测试只在设置环境变量 `HLS_WORKER_TEST_S3`（服务地址，如 `http://localhost:8333`）以及 `HLS_WORKER_TEST_S3_KEY`、`HLS_WORKER_TEST_S3_SECRET` 时运行。

打发布包需要 jellyfin 仓库的源码，TrueCache 工具从其中的 `src/Jellyfin.TrueCache.Tool` 发布：

```sh
sh scripts/publish.sh ../jellyfin [win-x64 linux-x64 linux-arm64]
```

```powershell
./scripts/publish.ps1 -Jellyfin ../jellyfin [-Runtime win-x64,linux-x64,linux-arm64]
```

包和压缩包输出到 `artifacts/`。

### CI

GitHub Actions（`.github/workflows/build.yml`）在推送到 `main` 和 PR 时运行测试并打包，三个平台的包可以在运行页面的 Artifacts 中下载。TrueCache 工具从 workflow 中 `JELLYFIN_REF` 指定的服务器版本发布，升级配套的服务器时一并修改。

推送 `v*` 标签时，先确认标签与 `Directory.Build.props` 中的 `Version` 一致，再把 `.zip` 和 `.tar.gz` 附加到同名 Release；Release 不存在时创建为预发布。
