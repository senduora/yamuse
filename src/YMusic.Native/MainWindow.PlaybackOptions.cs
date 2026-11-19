using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private readonly SleepTimerState _sleepTimer = new();
    private readonly Stopwatch _sleepClock = new();
    private DispatcherTimer? _sleepTick, _audioOutputRefreshTimer;
    private ComboBox? _sleepTimerCombo, _audioOutputCombo;
    private TextBlock? _sleepTimerText, _audioOutputText;
    private Button? _audioOutputRefresh;
    private IAudioOutputDevices? _audioOutputDevices;
    private Task _audioOutputReady = Task.CompletedTask;
    private Task _activeAudioOutputChange = Task.CompletedTask;
    private string _preferredOutputDeviceId = "";
    private long? _sleepTrackOccurrence;
    private TrackRow? _sleepTrack;
    private int _sleepTimerRevision;
    private int? ActiveMinuteSleepRevision => _sleepTimer.Active && !_sleepTimer.AfterTrack ? _sleepTimerRevision : null;
    private bool _playbackOptionsInitializing, _audioOutputSwitching, _audioOutputUnavailable;
    private bool IsAudioOutputUnavailable => _audioOutputUnavailable || !_player.OutputRouteIsValid;
    private bool AudioOutputChangePending => !_audioOutputReady.IsCompleted || !_activeAudioOutputChange.IsCompleted;
    private bool _audioOutputFixture;
    private int _audioOutputReadRevision, _audioOutputListedRevision;
    private IReadOnlyList<AudioOutputDevice>? _listedAudioOutputs;
    internal Func<TimeSpan>? SleepElapsedForChecks { get; set; }

    private void InitializePlaybackOptions(IAudioOutputDevices? audioOutputDevices = null)
    {
        if (_audioOutputDevices is not null) return;
        _audioOutputFixture = audioOutputDevices is not null;
        _audioOutputDevices = audioOutputDevices ?? new AudioOutputDevices();
        CreatePlaybackOptionsView();
        _sleepTick = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _sleepTick.Tick += SleepTimer_Tick;
        if (_restore && !_demo || _audioOutputFixture)
        {
            _audioOutputDevices.Changed += AudioOutputDevices_Changed;
            TryWatchAudioOutputs();
            _audioOutputReady = RestoreAudioOutputAsync();
        }
    }

    private void TryWatchAudioOutputs()
    {
        if (_closed) return;
        try { _audioOutputDevices?.Watch(); }
        catch (Exception ex) { _diagnostics.LastErrorType = "AudioDevicesWatch:" + ex.GetType().Name; }
    }

    private void CreatePlaybackOptionsView()
    {
        if (FindName("PlaybackOptionsHost") is not ContentControl host) return;
        var panel = new StackPanel();
        var heading = new TextBlock { Text = "Таймер сна и аудиоустройство" };
        heading.SetResourceReference(StyleProperty, "SettingsHeading"); panel.Children.Add(heading);
        panel.Children.Add(new TextBlock { Text = "Остановить музыку", Margin = new(0, 0, 0, 9) });
        _sleepTimerCombo = new ComboBox { Name = "SleepTimerCombo", SelectedValuePath = nameof(SleepTimerChoice.Mode), DisplayMemberPath = nameof(SleepTimerChoice.Label),
            ItemsSource = new[] { new SleepTimerChoice(SleepTimerMode.Off, "Выключен"), new(SleepTimerMode.Minutes15, "Через 15 минут"),
                new(SleepTimerMode.Minutes30, "Через 30 минут"), new(SleepTimerMode.Minutes60, "Через 60 минут"),
                new(SleepTimerMode.AfterCurrentTrack, "После текущего трека") } };
        AutomationProperties.SetName(_sleepTimerCombo, "Таймер сна");
        _sleepTimerCombo.SelectedValue = SleepTimerMode.Off;
        _sleepTimerCombo.SelectionChanged += (_, _) =>
        {
            if (!_playbackOptionsInitializing && _sleepTimerCombo.SelectedValue is SleepTimerMode mode) SetSleepTimer(mode);
        };
        panel.Children.Add(_sleepTimerCombo);
        _sleepTimerText = new TextBlock { Name = "SleepTimerRemaining", Text = "Музыка плавно затихнет за 5 секунд и встанет на паузу.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 22) };
        _sleepTimerText.SetResourceReference(StyleProperty, "SettingsNote"); panel.Children.Add(_sleepTimerText);
        panel.Children.Add(new TextBlock { Text = "Устройство вывода", Margin = new(0, 0, 0, 9) });
        var row = new DockPanel();
        _audioOutputRefresh = new Button { Name = "AudioOutputRefresh", Content = "Обновить", Margin = new(10, 0, 0, 0) };
        _audioOutputRefresh.Click += async (_, _) =>
        {
            if (_restore && !_demo || _audioOutputFixture) TryWatchAudioOutputs();
            await RefreshAudioOutputsAsync();
        };
        DockPanel.SetDock(_audioOutputRefresh, Dock.Right); row.Children.Add(_audioOutputRefresh);
        _audioOutputCombo = new ComboBox { Name = "AudioOutputCombo", SelectedValuePath = nameof(AudioOutputDevice.Id), DisplayMemberPath = nameof(AudioOutputDevice.Name),
            ItemsSource = new[] { new AudioOutputDevice("", AudioOutputDevices.SystemDefaultLabel) }, SelectedValue = "" };
        AutomationProperties.SetName(_audioOutputCombo, "Устройство вывода звука");
        _audioOutputCombo.DropDownOpened += async (_, _) => await RefreshAudioOutputsAsync();
        _audioOutputCombo.SelectionChanged += async (_, _) =>
        {
            if (_playbackOptionsInitializing || _audioOutputSwitching || _audioOutputCombo.SelectedValue is not string id || id == _preferredOutputDeviceId) return;
            _audioOutputReady = ChangeAudioOutputAsync(id);
            await _audioOutputReady;
        };
        row.Children.Add(_audioOutputCombo); panel.Children.Add(row);
        _audioOutputText = new TextBlock { Name = "AudioOutputStatus", Text = "Используется для онлайн-музыки, скачанных треков и кроссфейда.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
        _audioOutputText.SetResourceReference(StyleProperty, "SettingsNote"); panel.Children.Add(_audioOutputText);
        host.Content = panel;
    }

    private sealed record SleepTimerChoice(SleepTimerMode Mode, string Label);

    internal void SetSleepTimer(SleepTimerMode mode)
    {
        if (_closed) return;
        if (mode == SleepTimerMode.AfterCurrentTrack && !_player.IsPlaying && PlaybackTransportActive)
        {
            FinishSleepTimer();
            return;
        }
        if (mode == SleepTimerMode.AfterCurrentTrack && (!_player.HasSource || _current is null))
        {
            SetStatus("Сначала запустите трек, затем включите таймер."); mode = SleepTimerMode.Off;
        }
        var canceledWaveStart = mode == SleepTimerMode.AfterCurrentTrack && CancelExplicitWaveStart();
        _sleepTimer.Start(mode); _sleepTimerRevision++; _sleepClock.Restart(); _player.SetSleepGain(1);
        _sleepTrackOccurrence = _playbackQueue.CurrentOccurrenceToken; _sleepTrack = _current;
        if (_sleepTimer.Active) _sleepTick?.Start(); else { _sleepTick?.Stop(); _sleepClock.Reset(); }
        if (_sleepTimer.AfterTrack) CancelNextPreparation();
        else RefreshNextPreparation();
        SyncSleepTimerView();
        if (canceledWaveStart) UpdateControls();
    }

    private void SleepTimer_Tick(object? sender, EventArgs e)
        => ApplySleepTimerElapsed(SleepElapsedForChecks?.Invoke() ?? _sleepClock.Elapsed);

    private void ApplySleepTimerElapsed(TimeSpan elapsed)
    {
        if (_closed || !_sleepTimer.Active) return;
        TimeSpan? remaining = _player.HasSource && _player.Duration > TimeSpan.Zero
            ? TimeSpan.FromSeconds(Math.Max(0, (_player.Duration - _player.Position).TotalSeconds / _player.SpeedRatio)) : null;
        var tick = _sleepTimer.Tick(elapsed, remaining);
        // Duration and position can reach the end before Windows delivers MediaEnded.
        // Keep the after-track stop armed until that event consumes it.
        if (tick.Completed && !_sleepTimer.AfterTrack) { FinishSleepTimer(); return; }
        _player.SetSleepGain(tick.Gain);
        SyncSleepTimerView(tick.Remaining);
    }

    private void FinishSleepTimer()
    {
        if (QueuePlaybackBeforeFirstPlay) RememberPausedDesktopOccurrence();
        if (_loadingStream) _playCts?.Cancel();
        _player.Pause();
        // Gain is independent of the slider/persisted volume and is restored only
        // after all audible slots have paused, including an in-progress crossfade.
        _player.SetSleepGain(1); _sleepTimer.Cancel(); _sleepClock.Reset(); _sleepTick?.Stop();
        CancelNextPreparation(); SavePlaybackState(); SetWavePaused(true); _progressTimer.Stop();
        _diagnostics.Stage = "paused"; UpdateControls(); UpdateProgress(); SyncSleepTimerView();
        SetStatus("Таймер сна: музыка на паузе.");
    }

    private void SyncSleepTimerView(TimeSpan? remaining = null)
    {
        if (_sleepTimerCombo is not null)
        {
            _playbackOptionsInitializing = true;
            try { _sleepTimerCombo.SelectedValue = _sleepTimer.Mode; }
            finally { _playbackOptionsInitializing = false; }
        }
        if (_sleepTimerText is not null) _sleepTimerText.Text = !_sleepTimer.Active
            ? "Музыка плавно затихнет за 5 секунд и встанет на паузу."
            : _sleepTimer.AfterTrack ? "Остановится после этого трека. Следующий трек не начнётся."
            : remaining is { } value ? $"Осталось {Math.Ceiling(value.TotalMinutes):0} мин · плавное затихание и пауза."
            : "Таймер включён · плавное затихание и пауза.";
    }

    private void PlaybackOptionsTrackChanged()
    {
        if (_sleepTimer.AfterTrack && (_sleepTrackOccurrence != _playbackQueue.CurrentOccurrenceToken || !ReferenceEquals(_sleepTrack, _current)))
            SetSleepTimer(SleepTimerMode.Off);
    }
    private void PlaybackOptionsPlaybackChanged()
    {
        if (_closed) return;
        if (_sleepTimer.Active) SleepTimer_Tick(null, EventArgs.Empty);
    }
    private bool CanAutoAdvancePlaybackOptions() => !_sleepTimer.AfterTrack;
    private bool HasMinuteSleepExpired()
        => _sleepTimer.Active && !_sleepTimer.AfterTrack
            && _sleepTimer.Tick(SleepElapsedForChecks?.Invoke() ?? _sleepClock.Elapsed, null).Completed;

    private bool HandlePlaybackOptionsTrackEnded()
    {
        if (!_sleepTimer.AfterTrack && !HasMinuteSleepExpired()) return false;
        ReportWaveEnd(true);
        FinishSleepTimer(); return true;
    }

    private async Task RestoreAudioOutputAsync()
    {
        if (_preferredOutputDeviceId.Length > 0) await ChangeAudioOutputAsync(_preferredOutputDeviceId, restoring: true);
        await RefreshAudioOutputsAsync();
    }
    private async Task EnsureAudioOutputReadyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var ready = _audioOutputReady;
            var change = _activeAudioOutputChange;
            await ready.WaitAsync(cancellationToken);
            await change.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(ready, _audioOutputReady) && ReferenceEquals(change, _activeAudioOutputChange)) break;
        }
        if (IsAudioOutputUnavailable) throw new MusicException("Аудиоустройство недоступно. Выберите устройство в настройках воспроизведения.");
    }
    private async Task ChangeAudioOutputAsync(string id, bool restoring = false)
    {
        if (_audioOutputDevices is not { } outputs || _closed || _audioOutputSwitching) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeAudioOutputChange = completion.Task;
        _audioOutputSwitching = true;
        try
        {
            if (_audioOutputCombo is not null) _audioOutputCombo.IsEnabled = false;
            var knownList = _audioOutputListedRevision;
            var route = await outputs.ResolveAsync(id);
            if (_closed) return;
            if (_audioOutputListedRevision != knownList && !AudioOutputWasListed(id))
                throw new MusicException("Выбранное аудиоустройство недоступно.");
            await _player.SetOutputDeviceAsync(route, async () =>
            {
                if (id.Length > 0) await ReadAudioOutputListAsync(outputs);
                if (_closed) return;
                // Confirmation is part of route application so a disappeared endpoint
                // rolls back the slots before the preference or Play becomes available.
                if (!AudioOutputWasListed(id)) throw new MusicException("Выбранное аудиоустройство недоступно.");
            });
            if (_closed) return;
            if (!restoring && _preferredOutputDeviceId != id) RememberPreferenceEdit(PreferenceField.OutputDevice);
            _preferredOutputDeviceId = id;
            _audioOutputUnavailable = false;
            if (_audioOutputText is not null) _audioOutputText.Text = "Используется для онлайн-музыки, скачанных треков и кроссфейда.";
            if (!restoring) PersistSettingsImmediately();
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _diagnostics.LastErrorType = "AudioOutput:" + ex.GetType().Name;
            if (restoring || !_player.OutputRouteIsValid) _audioOutputUnavailable = true;
            PausePlaybackAfterOutputLoss();
            if (_audioOutputText is not null) _audioOutputText.Text = "Устройство недоступно. Музыка на паузе; выберите доступное устройство.";
            SetStatus("Не удалось выбрать аудиоустройство. Музыка на паузе.", true);
        }
        finally
        {
            _audioOutputSwitching = false;
            try
            {
                if (!_closed && _audioOutputCombo is not null)
                {
                    UpdateAudioOutputChoices();
                    _audioOutputCombo.IsEnabled = true;
                }
            }
            finally { completion.TrySetResult(); }
        }
    }

    private void PausePlaybackAfterOutputLoss()
    {
        if (QueuePlaybackBeforeFirstPlay) RememberPausedDesktopOccurrence();
        if (_loadingStream) _playCts?.Cancel();
        _player.PauseAfterOutputFailure(); SavePlaybackState(); SetWavePaused(true); _progressTimer.Stop();
        _diagnostics.Stage = "paused"; UpdateControls();
    }

    private void OnNativePlaybackFailed(TimeSpan position)
    {
        if (_closed) return;
        if (_current is { } track && ReferenceEquals(track, _playbackQueue.Current)
            && _playbackQueue.CurrentOccurrenceToken is { } occurrence && !TryGetRestoredPosition(track, out _))
        {
            var seconds = _pendingSeek ?? position.TotalSeconds;
            var storageLimit = TimeSpan.FromMilliseconds(PlaybackStateStore.MaximumDurationMs);
            _restoredPlaybackOccurrence = occurrence;
            // NativePlayer already closed the failed slot. Its captured duration
            // belongs to this occurrence only while the saved playback owner matches.
            _restoredPlaybackNativeDuration = ObservedPlaybackDuration is { } observed
                ? (observed <= storageLimit ? observed : storageLimit) : null;
            _restoredPlaybackSeconds = _restoredPlaybackNativeDuration is { } native && double.IsFinite(seconds)
                ? Math.Clamp(seconds, 0, native.TotalSeconds)
                : PlaybackStateStore.ClampPosition(seconds, track.DurationMs);
        }
        _pendingSeek = null;
        _playCts?.Cancel();
        CancelPendingWavePlayback();
        _progressTimer.Stop();
        ReportWaveEnd(false);
        if (_current is not null) _current.IsPlaying = false;
        HighlightTrack(null);
        SavePlaybackState();
        _diagnostics.Stage = "playback_error";
        SetStatus("Windows прервала воспроизведение. Попробуйте снова или выберите другой трек.", true);
        UpdateControls(); UpdateProgress();
    }

    private async Task RefreshAudioOutputsAsync()
    {
        if (_audioOutputDevices is not { } outputs || _closed || (!_restore || _demo) && !_audioOutputFixture) return;
        var listing = ReadAudioOutputListAsync(outputs);
        var revision = _audioOutputReadRevision;
        try
        {
            await listing;
            if (_closed || revision != _audioOutputListedRevision) return;
            UpdateAudioOutputChoices();
            if (IsAudioOutputUnavailable && !_audioOutputSwitching && AudioOutputWasListed(_preferredOutputDeviceId))
                await ChangeAudioOutputAsync(_preferredOutputDeviceId, restoring: true);
        }
        catch (Exception ex)
        {
            if (!_closed && revision == _audioOutputReadRevision)
            { _diagnostics.LastErrorType = "AudioDevices:" + ex.GetType().Name; if (_audioOutputText is not null) _audioOutputText.Text = "Не удалось получить список устройств. Нажмите «Обновить»."; }
        }
    }

    private async Task ReadAudioOutputListAsync(IAudioOutputDevices outputs)
    {
        var revision = ++_audioOutputReadRevision;
        var devices = await outputs.ListAsync();
        if (_closed || revision <= _audioOutputListedRevision) return;
        _audioOutputListedRevision = revision;
        _listedAudioOutputs = devices;
    }

    private bool AudioOutputWasListed(string id)
        => id.Length == 0 || _listedAudioOutputs?.Any(device => device.Id == id) == true;

    private void UpdateAudioOutputChoices()
    {
        if (_listedAudioOutputs is not { } devices) return;
        var choices = new List<AudioOutputDevice> { new("", AudioOutputDevices.SystemDefaultLabel) };
        choices.AddRange(devices);
        if (!AudioOutputWasListed(_preferredOutputDeviceId))
        {
            choices.Add(new(_preferredOutputDeviceId, "Выбранное устройство недоступно"));
            if (!_audioOutputUnavailable)
            {
                _audioOutputUnavailable = true;
                PausePlaybackAfterOutputLoss();
                if (_audioOutputText is not null) _audioOutputText.Text = "Устройство отключено. Музыка на паузе; выберите доступное устройство.";
                SetStatus("Аудиоустройство отключено. Музыка на паузе.");
            }
        }
        if (_audioOutputCombo is null) return;
        _playbackOptionsInitializing = true;
        try { _audioOutputCombo.ItemsSource = choices; _audioOutputCombo.SelectedValue = _preferredOutputDeviceId; }
        finally { _playbackOptionsInitializing = false; }
    }
    private void AudioOutputDevices_Changed()
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closed) return;
            _audioOutputRefreshTimer ??= CreateAudioOutputRefreshTimer();
            _audioOutputRefreshTimer.Stop(); _audioOutputRefreshTimer.Start();
        }));
    }
    private DispatcherTimer CreateAudioOutputRefreshTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += async (_, _) => { timer.Stop(); await RefreshAudioOutputsAsync(); };
        return timer;
    }
    private void DisposePlaybackOptions()
    {
        _sleepTick?.Stop(); _audioOutputRefreshTimer?.Stop();
        _sleepTimer.Cancel(); _sleepClock.Reset(); _player.Pause(); _player.SetSleepGain(1);
        var audioOutputDevices = _audioOutputDevices;
        _audioOutputDevices = null;
        if (audioOutputDevices is not null)
        {
            try
            {
                audioOutputDevices.Changed -= AudioOutputDevices_Changed;
                audioOutputDevices.Dispose();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                _diagnostics.LastErrorType = "AudioDevicesDispose:" + ex.GetType().Name;
            }
        }
    }
}
