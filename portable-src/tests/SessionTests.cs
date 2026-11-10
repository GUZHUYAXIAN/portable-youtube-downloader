using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using PortableYouTube;

class SessionTests
{
    static int failed;
    static void Check(string name, Action body)
    {
        try { body(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    sealed class Fixture : IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "便携 Session %PATH% $USER " + Guid.NewGuid().ToString("N"));
        public string Output { get { return Path.Combine(Root, "输出 空格 100% $USER"); } }
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "tools"));
            foreach (string tool in new[] { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe", "node.exe" })
                File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(Root, "tools", tool));
        }
        public DownloadOptions Options(string scenario) { return new DownloadOptions { UrlText = "https://youtu.be/" + scenario, OutputDirectory = Output }; }
        public DownloadResult Run(string scenario) { using (var session = new DownloadSession(Root)) return session.Run(Options(scenario), null); }
        public void Dispose()
        {
            for (int retry = 0; ; retry++) {
                try { if (Directory.Exists(Root)) Directory.Delete(Root, true); return; }
                catch (IOException) { if (retry == 10) throw; Thread.Sleep(100); }
            }
        }
    }
    static int Helper(string[] args)
    {
        string exe = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
        if (exe.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase)) {
            string path = args[args.Length - 1];
            string contents = File.ReadAllText(path);
            if (contents == "probefail") return 4;
            if (contents == "probewait") { Console.WriteLine("probe-ready"); Console.Out.Flush(); Thread.Sleep(20000); }
            Console.WriteLine("[STREAM]\ncodec_type=video\ncodec_name=" + (contents == "av1" ? "av1" : "h264") + "\n[/STREAM]");
            if (contents != "silent") Console.WriteLine("[STREAM]\ncodec_type=audio\ncodec_name=aac\n[/STREAM]");
            Console.WriteLine("[FORMAT]\nformat_name=" + (contents == "renamedmkv" ? "matroska,webm" : "mov,mp4,m4a,3gp,3g2,mj2") + "\nduration=" + (contents == "zero" ? "0.000000" : contents == "nan" ? "NaN" : "1.000000") + "\n[/FORMAT]");
            return 0;
        }
        int outputIndex = Array.IndexOf(args, "-P");
        if (outputIndex < 0) return 9;
        string output = args[outputIndex + 1].Replace("%%", "%").Replace("$$", "$");
        string scenario = new Uri(args[args.Length - 1]).AbsolutePath.Trim('/');
        if (scenario == "fail") { Console.Error.WriteLine("ERROR: fixture network failure"); return 7; }
        if (scenario != "parsewait") { Console.WriteLine("__PROGRESS__100.0%"); Console.Out.Flush(); }
        if (scenario == "wait" || scenario == "parsewait") { Console.WriteLine("download-ready"); Console.Out.Flush(); Thread.Sleep(20000); }
        if (scenario == "empty") return 0;
        string file = Path.Combine(output, scenario + (scenario == "webm" ? ".webm" : ".mp4"));
        if (scenario != "missing" && !File.Exists(file)) File.WriteAllText(file, scenario);
        Console.WriteLine("__FILE__" + file); Console.Out.Flush();
        if (scenario == "afterfilefail") { Console.Error.WriteLine("ERROR: later playlist entry failed"); return 2; }
        return 0;
    }
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        bool helper = !Path.GetFileName(Assembly.GetExecutingAssembly().Location).Equals("SessionTests.exe", StringComparison.OrdinalIgnoreCase);
        TestEnvironment.Initialize(!helper);
        if (helper) return Helper(args);
        Check("Invalid input fails before creating the output directory", delegate {
            using (var f = new Fixture()) using (var s = new DownloadSession(f.Root)) {
                var o = f.Options("good"); o.UrlText = "--exec bad";
                var r = s.Run(o, null);
                Assert(r.Failed == 1 && r.Errors.Count != 0 && r.Succeeded == 0, "Invalid input was not reported");
                Assert(!Directory.Exists(f.Output), "Invalid input created output directory");
            }
        });
        Check("Missing bundled tool prevents download", delegate {
            using (var f = new Fixture()) {
                File.Delete(Path.Combine(f.Root, "tools", "ffprobe.exe"));
                var r = f.Run("good"); Assert(r.Failed == 1 && r.Errors.Any(x => x.Contains("ffprobe.exe")), "Missing tool was not reported");
                Assert(!Directory.Exists(f.Output), "Output created despite invalid tools");
            }
        });
        Check("Invalid proxy prevents any output writes", delegate {
            using (var f = new Fixture()) using (var s = new DownloadSession(f.Root)) {
                var o = f.Options("good"); o.Proxy = "file:///C:/secret";
                var r = s.Run(o, null); Assert(r.Failed == 1 && r.Errors.Count > 0, "Invalid proxy accepted");
                Assert(!Directory.Exists(f.Output), "Output created before proxy validation");
            }
        });
        Check("A file in place of output directory returns a useful failure", delegate {
            using (var f = new Fixture()) { File.WriteAllText(f.Output, "do not overwrite");
                var r = f.Run("good"); Assert(r.Failed == 1 && r.Errors.Count > 0, "Unwritable output not rejected");
                Assert(File.ReadAllText(f.Output) == "do not overwrite", "Existing file changed");
            }
        });
        Check("Output directory that denies file creation fails before downloading", delegate {
            using (var f = new Fixture()) {
                Directory.CreateDirectory(f.Output);
                var original = Directory.GetAccessControl(f.Output);
                var denied = Directory.GetAccessControl(f.Output);
                denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.CreateFiles, AccessControlType.Deny));
                try {
                    Directory.SetAccessControl(f.Output, denied);
                    var r = f.Run("good"); Assert(r.Failed == 1 && r.Succeeded == 0 && r.Errors.Any(x => x.Contains("无法写入")), "Write access was not actually checked");
                    Assert(Directory.GetFiles(f.Output).Length == 0, "Output appeared in denied directory");
                } finally { Directory.SetAccessControl(f.Output, original); }
            }
        });
        Check("Successful link requires a probed audio and video MP4", delegate {
            using (var f = new Fixture()) {
                var r = f.Run("good"); Assert(r.Succeeded == 1 && r.Failed == 0 && !r.Cancelled && r.Files.Count == 1, "Valid file not accepted");
                Assert(r.Files[0] == Path.Combine(f.Output, "good.mp4"), "Special output path changed");
                Assert(Directory.GetFiles(f.Output).Length == 1, "Write-probe temporary file leaked");
            }
        });
        foreach (string scenario in new[] { "empty", "missing", "webm", "silent", "zero", "nan", "renamedmkv", "probefail" }) {
            string current = scenario;
            Check("Reject invalid completion: " + scenario, delegate {
                using (var f = new Fixture()) { var r = f.Run(current);
                    Assert(r.Succeeded == 0 && r.Failed == 1 && r.Files.Count == 0 && r.Errors.Count > 0, "Invalid result accepted: " + current);
                }
            });
        }
        Check("An existing silent file is preserved and reported as failed", delegate {
            using (var f = new Fixture()) {
                Directory.CreateDirectory(f.Output); string file = Path.Combine(f.Output, "good.mp4"); File.WriteAllText(file, "silent");
                var r = f.Run("good"); Assert(r.Failed == 1 && r.Succeeded == 0 && r.Files.Count == 0, "Silent existing file reported success");
                Assert(File.ReadAllText(file) == "silent", "Existing file was overwritten");
            }
        });
        Check("Failure of one link does not skip later links", delegate {
            using (var f = new Fixture()) using (var s = new DownloadSession(f.Root)) {
                var o = f.Options("fail"); o.UrlText += "\nhttps://youtu.be/good";
                var r = s.Run(o, null); Assert(r.Failed == 1 && r.Succeeded == 1 && r.Files.Count == 1 && r.Errors.Count > 0, "Partial failure status incorrect");
            }
        });
        Check("Nonzero playlist exit remains failed even if one valid file exists", delegate {
            using (var f = new Fixture()) { var r = f.Run("afterfilefail");
                Assert(r.Failed == 1 && r.Succeeded == 0 && r.Files.Count == 1 && r.Errors.Count > 0, "Partial playlist incorrectly succeeded");
            }
        });
        Check("Cancel before Run prevents all output writes", delegate {
            using (var f = new Fixture()) using (var s = new DownloadSession(f.Root)) {
                s.Cancel(); var r = s.Run(f.Options("good"), null);
                Assert(r.Cancelled && r.Failed == 0 && r.Succeeded == 0 && !Directory.Exists(f.Output), "Early cancel ignored");
            }
        });
        foreach (string scenario in new[] { "parsewait", "wait", "probewait" }) {
            string current = scenario;
            Check("Cancel interrupts " + scenario + " without reporting completion", delegate {
                using (var f = new Fixture()) using (var s = new DownloadSession(f.Root)) {
                    var timer = Stopwatch.StartNew();
                    var r = s.Run(f.Options(current), delegate(DownloadUpdate u) {
                        if (u.Message != null && (u.Message.Contains("download-ready") || u.Message.Contains("probe-ready"))) s.Cancel();
                    });
                    Assert(r.Cancelled && r.Succeeded == 0 && r.Failed == 0, "Cancellation counted as completion or failure");
                    Assert(timer.ElapsedMilliseconds < 8000, "Cancel did not stop active process promptly");
                }
            });
        }
        Check("Editing mode rejects existing incompatible AV1 media and preserves it", delegate {
            using (var f = new Fixture()) using (var session = new DownloadSession(f.Root)) {
                var options = f.Options("av1"); options.EditingCompatible = true;
                var result = session.Run(options, null);
                Assert(result.Failed == 1 && result.Succeeded == 0 && result.Files.Count == 0, "Editing mode accepted incompatible media");
                Assert(File.Exists(Path.Combine(f.Output, "av1.mp4")), "Rejected media was deleted");
            }
        });
        Check("Editing mode verifies H264 AAC", delegate {
            using (var f = new Fixture()) using (var session = new DownloadSession(f.Root)) {
                var options = f.Options("good"); options.EditingCompatible = true;
                Assert(session.Run(options, null).Succeeded == 1, "Compatible editing file was rejected");

            }
        });
        Check("Original quality mode retains AV1 support", delegate {
            using (var f = new Fixture()) {
                var original = f.Run("av1"); Assert(original.Succeeded == 1, string.Join(" | ", original.Errors));
            }
        });
        Console.WriteLine("Failures: " + failed); return failed == 0 ? 0 : 1;
    }
}
