# 便携版构建与打包

目标为 Windows 10/11 x64，运行时依赖 .NET Framework 4.8 或更高兼容版本。
源码使用 C# 5 和 WinForms，通过系统 Framework64 C# 编译器构建，不需要安装 .NET SDK。

## 构建

在本项目根目录执行：

```powershell
& .\portable-src\test.ps1
& .\portable-src\build.ps1
```

支持 Windows PowerShell 5.1 或 PowerShell 7。构建时会读取所有 `portable-src` 顶层 `*.cs`，
并将 `app.manifest` 嵌入 EXE。不会把 `tests` 中的源码编译进应用。

默认工具路径均相对项目目录定位，不依赖开发电脑的盘符或全局 Node 安装：

| 工具 | 默认位置 |
| --- | --- |
| yt-dlp 2026.08.19 | `portable-src\vendor\yt-dlp-2026.08.19\yt-dlp.exe` |
| Node v24.13.0 x64 | `portable-src\vendor\node-v24.13.0\node.exe` |
| FFmpeg / FFprobe 8.1.1 | 优先 `portable-src\vendor\ffmpeg-8.1.1\bin`；该目录不存在时使用同项目 `.tools\ffmpeg\ffmpeg-8.1.1-essentials_build\bin` |

源码仓库不包含这些二进制文件。Node 可从
[官方固定版本地址](https://nodejs.org/dist/v24.13.0/win-x64/node.exe) 获取，FFmpeg 使用
[Gyan 发布的 essentials 构建](https://www.gyan.dev/ffmpeg/builds/)。精确版本、来源与 SHA-256
见 `release-assets\VERSIONS-AND-SOURCES.txt`。已有同版本便携包时，可以将其中的 Node
复制到上述 `vendor` 目录，并复用原项目已有的 FFmpeg，避免重复保存大型 FFmpeg 文件。
`vendor` 内 FFmpeg 目录存在但文件缺失或哈希不符时会报错，不会静默切换其他副本。

也可以显式指定项目内其他工具位置：

```powershell
& .\portable-src\build.ps1 `
  -YtDlpPath (Join-Path $PWD 'tools\yt-dlp.exe') `
  -FFmpegDirectory (Join-Path $PWD 'tools\ffmpeg\bin') `
  -NodePath (Join-Path $PWD 'tools\node.exe')
```

脚本核对已记录版本的精确 SHA-256，拒绝静默使用版本不同的工具。升级时需重新核实
对应版本许可及依赖通知，并一起更新 `build.ps1` 中的哈希及 `release-assets` 文档。
构建和打包不会联网、安装工具、修改原 `.tools`、修改 PATH、读取浏览器数据或设置系统代理。

原项目 yt-dlp 2026.03.17 在真实下载中出现媒体 HTTP 403。依据官方
[问题记录 #17456](https://github.com/yt-dlp/yt-dlp/issues/17456) 与
[2026.08.19 发布说明](https://github.com/yt-dlp/yt-dlp/releases/tag/2026.08.19)，
新版已移除失效的默认 `android_vr` 客户端，因此单独下载该版本到 `vendor` 并核对官方
`SHA2-256SUMS`，作为此次便携包输入。没有运行自更新，也没有替换原 `.tools\yt-dlp.exe`。
若从纯源码重新准备依赖，须从上方官方版本页取得同版本 EXE；构建脚本本身不会下载。

输出固定为项目根目录下的 `油管下载器便携版`。脚本仅更新其管理的 EXE、四个工具、说明与许可文件，
保留 `Downloads` 和其他现有文件；如果已有程序正在运行导致文件占用，请先关闭再构建。
编译先在 `portable-src\build-output` 使用唯一临时文件，成功后才复制正式 EXE。
构建会生成 `CHECKSUMS.sha256`，用于打包前校验。

仅检查源码能否编译、保留现有发行目录时，使用：

```powershell
& .\portable-src\build.ps1 -CompileOnly
```

此模式同样检查固定工具输入和发行资产，编译后删除临时 EXE，不复制或替换发行文件。

## 项目内开发与临时文件

`build.ps1` 和 `test.ps1` 通过 `dev-environment.ps1`，只在当前 PowerShell 进程及其子进程中，
临时将 `TEMP`、`TMP`、`TMPDIR`、`SystemTemp` 指向项目根目录的 `.local\tmp`。
脚本会打印并核对 .NET 实际返回的临时目录；无论成功或抛出异常，都会在 `finally` 恢复
调用前的进程环境变量。它们不修改用户级或系统级环境变量，不改全局 PATH。
多个脚本应串行执行，避免在同一 PowerShell 进程中并发修改进程环境。

四组测试 EXE 还使用独立的测试初始化代码，因此直接从 `portable-src\test-output` 启动，
测试夹具和子进程也使用 `.local\tmp`，并报告实际路径。Core / Runner 测试省略 Node 参数时，
同样使用项目 `vendor` 中的 Node。测试 EXE 必须保留在项目目录内部；这段初始化代码不编译进应用。
构建产物和测试 EXE 分别在 `portable-src\build-output`、`portable-src\test-output`；
测试夹具正常结束后清理，异常退出留下的临时文件保留在 `.local` 中，不能提交到源码仓库。

开发时运行现有便携程序，请使用：

```powershell
& .\portable-src\start-development.ps1
# 指定项目内另一个已构建 EXE，并等待它退出：
& .\portable-src\start-development.ps1 -ExePath (Join-Path $PWD '油管下载器便携版\油管下载器.exe') -Wait
```

启动脚本通过 `ProcessStartInfo` 仅为新 GUI 进程设置临时目录 `.local\runtime-tmp`，
其 yt-dlp 等子进程会继承这些设置；PowerShell 自身的环境不受影响。
这可将 yt-dlp 独立 EXE 的开发运行解包位置留在项目目录，不修改 GUI 或下载逻辑。
直接双击已冻结的发行 EXE 不经过此脚本，仍可能使用 Windows 的默认临时目录。

项目放在 F 盘时，上述开发输出和可控临时文件均留在 F 盘。编译仍使用 Windows 已有的
`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`；无需搬迁系统组件或安装 SDK。
Windows、.NET、安全软件及 Codex 等宿主程序可能自行产生系统日志、缓存或其他记录，
上述设置不表示系统盘完全没有操作系统写入，也不会清理其他软件的数据。

## 打包

完成真实下载及 GUI 验证、关闭程序后运行：

```powershell
& .\portable-src\package.ps1
```

已有 ZIP 时会拒绝覆盖；确认要用当前已验证构建替换后可运行：

```powershell
& .\portable-src\package.ps1 -Replace
```

输出为项目根目录下的 `油管下载器便携版.zip` 和 `油管下载器便携版.zip.sha256`。
脚本根据源码资产构造白名单，逐项校验发行文件，复制到新建的临时目录再生成 ZIP；
不会直接压缩可能含下载结果的发行目录。ZIP 内包含顶层 `油管下载器便携版` 文件夹，
其中 `Downloads` 为空，未列入清单的视频、凭据、日志、测试文件不会进入 ZIP。
临时文件清理会先核对绝对路径位于 `portable-src\package-output` 内，并拒绝链接/重解析点。

“可复现”指输入工具固定、步骤可以重复执行；旧版 csc 与 ZIP 时间戳未做字节级确定性处理，
因此不同构建的 EXE/ZIP 哈希不保证相同。以每次生成的校验表为准。

## 验收边界

编译通过不等于实际下载可用。至少应完成这些检查并保留测试记录在发行目录之外：

1. 单元/进程测试通过，GUI 可打开，错误、取消和批量结果状态正确。
2. 下载公开 YouTube 视频，使用随包 `ffprobe` 确认最终 MP4 同时有视频流、音频流和合理时长。
3. 将最终 ZIP 解压到另一个含中文和空格的新目录，启动解压副本并真实下载；
   使用进程级受限 PATH 核对工具均来自副本，不能修改全局 PATH。
4. 检查 ZIP 文件清单、大小与 SHA-256；没有干净系统虚拟机时如实说明验证范围。

网站规则、访问网络、登录/地区限制仍可能阻止个别链接下载。随包不提供 Cookies 功能。

## 许可证维护

原文放在 `release-assets\licenses`；运行包内有完整上游许可证与依赖通知、版本及来源记录。
yt-dlp 源码的 Unlicense 不等于官方独立 EXE 的许可证：该版本官方 EXE 的合并作品为 GPLv3+。
现有 FFmpeg essentials 静态构建为 GPLv3。Node 的版本 LICENSE 包含 Node 主体和内部组件许可。

未收集全部对应源码与外部库构建源码。当前文件不能作为已经履行全部公开再分发义务的证明；
若之后向他人分发，需依据适用许可落实源码提供及其他义务。具体已核查来源见运行包的
`VERSIONS-AND-SOURCES.txt` 和 `THIRD-PARTY-NOTICES.txt`。
