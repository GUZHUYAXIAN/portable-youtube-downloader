# 油管下载器便携版

A portable YouTube MP4 downloader for Windows, built with C# and WinForms.

粘贴链接，选择保存位置，点击下载。支持多链接、清晰度上限、进度、取消和 MP4 音视频检查。

## 功能

- 多行输入，每行一条 YouTube 视频链接，自动去除重复项。
- 最佳可用画质或 2160p、1080p、720p、480p 上限；不会放大原视频。
- 默认下载单个视频，整张播放列表需主动勾选。
- 后台下载、速度和进度日志，取消时结束下载工具及其子进程。
- 自动合并为 MP4；成功前检查容器、视频流、音频流和有效时长。
- 一条链接失败后继续下一条，明确显示部分失败；保留已下载文件和可续传分段。
- 可选代理，仅用于当前任务，不改系统设置，不读取浏览器登录数据。

## 运行与构建

支持 Windows 10/11 x64，需要 .NET Framework 4.8 或兼容更高版本，以及可访问 YouTube 的网络。

**v1.0.0 是源码首发。** GitHub Release 提供源代码快照；本仓库不分发已打包的第三方工具二进制。准备对应组件后可以自行构建便携目录和 ZIP。

按照 [构建说明](portable-src/BUILD.md) 准备 yt-dlp、FFmpeg、ffprobe 和 Node，随后在仓库根目录运行：

```powershell
& .\portable-src\test.ps1
& .\portable-src\build.ps1
& .\portable-src\package.ps1
```

构建输出为根目录下的 `油管下载器便携版`。完整保留其 `tools` 文件夹，双击 `油管下载器.exe` 即可使用；不要求另装 Python 或 PowerShell 7。首次启动不会自动下载组件。

项目内开发启动方式：

```powershell
& .\portable-src\start-development.ps1
```

开发脚本将可控临时文件放在本项目 `.local`，不会修改全局 TEMP/TMP；系统编译器和 Git 仍从已有安装使用。

## Dots 舞台下载桥接（本地开发功能）

新增的独立命令接口可以接收舞台清单，按艺人、专辑、演出和机位保存视频，并返回 JSON 回执及本地 HTML 记录。按 YouTube 视频 ID 跨批次去重，保留失败原因和中断状态；只有通过原下载器音视频检查的文件才标记完成。

```powershell
& .\portable-src\build.ps1 -BridgeOnly
```

此命令仅构建到项目 `.local\stage-bridge\bin`，不更新便携发行目录或冻结 ZIP。配置与用法见 [桥接接口说明](portable-src/bridge/INTERFACE.txt)。个人 Dots 交接指令、清单和验收记录放在 `.local\stage-bridge`，不进入公开文件清单。本功能需要本地配置和联网电脑；云端发现及 Dots 到个人电脑的实际交接须单独验证。

## Windows 多平台开发版

本次开发版保留 v1.0.0 的 EXE、ZIP、标签和 Release，不覆盖冻结版本。新增：

- GUI 接受 YouTube 视频、X/Twitter 状态帖和 Instagram 视频帖子链接；每行一条，失败后继续其他链接。
- X/Instagram 主页不在 GUI 中整号抓取。只接受可信平台域名，不接受带账号密码或自定义端口的 URL；YouTube 频道/播放列表仍需明确勾选。
- 默认勾选剪辑兼容模式，选择可用的 H.264 视频与 AAC 音频，合并后再次核验实际编码。不会静默回退到 AV1；没有兼容格式时明确失败，可关闭此选项保留原画质模式。兼容格式可能低于所选最高分辨率。
- 兼容文件名带 `[H264]`，避免与已有其他编码的 MP4 混淆；原文件不覆盖。
- 保留现有舞台清单桥接、跨批次视频 ID 去重、失败/中断恢复和本地 HTML 回执；修复清单路径边界和报告文件名碰撞。

构建与启动新开发版：

```powershell
& .\portable-src\test.ps1
& .\portable-src\build.ps1 -DevelopmentOnly
& .\portable-src\start-development.ps1 -ExePath '.\.local\social-development\油管下载器.exe'
```

站点支持是链接处理能力，不代表所有帖子可下载。X/Instagram 的登录要求、私有/年龄/地区限制会直接报告；不读取浏览器 Cookies、不建立持久登录、不绕过限制。此 GUI 仅下载有声视频，不下载图片或静音视频。账号追更应使用独立增量跟踪入口；GUI 本身不安装后台定时任务。自动模式需联网电脑和已核实的来源，不能称为实时全网收集。

## 验证

第一版已在 Windows x64 上通过 43 项核心回归测试，并把交付 ZIP 解压到新的中文、空格目录实际操作：

- 仅为该进程限制 PATH，确认下载使用随包组件。
- GUI 下载公开 YouTube 视频，得到 854×480、224.28 秒的 MP4，包含 AV1 视频与 AAC 音频；完整音视频解码通过。
- 验证多链接部分失败、已有文件不覆盖、下载中取消和子进程清理。

没有在全新虚拟机或所有 Windows 版本上测试。站点规则、登录/地区限制及网络故障可能影响个别链接。MP4 容器内的视频编码也可能需要播放器支持。

## 源码结构

| 路径 | 用途 |
| --- | --- |
| `portable-src/MainForm.cs`、`Program.cs` | 图形界面和入口 |
| `portable-src/Core.cs` | 链接、选项、命令和参数引用 |
| `portable-src/DownloadSession.cs` | 批量任务、状态与音视频检查 |
| `portable-src/ProcessRunner.cs` | Windows Job Object、日志与进程树取消 |
| `portable-src/tests` | 真实进程和核心逻辑回归测试 |
| `portable-src/release-assets` | 用户说明及第三方许可、版本来源 |

## 许可

本项目原创源码使用 [MIT License](LICENSE)。yt-dlp、FFmpeg、Node 及其依赖分别适用各自许可证，不由本项目 MIT 许可证重新授权。对应通知见 [THIRD-PARTY-NOTICES](portable-src/release-assets/THIRD-PARTY-NOTICES.txt)。

仅下载你有权保存的内容。请勿在问题报告中上传 Cookies、代理凭据或带访问令牌的链接。
