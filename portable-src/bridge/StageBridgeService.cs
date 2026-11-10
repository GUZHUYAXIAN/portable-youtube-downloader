using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PortableYouTube.StageBridge
{
    public sealed class SessionStageDownloader : IStageDownloader
    {
        readonly object gate = new object();
        DownloadSession active;
        bool cancelled;

        internal void ResetCancellation()
        {
            lock (gate) {
                if (active != null) throw new InvalidOperationException("下载内核仍在运行。");
                cancelled = false;
            }
        }

        public DownloadResult Download(string toolsRoot, DownloadOptions options, Action<DownloadUpdate> update)
        {
            using (var session = new DownloadSession(toolsRoot))
            {
                lock (gate) { active = session; if (cancelled) session.Cancel(); }
                try { return session.Run(options, update); }
                finally { lock (gate) { active = null; cancelled = false; } }
            }
        }

        public void Cancel()
        {
            lock (gate) { cancelled = true; if (active != null) active.Cancel(); }
        }
    }

    public sealed class StageBridgeService
    {
        readonly BridgePaths paths;
        readonly BridgeSettings settings;
        readonly IStageDownloader downloader;
        readonly string snapshotMutexName;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 64 };
        volatile bool cancelled;
        public Action<string> Log;

        public StageBridgeService(string workspace) : this(workspace, new SessionStageDownloader()) { }

        public StageBridgeService(string workspace, IStageDownloader downloader)
        {
            if (downloader == null) throw new ArgumentNullException("downloader");
            this.downloader = downloader;
            paths = new BridgePaths(workspace);
            using (var hash = SHA256.Create()) {
                snapshotMutexName = "Local\\PortableYouTube.StageBridge.Snapshot." +
                    BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(paths.Workspace.ToUpperInvariant()))).Replace("-", "");
            }
            paths.CheckContained(paths.SettingsPath);
            if (!File.Exists(paths.SettingsPath)) throw new FileNotFoundException("缺少桥接配置：" + paths.SettingsPath);
            settings = json.Deserialize<BridgeSettings>(ReadText(paths.SettingsPath, 65536));
            if (settings == null) throw new InvalidDataException("桥接配置为空。");
            settings.toolsRoot = paths.ExistingDirectory(settings.toolsRoot, "toolsRoot");
            settings.outputRoot = paths.ExistingDirectory(settings.outputRoot, "outputRoot");
            if (!new[] { 0, 2160, 1080, 720, 480 }.Contains(settings.height)) throw new InvalidDataException("配置 height 不受支持。");
            if (settings.httpChunkSizeBytes < 0 || settings.httpChunkSizeBytes > 10 * 1024 * 1024)
                throw new InvalidDataException("配置 httpChunkSizeBytes 必须在 0 到 10485760 字节之间；0 使用下载工具默认值。");
            if (settings.proxy != null && settings.proxy.Any(char.IsControl)) throw new InvalidDataException("配置 proxy 包含控制字符。");
            paths.EnsureDirectory(paths.StateDirectory);
        }

        static string Now() { return DateTime.UtcNow.ToString("o"); }

        static string ReadText(string file, long maximum)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                if (stream.Length > maximum) throw new InvalidDataException("JSON 文件超过大小限制：" + file);
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true)) return reader.ReadToEnd();
            }
        }

        FileStream AcquireMutationLock()
        {
            paths.CheckContained(paths.LockPath);
            try { return new FileStream(paths.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) { throw new IOException("桥接器正忙，未接受新的修改；请稍后重试。原因：" + e.Message, e); }
        }

        FileStream TryAcquireStatusLock()
        {
            paths.CheckContained(paths.LockPath);
            try { return new FileStream(paths.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) {
                int code = e.HResult & 0xffff;
                if (code == 32 || code == 33) return null;
                throw;
            }
        }

        BridgeState LoadState()
        {
            BridgeState state = WithSnapshotLock(delegate {
                paths.CheckContained(paths.StatePath);
                if (!File.Exists(paths.StatePath)) return new BridgeState();
                return json.Deserialize<BridgeState>(ReadText(paths.StatePath, 64 * 1024 * 1024));
            });
            if (state == null || state.schemaVersion != 1 || state.tasks == null || state.batches == null)
                throw new InvalidDataException("桥接状态文件格式无效；未覆盖原文件。");
            foreach (var entry in state.tasks) {
                StageTask task = entry.Value;
                if (task == null || entry.Key != task.videoId || task.files == null ||
                    !new[] { "pending", "downloading", "succeeded", "failed", "interrupted" }.Contains(task.status))
                    throw new InvalidDataException("桥接状态中的任务无效：" + entry.Key);
                BridgeValidation.CanonicalVideoUrl(task.url, task.videoId);
                paths.CheckContained(task.targetDirectory);
                foreach (string file in task.files) CheckTaskFile(task, file);
            }
            foreach (var entry in state.batches) {
                if (entry.Value == null || entry.Key != entry.Value.batchId || entry.Value.videoIds == null || entry.Value.videoIds.Any(id => !state.tasks.ContainsKey(id)))
                    throw new InvalidDataException("桥接状态中的批次无效：" + entry.Key);
                BridgeValidation.BatchId(entry.Key);
            }
            return state;
        }

        T WithSnapshotLock<T>(Func<T> action)
        {
            // A Windows file replacement can briefly hide the destination pathname.
            // Serialize only state I/O across processes, never the network download.
            using (var mutex = new Mutex(false, snapshotMutexName)) {
                bool acquired = false;
                try {
                    try { acquired = mutex.WaitOne(10000); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new TimeoutException("读取或保存桥接状态时等待快照锁超时；请稍后重试。");
                    return action();
                } finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }

        void SaveState(BridgeState state)
        {
            paths.CheckContained(paths.StatePath);
            string temporary = paths.CheckContained(Path.Combine(paths.StateDirectory, "state-" + Guid.NewGuid().ToString("N") + ".tmp"));
            byte[] data = new UTF8Encoding(false).GetBytes(json.Serialize(state));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                stream.Write(data, 0, data.Length);
                stream.Flush(true);
            }
            WithSnapshotLock(delegate {
                paths.CheckContained(paths.StatePath);
                paths.CheckContained(temporary);
                if (File.Exists(paths.StatePath)) File.Replace(temporary, paths.StatePath, null);
                else File.Move(temporary, paths.StatePath);
                return true;
            });
        }

        void CheckTaskFile(StageTask task, string file)
        {
            string full = paths.CheckContained(file);
            if (!full.StartsWith(task.targetDirectory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("下载文件超出任务保存目录：" + full);
        }

        bool FilesPresent(StageTask task)
        {
            if (task.files.Count == 0) return false;
            foreach (string file in task.files) {
                CheckTaskFile(task, file);
                if (!File.Exists(file) || new FileInfo(file).Length == 0) return false;
            }
            return true;
        }

        bool Recover(BridgeState state)
        {
            bool changed = false;
            foreach (StageTask task in state.tasks.Values) {
                if (task.status == "downloading") {
                    task.status = "interrupted";
                    task.error = "上次下载未留下完成回执，已恢复为可重试任务。";
                    task.recoveryReason = task.error; task.recoveredUtc = Now();
                    task.updatedUtc = Now(); changed = true;
                } else if (task.status == "succeeded" && !FilesPresent(task)) {
                    task.status = "failed";
                    task.error = "已下载文件不存在或为空；使用 --retry-failed 重新下载。";
                    task.updatedUtc = Now(); changed = true;
                }
            }
            return changed;
        }

        public static StageManifest ValidateManifest(StageManifest manifest) { return BridgeValidation.Manifest(manifest); }

        public BridgeReceipt SubmitManifest(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath)) throw new ArgumentException("缺少 manifest 路径。");
            StageManifest manifest = ValidateManifest(json.Deserialize<StageManifest>(ReadText(paths.CheckContained(manifestPath), 4 * 1024 * 1024)));
            // Prepare every output path before acquiring or changing durable state.
            var prepared = new List<StageTask>();
            foreach (StageManifestItem item in manifest.items) {
                var task = new StageTask {
                    videoId = item.videoId, url = BridgeValidation.CanonicalVideoUrl(item.url, item.videoId), title = item.title, channel = item.channel,
                    artist = manifest.artist, release = manifest.release, eventName = item.eventName, performanceDate = item.performanceDate,
                    uploadDate = item.uploadDate, kind = item.kind, member = item.member, sourceUrl = item.sourceUrl,
                    status = "pending", createdUtc = Now(), updatedUtc = Now()
                };
                task.targetDirectory = paths.TargetDirectory(settings, task);
                prepared.Add(task);
            }
            using (AcquireMutationLock()) {
                BridgeState state = LoadState();
                StageBatch existing;
                if (state.batches.TryGetValue(manifest.batchId, out existing)) {
                    if (!new HashSet<string>(existing.videoIds, StringComparer.Ordinal).SetEquals(prepared.Select(item => item.videoId)))
                        throw new InvalidOperationException("batchId 已存在，但视频集合不同；原批次未改变。");
                    if (Recover(state)) SaveState(state);
                    BridgeReceipt repeated = Receipt("submit", manifest.batchId, state, false);
                    repeated.ok = true; repeated.reused = prepared.Count;
                    return repeated;
                }
                Recover(state);
                int accepted = 0, reused = 0;
                foreach (StageTask task in prepared) {
                    if (state.tasks.ContainsKey(task.videoId)) reused++;
                    else { state.tasks.Add(task.videoId, task); accepted++; }
                }
                state.batches.Add(manifest.batchId, new StageBatch { batchId = manifest.batchId, artist = manifest.artist, release = manifest.release,
                    createdUtc = Now(), videoIds = prepared.Select(item => item.videoId).ToList() });
                SaveState(state);
                BridgeReceipt receipt = Receipt("submit", manifest.batchId, state, false);
                receipt.ok = true; receipt.accepted = accepted; receipt.reused = reused;
                return receipt;
            }
        }

        public BridgeReceipt Run(string batchId, bool retryFailed)
        {
            BridgeValidation.BatchId(batchId);
            using (AcquireMutationLock()) {
                cancelled = false;
                var sessionDownloader = downloader as SessionStageDownloader;
                if (sessionDownloader != null) sessionDownloader.ResetCancellation();
                BridgeState state = LoadState();
                if (!state.batches.ContainsKey(batchId)) throw new ArgumentException("批次不存在：" + batchId);
                if (Recover(state)) SaveState(state);
                foreach (string id in state.batches[batchId].videoIds) {
                    if (cancelled) break;
                    StageTask task = state.tasks[id];
                    if (task.status != "pending" && task.status != "interrupted" && !(retryFailed && task.status == "failed")) continue;
                    task.targetDirectory = paths.TargetDirectory(settings, task);
                    task.status = "downloading"; task.error = null; task.startedUtc = Now(); task.updatedUtc = Now(); task.completedUtc = null;
                    task.files = new List<string>();
                    task.attempts++;
                    SaveState(state);
                    try {
                        paths.ExistingDirectory(settings.toolsRoot, "toolsRoot");
                        paths.ExistingDirectory(settings.outputRoot, "outputRoot");
                        foreach (string tool in new[] { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe", "node.exe" })
                            paths.CheckContained(Path.Combine(settings.toolsRoot, "tools", tool));
                        paths.EnsureDirectory(task.targetDirectory);
                        // Reject junctions or symlinks already placed under this output directory before invoking the engine.
                        foreach (string child in Directory.GetFileSystemEntries(task.targetDirectory)) paths.CheckContained(child);
                        DownloadResult result = downloader.Download(settings.toolsRoot, new DownloadOptions { UrlText = task.url,
                            OutputDirectory = task.targetDirectory, Height = settings.height, Proxy = settings.proxy,
                            HttpChunkSizeBytes = settings.httpChunkSizeBytes, Playlist = false },
                            update => { if (Log != null && update != null && !string.IsNullOrEmpty(update.Message)) Log(update.Message); });
                        if (result == null) throw new InvalidOperationException("下载内核没有返回结果。");
                        var completedFiles = result.Files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        foreach (string file in completedFiles) CheckTaskFile(task, file);
                        task.files = completedFiles;
                        if (result.Cancelled || cancelled) {
                            cancelled = true; task.status = "interrupted"; task.error = "下载已取消，可再次运行继续处理。";
                        } else if (result.Failed != 0 || result.Succeeded != 1 || result.Errors.Count != 0) {
                            task.status = "failed"; task.error = result.Errors.Count > 0 ? string.Join("\n", result.Errors) : "下载内核未确认单个视频成功。";
                        } else {
                            if (!FilesPresent(task)) throw new IOException("下载内核报告成功，但完成文件不存在或为空。");
                            task.status = "succeeded"; task.completedUtc = Now();
                        }
                    } catch (Exception e) {
                        task.status = cancelled ? "interrupted" : "failed";
                        task.error = e.Message;
                    }
                    task.updatedUtc = Now();
                    SaveState(state);
                    if (cancelled) break;
                }
                BridgeReceipt receipt = Receipt("run", batchId, state, false);
                receipt.cancelled = cancelled;
                receipt.ok = !cancelled && receipt.counts.failed == 0 && receipt.counts.missing == 0 && receipt.counts.pending == 0 && receipt.counts.interrupted == 0;
                return receipt;
            }
        }

        public void Cancel() { cancelled = true; downloader.Cancel(); }

        public BridgeReceipt GetStatus(string batchId = null)
        {
            if (batchId != null) BridgeValidation.BatchId(batchId);
            // Keep an acquired lock until the snapshot has been interpreted. Releasing
            // it before LoadState lets a newly started run look like an interrupted one.
            using (FileStream statusLock = TryAcquireStatusLock()) {
                BridgeState state = LoadState();
                if (batchId != null && !state.batches.ContainsKey(batchId)) throw new ArgumentException("批次不存在：" + batchId);
                BridgeReceipt receipt = Receipt("status", batchId, state, statusLock != null);
                receipt.ok = true;
                return receipt;
            }
        }

        BridgeReceipt Receipt(string action, string batchId, BridgeState state, bool projectInterrupted)
        {
            var receipt = new BridgeReceipt { action = action, batchId = batchId, generatedUtc = Now() };
            receipt.batches = batchId == null ? state.batches.Values.OrderBy(batch => batch.createdUtc).ToList() : new List<StageBatch> { state.batches[batchId] };
            IEnumerable<string> ids = batchId == null ? (IEnumerable<string>)state.tasks.Keys.OrderBy(id => id) : state.batches[batchId].videoIds;
            foreach (string id in ids) {
                StageTask task = state.tasks[id];
                if (projectInterrupted && task.status == "downloading") {
                    task.status = "interrupted"; task.error = "上次进程已退出且未留下完成回执，可再次运行。";
                }
                if (task.status == "succeeded" && !FilesPresent(task)) {
                    task.status = "missing"; task.error = "已下载文件不存在或为空；使用 --retry-failed 重新下载。";
                }
                receipt.tasks.Add(task); receipt.counts.total++;
                switch (task.status) {
                    case "pending": receipt.counts.pending++; break;
                    case "downloading": receipt.counts.downloading++; break;
                    case "succeeded": receipt.counts.succeeded++; break;
                    case "failed": receipt.counts.failed++; break;
                    case "interrupted": receipt.counts.interrupted++; break;
                    case "missing": receipt.counts.missing++; break;
                }
            }
            return receipt;
        }
    }
}
