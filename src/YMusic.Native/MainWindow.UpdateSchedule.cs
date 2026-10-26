using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace YMusic.Native;

internal enum UpdateCheckFrequency { Startup, Hourly, Daily, Weekly, Monthly, Off }

internal sealed class UpdateCheckFrequencyConverter : JsonConverter<UpdateCheckFrequency>
{
    public override UpdateCheckFrequency Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<UpdateCheckFrequency>(reader.GetString(), true, out var value)
            && Enum.IsDefined(value)) return value;
        using var ignored = JsonDocument.ParseValue(ref reader);
        return UpdateCheckFrequency.Startup;
    }
    public override void Write(Utf8JsonWriter writer, UpdateCheckFrequency value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

internal sealed record UpdateScheduleCheckpoint(DateTimeOffset? LastAttemptUtc = null, DateTimeOffset? LastSuccessUtc = null);

internal static class UpdateSchedulePolicy
{
    internal static UpdateScheduleCheckpoint Normalize(UpdateScheduleCheckpoint value, DateTimeOffset now)
    {
        DateTimeOffset? Bounded(DateTimeOffset? date) => date is null || date < DateTimeOffset.UnixEpoch ? null : date > now ? now : date.Value.ToUniversalTime();
        return new(Bounded(value.LastAttemptUtc), Bounded(value.LastSuccessUtc));
    }
    internal static DateTimeOffset? NextDue(UpdateCheckFrequency frequency, UpdateScheduleCheckpoint checkpoint, DateTimeOffset now)
    {
        if (frequency is UpdateCheckFrequency.Off or UpdateCheckFrequency.Startup || !Enum.IsDefined(frequency)) return null;
        var previous = Normalize(checkpoint, now).LastAttemptUtc;
        if (previous is null) return now;
        // A month is a calendar month in UTC, rather than thirty days.
        try
        {
            return frequency switch
            {
                UpdateCheckFrequency.Hourly => previous.Value.AddHours(1),
                UpdateCheckFrequency.Daily => previous.Value.AddDays(1),
                UpdateCheckFrequency.Weekly => previous.Value.AddDays(7),
                UpdateCheckFrequency.Monthly => previous.Value.AddMonths(1),
                _ => null
            };
        }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.MaxValue; }
    }
}

