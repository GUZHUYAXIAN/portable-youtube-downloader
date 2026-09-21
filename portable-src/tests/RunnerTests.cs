using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PortableYouTube;

class RunnerTests
{
    static string node;
    static ProcessStartInfo Node(string code) {
        return new ProcessStartInfo(node, "-e " + DownloadCommand.Quote(code)) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
    }
    static int Main(string[] args)
    {
        TestEnvironment.Initialize(args.Length <= 1);
        node = args.Length > 0 ? args[0] : TestEnvironment.NodePath;
        if (args.Length > 1 && args[1] == "--job-owner") {
            using (var runner = new ProcessRunner())
                return runner.Run(Node("let c=require('child_process').spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:'ignore'});console.log('PARENT='+process.pid);console.log('CHILD='+c.pid);setInterval(()=>{},1000)"), Console.WriteLine);
        }
        try {
            using (var runner = new ProcessRunner()) {
                var lines = new List<string>();
                int code = runner.Run(Node("console.log('中文输出'); console.error('失败原因'); process.exitCode=7"), s => { lock (lines) lines.Add(s); });
                if (code != 7 || !lines.Contains("中文输出") || !lines.Contains("失败原因")) throw new Exception("Exit code or Unicode output lost");
                Console.WriteLine("PASS Preserve nonzero exit code and both UTF-8 streams");
            }
            using (var runner = new ProcessRunner()) {
                runner.Cancel();
                int code = runner.Run(Node("process.exit(0)"), s => {});
                if (!runner.Cancelled || code == 0) throw new Exception("Cancel-before-start was ignored");
                Console.WriteLine("PASS Cancel before process launch");
            }
            using (var runner = new ProcessRunner())
            using (var ready = new ManualResetEventSlim()) {
                int child = 0;
                var task = Task.Run(() => runner.Run(Node("let c=require('child_process').spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:'ignore'}); console.log('CHILD='+c.pid); setInterval(()=>{},1000)"), s => {
                    if (s.StartsWith("CHILD=")) { child = int.Parse(s.Substring(6)); ready.Set(); }
                }));
                if (!ready.Wait(10000)) throw new Exception("Child never started");
                runner.Cancel();
                if (!task.Wait(5000) || !runner.Cancelled) throw new Exception("Cancellation hung");
                Thread.Sleep(200);
                try { using (var p = Process.GetProcessById(child)) { if (!p.HasExited) throw new Exception("Child process survived cancel"); } }
                catch (ArgumentException) { }
                Console.WriteLine("PASS Cancel terminates the process and its descendants");
            }
            using (var runner = new ProcessRunner()) {
                int stdout = 0, stderr = 0;
                var task = Task.Run(() => runner.Run(Node("for(let i=0;i<6000;i++){console.log('OUT='+i+'汉字');console.error('ERR='+i+'汉字');}process.stdout.write('TAIL');"), s => {
                    if (s.StartsWith("OUT=")) stdout++;
                    if (s.StartsWith("ERR=")) stderr++;
                    if (s == "TAIL") stdout++;
                }));
                if (!task.Wait(15000)) throw new Exception("Full output pipes caused a deadlock");
                if (task.Result != 0 || stdout != 6001 || stderr != 6000) throw new Exception("Buffered or unterminated output lost");
                Console.WriteLine("PASS Drain both full pipes and the final unterminated line");
            }
            using (var runner = new ProcessRunner()) {
                for (int i = 0; i < 20; i++) {
                    int code = runner.Run(Node("process.exit(0)"), s => {});
                    if (code != 0 || runner.Cancelled) throw new Exception("Fast exit or sequential reuse failed");
                }
                Console.WriteLine("PASS Fast exits and sequential runs retain correct state");
            }
            using (var runner = new ProcessRunner()) {
                var info = Node("console.log(process.cwd());console.log(process.env.PORTABLE_RUNNER_TEST);");
                info.WorkingDirectory = Path.GetTempPath();
                info.EnvironmentVariables["PORTABLE_RUNNER_TEST"] = "环境 & value";
                var lines = new List<string>();
                int code = runner.Run(info, lines.Add);
                if (code != 0 || !lines.Contains("环境 & value") ||
                    !lines.Any(s => string.Equals(s.TrimEnd('\\'), Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("Custom environment or working directory was ignored");
                Console.WriteLine("PASS Preserve working directory and Unicode environment");
            }
            using (var runner = new ProcessRunner()) {
                bool failed = false;
                var info = Node(""); info.FileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe");
                try { runner.Run(info, s => {}); } catch (System.ComponentModel.Win32Exception) { failed = true; }
                if (!failed || runner.Run(Node("process.exit(0)"), s => {}) != 0) throw new Exception("Failed launch leaked active state");
                Console.WriteLine("PASS Failed launch cleans up and permits retry");
            }
            using (var runner = new ProcessRunner()) {
                int pid = 0;
                var expected = new InvalidOperationException("log handler failure");
                Exception actual = null;
                var task = Task.Run(() => {
                    try { runner.Run(Node("console.log(process.pid);setInterval(()=>{},1000)"), s => { pid = int.Parse(s); throw expected; }); }
                    catch (Exception error) { actual = error; }
                });
                if (!task.Wait(5000)) throw new Exception("Throwing log callback hung the runner");
                if (actual == null || (actual != expected && actual.InnerException != expected)) throw new Exception("Log callback failure was lost");
                AssertExited(pid);
                if (runner.Cancelled) throw new Exception("Callback failure was misreported as user cancellation");
                Console.WriteLine("PASS Callback errors stop the process and propagate safely");
            }
            using (var runner = new ProcessRunner()) {
                int child = 0;
                var task = Task.Run(() => runner.Run(Node("let c=require('child_process').spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:'inherit'});console.log('CHILD='+c.pid);process.exit(0)"), s => {
                    if (s.StartsWith("CHILD=")) child = int.Parse(s.Substring(6));
                }));
                if (!task.Wait(5000) || task.Result != 0) throw new Exception("Orphaned child's pipe prevented completion");
                AssertExited(child);
                Console.WriteLine("PASS Parent completion cleans up descendants holding inherited output pipes");
            }
            using (var runner = new ProcessRunner())
            using (var ready = new ManualResetEventSlim()) {
                int pid = 0;
                var task = Task.Run(() => runner.Run(Node("console.log(process.pid);setInterval(()=>{},1000)"), s => { pid = int.Parse(s); ready.Set(); }));
                if (!ready.Wait(5000)) throw new Exception("Disposable process did not launch");
                bool refused = false;
                try { runner.Run(Node("process.exit(0)"), s => {}); } catch (InvalidOperationException) { refused = true; }
                if (!refused) throw new Exception("Concurrent Run replaced the active process");
                runner.Dispose(); runner.Dispose();
                if (!task.Wait(5000) || task.Result == 0 || !runner.Cancelled) throw new Exception("Dispose failed to cancel an active run");
                AssertExited(pid);
                bool disposed = false;
                try { runner.Run(Node("process.exit(0)"), s => {}); } catch (ObjectDisposedException) { disposed = true; }
                if (!disposed) throw new Exception("Disposed runner launched another process");
                Console.WriteLine("PASS Reject concurrent runs and dispose an active run safely");
            }
            for (int i = 0; i < 20; i++) {
                using (var runner = new ProcessRunner()) {
                    var task = Task.Run(() => runner.Run(Node("setInterval(()=>{},1000)"), s => {}));
                    runner.Cancel();
                    if (!task.Wait(5000) || task.Result == 0 || !runner.Cancelled) throw new Exception("Cancellation racing with launch was lost");
                }
            }
            Console.WriteLine("PASS Cancellation racing with launch always completes");
            using (var ready = new ManualResetEventSlim())
            using (var owner = new Process()) {
                int parent = 0, child = 0;
                owner.StartInfo = new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,
                    DownloadCommand.Quote(node) + " --job-owner") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                owner.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data == null) return;
                    if (e.Data.StartsWith("PARENT=")) parent = int.Parse(e.Data.Substring(7));
                    if (e.Data.StartsWith("CHILD=")) child = int.Parse(e.Data.Substring(6));
                    if (parent > 0 && child > 0) ready.Set();
                };
                owner.Start(); owner.BeginOutputReadLine();
                try {
                    if (!ready.Wait(10000)) throw new Exception("Independent job owner failed to start descendants");
                    owner.Kill();
                    if (!owner.WaitForExit(5000)) throw new Exception("Independent job owner did not terminate");
                    AssertExited(parent); AssertExited(child);
                }
                finally {
                    if (!owner.HasExited) owner.Kill();
                    CleanupProcess(parent); CleanupProcess(child);
                }
                Console.WriteLine("PASS Abrupt owner termination also kills the entire process tree");
            }
            return 0;
        } catch (Exception e) { Console.WriteLine("FAIL " + e); return 1; }
    }

    static void AssertExited(int pid)
    {
        if (pid <= 0) throw new Exception("Child process id was not captured");
        try {
            using (var process = Process.GetProcessById(pid)) {
                if (!process.WaitForExit(2000)) throw new Exception("Process survived cleanup: " + pid);
            }
        } catch (ArgumentException) { }
    }

    static void CleanupProcess(int pid)
    {
        if (pid <= 0) return;
        try { using (var process = Process.GetProcessById(pid)) { if (!process.HasExited) process.Kill(); } }
        catch (ArgumentException) { }
    }
}
