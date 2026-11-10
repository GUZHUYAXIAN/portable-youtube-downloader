using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PortableYouTube
{
    public sealed class MainForm : Form
    {
        readonly string root = AppDomain.CurrentDomain.BaseDirectory;
        readonly TextBox links = new TextBox { Name = "Links", AccessibleName = "视频链接", Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
        readonly TextBox output = new TextBox { Name = "Output", AccessibleName = "保存位置" };
        readonly TextBox proxy = new TextBox { Name = "Proxy", AccessibleName = "可选代理" };
        readonly ComboBox quality = new ComboBox { Name = "Quality", AccessibleName = "最高清晰度", DropDownStyle = ComboBoxStyle.DropDownList };
        readonly CheckBox playlist = new CheckBox { Name = "Playlist", Text = "允许 YouTube 播放列表 / 频道", AutoSize = true };
        readonly CheckBox editing = new CheckBox { Name = "Editing", Text = "剪辑兼容 · H.264 / AAC（可能降低到可用画质）", AutoSize = true, Checked = true };
        readonly Button start = new Button { Name = "Start", Text = "开始下载", AccessibleName = "开始下载" };
        readonly Button cancel = new Button { Name = "Cancel", Text = "取消下载", AccessibleName = "取消下载", Enabled = false };
        readonly Button browse = new Button { Text = "选择文件夹…", AccessibleName = "选择文件夹" };
        readonly Button open = new Button { Text = "打开保存位置", AccessibleName = "打开保存位置" };
        readonly Label status = new Label { Name = "Status", Text = "准备就绪 · 粘贴链接即可开始", AutoEllipsis = true };
        readonly Label detail = new Label { Name = "ProgressDetail", Text = "下载完成后自动合并，并检查音频和视频。", AutoEllipsis = true };
        readonly ProgressBar progress = new ProgressBar { Name = "Progress", Minimum = 0, Maximum = 100 };
        readonly TextBox log = new TextBox { Name = "Log", AccessibleName = "运行日志", ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false };
        DownloadSession session;
        bool busy, closeWhenDone;
        int currentLink;
        readonly Color accent = Color.FromArgb(34, 92, 178);

        public MainForm()
        {
            SuspendLayout();
            Text = "NMIXX 素材下载器 · Windows 开发版";
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(860, 830);
            MinimumSize = new Size(820, 800);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 247, 251);
            Icon = SystemIcons.Application;
            Padding = new Padding(24, 18, 24, 18);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 14 };
            int[] heights = { 43, 27, 108, 27, 40, 42, 30, 30, 44, 48, 34, 26, 24 };
            foreach (int h in heights) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            var title = new Label { Text = "把喜欢的舞台，留给下一次剪辑。", Font = new Font(Font.FontFamily, 20F, FontStyle.Bold), ForeColor = Color.FromArgb(26, 41, 65), Dock = DockStyle.Fill };
            layout.Controls.Add(title, 0, 0);
            layout.Controls.Add(MakeLabel("YouTube / X / Instagram 视频帖子 · 每行一条"), 0, 1);
            links.Dock = DockStyle.Fill;
            links.Margin = new Padding(0, 0, 0, 8);
            links.BackColor = Color.White;
            layout.Controls.Add(links, 0, 2);
            layout.Controls.Add(MakeLabel("保存位置"), 0, 3);
            var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            output.Text = Path.Combine(root, "Downloads");
            output.Dock = DockStyle.Fill; output.Margin = new Padding(0, 3, 9, 0);
            browse.Dock = DockStyle.Fill; browse.Margin = new Padding(0, 0, 0, 6);
            folderRow.Controls.Add(output, 0, 0); folderRow.Controls.Add(browse, 1, 0);
            layout.Controls.Add(folderRow, 0, 4);

            var optionsRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            var qualityLabel = MakeLabel("最高清晰度"); qualityLabel.AutoSize = true; qualityLabel.Margin = new Padding(0, 8, 12, 0);
            quality.Items.AddRange(new object[] { "最佳可用画质", "2160p / 4K", "1080p", "720p", "480p" });
            quality.SelectedIndex = 0; quality.Width = 172; quality.Margin = new Padding(0, 3, 20, 0);
            playlist.Margin = new Padding(0, 7, 0, 0);
            optionsRow.Controls.Add(qualityLabel); optionsRow.Controls.Add(quality); optionsRow.Controls.Add(playlist);
            layout.Controls.Add(optionsRow, 0, 5);

            editing.Margin = new Padding(0, 2, 0, 0);
            layout.Controls.Add(editing, 0, 6);
            var limits = MakeLabel("仅下载公开且有权保存的视频；X / INS 登录限制会直接报告，不读取浏览器登录数据。");
            limits.Font = new Font(Font.FontFamily, 9F);
            layout.Controls.Add(limits, 0, 7);
            var proxyRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            proxyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 178));
            proxyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var proxyLabel = MakeLabel("代理（可选，留空用系统）"); proxyLabel.Font = new Font(Font.FontFamily, 9F); proxyLabel.Padding = new Padding(0, 7, 0, 0);
            proxy.Dock = DockStyle.Fill; proxy.Margin = new Padding(0, 3, 0, 0);
            proxyRow.Controls.Add(proxyLabel, 0, 0); proxyRow.Controls.Add(proxy, 1, 0);
            layout.Controls.Add(proxyRow, 0, 8);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            foreach (var button in new[] { start, cancel, open }) {
                button.Size = new Size(142, 39); button.Margin = new Padding(0, 0, 10, 0);
                button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = Color.FromArgb(204, 213, 224);
                button.BackColor = Color.White; buttons.Controls.Add(button);
            }
            start.BackColor = accent; start.ForeColor = Color.White; start.FlatAppearance.BorderColor = accent;
            layout.Controls.Add(buttons, 0, 9);
            status.Dock = DockStyle.Fill; status.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold); status.Padding = new Padding(0, 7, 0, 0);
            layout.Controls.Add(status, 0, 10);
            progress.Dock = DockStyle.Fill; progress.Margin = new Padding(0, 3, 0, 6); layout.Controls.Add(progress, 0, 11);
            detail.Dock = DockStyle.Fill; detail.ForeColor = Color.FromArgb(88, 104, 125); detail.Font = new Font(Font.FontFamily, 9F);
            layout.Controls.Add(detail, 0, 12);
            log.Dock = DockStyle.Fill; log.Margin = new Padding(0, 3, 0, 0); log.Font = new Font("Microsoft YaHei UI", 9F); log.BackColor = Color.White; layout.Controls.Add(log, 0, 13);

            start.Click += delegate { StartDownload(); };
            cancel.Click += delegate { CancelDownload(); };
            browse.Click += delegate {
                using (var dialog = new FolderBrowserDialog { Description = "选择下载文件的保存位置", SelectedPath = output.Text, ShowNewFolderButton = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = dialog.SelectedPath;
            };
            open.Click += delegate {
                try {
                    string directory = Path.GetFullPath(output.Text);
                    if (!Directory.Exists(directory)) throw new IOException("保存位置尚不存在，请先选择已有文件夹或开始下载。");
                    Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
                } catch (Exception e) { ShowFailure(e.Message); }
            };
            FormClosing += OnClosing;
            ResumeLayout(true);
        }

        static Label MakeLabel(string text) { return new Label { Text = text, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(65, 79, 100) }; }

        void SetBusy(bool value)
        {
            busy = value;
            foreach (Control c in new Control[] { links, output, proxy, quality, playlist, editing, browse, start }) c.Enabled = !value;
            cancel.Enabled = value;
        }

        void StartDownload()
        {
            if (busy) return;
            var options = new DownloadOptions {
                UrlText = links.Text, OutputDirectory = output.Text.Trim(), Proxy = proxy.Text.Trim(),
                Height = new[] { 0, 2160, 1080, 720, 480 }[quality.SelectedIndex], Playlist = playlist.Checked, EditingCompatible = editing.Checked
            };
            try { DownloadCommand.Build(root, options); }
            catch (Exception e) { ShowFailure(e.Message); return; }
            log.Clear(); currentLink = 0; progress.Value = 0; progress.Style = ProgressBarStyle.Marquee;
            status.ForeColor = accent; status.Text = "正在准备下载…";
            detail.Text = "正在解析链接；下载流的 100% 不代表整批任务完成。";
            SetBusy(true);
            var active = new DownloadSession(root); session = active;
            Task.Run(delegate {
                DownloadResult result = null;
                Exception error = null;
                try { result = active.Run(options, update => Post(delegate { ApplyUpdate(update); })); }
                catch (Exception e) { error = e; }
                finally { active.Dispose(); }
                Post(delegate {
                    session = null;
                    SetBusy(false); progress.Style = ProgressBarStyle.Continuous;
                    if (error != null) ShowFailure(error.Message);
                    else {
                        status.Text = UiMessages.Summary(result.Cancelled, result.Succeeded, result.Failed, result.Files.Count);
                        status.ForeColor = result.Cancelled ? Color.FromArgb(125, 87, 28) : result.Failed > 0 ? Color.Firebrick : Color.FromArgb(26, 111, 75);
                        if (!result.Cancelled && result.Failed == 0 && result.Files.Count > 0) progress.Value = 100;
                        detail.Text = result.Cancelled ? "已停止下载和合并进程。再次下载可继续未完成部分。" : result.Failed > 0 ? "请查看下方错误；检查链接、网络或代理后可以重试。" : "MP4 已通过音频与视频检查，可以打开保存位置查看。";
                        foreach (string message in result.Errors) AppendLog("提示：" + message);
                        AppendLog(status.Text);
                    }
                    if (closeWhenDone) Close();
                });
            });
        }

        void ApplyUpdate(DownloadUpdate update)
        {
            if (!busy || closeWhenDone) return;
            if (!string.IsNullOrEmpty(update.Message)) AppendLog(update.Message);
            if (session != null && cancel.Enabled) {
                if (currentLink != update.LinkIndex) {
                    currentLink = update.LinkIndex;
                    progress.Value = 0; progress.Style = ProgressBarStyle.Marquee;
                    detail.Text = "正在解析链接，等待下载进度…";
                }
                status.Text = "正在处理第 " + update.LinkIndex + " / " + update.LinkCount + " 条链接";
                if (update.Percent.HasValue) {
                    progress.Style = ProgressBarStyle.Continuous; progress.Value = Math.Max(0, Math.Min(100, update.Percent.Value));
                    detail.Text = "当前下载流 " + progress.Value + "% · " + (progress.Value == 100 ? "继续等待音频、合并及检查…" : "完成后将自动合并并检查。");
                } else if (!string.IsNullOrEmpty(update.FilePath)) {
                    progress.Style = ProgressBarStyle.Marquee;
                    detail.Text = "正在处理文件：" + Path.GetFileName(update.FilePath);
                }
            }
        }

        void CancelDownload()
        {
            if (!busy || session == null) return;
            cancel.Enabled = false; status.Text = "正在取消，请稍候…"; detail.Text = "正在停止下载、合并和检查进程。";
            session.Cancel();
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!busy) return;
            e.Cancel = true; closeWhenDone = true; CancelDownload();
        }

        void AppendLog(string text)
        {
            if (log.TextLength > 110000) log.Text = log.Text.Substring(log.TextLength - 75000);
            log.AppendText(text + Environment.NewLine);
        }

        void ShowFailure(string message)
        {
            status.ForeColor = Color.Firebrick; status.Text = "无法开始或完成下载";
            detail.Text = "请根据下方提示调整后重试。";
            AppendLog("错误：" + message);
        }

        void Post(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); } catch (InvalidOperationException) { }
        }
    }
}
