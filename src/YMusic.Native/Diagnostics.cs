using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace YMusic.Native;

internal sealed class Diagnostics : IDisposable
{
    internal sealed class ReplacementUnavailableException(Exception innerException, string message =
        "Том не поддерживает безопасную замену отчёта. Выберите новое имя или другое расположение. Прежний отчёт сохранён.")
        : IOException(message, innerException);

    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastElapsed;
    private double _lastCpu;
    private readonly Queue<ResourceSample> _samples = new();
    private readonly Dictionary<string, double> _timings = new();
    public double WindowReadyMs { get; private set; }
    public string Stage { get; set; } = "signed_out";
    public int LoadedTracks { get; set; }
    public int TotalTracks { get; set; }
    public string? LastErrorType { get; set; }
    public ResourceSample Latest { get; private set; } = new(0, 0, 0, 0, "signed_out");
    public void WindowReady()
    {
        WindowReadyMs = (DateTime.UtcNow - _process.StartTime.ToUniversalTime()).TotalMilliseconds;
        Timing("process_to_content_rendered_ms", WindowReadyMs);
    }
    public void Timing(string name, double milliseconds) => _timings[name] = Math.Round(milliseconds, 1);
    public ResourceSample Sample()
    {
        _process.Refresh();
        var elapsed = _clock.Elapsed.TotalMilliseconds;
        var cpu = _process.TotalProcessorTime.TotalMilliseconds;
        var percent = _lastElapsed > 0 ? Math.Max(0, (cpu - _lastCpu) / (elapsed - _lastElapsed) / Environment.ProcessorCount * 100) : 0;
        _lastElapsed = elapsed;
        _lastCpu = cpu;
        Latest = new(Math.Round(elapsed), _process.WorkingSet64, _process.PrivateMemorySize64, Math.Round(percent, 3), Stage);
        _samples.Enqueue(Latest);
        while (_samples.Count > 900) _samples.Dequeue();
        return Latest;
    }
    internal Action? BeforeReportPublicationForChecks { get; set; }
    internal Action? CheckReportCapabilitiesForChecks { get; set; }

    public bool Save(string path, Func<bool>? confirmReplacement = null)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        using var storage = GuardedStoreFile.BeginWrite(path, allowNetworkShare: true);
        if (storage.Exists && confirmReplacement?.Invoke() != true) return false;
        var bytes = BuildReport();
        if (!storage.Exists) return SaveNewReport(storage, directory, path, bytes);
        try
        {
            using var stage = storage.CreateStage(TemporaryReport(directory));
            CheckReportCapabilitiesForChecks?.Invoke();
            RandomAccess.Write(stage, bytes, 0);
            RandomAccess.FlushToDisk(stage);
            storage.AfterParkForChecks = BeforeReportPublicationForChecks;
            return storage.Commit(stage);
        }
        catch (FileSecurityPreservation.UnsupportedMetadataException error)
        {
            var reason = error.Metadata == FileSecurityPreservation.ReplacementMetadata.NamedStreams
                ? "При замене нельзя безопасно перенести именованные потоки файла."
                : "При замене нельзя безопасно сохранить специальные свойства файла.";
            throw new ReplacementUnavailableException(error, $"{reason} Выберите новое имя или другое расположение. Прежний отчёт сохранён.");
        }
        catch (Exception error) when (error is GuardedStoreRecovery.ReplacementCapabilityException
            || FileSecurityPreservation.IsUnsupportedCapability(error))
        {
            throw new ReplacementUnavailableException(error);
        }
    }

    private bool SaveNewReport(GuardedStoreFile.WriteTransaction storage, string directory, string path, byte[] bytes)
    {
        var beforePublish = BeforeReportPublicationForChecks;
        try
        {
            using var stage = storage.CreateStage(TemporaryReport(directory));
            CheckReportCapabilitiesForChecks?.Invoke();
            RandomAccess.Write(stage, bytes, 0);
            RandomAccess.FlushToDisk(stage);
            beforePublish?.Invoke();
            beforePublish = null;
            ManagedCacheCleanup.RenameByHandle(stage, path);
            UninstallPathGuard.KeepFileOnClose(stage);
            return true;
        }
        catch (Exception error) when (FileSecurityPreservation.IsUnsupportedCapability(error))
        {
            // The strict stage has closed. Use classic disposition for a new
            // report on this volume. Rename still cannot replace another file.
            try
            {
                using var stage = UninstallPathGuard.CreateStagedFile(TemporaryReport(directory), writeThrough: true);
                var published = false;
                try
                {
                    UninstallPathGuard.SetDeleteDisposition(stage, delete: true);
                    FileSecurityPreservation.CheckNewReportMetadata(stage);
                    RandomAccess.Write(stage, bytes, 0);
                    RandomAccess.FlushToDisk(stage);
                    beforePublish?.Invoke();
                    ManagedCacheCleanup.RenameByHandle(stage, path, releaseDeleteDisposition: true);
                    published = true;
                    return true;
                }
                finally { if (!published) UninstallPathGuard.DiscardStagedFile(stage); }
            }
            catch (Win32Exception failure) { throw new IOException("Не удалось сохранить новый отчёт в выбранной папке.", failure); }
        }
        catch (Win32Exception error) { throw new IOException("Не удалось сохранить новый отчёт в выбранной папке.", error); }
    }

    private static string TemporaryReport(string directory) => Path.Combine(directory, $".yamuse-report-{Guid.NewGuid():N}.tmp");

    private byte[] BuildReport()
    {
        Sample();
        var executable = Environment.ProcessPath!;
        var report = new
        {
            schema = 1, appVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown", utc = DateTimeOffset.UtcNow,
            executableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))).ToLowerInvariant(),
            os = RuntimeInformation.OSDescription, runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), logicalProcessors = Environment.ProcessorCount,
            processCount = 1, processId = Environment.ProcessId,
            stage = Stage, loadedTracks = LoadedTracks, totalTracks = TotalTracks,
            windowReadyMs = Math.Round(WindowReadyMs, 1), timings = _timings,
            lastErrorType = LastErrorType, samples = _samples.ToArray(),
            notes = new[] { "Private bytes is process commit, not physical RAM.", "CPU is percent of all logical processors.", "ContentRendered is window readiness, not account/library readiness.", "No tokens, account details, track titles, or media URLs are saved." }
        };
        return JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
    }

    public string SaveAuto(string directory)
    {
        var path = Path.Combine(directory, "diagnostics", $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        using var report = GuardedStoreFile.BeginWrite(path);
        if (report.Exists) throw new IOException("Имя автоматического отчёта уже используется.");
        report.Write(BuildReport());
        return path;
    }
    public void Dispose() => _process.Dispose();
}

internal sealed record ResourceSample(double ElapsedMs, long WorkingSetBytes, long PrivateBytes, double CpuPercent, string Stage);
