using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PortableYouTube
{
    public sealed class ProcessRunner : IDisposable
    {
        private readonly object stateLock = new object();
        private SafeFileHandle job;
        private bool cancelled, disposed, running;

        public bool Cancelled { get { lock (stateLock) return cancelled; } }

        // Synchronous caller API; both UTF-8 output streams are drained concurrently.
        // The callback is serialized, must return promptly, and may call Cancel/Dispose.
        public int Run(ProcessStartInfo info, Action<string> log)
        {
            if (info == null) throw new ArgumentNullException("info");
            if (log == null) throw new ArgumentNullException("log");
            if (info.UseShellExecute) throw new ArgumentException("ProcessRunner requires UseShellExecute=false.", "info");
            lock (stateLock)
            {
                if (disposed) throw new ObjectDisposedException("ProcessRunner");
                if (running) throw new InvalidOperationException("A process is already running.");
                if (cancelled) return -1;
                running = true;
            }

            SafeFileHandle process = null;
            StreamReader stdout = null, stderr = null;
            Thread outputThread = null, errorThread = null;
            object outputLock = new object();
            Exception outputError = null;
            try
            {
                lock (stateLock)
                {
                    if (cancelled) return -1;
                    job = CreateJob();
                    process = StartProcess(info, job, out stdout, out stderr);
                }

                Action<StreamReader> read = delegate(StreamReader reader)
                {
                    try
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            lock (outputLock)
                            {
                                if (outputError == null) log(line);
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        lock (outputLock) { if (outputError == null) outputError = error; }
                        // A failed consumer must not leave a writer blocked on a full pipe.
                        CloseJob();
                    }
                };
                outputThread = new Thread(delegate() { read(stdout); }) { IsBackground = true };
                errorThread = new Thread(delegate() { read(stderr); }) { IsBackground = true };
                outputThread.Start();
                errorThread.Start();

                if (WaitForSingleObject(process.DangerousGetHandle(), uint.MaxValue) != 0)
                    throw NativeError("等待下载进程失败");
                uint exitCode;
                if (!GetExitCodeProcess(process.DangerousGetHandle(), out exitCode))
                    throw NativeError("读取下载进程退出码失败");

                // A child can outlive its parent while retaining an output pipe. Closing the
                // job first kills such descendants and lets both readers reach EOF.
                CloseJob();
                outputThread.Join();
                errorThread.Join();
                if (outputError != null) throw new InvalidOperationException("处理下载日志时发生错误。", outputError);
                return Cancelled ? -1 : unchecked((int)exitCode);
            }
            finally
            {
                CloseJob();
                if (process != null && !process.IsInvalid)
                    WaitForSingleObject(process.DangerousGetHandle(), uint.MaxValue);
                JoinIfStarted(outputThread);
                JoinIfStarted(errorThread);
                if (stdout != null) stdout.Dispose();
                if (stderr != null) stderr.Dispose();
                if (process != null) process.Dispose();
                lock (stateLock) running = false;
            }
        }

        public void Cancel()
        {
            lock (stateLock)
            {
                cancelled = true;
                CloseJob();
            }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed) return;
                disposed = true;
                if (running) cancelled = true;
                CloseJob();
            }
        }

        private void CloseJob()
        {
            lock (stateLock)
            {
                if (job == null) return;
                // This is the only handle to an unnamed, non-inheritable job. Its
                // KILL_ON_JOB_CLOSE policy also applies if the GUI crashes or is killed.
                job.Dispose();
                job = null;
            }
        }

        private static void JoinIfStarted(Thread thread)
        {
            if (thread != null && (thread.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
                thread.Join();
        }

        private static SafeFileHandle CreateJob()
        {
            SafeFileHandle handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid) { handle.Dispose(); throw NativeError("创建下载进程组失败"); }
            try
            {
                var limits = new JobExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(limits)))
                    throw NativeError("设置下载进程组失败");
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }

        private static SafeFileHandle StartProcess(ProcessStartInfo info, SafeFileHandle jobHandle,
            out StreamReader stdout, out StreamReader stderr)
        {
            stdout = null; stderr = null;
            SafeFileHandle outputRead = null, outputWrite = null, errorRead = null, errorWrite = null, input = null;
            IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, jobs = IntPtr.Zero, environment = IntPtr.Zero;
            bool attributesInitialized = false;
            ProcessInformation nativeProcess = new ProcessInformation();
            try
            {
                var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), InheritHandle = true };
                if (!CreatePipe(out outputRead, out outputWrite, ref security, 0) ||
                    !CreatePipe(out errorRead, out errorWrite, ref security, 0))
                    throw NativeError("创建下载输出管道失败");
                if (!SetHandleInformation(outputRead, 1, 0) || !SetHandleInformation(errorRead, 1, 0))
                    throw NativeError("设置下载输出管道失败");
                input = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
                if (input.IsInvalid) throw NativeError("创建下载标准输入失败");

                IntPtr bytes = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref bytes);
                if (bytes == IntPtr.Zero) throw NativeError("初始化下载进程属性失败");
                attributes = Marshal.AllocHGlobal(bytes);
                if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref bytes))
                    throw NativeError("初始化下载进程属性失败");
                attributesInitialized = true;

                // Limit handle inheritance to these three streams. The job handle must
                // never be inherited, otherwise closing the GUI cannot terminate it.
                handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                Marshal.WriteIntPtr(handles, 0, input.DangerousGetHandle());
                Marshal.WriteIntPtr(handles, IntPtr.Size, outputWrite.DangerousGetHandle());
                Marshal.WriteIntPtr(handles, IntPtr.Size * 2, errorWrite.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), handles,
                    new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                    throw NativeError("设置下载进程句柄失败");

                // Windows 10+: associate the process atomically during creation. This
                // eliminates both the early-child race and orphaned suspended processes.
                jobs = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(jobs, jobHandle.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x2000D), jobs,
                    new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                    throw NativeError("设置下载进程组失败（需要 Windows 10 或更新版本）");

                var startup = new StartupInfoEx();
                startup.StartupInfo.Size = Marshal.SizeOf(typeof(StartupInfoEx));
                startup.StartupInfo.Flags = 0x100; // STARTF_USESTDHANDLES
                startup.StartupInfo.StandardInput = input.DangerousGetHandle();
                startup.StartupInfo.StandardOutput = outputWrite.DangerousGetHandle();
                startup.StartupInfo.StandardError = errorWrite.DangerousGetHandle();
                startup.AttributeList = attributes;
                environment = Marshal.StringToHGlobalUni(BuildEnvironment(info));
                var command = new StringBuilder(DownloadCommand.Quote(info.FileName) + " " + info.Arguments);
                uint flags = 0x80000 | 0x400; // EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT
                if (info.CreateNoWindow) flags |= 0x8000000;
                if (!CreateProcess(info.FileName, command, IntPtr.Zero, IntPtr.Zero, true, flags,
                    environment, string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory,
                    ref startup, out nativeProcess))
                    throw NativeError("无法启动下载工具");

                stdout = new StreamReader(new FileStream(outputRead, FileAccess.Read, 4096, false), Encoding.UTF8, true);
                outputRead = null; // owned by the FileStream
                stderr = new StreamReader(new FileStream(errorRead, FileAccess.Read, 4096, false), Encoding.UTF8, true);
                errorRead = null;
                var process = new SafeFileHandle(nativeProcess.Process, true);
                nativeProcess.Process = IntPtr.Zero;
                return process;
            }
            finally
            {
                if (nativeProcess.Thread != IntPtr.Zero) CloseHandle(nativeProcess.Thread);
                if (nativeProcess.Process != IntPtr.Zero) CloseHandle(nativeProcess.Process);
                if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
                if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                if (input != null) input.Dispose();
                if (outputRead != null) outputRead.Dispose();
                if (errorRead != null) errorRead.Dispose();
                if (outputWrite != null) outputWrite.Dispose();
                if (errorWrite != null) errorWrite.Dispose();
            }
        }

        private static string BuildEnvironment(ProcessStartInfo info)
        {
            var names = new List<string>();
            foreach (string name in info.EnvironmentVariables.Keys) names.Add(name);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            var block = new StringBuilder();
            foreach (string name in names)
                block.Append(name).Append('=').Append(info.EnvironmentVariables[name]).Append('\0');
            // An empty block still needs two terminating null characters.
            if (names.Count == 0) block.Append('\0');
            return block.Append('\0').ToString();
        }

        private static Win32Exception NativeError(string message)
        {
            int error = Marshal.GetLastWin32Error();
            return new Win32Exception(error, message + "：" + new Win32Exception(error).Message);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved, Desktop, Title;
            public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, ReservedSize;
            public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)]
        private struct JobBasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct JobExtendedLimitInformation
        {
            public JobBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr security, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
            ref JobExtendedLimitInformation information, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes security,
            uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute,
            IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity,
            IntPtr threadSecurity, bool inheritHandles, uint flags, IntPtr environment, string directory,
            ref StartupInfoEx startup, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
