using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PortableYouTube;

class CoreTests
{
    static int failed;
    static void Check(string name, Action body)
    {
        try { body(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static void Invalid(string text)
    {
        try { DownloadCommand.ParseUrls(text); }
        catch (ArgumentException) { return; }
        throw new Exception("Unsafe or empty input was accepted");
    }
    static void WithTools(Action<string> body)
    {
        string root = Path.Combine(Path.GetTempPath(), "PortableCore-" + Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(Path.Combine(root, "tools"));
            foreach (string tool in new[] { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe", "node.exe" })
                File.WriteAllText(Path.Combine(root, "tools", tool), "test fixture");
            body(root);
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    static void RejectBuild(string root, string url, string expectedMessage)
    {
        try { DownloadCommand.Build(root, new DownloadOptions { UrlText = url, OutputDirectory = Path.Combine(root, "output") }); }
        catch (ArgumentException e) { Assert(e.Message.Contains(expectedMessage), "Wrong rejection for " + url + ": " + e.Message); return; }
        throw new Exception("Non-video link accepted in single-video mode: " + url);
    }
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        TestEnvironment.Initialize(true);
        string nodePath = args.Length > 0 ? args[0] : TestEnvironment.NodePath;
        Check("Pasted links retain query parameters and remove exact duplicates", delegate {
            var urls = DownloadCommand.ParseUrls(" https://www.youtube.com/watch?v=abc&list=123 \r\n\r\nhttps://youtu.be/def\nhttps://youtu.be/def ");
            Assert(urls.SequenceEqual(new[] { "https://www.youtube.com/watch?v=abc&list=123", "https://youtu.be/def" }), "Incorrect pasted URL handling");
        });
        Check("Reject empty input, options, local files and malformed links", delegate {
            foreach (var text in new[] { "", "  ", "--exec calc.exe", "file:///C:/private.txt", "javascript:alert(1)", "https://", "https://youtube.com/a b", "https://youtube.com/a\t--exec" }) Invalid(text);
        });
        Check("Windows argument quoting survives actual Unicode and metacharacter round trip", delegate {
            string[] values = { "", "a b", "中文路径 & % ! (test)", "x\"y", "C:\\目录 name\\", "two\\\\\"quotes", "https://youtube.com/watch?v=a&list=b" };
            string script = "process.argv.slice(1).forEach(x=>console.log(Buffer.from(x,'utf8').toString('base64')))";
            var all = new[] { "-e", script, "--" }.Concat(values);
            var info = new ProcessStartInfo(nodePath, string.Join(" ", all.Select(DownloadCommand.Quote))) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using (var p = Process.Start(info)) {
                string output = p.StandardOutput.ReadToEnd(); p.WaitForExit();
                var actual = output.Replace("\r", "").Split('\n').Take(values.Length).Select(x => Encoding.UTF8.GetString(Convert.FromBase64String(x))).ToArray();
                Assert(p.ExitCode == 0 && actual.SequenceEqual(values), "Arguments were altered in transit");
            }
        });
        Check("Progress ignores unrelated logs and handles fractional percentages", delegate {
            Assert(DownloadCommand.Progress("__PROGRESS__ 42.7%") == 42, "Fractional percent missing");
            Assert(DownloadCommand.Progress("__PROGRESS__100.0%") == 100, "Completion percent missing");
            Assert(DownloadCommand.Progress("__PROGRESS__ NA") == null, "Unknown percent must remain unknown");
            Assert(DownloadCommand.Progress("[download] 100% unrelated text") == null, "Unrelated log parsed as progress");
        });
        Check("Default mode rejects playlist and channel links before launch", delegate {
            WithTools(delegate(string root) {
                foreach (string url in new[] {
                    "https://www.youtube.com/playlist?list=PLexample", "https://youtube.com/@channel/videos",
                    "https://m.youtube.com/channel/UCexample", "https://youtube.com/user/example", "https://youtube.com/c/example",
                    "https://youtube.com/", "https://youtu.be/", "https://youtube.com/watch?list=PLexample", "https://youtube.com/watch?v=",
                    "https://youtube.com/watch?v=%20", "https://youtube.com/shorts/", "https://youtube.com/live/",
                    "https://youtube.com/embed/videoseries?list=PLexample", "https://youtube.com/shorts/id/extra" })
                    RejectBuild(root, url, "请勾选下载整个播放列表或改用具体视频链接");
            });
        });
        Check("Default mode rejects non-YouTube and lookalike hosts", delegate {
            WithTools(delegate(string root) {
                foreach (string url in new[] { "https://example.com/video", "https://youtube.com.example.com/watch?v=id", "https://notyoutube.com/watch?v=id" })
                    RejectBuild(root, url, "请输入YouTube视频链接");
            });
        });
        Check("Default mode accepts supported single-video URL shapes", delegate {
            WithTools(delegate(string root) {
                foreach (string url in new[] {
                    "https://youtu.be/fake-id?t=1", "https://youtube.com/watch?v=id", "https://www.youtube.com/watch?v=id&list=PLexample",
                    "https://m.youtube.com/watch?feature=share&v=id", "https://music.youtube.com/watch?v=id",
                    "https://youtube.com/shorts/id", "https://youtube.com/live/id", "https://youtube.com/embed/id", "https://youtube.com/clip/id" }) {
                    var info = DownloadCommand.Build(root, new DownloadOptions { UrlText = url, OutputDirectory = Path.Combine(root, "output") });
                    Assert(info.Arguments.Contains("--no-playlist"), "Single-video restriction missing: " + url);
                }
            });
        });
        Check("Explicit playlist mode allows playlist channel and ordinary HTTP inputs", delegate {
            WithTools(delegate(string root) {
                foreach (string url in new[] { "https://youtube.com/playlist?list=PLexample", "https://youtube.com/@channel", "https://example.com/media" }) {
                    var info = DownloadCommand.Build(root, new DownloadOptions { UrlText = url, OutputDirectory = Path.Combine(root, "output"), Playlist = true });
                    Assert(info.Arguments.Contains("--yes-playlist"), "Explicit playlist setting ignored");
                }
            });
        });
        Check("Output paths are literal despite yt-dlp variable expansion", delegate {
            string id = Guid.NewGuid().ToString("N");
            string root = Path.Combine(Path.GetTempPath(), "PortableCore %PATH% $USER " + id);
            try {
                Directory.CreateDirectory(Path.Combine(root, "tools"));
                foreach (string tool in new[] { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe", "node.exe" })
                    File.WriteAllText(Path.Combine(root, "tools", tool), "test fixture");
                var info = DownloadCommand.Build(root, new DownloadOptions { UrlText = "https://youtu.be/example", OutputDirectory = Path.Combine(root, "中文 %PATH% $USER 100%") });
                Assert(info.Arguments.Contains("中文 %%PATH%% $$USER 100%%"), "Output path variables are not escaped for yt-dlp");
                Assert(info.Arguments.Contains("--no-playlist") && info.Arguments.Contains("--no-overwrites"), "Safe download defaults missing");
                Assert(info.Arguments.Contains("--no-cache-dir") && info.Arguments.Contains("--no-quiet"), "Portable cache or visible logs option missing");
                Assert(info.Arguments.Contains("--abort-on-unavailable-fragments"), "Missing media fragments could be silently skipped");
                string ffmpegArgument = "\"--ffmpeg-location\" \"" + Path.Combine(Path.GetTempPath(), "PortableCore %%PATH%% $$USER " + id, "tools") + "\"";
                Assert(info.Arguments.Contains(ffmpegArgument), "Bundled FFmpeg path variables are not escaped");
            } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        Console.WriteLine("Failures: " + failed);
        return failed == 0 ? 0 : 1;
    }
}
