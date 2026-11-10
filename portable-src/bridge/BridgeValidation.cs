using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace PortableYouTube.StageBridge
{
    public static class BridgeValidation
    {
        public static string BatchId(string value)
        {
            if (value == null || !Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\z"))
                throw new ArgumentException("batchId 必须是 1–80 个 ASCII 字母、数字、下划线或连字符，且以字母或数字开头。");
            return value;
        }

        static void Text(string value, string name, int limit, bool required)
        {
            if ((required && string.IsNullOrWhiteSpace(value)) || (value != null && (value.Length > limit || value.Any(char.IsControl))))
                throw new ArgumentException(name + "为空、过长或包含控制字符。");
        }

        static void Date(string value, string name)
        {
            DateTime parsed;
            if (!string.IsNullOrEmpty(value) && !DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                throw new ArgumentException(name + "必须为 yyyy-MM-dd 或空值。");
        }

        public static string CanonicalVideoUrl(string url, string videoId)
        {
            if (videoId == null || !Regex.IsMatch(videoId, @"\A[A-Za-z0-9_-]{11}\z")) throw new ArgumentException("无效的 YouTube videoId。");
            Uri uri;
            if (url == null || url.Any(char.IsWhiteSpace) || !Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != "https" ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0) throw new ArgumentException("视频链接必须是有效的 HTTPS YouTube 链接。");
            string host = uri.Host.ToLowerInvariant();
            string actual = null;
            string[] pieces = uri.AbsolutePath.Trim('/').Split('/');
            if (host == "youtu.be" && pieces.Length == 1) actual = pieces[0];
            else if (new[] { "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com" }.Contains(host))
            {
                if (uri.AbsolutePath.TrimEnd('/') == "/watch")
                {
                    var values = new List<string>();
                    foreach (string part in uri.Query.TrimStart('?').Split('&'))
                    {
                        int split = part.IndexOf('=');
                        if (split >= 0 && Uri.UnescapeDataString(part.Substring(0, split)) == "v")
                            values.Add(Uri.UnescapeDataString(part.Substring(split + 1)));
                    }
                    if (values.Count == 1) actual = values[0];
                }
                else if (pieces.Length == 2 && new[] { "shorts", "live", "embed" }.Contains(pieces[0])) actual = pieces[1];
            }
            if (actual != videoId) throw new ArgumentException("视频链接与 videoId 不一致，或不是支持的单个 YouTube 视频链接：" + videoId);
            return "https://www.youtube.com/watch?v=" + videoId;
        }

        public static StageManifest Manifest(StageManifest manifest)
        {
            if (manifest == null || manifest.schemaVersion != 1) throw new ArgumentException("manifest schemaVersion 必须为 1。");
            BatchId(manifest.batchId);
            Text(manifest.artist, "artist", 160, true);
            Text(manifest.release, "release", 160, true);
            if (manifest.items == null || manifest.items.Count == 0 || manifest.items.Count > 500) throw new ArgumentException("每批必须包含 1–500 条视频。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (StageManifestItem item in manifest.items)
            {
                if (item == null) throw new ArgumentException("items 不能包含空项。");
                CanonicalVideoUrl(item.url, item.videoId);
                if (!ids.Add(item.videoId)) throw new ArgumentException("同一 manifest 内 videoId 重复：" + item.videoId);
                Text(item.title, "title", 1000, true);
                Text(item.channel, "channel", 300, false);
                Text(item.eventName, "eventName", 300, false);
                Text(item.member, "member", 160, false);
                Text(item.sourceUrl, "sourceUrl", 2048, false);
                if (!string.IsNullOrEmpty(item.sourceUrl)) {
                    Uri source;
                    if (!Uri.TryCreate(item.sourceUrl, UriKind.Absolute, out source) || (source.Scheme != "https" && source.Scheme != "http") || source.UserInfo.Length > 0)
                        throw new ArgumentException("sourceUrl 必须为公开 HTTP(S) 来源链接或空值。");
                }
                Date(item.performanceDate, "performanceDate");
                Date(item.uploadDate, "uploadDate");
                if (!new[] { "official-stage", "fullcam", "member-fancam", "fancam" }.Contains(item.kind)) throw new ArgumentException("不支持的 kind：" + item.kind);
            }
            return manifest;
        }
    }
}
