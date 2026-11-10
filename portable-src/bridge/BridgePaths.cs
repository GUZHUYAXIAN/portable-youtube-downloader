using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PortableYouTube.StageBridge
{
    public sealed class BridgePaths
    {
        public readonly string Workspace, StateDirectory, SettingsPath, StatePath, LockPath;

        public BridgePaths(string workspace)
        {
            Workspace = FullLocalPath(workspace);
            if (!Directory.Exists(Workspace)) throw new DirectoryNotFoundException("工作目录不存在：" + Workspace);
            CheckNoReparse(Workspace);
            StateDirectory = Path.Combine(Workspace, ".local", "stage-bridge");
            SettingsPath = Path.Combine(StateDirectory, "settings.json");
            StatePath = Path.Combine(StateDirectory, "state.json");
            LockPath = Path.Combine(StateDirectory, "mutation.lock");
            CheckContained(StateDirectory);
        }

        public static string FullLocalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Regex.IsMatch(path, @"^[A-Za-z]:[\\/]"))
                throw new ArgumentException("必须使用本机磁盘的完整绝对路径。");
            string full = Path.GetFullPath(path);
            return full.Length == Path.GetPathRoot(full).Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public string CheckContained(string path)
        {
            string full = FullLocalPath(path);
            if (!full.StartsWith(Workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("路径必须位于工作目录内：" + full);
            CheckNoReparse(full);
            return full;
        }

        public static void CheckNoReparse(string path)
        {
            string full = Path.GetFullPath(path);
            string current = Path.GetPathRoot(full);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不允许重解析路径：" + current);
            foreach (string piece in full.Substring(current.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, piece);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不允许重解析路径：" + current);
            }
        }

        public void EnsureDirectory(string path)
        {
            string full = CheckContained(path);
            Directory.CreateDirectory(full);
            CheckContained(full);
        }

        public string ExistingDirectory(string path, string label)
        {
            string full = CheckContained(path);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException(label + "不存在：" + full);
            return full;
        }

        public static string SafeSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "未命名";
            var text = new StringBuilder();
            foreach (char character in value.Normalize(NormalizationForm.FormC))
                text.Append(char.IsControl(character) || "<>:\"/\\|?*".IndexOf(character) >= 0 ? '_' : character);
            string result = text.ToString().Trim().TrimEnd('.', ' ');
            if (result.Length > 48) result = result.Substring(0, 48).TrimEnd('.', ' ');
            if (result.Length > 0 && char.IsHighSurrogate(result[result.Length - 1])) result = result.Substring(0, result.Length - 1);
            if (result.Length == 0 || result == "." || result == "..") result = "未命名";
            if (Regex.IsMatch(result, @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase)) result = "_" + result;
            return result;
        }

        public string TargetDirectory(BridgeSettings settings, StageTask task)
        {
            string date = string.IsNullOrEmpty(task.performanceDate) ? "日期待确认" : task.performanceDate;
            string eventName = string.IsNullOrWhiteSpace(task.eventName) ? "活动待确认" : task.eventName;
            string kind = task.kind + (string.IsNullOrWhiteSpace(task.member) ? "" : "_" + task.member);
            return CheckContained(Path.Combine(settings.outputRoot, SafeSegment(task.artist), SafeSegment(task.release),
                SafeSegment(date + "_" + eventName), SafeSegment(kind)));
        }
    }
}
