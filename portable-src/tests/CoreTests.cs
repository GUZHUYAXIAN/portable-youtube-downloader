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
    static string[] CommandArgumentsThroughProcess(string nodePath, ProcessStartInfo command)
    {
        string script = "process.argv.slice(1).forEach(x=>console.log(Buffer.from(x,'utf8').toString('base64')))";
        var info = new ProcessStartInfo(nodePath,
            "-e " + DownloadCommand.Quote(script) + " -- " + command.Arguments) {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        using (var process = Process.Start(info)) {
            string output = process.StandardOutput.ReadToEnd(); string error = process.StandardError.ReadToEnd(); process.WaitForExit();
            Assert(process.ExitCode == 0, "Argument probe process failed: " + error);
            return output.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => Encoding.UTF8.GetString(Convert.FromBase64String(value))).ToArray();
        }
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
        Check("Explicit playlist mode allows YouTube playlists and channels", delegate {
            WithTools(delegate(string root) {
                foreach (string url in new[] { "https://youtube.com/playlist?list=PLexample", "https://youtube.com/@channel" }) {
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
        Check("HTTP chunk option is opt-in and survives process argument parsing before URL boundary", delegate {
            WithTools(delegate(string root) {
                foreach (int bytes in new[] { 0, 1048576, 10485760 }) {
                    string url = "https://www.youtube.com/watch?v=stage000001";
                    var options = new DownloadOptions { UrlText = url, OutputDirectory = Path.Combine(root, "output"), HttpChunkSizeBytes = bytes };
                    string[] actual = CommandArgumentsThroughProcess(nodePath, DownloadCommand.Build(root, options));
                    int chunk = Array.IndexOf(actual, "--http-chunk-size");
                    int boundary = Array.IndexOf(actual, "--");
                    Assert(boundary >= 0 && actual[boundary + 1] == url, "URL boundary changed in actual child argv");
                    if (bytes == 0) Assert(chunk == -1, "Default GUI options unexpectedly enable HTTP chunks");
                    else Assert(chunk >= 0 && chunk + 1 < boundary && actual[chunk + 1] == bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "Explicit byte count changed in child argv or became part of the URL list");
                }
            });
        });
        Check("Out-of-range HTTP chunk values reject command construction without creating output", delegate {
            WithTools(delegate(string root) {
                string output = Path.Combine(root, "not-created");
                foreach (int bytes in new[] { -1, 10485761, int.MaxValue }) {
                    bool rejected = false;
                    try { DownloadCommand.Build(root, new DownloadOptions { UrlText = "https://youtu.be/stage000001", OutputDirectory = output, HttpChunkSizeBytes = bytes }); }
                    catch (ArgumentException) { rejected = true; }
                    Assert(rejected && !Directory.Exists(output), "Invalid chunk value accepted or created output: " + bytes);
                }
            });
        });
        Check("Social post URLs are accepted without enabling account-wide downloads", delegate {
            WithTools(delegate(string root) {
                foreach (string url in new[] { "https://x.com/NMIXX_official/status/1234567890", "https://twitter.com/NMIXX_official/status/1234567890/video/1", "https://www.instagram.com/p/ABC_123/", "https://www.instagram.com/reel/ABC-123/" }) {
                    var info = DownloadCommand.Build(root, new DownloadOptions { UrlText = url, OutputDirectory = Path.Combine(root, "output") });
                    Assert(info.Arguments.Contains("--no-playlist"), "Social post must stay bounded");
                }
            });
        });
        Check("Social account pages, forged hosts and URL credentials are rejected in both modes", delegate {
            WithTools(delegate(string root) {
                foreach (bool playlist in new[] { false, true }) foreach (string url in new[] {
                    "https://x.com/NMIXX_official", "https://instagram.com/nmixx_official/", "https://x.com.evil.test/nmixx/status/12",
                    "https://example.com/media", "https://user:password@youtube.com/watch?v=id", "https://x.com:8443/nmixx/status/12" }) {
                    bool rejected = false;
                    try { DownloadCommand.Build(root, new DownloadOptions { UrlText = url, OutputDirectory = Path.Combine(root, "output"), Playlist = playlist }); }
                    catch (ArgumentException) { rejected = true; }
                    Assert(rejected, "Unbounded or untrusted URL accepted: " + url);
                }
            });
        });
        Check("Editing mode selects H264 and AAC without silent incompatible fallback", delegate {
            WithTools(delegate(string root) {
                var options = new DownloadOptions { UrlText = "https://youtu.be/id", OutputDirectory = Path.Combine(root, "output"), Height = 1080 };
                var field = typeof(DownloadOptions).GetField("EditingCompatible");
                Assert(field != null, "Editing mode is missing"); field.SetValue(options, true);
                string[] actual = CommandArgumentsThroughProcess(nodePath, DownloadCommand.Build(root, options));
                string format = actual[Array.IndexOf(actual, "-f") + 1];
                Assert(format.Contains("vcodec^=avc1") && format.Contains("acodec^=mp4a") && !format.Contains("/best"), "Incompatible codec fallback is allowed");
                Assert(actual[Array.IndexOf(actual, "-o") + 1].Contains("H264"), "Editing files can collide with old AV1 downloads");
            });
        });
        Console.WriteLine("Failures: " + failed);
        return failed == 0 ? 0 : 1;
    }
}
