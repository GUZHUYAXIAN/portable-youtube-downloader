using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using PortableYouTube;
using PortableYouTube.StageBridge;

class BridgeTests
{
    static int failed;
    static string testFilter;
    const string FirstId = "stage000001", SecondId = "stage000002", SilentId = "silent00001", HoldingId = "holding0001";
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    static void Check(string name, Action body)
    {
        if (testFilter != null && name.IndexOf(testFilter, StringComparison.OrdinalIgnoreCase) < 0) return;
        try { body(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); }
    }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action body, string message)
    {
        try { body(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        catch (InvalidDataException) { return; }
        catch (IOException) { return; }
        throw new Exception(message);
    }

    sealed class FakeDownloader : IStageDownloader
    {
        public readonly List<DownloadOptions> calls = new List<DownloadOptions>();
        public Func<DownloadOptions, DownloadResult> handler;
        public bool cancelled;
        public DownloadResult Download(string toolsRoot, DownloadOptions options, Action<DownloadUpdate> update)
        {
            calls.Add(options);
            if (handler != null) return handler(options);
            return Success(options);
        }
        public void Cancel() { cancelled = true; }
    }

    static DownloadResult Success(DownloadOptions options)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        string id = options.UrlText.Substring(options.UrlText.LastIndexOf('=') + 1);
        string file = Path.Combine(options.OutputDirectory, "verified [" + id + "].mp4");
        File.WriteAllText(file, "verified downloader fixture");
        var result = new DownloadResult { Succeeded = 1 };
        result.Files.Add(file);
        return result;
    }

    sealed class Fixture : IDisposable
    {
        readonly string temporaryRoot;
        public readonly string Root, Output, Bundle, Data, ManifestPath;
        public readonly FakeDownloader Downloader = new FakeDownloader();
        public Fixture(bool realAdapter)
        {
            temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            Root = Path.Combine(temporaryRoot, "StageBridge-tests-" + Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "视频库");
            Bundle = Path.Combine(Root, "bundle");
            Data = Path.Combine(Root, ".local", "stage-bridge");
            ManifestPath = Path.Combine(Root, "manifest.json");
            Directory.CreateDirectory(Output);
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Path.Combine(Bundle, "tools"));
            foreach (string tool in new[] { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe", "node.exe" }) {
                string destination = Path.Combine(Bundle, "tools", tool);
                if (realAdapter) File.Copy(Assembly.GetExecutingAssembly().Location, destination);
                else File.WriteAllText(destination, "not executed by injected downloader");
            }
            SaveSettings(Output);
        }
        public Fixture() : this(false) { }
        public StageBridgeService Service() { return new StageBridgeService(Root, Downloader); }
        public string StatePath { get { return Path.Combine(Data, "state.json"); } }
        public void SaveSettings(string output, int httpChunkSizeBytes = 0)
        {
            WriteJson(Path.Combine(Data, "settings.json"), new BridgeSettings {
                toolsRoot = Bundle, outputRoot = output, height = 1080, proxy = "", httpChunkSizeBytes = httpChunkSizeBytes
            });
        }
        public StageManifest Manifest(params string[] ids)
        {
            return new StageManifest {
                schemaVersion = 1, batchId = "nmixx-test", artist = "NMIXX", release = "Test release",
                items = ids.Select(delegate(string id) { return new StageManifestItem {
                    videoId = id, url = "https://www.youtube.com/watch?v=" + id,
                    title = "테스트 中文 stage " + id, channel = "Test official channel", eventName = "Music Show",
                    performanceDate = "2026-10-23", uploadDate = "2026-10-24", kind = "official-stage",
                    member = "", sourceUrl = "https://www.youtube.com/watch?v=" + id
                }; }).ToList()
            };
        }
        public BridgeReceipt Submit(StageManifest manifest)
        {
            WriteJson(ManifestPath, manifest);
            return Service().SubmitManifest(ManifestPath);
        }
        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            Assert(resolved.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("StageBridge-tests-", StringComparison.Ordinal), "Unsafe fixture cleanup target");
            for (int retry = 0; ; retry++) {
                try { if (Directory.Exists(resolved)) Directory.Delete(resolved, true); return; }
                catch (IOException) { if (retry == 10) throw; Thread.Sleep(100); }
            }
        }
    }

    static void WriteJson(string path, object value) { File.WriteAllText(path, Json.Serialize(value), new UTF8Encoding(false)); }
    static string VideoId(string url)
    {
        var uri = new Uri(url);
        return uri.Host == "youtu.be" ? uri.AbsolutePath.Trim('/') : uri.Query.TrimStart('?').Split('&').First(x => x.StartsWith("v=", StringComparison.Ordinal)).Substring(2);
    }
    static Process StartSelf(string[] arguments, string parentId)
    {
        var info = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, string.Join(" ", arguments.Select(DownloadCommand.Quote))) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.EnvironmentVariables.Remove("STAGE_BRIDGE_PARENT_PID");
        if (parentId != null) info.EnvironmentVariables["STAGE_BRIDGE_PARENT_PID"] = parentId;
        return Process.Start(info);
    }
    static bool WaitUntil(Func<bool> condition, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        do { if (condition()) return true; Thread.Sleep(25); } while (timer.ElapsedMilliseconds < milliseconds);
        return condition();
    }
    static bool ProcessEnded(int id)
    {
        try { using (var process = Process.GetProcessById(id)) return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
    static bool IsSharingConflict(Exception error)
    {
        for (Exception current = error; current != null; current = current.InnerException) {
            int code = current.HResult & 0xffff;
            if (current is IOException && (code == 32 || code == 33)) return true;
        }
        return false;
    }
    static void StopOwnedProcess(Process process)
    {
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(); process.WaitForExit(5000); }
        finally { process.Dispose(); }
    }
    static int Helper(string[] args)
    {
        string exe = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
        if (exe.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase)) {
            string content = File.ReadAllText(args[args.Length - 1]);
            Console.WriteLine("codec_type=video");
            if (content != SilentId) Console.WriteLine("codec_type=audio");
            Console.WriteLine("format_name=mov,mp4,m4a,3gp,3g2,mj2\nduration=10.0");
            return 0;
        }
        int index = Array.IndexOf(args, "-P");
        if (index < 0) return 9;
        string output = args[index + 1].Replace("%%", "%").Replace("$$", "$");
        string id = VideoId(args[args.Length - 1]);
        Directory.CreateDirectory(output);
        if (id == HoldingId) {
            File.WriteAllText(Path.Combine(output, "download.pid"), Process.GetCurrentProcess().Id.ToString());
            var childInfo = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "--leaf-helper " + DownloadCommand.Quote(Path.Combine(output, "leaf.pid"))) { UseShellExecute = false, CreateNoWindow = true };
            using (Process child = Process.Start(childInfo)) Thread.Sleep(Timeout.Infinite);
        }
        string file = Path.Combine(output, "test [" + id + "].mp4");
        File.WriteAllText(file, id);
        Console.WriteLine("__FILE__" + file);
        return 0;
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length > 0 && args[0] == "--bridge-cli") return StageBridgeProgram.Main(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "--bridge-self-parent") {
            Environment.SetEnvironmentVariable("STAGE_BRIDGE_PARENT_PID", Process.GetCurrentProcess().Id.ToString());
            return StageBridgeProgram.Main(args.Skip(1).ToArray());
        }
        if (args.Length == 2 && args[0] == "--parent-helper") {
            if (!WaitUntil(() => File.Exists(args[1]), 30000)) return 9;
            return 0;
        }
        if (args.Length == 2 && args[0] == "--leaf-helper") {
            File.WriteAllText(args[1], Process.GetCurrentProcess().Id.ToString());
            Thread.Sleep(Timeout.Infinite); return 0;
        }
        string exe = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
        if (new[] { "yt-dlp.exe", "ffprobe.exe", "ffmpeg.exe", "node.exe" }.Contains(exe, StringComparer.OrdinalIgnoreCase)) return Helper(args);
        TestEnvironment.Initialize(true);
        if (args.Length == 2 && args[0] == "--filter") testFilter = args[1];

        Check("Submit persists pending tasks without launching a downloader", delegate {
            using (var f = new Fixture()) {
                var receipt = f.Submit(f.Manifest(FirstId, SecondId));
                Assert(receipt.ok && receipt.counts.total == 2 && receipt.counts.pending == 2, "Incorrect pending receipt");
                Assert(f.Downloader.calls.Count == 0 && Directory.GetFiles(f.Output, "*", SearchOption.AllDirectories).Length == 0, "Submission downloaded media");
                Assert(f.Service().GetStatus("nmixx-test").tasks.Count == 2, "Accepted tasks did not survive a fresh service");
            }
        });

        Check("Manifest outside the workspace is rejected before state mutation", delegate {
            using (var outside = new Fixture()) using (var f = new Fixture()) {
                WriteJson(outside.ManifestPath, f.Manifest(FirstId));
                Reject(delegate { f.Service().SubmitManifest(outside.ManifestPath); }, "Outside manifest accepted");
                Assert(!File.Exists(f.StatePath), "Rejected manifest changed state");
            }
        });
        Check("Invalid later manifest item leaves the previously saved queue unchanged", delegate {
            using (var f = new Fixture()) {
                f.Submit(f.Manifest(FirstId));
                string before = File.ReadAllText(f.StatePath);
                var manifest = f.Manifest(SecondId, "stage000003"); manifest.batchId = "bad-batch";
                manifest.items[1].url = "https://youtube.com/watch?v=" + FirstId;
                Reject(delegate { f.Submit(manifest); }, "Mismatched ID accepted");
                Assert(File.ReadAllText(f.StatePath) == before && f.Downloader.calls.Count == 0, "Partial invalid manifest changed state");
            }
        });

        Check("Video identity deduplicates URL forms and batches while preserving other cameras", delegate {
            using (var f = new Fixture()) {
                var manifest = f.Manifest(FirstId, SecondId);
                manifest.items[0].url = "https://youtu.be/" + FirstId + "?si=share";
                manifest.items[1].kind = "member-fancam"; manifest.items[1].member = "HAEWON";
                f.Submit(manifest);
                var again = f.Submit(manifest);
                Assert(again.counts.total == 2, "Repeated batch duplicated its tasks");
                var another = f.Manifest(FirstId); another.batchId = "another-batch";
                f.Submit(another);
                Assert(f.Service().GetStatus().tasks.Count == 2, "Same video was duplicated across batches");
                var result = f.Service().Run("nmixx-test", false);
                Assert(result.counts.succeeded == 2 && f.Downloader.calls.Count == 2, "Different camera was dropped");
                f.Service().Run("another-batch", false);
                Assert(f.Downloader.calls.Count == 2, "Already successful shared video was downloaded again");
            }
        });

        Check("Reusing a batch ID for a different set is rejected without changing its history", delegate {
            using (var f = new Fixture()) {
                f.Submit(f.Manifest(FirstId));
                string before = File.ReadAllText(f.StatePath);
                Reject(delegate { f.Submit(f.Manifest(SecondId)); }, "Batch ID collision accepted");
                Assert(File.ReadAllText(f.StatePath) == before, "Rejected collision changed history");
            }
        });

        Check("Untrusted URLs dates schema and video IDs are rejected before acceptance", delegate {
            using (var f = new Fixture()) {
                foreach (string url in new[] { "file:///C:/private", "https://youtube.com.example.org/watch?v=" + FirstId,
                    "https://youtube.com/playlist?list=PLtest", "https://youtube.com/watch?v=" + SecondId }) {
                    var manifest = f.Manifest(FirstId); manifest.items[0].url = url;
                    Reject(delegate { f.Submit(manifest); }, "Invalid URL accepted: " + url);
                }
                var badDate = f.Manifest(FirstId); badDate.items[0].performanceDate = "2026-02-30";
                Reject(delegate { f.Submit(badDate); }, "Impossible event date accepted");
                var badSchema = f.Manifest(FirstId); badSchema.schemaVersion = 999;
                Reject(delegate { f.Submit(badSchema); }, "Unknown schema accepted");
                var badId = f.Manifest("..\\outside");
                Reject(delegate { f.Submit(badId); }, "Unsafe video ID accepted");
                Assert(!File.Exists(f.StatePath) || f.Service().GetStatus().tasks.Count == 0, "Invalid data created tasks");
            }
        });

        Check("Metadata cannot escape the root and Windows reserved folder names are safe", delegate {
            using (var f = new Fixture()) {
                var manifest = f.Manifest(FirstId); manifest.artist = "CON"; manifest.release = "..\\outside/album";
                manifest.items[0].eventName = "AUX. : event";
                BridgeReceipt receipt = null;
                try { receipt = f.Submit(manifest); }
                catch (ArgumentException) { return; }
                catch (InvalidOperationException) { return; }
                Assert(receipt != null && receipt.tasks.Count == 1, "Missing accepted task");
                string target = Path.GetFullPath(receipt.tasks[0].targetDirectory);
                Assert(target.StartsWith(f.Output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Metadata escaped output root");
                string[] segments = target.Substring(f.Output.Length + 1).Split(Path.DirectorySeparatorChar);
                Assert(segments.All(x => x != "." && x != ".." && !x.Equals("CON", StringComparison.OrdinalIgnoreCase) && !x.Equals("AUX", StringComparison.OrdinalIgnoreCase) && x.IndexOfAny(Path.GetInvalidFileNameChars()) < 0), "Unsafe Windows segment survived");
                Assert(f.Service().Run("nmixx-test", false).counts.succeeded == 1, "Sanitized destination is not usable");
            }
        });

        Check("Unknown performance date stays unknown and does not borrow the upload date", delegate {
            using (var f = new Fixture()) {
                var manifest = f.Manifest(FirstId); manifest.items[0].performanceDate = "";
                var task = f.Submit(manifest).tasks.Single();
                Assert(string.IsNullOrEmpty(task.performanceDate) && task.uploadDate == "2026-10-24", "Dates were conflated");
                Assert(task.targetDirectory.Contains("日期待确认") && !task.targetDirectory.Contains("2026-10-24"), "Unknown date silently became upload date");
            }
        });

        Check("Configured output outside the workspace is rejected", delegate {
            using (var f = new Fixture()) {
                f.SaveSettings(Path.GetDirectoryName(f.Root));
                Reject(delegate { f.Submit(f.Manifest(FirstId)); }, "Outside output root was accepted");
            }
        });

        Check("Junction output path is rejected without writing through it", delegate {
            using (var f = new Fixture()) {
                string target = Path.Combine(f.Root, "junction-target");
                string link = Path.Combine(f.Root, "junction-link");
                Directory.CreateDirectory(target);
                var info = new ProcessStartInfo("cmd.exe", "/c mklink /J " + DownloadCommand.Quote(link) + " " + DownloadCommand.Quote(target)) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                try {
                    using (var process = Process.Start(info)) {
                        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit();
                        Assert(process.ExitCode == 0, "Cannot create junction fixture: " + output);
                    }
                    f.SaveSettings(link);
                    Reject(delegate { f.Submit(f.Manifest(FirstId)); }, "Junction output accepted");
                    Assert(Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length == 0, "Wrote through junction");
                } finally {
                    // Only remove the junction itself; Fixture owns and removes its target separately.
                    if (Directory.Exists(link)) Directory.Delete(link);
                }
            }
        });

        Check("Download failure is retained and later items continue; retries are explicit", delegate {
            using (var f = new Fixture()) {
                f.Submit(f.Manifest(FirstId, SecondId));
                f.Downloader.handler = delegate(DownloadOptions options) {
                    if (VideoId(options.UrlText) != FirstId) return Success(options);
                    var result = new DownloadResult { Failed = 1 }; result.Errors.Add("fixture network failure"); return result;
                };
                var first = f.Service().Run("nmixx-test", false);
                Assert(!first.ok && first.counts.failed == 1 && first.counts.succeeded == 1, "Partial failure became success or aborted the batch");
                Assert(first.tasks.Single(x => x.videoId == FirstId).error.Contains("fixture network failure"), "Original failure reason was lost");
                f.Service().Run("nmixx-test", false);
                Assert(f.Downloader.calls.Count == 2, "Failed item retried without request");
                f.Downloader.handler = null;
                var retry = f.Service().Run("nmixx-test", true);
                Assert(retry.ok && retry.counts.succeeded == 2 && f.Downloader.calls.Count == 3, "Explicit retry reran successful media or failed to finish");
            }
        });

        Check("Cancelled item is recoverable and never reported as successfully downloaded", delegate {
            using (var f = new Fixture()) {
                f.Submit(f.Manifest(FirstId, SecondId));
                var service = f.Service();
                f.Downloader.handler = delegate { service.Cancel(); return new DownloadResult { Cancelled = true }; };
                var first = service.Run("nmixx-test", false);
                Assert(first.cancelled && first.counts.succeeded == 0 && first.counts.interrupted == 1, "Cancellation misreported");
                Assert(f.Downloader.cancelled, "Cancel was not forwarded to the active downloader");
                Assert(first.counts.pending == 1 && f.Downloader.calls.Count == 1, "Cancellation continued with later items");
                f.Downloader.handler = null;
                var resumed = f.Service().Run("nmixx-test", false);
                Assert(resumed.counts.succeeded == 2, "Cancelled work could not resume");
            }
        });

        Check("Process interruption state resumes on the next run", delegate {
            using (var f = new Fixture()) {
                f.Submit(f.Manifest(FirstId));
                var state = Json.Deserialize<BridgeState>(File.ReadAllText(f.StatePath));
                state.tasks[FirstId].status = "downloading"; state.tasks[FirstId].startedUtc = DateTime.UtcNow.AddMinutes(-10).ToString("o");
                WriteJson(f.StatePath, state);
                var result = f.Service().Run("nmixx-test", false);
                Assert(result.counts.succeeded == 1 && f.Downloader.calls.Count == 1, "Stale running task was stranded");
            }
        });

        Check("Missing saved media is reported instead of silently remaining available", delegate {
            using (var f = new Fixture()) {
                f.Submit(f.Manifest(FirstId));
                var completed = f.Service().Run("nmixx-test", false);
                string file = completed.tasks.Single().files.Single();
                Assert(Path.GetFullPath(file).StartsWith(f.Output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Unexpected test file path");
                File.Delete(file);
                var status = f.Service().GetStatus("nmixx-test");
                Assert(status.counts.succeeded == 0 && status.counts.missing == 1, "Missing media still reported successful");
                f.Service().Run("nmixx-test", false);
                Assert(f.Downloader.calls.Count == 1, "Missing media retried without explicit request");
                Assert(f.Service().Run("nmixx-test", true).counts.succeeded == 1, "Missing media could not be repaired");
            }
        });

        Check("Corrupt persisted state is an error and is never replaced by an empty queue", delegate {
            using (var f = new Fixture()) {
                f.Submit(f.Manifest(FirstId));
                File.WriteAllText(f.StatePath, "{ broken state");
                Reject(delegate { f.Service().GetStatus("nmixx-test"); }, "Corrupt state was silently accepted");
                Reject(delegate { f.Submit(f.Manifest(SecondId)); }, "Submit replaced corrupt state");
                Assert(File.ReadAllText(f.StatePath) == "{ broken state", "Corruption evidence was overwritten");
            }
        });

        Check("Status stays readable during download while a second run cannot duplicate work", delegate {
            using (var f = new Fixture()) using (var entered = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false)) {
                f.Submit(f.Manifest(FirstId));
                f.Downloader.handler = delegate(DownloadOptions options) { entered.Set(); Assert(release.WaitOne(10000), "Fixture release timeout"); return Success(options); };
                Exception runError = null, readError = null, duplicateError = null;
                BridgeReceipt status = null;
                var runner = new Thread(delegate() { try { f.Service().Run("nmixx-test", false); } catch (Exception e) { runError = e; } });
                runner.IsBackground = true; runner.Start();
                var reader = new Thread(delegate() { try { status = f.Service().GetStatus("nmixx-test"); } catch (Exception e) { readError = e; } });
                var duplicate = new Thread(delegate() { try { f.Service().Run("nmixx-test", false); } catch (Exception e) { duplicateError = e; } });
                reader.IsBackground = true; duplicate.IsBackground = true;
                bool readable = false, rejected = false;
                try {
                    Assert(entered.WaitOne(5000), "Download did not start");
                    reader.Start(); duplicate.Start();
                    readable = reader.Join(2000); rejected = duplicate.Join(2000);
                } finally { release.Set(); runner.Join(5000); if ((reader.ThreadState & System.Threading.ThreadState.Unstarted) == 0) reader.Join(5000); if ((duplicate.ThreadState & System.Threading.ThreadState.Unstarted) == 0) duplicate.Join(5000); }
                Assert(runError == null, "First run failed: " + runError);
                Assert(readable && readError == null && status != null && status.counts.downloading == 1, "Status blocked or lost running state");
                Assert(rejected && duplicateError != null && f.Downloader.calls.Count == 1, "Concurrent run duplicated work instead of reporting busy");
            }
        });

        Check("Status snapshots at normal run boundaries never invent an interruption", delegate {
            using (var f = new Fixture()) using (var stop = new ManualResetEvent(false))
                using (var firstSnapshot = new ManualResetEvent(false)) using (var runningSnapshot = new AutoResetEvent(false))
                using (var allowStatus = new ManualResetEvent(true)) {
                const int batches = 24;
                for (int index = 0; index < batches; index++) {
                    var manifest = f.Manifest("edge" + index.ToString("D7"));
                    manifest.batchId = "edge-" + index;
                    f.Submit(manifest);
                }
                var readerService = f.Service();
                var writerService = f.Service();
                Exception readerError = null;
                int snapshots = 0;
                f.Downloader.handler = delegate(DownloadOptions options) {
                    allowStatus.Set();
                    Assert(runningSnapshot.WaitOne(3000), "No live status snapshot during the fixture download");
                    return Success(options);
                };
                var reader = new Thread(delegate() {
                    try {
                        while (!stop.WaitOne(0)) {
                            Assert(allowStatus.WaitOne(3000), "Status reader was not resumed after lock contention");
                            if (stop.WaitOne(0)) break;
                            var receipt = readerService.GetStatus();
                            Assert(receipt.counts.total == batches, "A persisted queue disappeared from a status snapshot during normal writes");
                            Assert(receipt.counts.interrupted == 0 && receipt.tasks.All(task => task.status != "interrupted"),
                                "Status reported interrupted while all writers were running or completed normally");
                            Interlocked.Increment(ref snapshots);
                            firstSnapshot.Set();
                            if (receipt.counts.downloading > 0) runningSnapshot.Set();
                            Thread.Yield();
                        }
                    } catch (Exception error) { readerError = error; firstSnapshot.Set(); runningSnapshot.Set(); }
                });
                reader.IsBackground = true;
                reader.Start();
                try {
                    Assert(firstSnapshot.WaitOne(3000), "Status reader did not start");
                    for (int index = 0; index < batches; index++) {
                        if (readerError != null) throw new Exception("Status reader failed", readerError);
                        runningSnapshot.Reset();
                        BridgeReceipt completed = null;
                        // A status snapshot may legitimately hold the mutation lock briefly.
                        // Retry only an explicit Windows sharing/lock conflict, with a fixed bound.
                        for (int attempt = 0; attempt < 100; attempt++) {
                            try { completed = writerService.Run("edge-" + index, false); break; }
                            catch (IOException error) {
                                if (!IsSharingConflict(error) || attempt == 99) throw;
                                // Let an already-running snapshot finish, then give this writer
                                // its turn instead of allowing the synthetic reader to starve it.
                                // Reads resume as soon as the downloader starts.
                                allowStatus.Reset();
                                Thread.Sleep(1);
                            }
                        }
                        Assert(completed != null && completed.ok && completed.counts.succeeded == 1,
                            "A normal fixture run did not finish successfully");
                    }
                } finally { stop.Set(); allowStatus.Set(); Assert(reader.Join(5000), "Status reader did not stop"); }
                Assert(readerError == null, "Status reader failed: " + readerError);
                var final = f.Service().GetStatus();
                Assert(final.counts.succeeded == batches && final.counts.interrupted == 0 && f.Downloader.calls.Count == batches,
                    "Normal runs lost completion or were duplicated");
                Assert(snapshots > batches, "Run boundaries were not exercised by concurrent status reads");
            }
        });

        Check("Real adapter rejects missing audio before reporting success", delegate {
            using (var f = new Fixture(true)) {
                f.Submit(f.Manifest(SilentId));
                var result = new StageBridgeService(f.Root).Run("nmixx-test", false);
                Assert(result.counts.failed == 1 && result.counts.succeeded == 0 && !result.ok, "Unexpected verification result: " + Json.Serialize(result));
                Assert(result.tasks.Single(x => x.videoId == SilentId).status == "failed", "Silent MP4 accepted");
                Assert(result.tasks.Single().error.Contains("MP4"), "Failure did not come from media verification");
            }
        });

        Check("Real adapter returns verified audio-video completion and file path", delegate {
            using (var f = new Fixture(true)) {
                f.Submit(f.Manifest(FirstId));
                var result = new StageBridgeService(f.Root).Run("nmixx-test", false);
                Assert(result.ok && result.counts.succeeded == 1 && result.counts.failed == 0, "Unexpected verification result: " + Json.Serialize(result));
                Assert(result.tasks.Single().files.Count == 1 && result.tasks.Single().files.All(File.Exists), "Successful output missing");
            }
        });

        Check("Bridge settings pass explicit HTTP chunk bytes to the downloader", delegate {
            using (var f = new Fixture()) {
                f.SaveSettings(f.Output, 1048576);
                f.Submit(f.Manifest(FirstId, SecondId));
                var result = f.Service().Run("nmixx-test", false);
                Assert(result.ok && f.Downloader.calls.Count == 2 && f.Downloader.calls.All(options => options.HttpChunkSizeBytes == 1048576),
                    "Bridge dropped the configured HTTP chunk byte count");
            }
        });

        Check("Invalid HTTP chunk settings fail before bridge acceptance or Session media writes", delegate {
            foreach (int bytes in new[] { -1, 10485761 }) using (var f = new Fixture()) {
                f.SaveSettings(f.Output, bytes);
                Reject(delegate { f.Submit(f.Manifest(FirstId)); }, "Bridge accepted invalid chunk setting");
                Assert(!File.Exists(f.StatePath) && Directory.GetFiles(f.Output, "*", SearchOption.AllDirectories).Length == 0, "Invalid bridge config accepted tasks or wrote media");
                string output = Path.Combine(f.Output, "not-created");
                using (var session = new DownloadSession(f.Bundle)) {
                    var result = session.Run(new DownloadOptions { UrlText = "https://youtu.be/" + FirstId, OutputDirectory = output, HttpChunkSizeBytes = bytes }, null);
                    Assert(result.Failed == 1 && result.Succeeded == 0 && result.Errors.Count > 0 && !Directory.Exists(output), "Session accepted invalid bytes or touched output");
                }
            }
        });

        Check("CLI parent guard rejects invalid absent-process and self PIDs as JSON errors", delegate {
            using (var f = new Fixture()) {
                foreach (string value in new[] { "invalid", "0", "-1", "2147483647" }) {
                    using (var process = StartSelf(new[] { "--bridge-cli", "--workspace", f.Root, "status" }, value)) {
                        Assert(process.WaitForExit(5000), "Invalid parent PID did not terminate");
                        string output = process.StandardOutput.ReadToEnd();
                        var receipt = Json.Deserialize<BridgeReceipt>(output);
                        Assert(process.ExitCode == 2 && !receipt.ok && !string.IsNullOrWhiteSpace(receipt.error), "Invalid parent PID did not produce an explicit JSON failure");
                    }
                }
                using (var process = StartSelf(new[] { "--bridge-self-parent", "--workspace", f.Root, "status" }, null)) {
                    Assert(process.WaitForExit(5000), "Self parent PID did not terminate");
                    var receipt = Json.Deserialize<BridgeReceipt>(process.StandardOutput.ReadToEnd());
                    Assert(process.ExitCode == 2 && !receipt.ok && receipt.error.Contains("PID"), "Self parent PID accepted");
                }
            }
        });

        Check("CLI works with no parent guard and disposes a valid guard on normal completion", delegate {
            using (var f = new Fixture()) {
                foreach (string value in new[] { null, Process.GetCurrentProcess().Id.ToString() }) {
                    using (var process = StartSelf(new[] { "--bridge-cli", "--workspace", f.Root, "status" }, value)) {
                        Assert(process.WaitForExit(5000), "Status or guard disposal did not finish");
                        var receipt = Json.Deserialize<BridgeReceipt>(process.StandardOutput.ReadToEnd());
                        Assert(process.ExitCode == 0 && receipt.ok && receipt.counts.total == 0, "Normal CLI completion failed");
                    }
                }
            }
        });

        Check("Parent exit kills bridge and downloader subtree; durable work can resume", delegate {
            using (var f = new Fixture(true)) {
                var accepted = f.Submit(f.Manifest(HoldingId));
                string target = accepted.tasks.Single().targetDirectory;
                string release = Path.Combine(f.Root, "parent.release");
                string downloadMarker = Path.Combine(target, "download.pid"), leafMarker = Path.Combine(target, "leaf.pid");
                Process parent = null, bridge = null;
                int downloadId = 0, leafId = 0;
                try {
                    parent = StartSelf(new[] { "--parent-helper", release }, null);
                    bridge = StartSelf(new[] { "--bridge-cli", "--workspace", f.Root, "run", "nmixx-test" }, parent.Id.ToString());
                    Assert(WaitUntil(() => File.Exists(downloadMarker) && File.Exists(leafMarker), 8000),
                        "Downloader subtree did not start" + (bridge.HasExited ? ": " + bridge.StandardOutput.ReadToEnd() + bridge.StandardError.ReadToEnd() : ""));
                    downloadId = int.Parse(File.ReadAllText(downloadMarker)); leafId = int.Parse(File.ReadAllText(leafMarker));
                    Assert(!ProcessEnded(downloadId) && !ProcessEnded(leafId), "Subtree was not alive before parent exit");
                    var running = Json.Deserialize<BridgeState>(File.ReadAllText(f.StatePath));
                    Assert(running.tasks[HoldingId].status == "downloading", "Running work was not durably recorded");
                    File.WriteAllText(release, "exit");
                    Assert(parent.WaitForExit(5000) && parent.ExitCode == 0, "Parent did not exit normally");
                    Assert(bridge.WaitForExit(5000) && bridge.ExitCode == 130, "Bridge did not exit 130 after parent disappeared");
                    Assert(WaitUntil(() => ProcessEnded(downloadId) && ProcessEnded(leafId), 5000), "Download process subtree outlived bridge");
                    var interrupted = f.Service().GetStatus("nmixx-test");
                    Assert(interrupted.counts.interrupted == 1 && interrupted.counts.succeeded == 0, "Terminated download was not recoverable");
                    var resumed = f.Service().Run("nmixx-test", false);
                    Assert(resumed.ok && resumed.counts.succeeded == 1 && f.Downloader.calls.Count == 1, "Recovered task could not be resumed");
                } finally {
                    StopOwnedProcess(bridge); StopOwnedProcess(parent);
                    // The job should already have removed both descendants. On a failed assertion,
                    // retain the captured process identity and terminate only the fixture children.
                    foreach (int id in new[] { downloadId, leafId }) if (id != 0 && !ProcessEnded(id)) {
                        using (var owned = Process.GetProcessById(id)) { owned.Kill(); owned.WaitForExit(5000); }
                    }
                }
            }
        });

        Console.WriteLine("Failures: " + failed);
        return failed == 0 ? 0 : 1;
    }
}
