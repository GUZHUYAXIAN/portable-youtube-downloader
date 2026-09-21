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
