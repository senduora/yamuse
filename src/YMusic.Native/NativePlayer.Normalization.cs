using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace YMusic.Native;

internal sealed partial class NativePlayer
{
    private bool _normalizeVolume;
    private LoudnessCache? _loudnessCache;
    private DispatcherTimer? _gainTimer;
    internal bool NormalizeVolume => _normalizeVolume;
    internal double CurrentNormalizationGain => _current?.Gain ?? 1;
    internal bool IsAnalyzingLoudness => _current?.NormalizationTask is { IsCompleted: false };

    internal void SetNormalization(bool enabled)
    {
        _normalizeVolume = enabled;
        foreach (var slot in new[] { _current, _prepared, _fading }.OfType<Slot>().Distinct())
        {
            slot.SetGain(enabled ? slot.MeasuredGain : 1);
            if (enabled) BeginNormalization(slot);
        }
        StartGainTimer();
    }
    private void StartGainTimer()
    {
        if (_disposed) return;
        _gainTimer ??= CreateGainTimer();
        if (new[] { _current, _fading }.OfType<Slot>().Any(slot => slot.GainChanging)) _gainTimer.Start();
        else { _gainTimer.Stop(); ApplyGains(); }
    }
    private DispatcherTimer CreateGainTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { ApplyGains(); StartGainTimer(); };
        return timer;
    }
    private void BeginNormalization(Slot slot)
    {
        if (!_normalizeVolume || !slot.Ready || slot.NormalizationTask is not null || slot.Closed) return;
        _loudnessCache ??= new LoudnessCache(Path.Combine(_buffer.DirectoryPath, "loudness"));
        slot.NormalizationTask = AnalyzeAsync();
        async Task AnalyzeAsync()
        {
            try
            {
                if (slot.Input is { } input) await input.Completion.WaitAsync(slot.NormalizationCancellation.Token);
                var path = slot.Input?.Path ?? (slot.OpenedSource?.IsFile == true ? slot.OpenedSource.LocalPath : null);
                if (path is null) return;
                var result = await Task.Run(() => _loudnessCache.MeasureAsync(path, slot.NormalizationCancellation.Token), slot.NormalizationCancellation.Token);
                if (slot.Closed) return;
                slot.MeasuredGain = LoudnessAnalysis.Gain(result);
                if (_normalizeVolume) slot.SetGain(slot.MeasuredGain);
                StartGainTimer();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or MusicException or System.Net.Http.HttpRequestException or System.Runtime.InteropServices.COMException) { }
        }
    }
}
