using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace YMusic.Native;

internal sealed class Diagnostics : IDisposable
{
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
    public string? AutoReportPath { get; private set; }
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
    public void Save(string path)
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
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    public string SaveAuto(string directory)
    {
        AutoReportPath ??= Path.Combine(directory, "diagnostics", $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.json");
        Save(AutoReportPath);
        return AutoReportPath;
    }
    public void Dispose() => _process.Dispose();
}

internal sealed record ResourceSample(double ElapsedMs, long WorkingSetBytes, long PrivateBytes, double CpuPercent, string Stage);
