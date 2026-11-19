using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Yamuse.Tools
{
    public sealed class ToolProcess : IDisposable
    {
        private const int HandleListAttribute = 0x00020002;
        private const int JobListAttribute = 0x0002000D;
        private const int CleanupTimeoutMs = 5000;
        private SafeFileHandle job;
        private SafeFileHandle process;
        private AnonymousPipeServerStream outputPipe;
        private AnonymousPipeServerStream errorPipe;
        private bool jobDrained;

        public StreamReader StandardOutput { get; private set; }
        public StreamReader StandardError { get; private set; }

        public void Start(ProcessStartInfo start)
        {
            using (var inputPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable))
            {
                outputPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                errorPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                job = CreateJobObjectW(IntPtr.Zero, null);
                Check(!job.IsInvalid, "CreateJobObjectW");
                var limits = new JobExtendedLimits();
                limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(JobExtendedLimits))),
                    "SetInformationJobObject(KILL_ON_JOB_CLOSE)");

                var attributeBytes = UIntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref attributeBytes);
                Check(attributeBytes != UIntPtr.Zero, "InitializeProcThreadAttributeList(size)");
                IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, jobs = IntPtr.Zero, environment = IntPtr.Zero;
                var initialized = false;
                try
                {
                    attributes = Marshal.AllocHGlobal(checked((int)attributeBytes.ToUInt64()));
                    Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref attributeBytes),
                        "InitializeProcThreadAttributeList");
                    initialized = true;

                    var input = inputPipe.ClientSafePipeHandle.DangerousGetHandle();
                    var output = outputPipe.ClientSafePipeHandle.DangerousGetHandle();
                    var error = errorPipe.ClientSafePipeHandle.DangerousGetHandle();
                    handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                    Marshal.WriteIntPtr(handles, input);
                    Marshal.WriteIntPtr(handles, IntPtr.Size, output);
                    Marshal.WriteIntPtr(handles, IntPtr.Size * 2, error);
                    Check(UpdateProcThreadAttribute(attributes, 0, new IntPtr(HandleListAttribute), handles,
                        (UIntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero), "UpdateProcThreadAttribute(HANDLE_LIST)");

                    // Windows 10 creates the child in this unnamed job before it can run.
                    // The job handle itself is neither inheritable nor in the child handle list.
                    jobs = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
                    Check(UpdateProcThreadAttribute(attributes, 0, new IntPtr(JobListAttribute), jobs,
                        (UIntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), "UpdateProcThreadAttribute(JOB_LIST)");

                    var startup = new StartupInfoEx();
                    startup.Info.Size = Marshal.SizeOf(typeof(StartupInfoEx));
                    startup.Info.Flags = 0x100; // STARTF_USESTDHANDLES
                    startup.Info.StandardInput = input;
                    startup.Info.StandardOutput = output;
                    startup.Info.StandardError = error;
                    startup.Attributes = attributes;
                    var command = new StringBuilder("\"" + start.FileName + "\" " + start.Arguments);
                    environment = Marshal.StringToHGlobalUni(EnvironmentBlock(start.EnvironmentVariables));
                    ProcessInformation created;
                    Check(CreateProcessW(start.FileName, command, IntPtr.Zero, IntPtr.Zero, true,
                        0x08000000 | 0x00080000 | 0x00000400, environment, start.WorkingDirectory, ref startup, out created),
                        "CreateProcessW"); // NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | UNICODE_ENVIRONMENT
                    process = new SafeFileHandle(created.Process, true);
                    CloseHandle(created.Thread);
                }
                finally
                {
                    if (initialized) DeleteProcThreadAttributeList(attributes);
                    Marshal.FreeHGlobal(attributes);
                    Marshal.FreeHGlobal(handles);
                    Marshal.FreeHGlobal(jobs);
                    Marshal.FreeHGlobal(environment);
                    outputPipe.DisposeLocalCopyOfClientHandle();
                    errorPipe.DisposeLocalCopyOfClientHandle();
                }
            } // The unattended tools receive EOF on stdin.
            // The pinned SDK writes redirected output as UTF-8.
            StandardOutput = new StreamReader(outputPipe, Encoding.UTF8);
            StandardError = new StreamReader(errorPipe, Encoding.UTF8);
        }

        public bool WaitForExit(uint milliseconds)
        {
            var result = WaitForSingleObject(process, milliseconds);
            Check(result != uint.MaxValue, "WaitForSingleObject(tool process)");
            return result == 0;
        }

        public int ReadExitCode()
        {
            uint code;
            Check(GetExitCodeProcess(process, out code), "GetExitCodeProcess");
            return unchecked((int)code);
        }

        public void DrainJob()
        {
            if (process == null || jobDrained) return;
            if (ActiveProcesses() != 0)
            {
                Check(TerminateJobObject(job, 125), "TerminateJobObject(owned tool processes)");
                var clock = Stopwatch.StartNew();
                while (ActiveProcesses() != 0)
                {
                    if (clock.ElapsedMilliseconds >= CleanupTimeoutMs)
                        throw new IOException("Owned tool processes remained active after cancellation.");
                    Thread.Sleep(20);
                }
            }
            if (!WaitForExit(CleanupTimeoutMs))
                throw new IOException("The owned tool process did not exit during cleanup.");
            jobDrained = true;
        }

        public void Dispose()
        {
            try
            {
                DrainJob();
            }
            finally
            {
                // KILL_ON_JOB_CLOSE is the fallback if accounting or the explicit wait fails.
                if (job != null) job.Dispose();
                if (process != null) process.Dispose();
                if (StandardOutput != null) StandardOutput.Dispose();
                if (StandardError != null) StandardError.Dispose();
                if (outputPipe != null) outputPipe.Dispose();
                if (errorPipe != null) errorPipe.Dispose();
            }
        }

        private uint ActiveProcesses()
        {
            JobAccounting accounting;
            Check(QueryInformationJobObject(job, 1, out accounting, (uint)Marshal.SizeOf(typeof(JobAccounting)),
                IntPtr.Zero), "QueryInformationJobObject(accounting)");
            return accounting.ActiveProcesses;
        }

        private static string EnvironmentBlock(StringDictionary values)
        {
            var names = new string[values.Count];
            values.Keys.CopyTo(names, 0);
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            var block = new StringBuilder();
            foreach (var name in names)
                block.Append(name).Append('=').Append(values[name]).Append('\0');
            return block.Append('\0').ToString();
        }

        private static void Check(bool condition, string operation)
        {
            if (!condition) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            internal int Size;
            internal string Reserved, Desktop, Title;
            internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            internal short ShowWindow, ReservedSize;
            internal IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx { internal StartupInfo Info; internal IntPtr Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobBasicLimits
        {
            internal long PerProcessTime, PerJobTime;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobExtendedLimits
        {
            internal JobBasicLimits Basic;
            internal ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
            internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobAccounting
        {
            internal long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
            internal uint TotalPageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObjectW(IntPtr security, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobExtendedLimits info, uint bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out JobAccounting info, uint bytes, IntPtr returned);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref UIntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
            UIntPtr size, IntPtr previous, IntPtr returned);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity,
            IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup,
            out ProcessInformation process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