// The checkpoint is separate from settings: a timer must never commit an open
// settings draft or rewrite playback preferences. It contains no account data.
internal sealed class UpdateScheduleStore
{
    internal string FilePath { get; }
    internal UpdateScheduleStore(string directory) => FilePath = Path.Combine(Path.GetFullPath(directory), "update-schedule.json");
    internal UpdateScheduleCheckpoint Load(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 4096) return new();
            var value = JsonSerializer.Deserialize<UpdateScheduleCheckpoint>(stream, new JsonSerializerOptions { MaxDepth = 4 });
            return UpdateSchedulePolicy.Normalize(value ?? new(), now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save(UpdateScheduleCheckpoint checkpoint)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".update-schedule-{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
        finally { try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}

public partial class MainWindow
{
    private UpdateCheckFrequency _updateCheckFrequency = UpdateCheckFrequency.Startup;
    private UpdateScheduleStore? _updateScheduleStore;
    private UpdateScheduleCheckpoint _updateCheckpoint = new();
    private Func<DateTimeOffset> _updateClock = static () => DateTimeOffset.UtcNow;
    private DispatcherTimer? _updateScheduleTimer;
    private bool _updateScheduleInitialized, _updateCheckInProgress;
    private ComboBox? _updateFrequencyCombo;
    private Action<Uri>? _updateLinkLauncher = null;
    private static readonly Uri ProjectUri = new("https://github.com/senduora/yamuse");
    private static readonly Uri ReleasesUri = new("https://github.com/senduora/yamuse/releases");

    private void InitializeUpdateSchedule()
    {
        if (_updateScheduleInitialized || _closed) return;
        _updateScheduleInitialized = true;
        if (_preferenceStore is not null) _updateScheduleStore ??= new(Path.GetDirectoryName(_preferenceStore.FilePath)!);
        _updateCheckpoint = _updateScheduleStore?.Load(_updateClock()) ?? new();
        // Commit a future-clock reset once so reopening the app cannot keep
        // moving the next attempt farther into the future.
        if (_updateCheckpoint.LastAttemptUtc is not null || _updateCheckpoint.LastSuccessUtc is not null) PersistUpdateCheckpoint();
        Closed += CloseUpdateSchedule;
    }

    private void CloseUpdateSchedule(object? sender, EventArgs e)
    {
        if (_updateScheduleTimer is not null)
        { _updateScheduleTimer.Stop(); _updateScheduleTimer.Tick -= UpdateSchedule_Tick; _updateScheduleTimer = null; }
        Closed -= CloseUpdateSchedule;
    }

    private void PersistUpdateCheckpoint()
    {
        try { _updateScheduleStore?.Save(_updateCheckpoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _diagnostics.LastErrorType = "UpdateScheduleSave:" + ex.GetType().Name; }
    }

    private void ArmUpdateSchedule()
    {
        _updateScheduleTimer?.Stop();
        if (!_updateScheduleInitialized || _closed || _lifetime.IsCancellationRequested || !AppUpdates.State.FeedConfigured) return;
        var now = _updateClock();
        var normalized = UpdateSchedulePolicy.Normalize(_updateCheckpoint, now);
        if (normalized != _updateCheckpoint) { _updateCheckpoint = normalized; PersistUpdateCheckpoint(); }
        var next = UpdateSchedulePolicy.NextDue(_updateCheckFrequency, _updateCheckpoint, now);
        if (next is null) return;
        if (_updateScheduleTimer is null)
        {
            _updateScheduleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _updateScheduleTimer.Tick += UpdateSchedule_Tick;
        }
        // Reassess the wall clock at least hourly after clock changes or resume;
        // no per-frame timer, busy loop, or callback survives the window.
        var delay = next.Value - now;
        _updateScheduleTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds,
            _updateCheckInProgress || _appUpdateActionInProgress ? 30000 : 1000, 3600000));
        _updateScheduleTimer.Start();
    }

    private async void UpdateSchedule_Tick(object? sender, EventArgs e)
    {
        _updateScheduleTimer?.Stop();
        await RunScheduledUpdateCheckAsync();
    }

    private async Task RunScheduledUpdateCheckAsync()
    {
        if (_closed || _lifetime.IsCancellationRequested) return;
        var now = _updateClock();
        var next = UpdateSchedulePolicy.NextDue(_updateCheckFrequency, _updateCheckpoint, now);
        if (next is not null && next <= now && !_appUpdateActionInProgress && !_updateCheckInProgress)
            await CheckAppUpdateNowAsync(AppUpdates);
        ArmUpdateSchedule();
    }

    private async Task CheckAppUpdateNowAsync(UpdateSupport updates)
    {
        if (_closed || _updateCheckInProgress || !updates.State.FeedConfigured || _lifetime.IsCancellationRequested) return;
        InitializeUpdateSchedule();
        _updateCheckInProgress = true;
        _updateCheckpoint = UpdateSchedulePolicy.Normalize(_updateCheckpoint, _updateClock()) with { LastAttemptUtc = _updateClock() };
        PersistUpdateCheckpoint();
        try
        {
            var pending = updates.CheckAsync(_lifetime.Token);
            RefreshAppUpdateUi();
            await pending;
            if (_closed || _lifetime.IsCancellationRequested) return;
            if (updates.State.Status is AppUpdateStatus.NoUpdate or AppUpdateStatus.Available or AppUpdateStatus.Downloaded)
            { _updateCheckpoint = _updateCheckpoint with { LastSuccessUtc = _updateClock() }; PersistUpdateCheckpoint(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _diagnostics.LastErrorType = "Updates:" + ex.GetType().Name; }
        finally
        {
            _updateCheckInProgress = false;
            if (!_closed) { RefreshAppUpdateUi(); ArmUpdateSchedule(); }
        }
    }

    private async void FooterUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _appUpdateActionInProgress || _updateCheckInProgress) return;
        if (AppUpdates.State.Status is AppUpdateStatus.Available or AppUpdateStatus.Downloaded)
        {
            if (!AppUpdates.State.IsInstalled) OpenUpdateLink(ReleasesUri);
            else { if (!_settingsOpen) Settings_Click(SettingsButton, e); SettingsUpdates.BringIntoView(); }
            return;
        }
        await CheckAppUpdateNowAsync(AppUpdates);
    }

    private void Github_Click(object sender, RoutedEventArgs e) => OpenUpdateLink(ProjectUri);
    private void Changelog_Click(object sender, RoutedEventArgs e) => OpenUpdateLink(ReleasesUri);
    private void OpenUpdateLink(Uri uri)
    {
        if (_closed || uri != ProjectUri && uri != ReleasesUri) return;
        try
        {
            if (_updateLinkLauncher is not null) _updateLinkLauncher(uri);
            else Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { SetStatus("Не удалось открыть страницу в браузере.", true); }
    }
}
