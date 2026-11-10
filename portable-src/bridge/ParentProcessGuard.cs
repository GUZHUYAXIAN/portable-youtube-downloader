using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace PortableYouTube.StageBridge
{
    // The PowerShell wrapper owns this process. A terminated wrapper must not leave
    // a downloader running unattended; Windows closes the engine's kill-on-close job.
    public sealed class ParentProcessGuard : IDisposable
    {
        readonly Process parent;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly Thread monitor;

        ParentProcessGuard(int parentId)
        {
            if (parentId == Process.GetCurrentProcess().Id) throw new ArgumentException("父进程 PID 不能指向桥接器自身。");
            parent = Process.GetProcessById(parentId);
            try {
                // Acquire the handle before starting any work, so a later PID reuse
                // cannot turn a terminated parent into an unrelated live process.
                if (parent.Handle == IntPtr.Zero || parent.HasExited) throw new InvalidOperationException("指定的父进程已经退出。");
                monitor = new Thread(WatchParent) { IsBackground = true, Name = "StageBridge parent guard" };
                monitor.Start();
            } catch {
                parent.Dispose(); stop.Dispose();
                throw;
            }
        }

        public static ParentProcessGuard FromEnvironment()
        {
            string value = Environment.GetEnvironmentVariable("STAGE_BRIDGE_PARENT_PID");
            if (value == null) return null;
            int parentId;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parentId) || parentId <= 0)
                throw new ArgumentException("STAGE_BRIDGE_PARENT_PID 必须是仍在运行的父进程的正整数 PID。");
            return new ParentProcessGuard(parentId);
        }

        void WatchParent()
        {
            while (!stop.WaitOne(0)) {
                if (!parent.WaitForExit(200)) continue;
                if (!stop.WaitOne(0)) Environment.Exit(130);
                return;
            }
        }

        public void Dispose()
        {
            stop.Set();
            monitor.Join();
            parent.Dispose();
            stop.Dispose();
        }
    }
}
