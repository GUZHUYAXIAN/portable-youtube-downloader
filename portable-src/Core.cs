using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PortableYouTube
{
    public static class DownloadCommand
    {
        public static string[] ParseUrls(string text)
        {
            var result = new List<string>();
            foreach (string item in (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string url = item.Trim();
                if (url.Length == 0) continue;
                Uri parsed;
                if (url.Any(char.IsWhiteSpace) || !Uri.TryCreate(url, UriKind.Absolute, out parsed) ||
                    (parsed.Scheme != "https" && parsed.Scheme != "http") || string.IsNullOrEmpty(parsed.Host))
                    throw new ArgumentException("请输入完整的 http 或 https 视频链接，每行一条。\n无效内容：" + url);
                if (!result.Contains(url)) result.Add(url);
            }
            if (result.Count == 0) throw new ArgumentException("请先粘贴视频链接，每行一条。");
            return result.ToArray();
        }

        // Windows command-line quoting; no command interpreter is involved.
        public static string Quote(string value)
        {
            var output = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') output.Append('\\', slashes * 2 + 1).Append(c);
                else output.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return output.Append('\\', slashes * 2).Append('"').ToString();
        }

        public static int? Progress(string line)
        {
            var match = Regex.Match(line ?? "", @"^__PROGRESS__\s*(\d+(?:\.\d+)?)%");
            double value;
            if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value)) return null;
            return Math.Max(0, Math.Min(100, (int)value));
        }

        static void RequireSingleYouTubeVideo(string url)
        {
            var uri = new Uri(url);
            string host = uri.Host.ToLowerInvariant();
            bool shortHost = host == "youtu.be";
            if (!shortHost && host != "youtube.com" && host != "www.youtube.com" && host != "m.youtube.com" && host != "music.youtube.com")
                throw new ArgumentException("请输入YouTube视频链接。\n无效内容：" + url);
            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            bool single = shortHost && Regex.IsMatch(path, @"^/[^/\s]+/?$");
            if (!shortHost) {
                if (path.TrimEnd('/') == "/watch") {
                    // --no-playlist protects watch?v=...&list=... only when a video is present.
                    string video = uri.Query.TrimStart('?').Split('&').FirstOrDefault(x => x.StartsWith("v=", StringComparison.Ordinal));
                    single = video != null && !string.IsNullOrWhiteSpace(Uri.UnescapeDataString(video.Substring(2).Replace("+", " ")));
                } else {
                    single = Regex.IsMatch(path, @"^/(shorts|live|embed|clip)/[^/\s]+/?$") && path.TrimEnd('/') != "/embed/videoseries";
                }
            }
            if (!single) throw new ArgumentException("链接指向播放列表或频道，请勾选下载整个播放列表或改用具体视频链接。\n无效内容：" + url);
        }

        public static ProcessStartInfo Build(string root, DownloadOptions options)
        {
            if (options == null) throw new ArgumentException("下载选项不能为空。");
            root = Path.GetFullPath(root);
            string[] urls = ParseUrls(options.UrlText);
            if (!options.Playlist) foreach (string url in urls) RequireSingleYouTubeVideo(url);
            string tools = Path.Combine(root, "tools");
            foreach (string file in new[] { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe", "node.exe" })
                if (!File.Exists(Path.Combine(tools, file)))
                    throw new FileNotFoundException("缺少 tools\\" + file + "。请完整解压压缩包后再运行。");
            if (string.IsNullOrWhiteSpace(options.OutputDirectory)) throw new ArgumentException("请选择保存目录。");
            string output = Path.GetFullPath(options.OutputDirectory);
            if (options.Height != 0 && options.Height != 2160 && options.Height != 1080 && options.Height != 720 && options.Height != 480)
                throw new ArgumentException("不支持的清晰度。");
            string limit = options.Height == 0 ? "" : "[height<=" + options.Height + "]";
            string format = "bv*[ext=mp4]" + limit + "+ba[ext=m4a]/bv*[ext=mp4]" + limit + "+ba/b[ext=mp4]" + limit + "/best" + limit;
            var args = new List<string> {
                "--ignore-config", "--no-plugin-dirs", "--no-remote-components", "--no-js-runtimes", "--no-cache-dir",
                "--js-runtimes", "node:" + Path.Combine(tools, "node.exe"),
                "--ffmpeg-location", tools.Replace("%", "%%").Replace("$", "$$"), options.Playlist ? "--yes-playlist" : "--no-playlist",
                "--newline", "--progress", "--progress-delta", "0.5", "--encoding", "utf-8",
                "--progress-template", "download:__PROGRESS__%(progress._percent_str)s | %(progress._speed_str)s | ETA %(progress._eta_str)s",
                "--print", "after_move:__FILE__%(filepath)s", "--no-simulate", "--no-quiet",
                "--windows-filenames", "--no-overwrites", "--continue", "--abort-on-unavailable-fragments", "--socket-timeout", "20", "--retries", "3",
                "-f", format, "--merge-output-format", "mp4", "--remux-video", "mp4",
                // yt-dlp expands Windows environment variables even without a shell.
                "-P", output.Replace("%", "%%").Replace("$", "$$"), "-o", "%(title).150B [%(id)s].%(ext)s"
            };
            if (!string.IsNullOrWhiteSpace(options.Proxy))
            {
                Uri proxy;
                if (!Uri.TryCreate(options.Proxy.Trim(), UriKind.Absolute, out proxy) ||
                    !(new[] { "http", "https", "socks4", "socks4a", "socks5", "socks5h" }).Contains(proxy.Scheme) || string.IsNullOrEmpty(proxy.Host))
                    throw new ArgumentException("代理格式示例：http://127.0.0.1:7890；留空使用系统设置。");
                args.AddRange(new[] { "--proxy", options.Proxy.Trim() });
            }
            args.Add("--"); args.AddRange(urls);
            return new ProcessStartInfo(Path.Combine(tools, "yt-dlp.exe"), string.Join(" ", args.Select(Quote))) {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
        }
    }

    public class DownloadOptions
    {
        public string UrlText, OutputDirectory, Proxy;
        public int Height;
        public bool Playlist;
    }
}
