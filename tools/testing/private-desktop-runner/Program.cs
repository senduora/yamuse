using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed record Configuration(string Executable, string[] Arguments, string WorkingDirectory,
    string LogPath, string ReceiptPath, int TimeoutSeconds = 1800);

internal static class Program
{
    private const uint DesktopAccess = 0x0001 | 0x0002 | 0x0004 | 0x0080;
    private const uint WaitTimeout = 258;
    private const int HandleListAttribute = 0x00020002;
    private const int JobListAttribute = 0x0002000D;
    private static readonly IntPtr InvalidHandle = new(-1);

    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Usage: PrivateDesktopRunner.exe <configuration.json>"); return 125; }
        Configuration? config = null;
        FileStream? receiptFile = null;
        var receipt = new Dictionary<string, object?>
        {
            ["startedUtc"] = DateTimeOffset.UtcNow, ["launcherPid"] = Environment.ProcessId,
            ["desktopAccessMask"] = "0x00000087", ["switchDesktopApiPresent"] = false,
            ["setThreadDesktopApiPresent"] = false, ["screenOrInputApisPresent"] = false,
            ["childCreated"] = false, ["childResumed"] = false, ["childStopped"] = true,
            ["desktopClosed"] = false, ["jobKillOnClose"] = false,
            ["stdio"] = "Only whitelisted inheritable handles: NUL read, combined log write; no console window.",
            ["status"] = "ERROR"
        };
        IntPtr desktop = IntPtr.Zero, job = IntPtr.Zero, output = InvalidHandle, input = InvalidHandle;
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, jobs = IntPtr.Zero;
        Native.ProcessInformation process = default;
        bool attributesInitialized = false, processRunning = false;
        int result = 125;
        var watch = Stopwatch.StartNew();
        try
        {
            var requested = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(args[0]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Missing runner configuration.");
            config = Validate(requested);
            Directory.CreateDirectory(Path.GetDirectoryName(config.ReceiptPath)!);
            receiptFile = new FileStream(config.ReceiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
                throw new PlatformNotSupportedException("A creation-time job list requires Windows 10 build 10240 or newer.");
            Directory.CreateDirectory(Path.GetDirectoryName(config.LogPath)!);
            receipt["executable"] = config.Executable;
            receipt["executableSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(config.Executable)));
            var runnerAssembly = typeof(Program).Assembly.Location;
            receipt["runnerAssemblySha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(runnerAssembly)));
            var companion = Path.ChangeExtension(config.Executable, ".dll");
            if (File.Exists(companion)) receipt["executableCompanionDllSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(companion)));
            receipt["arguments"] = config.Arguments;
            receipt["workingDirectory"] = config.WorkingDirectory;
            receipt["logPath"] = config.LogPath;
            receipt["timeoutSeconds"] = config.TimeoutSeconds;
            receipt["windowStation"] = Name(Native.GetProcessWindowStation());
            if (!string.Equals((string?)receipt["windowStation"], "WinSta0", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Launcher is not on WinSta0; no station rebinding is permitted.");
            var parentBefore = CurrentDesktop(); var inputBefore = InputDesktop();
            receipt["launcherDesktopBefore"] = parentBefore; receipt["inputDesktopBefore"] = inputBefore;
            var desktopName = "YamuseChecks_" + Guid.NewGuid().ToString("N");
            receipt["privateDesktopName"] = desktopName;
            desktop = Native.CreateDesktopW(desktopName, null, IntPtr.Zero, 0, DesktopAccess, IntPtr.Zero);
            Check(desktop != IntPtr.Zero, "CreateDesktopW");
            if (InputDesktop().Equals(desktopName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Private desktop unexpectedly receives input; launch refused.");
            receipt["privateDesktopInactiveBeforeLaunch"] = true;
            receipt["launcherDesktopAfterCreate"] = CurrentDesktop();

            var security = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = true };
            output = Native.CreateFileW(config.LogPath, 0x40000000, 0x00000001, ref security, 1, 0x80, IntPtr.Zero); // CREATE_NEW
            Check(output != InvalidHandle, "CreateFileW(log)");
            input = Native.CreateFileW("NUL", 0x80000000, 0x00000003, ref security, 3, 0x80, IntPtr.Zero);
            Check(input != InvalidHandle, "CreateFileW(NUL)");

            job = Native.CreateJobObjectW(IntPtr.Zero, null); Check(job != IntPtr.Zero, "CreateJobObjectW");
            var limits = new Native.JobExtendedLimitInformation(); limits.BasicLimitInformation.LimitFlags = 0x00002000;
            Check(Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.JobExtendedLimitInformation>()), "SetInformationJobObject(KILL_ON_JOB_CLOSE)");
            receipt["jobKillOnClose"] = true;

            nuint attributeSize = 0;
            _ = Native.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref attributeSize);
            Check(attributeSize > 0, "InitializeProcThreadAttributeList(size)");
            attributes = Marshal.AllocHGlobal(checked((int)attributeSize));
            Check(Native.InitializeProcThreadAttributeList(attributes, 2, 0, ref attributeSize), "InitializeProcThreadAttributeList");
            attributesInitialized = true;
            handles = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(handles, 0, input); Marshal.WriteIntPtr(handles, IntPtr.Size, output);
            Check(Native.UpdateProcThreadAttribute(attributes, 0, new IntPtr(HandleListAttribute), handles,
                (nuint)(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero), "UpdateProcThreadAttribute(HANDLE_LIST)");
            jobs = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobs, job);
            Check(Native.UpdateProcThreadAttribute(attributes, 0, new IntPtr(JobListAttribute), jobs,
                (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), "UpdateProcThreadAttribute(JOB_LIST)");

            var startup = new Native.StartupInfoEx();
            startup.StartupInfo.Size = Marshal.SizeOf<Native.StartupInfoEx>();
            startup.StartupInfo.Desktop = "WinSta0\\" + desktopName;
            startup.StartupInfo.Flags = 0x00000100 | 0x00000080; // std handles and no feedback cursor; preserve child Window.Show semantics
            startup.StartupInfo.ShowWindow = 0;
            startup.StartupInfo.StandardInput = input; startup.StartupInfo.StandardOutput = output; startup.StartupInfo.StandardError = output;
            startup.AttributeList = attributes;
            var command = string.Join(" ", new[] { config.Executable }.Concat(config.Arguments).Select(Quote));
            if (command.Length >= 32767) throw new ArgumentException("Child command line exceeds the Win32 Unicode limit.");
            receipt["encodedCommandLine"] = command;
            Check(Native.CreateProcessW(config.Executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true,
                0x00080000 | 0x08000000 | 0x00000004, IntPtr.Zero, config.WorkingDirectory, ref startup, out process), "CreateProcessW");
            processRunning = true; receipt["childCreated"] = true; receipt["childStopped"] = false;
            receipt["childPid"] = process.ProcessId; receipt["childMainThreadId"] = process.ThreadId;
            Check(Native.IsProcessInJob(process.Process, job, out var inOwnJob), "IsProcessInJob");
            if (!inOwnJob) throw new InvalidOperationException("The child was not created in the owned job.");
            receipt["childAssignedToOwnJob"] = true;
            receipt["childJobAssignedAtCreation"] = true;
            receipt["childDesktopBeforeResume"] = TryThreadDesktop(process.ThreadId);
            Check(Native.ResumeThread(process.Thread) != uint.MaxValue, "ResumeThread");
            receipt["childResumed"] = true;
            var childClock = Stopwatch.StartNew();
            uint wait;
            do
            {
                wait = Native.WaitForSingleObject(process.Process, 250);
                if (wait == uint.MaxValue) throw Error("WaitForSingleObject");
                if (wait != WaitTimeout) break;
                if (InputDesktop().Equals(desktopName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Private desktop became the input desktop; terminating only the owned job.");
                if (childClock.Elapsed.TotalSeconds >= config.TimeoutSeconds)
                {
                    receipt["timedOut"] = true;
                    Check(Native.TerminateJobObject(job, 124), "TerminateJobObject(timeout)");
                    Check(Native.WaitForSingleObject(process.Process, 10000) == 0, "WaitForSingleObject(timeout cleanup)");
                    break;
                }
            } while (true);
            Check(Native.GetExitCodeProcess(process.Process, out var childExit), "GetExitCodeProcess");
            processRunning = false; receipt["childStopped"] = true; receipt["childExitCode"] = childExit;
            receipt["childElapsedMs"] = childClock.ElapsedMilliseconds;
            var timedOut = receipt.ContainsKey("timedOut");
            result = timedOut ? 124 : unchecked((int)childExit);
            receipt["launcherDesktopAfterChild"] = CurrentDesktop(); receipt["inputDesktopAfterChild"] = InputDesktop();
            receipt["inputDesktopNameUnchanged"] = Equals(receipt["inputDesktopAfterChild"], inputBefore);
            receipt["launcherDesktopNameUnchanged"] = Equals(receipt["launcherDesktopAfterChild"], parentBefore);
            if (!Equals(receipt["inputDesktopNameUnchanged"], true) || !Equals(receipt["launcherDesktopNameUnchanged"], true))
                throw new InvalidOperationException("The input or launcher desktop changed during the run.");
            receipt["status"] = timedOut ? "TIMEOUT" : result == 0 ? "PASS" : "CHILD_FAILED";
        }
        catch (Exception error)
        {
            result = 125;
            receipt["errorType"] = error.GetType().Name; receipt["error"] = error.Message;
            if (error is Win32Exception native) receipt["win32Error"] = native.NativeErrorCode;
            Console.Error.WriteLine(error.Message);
        }
        finally
        {
            if (processRunning && process.Process != IntPtr.Zero)
            {
                // Both handles are our own CreateProcess result. Never locate or
                // terminate any process by name, arbitrary PID or product install.
                bool terminated = receipt.ContainsKey("childAssignedToOwnJob") && job != IntPtr.Zero && Native.TerminateJobObject(job, 125);
                if (Native.WaitForSingleObject(process.Process, 0) != 0)
                    terminated = Native.TerminateProcess(process.Process, 125) || terminated;
                receipt["ownedChildEmergencyTermination"] = terminated;
                receipt["childStopped"] = Native.WaitForSingleObject(process.Process, 10000) == 0;
            }
            if (job != IntPtr.Zero && receipt.ContainsKey("childAssignedToOwnJob") &&
                !VerifyJobCleanup(receipt, () => ReadActiveJobProcesses(job), () => Native.TerminateJobObject(job, 125)))
            {
                result = 125;
                receipt["status"] = "CLEANUP_FAILED";
            }
            Close(process.Thread); Close(process.Process);
            if (attributesInitialized) Native.DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
            Close(job); // Also removes any remaining descendants of our child.
            Close(input); Close(output);
            if (desktop != IntPtr.Zero)
            {
                var closed = Native.CloseDesktop(desktop); receipt["desktopClosed"] = closed;
                if (!closed) { receipt["desktopCloseWin32Error"] = Marshal.GetLastWin32Error(); result = 125; receipt["status"] = "CLEANUP_FAILED"; }
            }
            if (receiptFile is not null)
            {
                try
                {
                    if (output != InvalidHandle)
                        receipt["logSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(config!.LogPath)));
                }
                catch (Exception error)
                {
                    result = 125;
                    if (!Equals(receipt["status"], "CLEANUP_FAILED")) receipt["status"] = "ERROR";
                    receipt["logHashError"] = error.Message;
                    Console.Error.WriteLine("Log read failed: " + error.Message);
                }
                receipt["finishedUtc"] = DateTimeOffset.UtcNow;
                receipt["elapsedMs"] = watch.ElapsedMilliseconds;
                receipt["runnerExitCode"] = result;
                try
                {
                    using (receiptFile)
                        JsonSerializer.Serialize(receiptFile, receipt, new JsonSerializerOptions { WriteIndented = true });
                }
                catch (Exception error) { Console.Error.WriteLine("Receipt write failed: " + error.Message); result = 125; }
            }
        }
        return result;
    }

    private static (uint? Active, int Error) ReadActiveJobProcesses(IntPtr job)
    {
        var size = (uint)Marshal.SizeOf<Native.JobBasicAccountingInformation>();
        if (Native.QueryInformationJobObject(job, 1, out var accounting, size, IntPtr.Zero))
            return (accounting.ActiveProcesses, 0);
        return (null, Marshal.GetLastWin32Error());
    }

    private static bool VerifyJobCleanup(Dictionary<string, object?> receipt, Func<(uint? Active, int Error)> query, Action terminate)
    {
        var snapshot = query();
        if (snapshot.Active is not uint active)
        {
            receipt["jobQueryWin32Error"] = snapshot.Error;
            return false;
        }
        receipt["ownJobActiveProcessesBeforeCleanup"] = active;
        if (active != 0)
        {
            terminate();
            var cleanup = Stopwatch.StartNew();
            do
            {
                snapshot = query();
                if (snapshot.Active is not uint checkedCount)
                {
                    receipt["jobQueryWin32Error"] = snapshot.Error;
                    return false;
                }
                active = checkedCount;
                if (active == 0) break;
                Thread.Sleep(25);
            } while (cleanup.ElapsedMilliseconds < 10000);
        }
        receipt["ownJobActiveProcessesAfterCleanup"] = active;
        return active == 0;
    }

    private static Configuration Validate(Configuration c)
    {
        foreach (var path in new[] { c.Executable, c.WorkingDirectory, c.LogPath, c.ReceiptPath })
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("All runner paths must be absolute.");
        if (!File.Exists(c.Executable)) throw new FileNotFoundException("Child executable is missing.", c.Executable);
        if (!Directory.Exists(c.WorkingDirectory)) throw new DirectoryNotFoundException(c.WorkingDirectory);
        if (c.Arguments is null || c.Arguments.Any(argument => argument is null || argument.Contains('\0')))
            throw new ArgumentException("Arguments must be a non-null string array without NUL characters.");
        if (c.TimeoutSeconds is < 1 or > 86400) throw new ArgumentOutOfRangeException(nameof(c.TimeoutSeconds));
        c = c with
        {
            Executable = Path.GetFullPath(c.Executable), WorkingDirectory = Path.GetFullPath(c.WorkingDirectory),
            LogPath = Path.GetFullPath(c.LogPath), ReceiptPath = Path.GetFullPath(c.ReceiptPath)
        };
        if (new[] { c.Executable, c.LogPath, c.ReceiptPath }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw new ArgumentException("Executable, log and receipt paths must differ.");
        if (c.LogPath.StartsWith(c.ReceiptPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || c.ReceiptPath.StartsWith(c.LogPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The log and receipt cannot be nested.");
        ValidateNewOutputPath(c.LogPath);
        ValidateNewOutputPath(c.ReceiptPath);
        return c;
    }

    private static void ValidateNewOutputPath(string path)
    {
        var root = Path.GetPathRoot(path)!;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || string.IsNullOrEmpty(Path.GetFileName(path)) || path.AsSpan(root.Length).Contains(':'))
            throw new ArgumentException("The log and receipt must use ordinary file paths.");

        var components = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(component => component.EndsWith(' ') || component.EndsWith('.') ||
            System.Text.RegularExpressions.Regex.IsMatch(component, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            throw new ArgumentException("The log and receipt must use ordinary file paths.");
        var current = root;
        for (var i = -1; i < components.Length; i++)
        {
            if (i >= 0) current = Path.Combine(current, components[i]);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) when (i >= 0) { return; }
            catch (DirectoryNotFoundException) when (i >= 0) { return; }
            if (i == components.Length - 1)
                throw new IOException("Runner output already exists: " + path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("Runner output parent is not an ordinary directory: " + current);
        }
    }

    // Windows CRT/CommandLineToArgvW quoting: each argument is quoted; backslashes
    // preceding a quote or the final quote must be doubled, including empty args.
    private static string Quote(string value)
    {
        var text = new StringBuilder("\""); int slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') { text.Append('\\', slashes * 2 + 1); text.Append('"'); }
            else { text.Append('\\', slashes); text.Append(character); }
            slashes = 0;
        }
        text.Append('\\', slashes * 2); text.Append('"'); return text.ToString();
    }
    private static string Name(IntPtr handle)
    {
        Check(handle != IntPtr.Zero, "Desktop/window station handle");
        var value = new StringBuilder(512);
        Check(Native.GetUserObjectInformationW(handle, 2, value, value.Capacity * 2, out _), "GetUserObjectInformationW(UOI_NAME)");
        return value.ToString();
    }
    private static string CurrentDesktop() => Name(Native.GetThreadDesktop(Native.GetCurrentThreadId()));
    private static string InputDesktop()
    {
        var handle = Native.OpenInputDesktop(0, false, 0x0001); Check(handle != IntPtr.Zero, "OpenInputDesktop(READOBJECTS)");
        try { return Name(handle); } finally { _ = Native.CloseDesktop(handle); }
    }
    private static string? TryThreadDesktop(uint thread) { var handle = Native.GetThreadDesktop(thread); return handle == IntPtr.Zero ? null : Name(handle); }
    private static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation);
    private static void Check(bool condition, string operation) { if (!condition) throw Error(operation); }
    private static void Close(IntPtr handle) { if (handle != IntPtr.Zero && handle != InvalidHandle) _ = Native.CloseHandle(handle); }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
        {
            internal int Size; internal string? Reserved, Desktop, Title;
            internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            internal short ShowWindow, ReservedSize; internal IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { internal StartupInfo StartupInfo; internal IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] internal struct JobBasicLimitInformation
        { internal long PerProcessUserTimeLimit, PerJobUserTimeLimit; internal uint LimitFlags; internal nuint MinimumWorkingSetSize, MaximumWorkingSetSize; internal uint ActiveProcessLimit; internal nuint Affinity; internal uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] internal struct JobExtendedLimitInformation
        { internal JobBasicLimitInformation BasicLimitInformation; internal IoCounters IoInfo; internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [StructLayout(LayoutKind.Sequential)] internal struct JobBasicAccountingInformation
        { internal long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime; internal uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateDesktopW(string name, string? device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetProcessWindowStation();
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder value, int bytes, out int needed);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateFileW(string path, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateJobObjectW(IntPtr security, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobExtendedLimitInformation info, uint bytes);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out JobBasicAccountingInformation info, uint bytes, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
    }
}
