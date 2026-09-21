using System;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("油管下载器便携版")]
[assembly: AssemblyDescription("便携 YouTube MP4 下载器")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace PortableYouTube
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) {
                MessageBox.Show("操作未完成：" + e.Exception.Message, "油管下载器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new MainForm());
        }
    }
}
