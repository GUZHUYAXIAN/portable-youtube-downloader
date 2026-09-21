using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace PortableYouTube
{
    public sealed class DownloadUpdate
    {
        public string Message;
        public int? Percent;
        public int LinkIndex, LinkCount;
        public string FilePath;
    }

    public sealed class DownloadResult
    {
        public bool Cancelled;
        public int Succeeded, Failed;
        public List<string> Files = new List<string>();
        public List<string> Errors = new List<string>();
    }

    public sealed class DownloadSession : IDisposable
    {
        readonly string root;
        readonly object gate = new object();
        volatile bool cancelled;
        ProcessRunner active;
        int started;

        public DownloadSession(string root) { this.root = Path.GetFullPath(root); }

        // One session represents one batch. Make a fresh session when starting again.
        public DownloadResult Run(DownloadOptions options, Action<DownloadUpdate> update)
        {
            if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("此下载任务已运行，请新建下载任务。");
            var result = new DownloadResult();
            int count = 0;
            try {
                ThrowIfCancelled();
                if (options == null) throw new ArgumentException("下载选项不能为空。");
                string[] urls = DownloadCommand.ParseUrls(options.UrlText);
                count = urls.Length;
                ThrowIfCancelled();
                // Validate every option and bundled tool before touching the output folder.
                DownloadCommand.Build(root, options);
                ThrowIfCancelled();
                string output = Path.GetFullPath(options.OutputDirectory);
                Directory.CreateDirectory(output);
                ProbeWrite(output);
                ThrowIfCancelled();
                for (int i = 0; i < urls.Length; i++) {
                    ThrowIfCancelled();
                    int index = i + 1;
                    Emit(update, "正在解析并下载第 " + index + " / " + count + " 条链接…", null, index, count, null);
                    var files = new List<string>();
                    var messages = new Queue<string>();
                    var outputGate = new object();
                    try {
                        var current = new DownloadOptions { UrlText = urls[i], OutputDirectory = output, Proxy = options.Proxy, Height = options.Height, Playlist = options.Playlist };
                        int exit = RunProcess(DownloadCommand.Build(root, current), delegate(string line) {
                            if (line == null) return;
                            lock (outputGate) {
                                if (line.StartsWith("__FILE__", StringComparison.Ordinal)) files.Add(line.Substring(8));
                                else if (DownloadCommand.Progress(line) == null) {
                                    messages.Enqueue(line);
                                    while (messages.Count > 8) messages.Dequeue();
                                }
                            }
                            Emit(update, line.StartsWith("__FILE__", StringComparison.Ordinal) ? "下载进程已输出文件，正在等待检查…" : line,
                                DownloadCommand.Progress(line), index, count, null);
                        });
                        ThrowIfCancelled();
                        var problems = new List<string>();
                        if (exit != 0) {
                            string details;
                            lock (outputGate) details = string.Join("\n", messages);
                            problems.Add("下载进程退出码 " + exit + (details.Length == 0 ? "。" : "：\n" + details));
                        }
                        string[] candidates;
                        lock (outputGate) candidates = files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        if (candidates.Length == 0) problems.Add("没有收到完成文件。链接可能无法下载，或站点要求登录；请查看日志。");
                        foreach (string candidate in candidates) {
                            ThrowIfCancelled();
                            try {
                                string file = VerifyFile(candidate, output, update, index, count);
                                if (!result.Files.Contains(file, StringComparer.OrdinalIgnoreCase)) result.Files.Add(file);
                                Emit(update, "已验证 MP4 音视频：" + Path.GetFileName(file), null, index, count, file);
                            } catch (OperationCanceledException) { throw; }
                            catch (Exception e) { problems.Add(e.Message); }
                        }
                        ThrowIfCancelled();
                        if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
                        result.Succeeded++;
                        Emit(update, "第 " + index + " 条链接已完成并通过音视频检查。", null, index, count, null);
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception e) {
                        ThrowIfCancelled();
                        result.Failed++;
                        string error = "第 " + index + " 条链接失败：" + e.Message;
                        result.Errors.Add(error);
                        Emit(update, error, null, index, count, null);
                    }
                }
            } catch (OperationCanceledException) { result.Cancelled = true; }
            catch (Exception e) {
                if (cancelled) result.Cancelled = true;
                else {
                    result.Failed++;
                    result.Errors.Add(e.Message);
                    Emit(update, e.Message, null, 0, count, null);
                }
            }
            if (cancelled) result.Cancelled = true;
            return result;
        }

        void ProbeWrite(string output)
        {
            ThrowIfCancelled();
            string path = Path.Combine(output, ".portable-write-check-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool created = false;
            try {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    created = true;
                    stream.WriteByte(1);
                    stream.Flush(true);
                }
            } catch (Exception e) { throw new IOException("保存目录无法写入：" + output + "\n" + e.Message, e); }
            finally { if (created) File.Delete(path); }
        }

        string VerifyFile(string candidate, string output, Action<DownloadUpdate> update, int index, int count)
        {
            ThrowIfCancelled();
            string file = Path.GetFullPath(candidate);
            string prefix = output.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("完成文件不在所选保存目录内：" + file);
            if (!File.Exists(file)) throw new FileNotFoundException("下载报告的完成文件不存在：" + file);
            if (!string.Equals(Path.GetExtension(file), ".mp4", StringComparison.OrdinalIgnoreCase)) throw new IOException("完成文件不是 MP4：" + file);
            if (new FileInfo(file).Length == 0) throw new IOException("完成文件为空：" + file);
            Emit(update, "正在检查视频、音频和时长：" + Path.GetFileName(file), null, index, count, null);
            bool video = false, audio = false, duration = false, mp4 = false;
            var probeGate = new object();
            var info = new ProcessStartInfo(Path.Combine(root, "tools", "ffprobe.exe"), string.Join(" ",
                new[] { "-v", "error", "-show_entries", "stream=codec_type:format=duration,format_name", "-of", "default=noprint_wrappers=1", file }.Select(DownloadCommand.Quote))) {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            int exit = RunProcess(info, delegate(string line) {
                lock (probeGate) {
                    if (line == "codec_type=video") video = true;
                    if (line == "codec_type=audio") audio = true;
                    if (line.StartsWith("format_name=", StringComparison.Ordinal)) mp4 = line.Substring(12).Split(',').Contains("mp4");
                    double seconds;
                    if (line.StartsWith("duration=", StringComparison.Ordinal) && double.TryParse(line.Substring(9), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out seconds) && seconds > 0 && !double.IsInfinity(seconds) && !double.IsNaN(seconds)) duration = true;
                }
                Emit(update, line, null, index, count, null);
            });
            ThrowIfCancelled();
            lock (probeGate) {
                if (exit != 0 || !video || !audio || !duration || !mp4)
                    throw new IOException("MP4 检查失败（必须同时包含视频、音频和有效时长）：" + Path.GetFileName(file) + "。已有文件不会被覆盖。请检查后移走该文件或选择其他保存目录重试。");
            }
            return file;
        }

        int RunProcess(ProcessStartInfo info, Action<string> log)
        {
            ThrowIfCancelled();
            using (var runner = new ProcessRunner()) {
                lock (gate) {
                    ThrowIfCancelled();
                    active = runner;
                }
                try {
                    int result = runner.Run(info, log);
                    if (runner.Cancelled) cancelled = true;
                    ThrowIfCancelled();
                    return result;
                } finally { lock (gate) if (active == runner) active = null; }
            }
        }

        void ThrowIfCancelled() { if (cancelled) throw new OperationCanceledException(); }
        static void Emit(Action<DownloadUpdate> update, string message, int? percent, int index, int count, string file)
        {
            if (update != null) update(new DownloadUpdate { Message = message, Percent = percent, LinkIndex = index, LinkCount = count, FilePath = file });
        }
        public void Cancel()
        {
            cancelled = true;
            lock (gate) if (active != null) active.Cancel();
        }
        public void Dispose() { Cancel(); }
    }
}
