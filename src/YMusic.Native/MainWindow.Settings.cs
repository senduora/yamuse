using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private const string PreferencesReadError = "Не удалось прочитать настройки. Проверьте доступ к файлу и повторите сохранение.";
    [Flags]
    private enum PreferenceField
    {
        None = 0, Quality = 1 << 0, Volume = 1 << 1, PlaybackSpeed = 1 << 2, MinimizeToTray = 1 << 3,
        OutputDevice = 1 << 4, AutoDownloadFavorites = 1 << 5, ConcurrentDownloads = 1 << 6,
        Crossfade = 1 << 7, NormalizeVolume = 1 << 8, AccentFromCover = 1 << 9,
        StaticAccentColor = 1 << 10, Theme = 1 << 11, UpdateFrequency = 1 << 12
    }
    private PreferencesStore? _preferenceStore;
    private bool _preferencesReady;
    private bool _preferencesLoadFailed;
    private bool _updateFrequencyRecovered;
    private bool UpdateFrequencyKnown => !_preferencesLoadFailed || _updateFrequencyRecovered;
    private UpdateCheckFrequency? _pendingUpdateFrequency;
    private PreferenceField _pendingPreferenceEdits;
    private bool _minimizeToTray;
    private bool _autoFavoritesEnabled;
    private int _concurrentDownloads = PlayerPreferences.DefaultConcurrentDownloads;
    private bool _accentFromCover = true;
    private string _staticAccentColor = "#E7BB59";
    private DesktopShell? _shell;
    private bool _settingsInitializing, _settingsSavePending;
    private DispatcherTimer? _settingsSaveTimer;
    private void LoadPreferences()
    {
        _preferencesReady = false;
        _preferencesLoadFailed = false;
        _updateFrequencyRecovered = false;
        _pendingUpdateFrequency = null;
        _pendingPreferenceEdits = PreferenceField.None;
        _settingsInitializing = true;
        if (_restore && !_demo) _preferenceStore ??= new PreferencesStore(_diagnosticsDirectory);
        try
        {
            var preferences = _preferenceStore?.Load() ?? new PlayerPreferences();
            QualityCombo.SelectedIndex = preferences.Quality switch { AudioQuality.Excellent => 0, AudioQuality.Optimal => 1, _ => 2 };
            VolumeSlider.Value = preferences.Volume * 100;
            _player.SetSpeed(preferences.PlaybackSpeed);
            _player.SetCrossfade(preferences.CrossfadeSeconds);
            _player.SetNormalization(preferences.NormalizeVolume);
            _preferredOutputDeviceId = preferences.OutputDeviceId;
            _minimizeToTray = preferences.MinimizeToTray;
            _autoFavoritesEnabled = preferences.AutoDownloadFavorites;
            _concurrentDownloads = preferences.ConcurrentDownloads;
            _accentFromCover = preferences.AccentFromCover;
            _staticAccentColor = preferences.StaticAccentColor;
            ThemeManager.SetMode(preferences.Theme);
            _updateCheckFrequency = preferences.UpdateCheckFrequency;
            _coverAccent?.Configure(_accentFromCover, _staticAccentColor);
            AutoFavoritesCheckBox.IsChecked = _autoFavoritesEnabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _preferencesLoadFailed = true;
            _diagnostics.LastErrorType = "PreferencesLoad:" + ex.GetType().Name;
            SetStatus(PreferencesReadError, true);
        }
        finally
        {
            _settingsInitializing = false;
            _preferencesReady = true;
        }
    }
    private void RememberPreferenceEdit(PreferenceField field)
    {
        if (_preferencesReady && _preferencesLoadFailed && !_settingsInitializing && !_closed)
            _pendingPreferenceEdits |= field;
    }

    private void SavePreferences() => TrySavePreferences(out _);

    private bool TrySavePreferencesForUpdate(out string? detail)
    {
        detail = null;
        if (!_restore || _demo) return true;
        if (!_preferencesLoadFailed || _pendingPreferenceEdits != PreferenceField.None || _preferenceStore is not { } store)
            return TrySavePreferences(out detail);
        try
        {
            // The runtime defaults must not replace possibly different saved
            // values. An untouched window only needs to verify those values.
            _ = store.Load();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            detail = PreferencesReadError;
            _diagnostics.LastErrorType = "PreferencesLoad:" + error.GetType().Name;
            return false;
        }
    }

    private bool TrySavePreferences(out string? detail, bool? autoDownloadFavorites = null)
    {
        detail = null;
        if (_preferenceStore is not { } store) { CommitUpdateFrequency(); return true; }
        if (!_preferencesReady) return false;
        // The auto-download toggle and offline clear only adopt their proposed
        // setting if this write succeeds. A failed proposal must not be retried.
        var edits = _pendingPreferenceEdits;
        if (autoDownloadFavorites.HasValue) edits |= PreferenceField.AutoDownloadFavorites;
        if (_preferencesLoadFailed && edits == PreferenceField.None) return true;
        try
        {
            var runtime = new PlayerPreferences
            {
                Quality = SelectedQuality,
                Volume = VolumeSlider.Value / 100,
                PlaybackSpeed = _player.SpeedRatio,
                MinimizeToTray = _minimizeToTray,
                OutputDeviceId = _preferredOutputDeviceId,
                AutoDownloadFavorites = autoDownloadFavorites ?? _autoFavoritesEnabled,
                ConcurrentDownloads = _concurrentDownloads,
                CrossfadeSeconds = _player.CrossfadeSeconds,
                NormalizeVolume = _player.NormalizeVolume,
                AccentFromCover = _accentFromCover,
                StaticAccentColor = _staticAccentColor,
                Theme = ThemeManager.Mode,
                UpdateCheckFrequency = _pendingUpdateFrequency ?? _updateCheckFrequency
            };
            // The window still contains fallback values for untouched fields,
            // including after a successful merge. Continue using field edits.
            if (_preferencesLoadFailed) store.Merge(saved => ApplyPreferenceEdits(saved, runtime, edits));
            else store.Save(runtime);
            _pendingPreferenceEdits = PreferenceField.None;
            _settingsSavePending = false;
            _settingsSaveTimer?.Stop();
            CommitUpdateFrequency();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics.LastErrorType = "PreferencesSave:" + ex.GetType().Name;
            if (ex is OversizedSettingsArchiveException) detail = ex.Message;
            SetStatus(detail ?? "Не удалось сохранить настройки на диске.", true);
            return false;
        }
    }

    private static PlayerPreferences ApplyPreferenceEdits(PlayerPreferences saved, PlayerPreferences runtime, PreferenceField fields)
        => saved with
        {
            Quality = fields.HasFlag(PreferenceField.Quality) ? runtime.Quality : saved.Quality,
            Volume = fields.HasFlag(PreferenceField.Volume) ? runtime.Volume : saved.Volume,
            PlaybackSpeed = fields.HasFlag(PreferenceField.PlaybackSpeed) ? runtime.PlaybackSpeed : saved.PlaybackSpeed,
            MinimizeToTray = fields.HasFlag(PreferenceField.MinimizeToTray) ? runtime.MinimizeToTray : saved.MinimizeToTray,
            OutputDeviceId = fields.HasFlag(PreferenceField.OutputDevice) ? runtime.OutputDeviceId : saved.OutputDeviceId,
            AutoDownloadFavorites = fields.HasFlag(PreferenceField.AutoDownloadFavorites) ? runtime.AutoDownloadFavorites : saved.AutoDownloadFavorites,
            ConcurrentDownloads = fields.HasFlag(PreferenceField.ConcurrentDownloads) ? runtime.ConcurrentDownloads : saved.ConcurrentDownloads,
            CrossfadeSeconds = fields.HasFlag(PreferenceField.Crossfade) ? runtime.CrossfadeSeconds : saved.CrossfadeSeconds,
            NormalizeVolume = fields.HasFlag(PreferenceField.NormalizeVolume) ? runtime.NormalizeVolume : saved.NormalizeVolume,
            AccentFromCover = fields.HasFlag(PreferenceField.AccentFromCover) ? runtime.AccentFromCover : saved.AccentFromCover,
            StaticAccentColor = fields.HasFlag(PreferenceField.StaticAccentColor) ? runtime.StaticAccentColor : saved.StaticAccentColor,
            Theme = fields.HasFlag(PreferenceField.Theme) ? runtime.Theme : saved.Theme,
            UpdateCheckFrequency = fields.HasFlag(PreferenceField.UpdateFrequency) ? runtime.UpdateCheckFrequency : saved.UpdateCheckFrequency
        };
    private void InitializeShell()
    {
        try
        {
            _shell = new DesktopShell(this) { MinimizeToTray = _minimizeToTray };
            _shell.Command += async action => await HandleShellCommandAsync(action);
            _shell.SeekRequested += seconds => { if (!_closed && SeekSlider.IsEnabled) { CommitSeek(seconds); UpdateShellState(); } };
            _shell.VolumeRequested += percent => { if (!_closed && double.IsFinite(percent)) VolumeSlider.Value = Math.Clamp(percent, 0, 100); };
            _shell.MiniCoverChanged += cover => _coverAccent?.SetMiniCover(cover);
            UpdateShellState();
        }
        catch (Exception ex) { _diagnostics.LastErrorType = "DesktopShell:" + ex.GetType().Name; }
    }
    private void MiniPlayer_Click(object sender, RoutedEventArgs e) => _shell?.ShowMiniPlayer();
    private bool _settingsOpen;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _settingsOpen) return;
        _settingsInitializing = true;
        try
        {
        SettingsQuality.SelectedIndex = QualityCombo.SelectedIndex;
        SettingsSpeed.ItemsSource ??= new double[] { 0.5, 0.75, 1, 1.25, 1.5, 2 };
        SettingsSpeed.SelectedItem = _player.SpeedRatio;
        CrossfadeSetting.Value = _player.CrossfadeSeconds;
        ShowSettingsCrossfade();
        SettingsNormalizeVolume.IsChecked = _player.NormalizeVolume;
        SettingsTheme.SelectedValue = ThemeManager.Mode;
        SettingsAccentFromCover.IsChecked = _accentFromCover;
        InitializeAccentSwatches();
        SettingsAccentHex.Text = _staticAccentColor;
        UpdateAccentDraft();
        SettingsTray.IsChecked = _minimizeToTray;
        SettingsAutoFavorites.IsChecked = _autoFavoritesEnabled;
        SettingsDownloads.ItemsSource ??= Enumerable.Range(1, 10).ToArray();
        SettingsDownloads.SelectedItem = _concurrentDownloads;
        SettingsUpdates.Content ??= CreateUpdateSettingsPanel();
        ShowCommittedUpdateFrequency();
        RetryWindowsUninstallRegistration();
        RefreshFullUninstallAction();
        }
        finally { _settingsInitializing = false; }
        SetLyricsVisible(false);
        SetSettingsVisible(true);
        SettingsScroll.ScrollToTop();
        SettingsBackButton.Focus();
        _ = RefreshAudioOutputsAsync();
    }

    private void SetSettingsVisible(bool visible)
    {
        if (_settingsOpen == visible) return;
        if (!visible) FlushSettingsSave();
        _settingsOpen = visible;
        UpdateMainContentVisibility();
        SettingsButton.Background = visible ? (System.Windows.Media.Brush)FindResource("PanelBrush") : System.Windows.Media.Brushes.Transparent;
        if (!_closed) Motion.Enter(CurrentContent(), 0);
    }

    private void SettingsClose_Click(object sender, RoutedEventArgs e)
    {
        SetSettingsVisible(false);
        SettingsButton.Focus();
    }

    private async void SettingsUninstall_Click(object sender, RoutedEventArgs e) => await ConfirmFullUninstallAsync();

    private void SettingsCrossfade_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ShowSettingsCrossfade();
        if (!CanApplySettingsControl) return;
        var previous = _player.CrossfadeSeconds;
        _player.SetCrossfade(CrossfadeSetting.Value);
        if (_player.CrossfadeSeconds != previous) RememberPreferenceEdit(PreferenceField.Crossfade);
        QueueSettingsSave();
    }
    private void ShowSettingsCrossfade()
    {
        if (SettingsCrossfadeValue is not null && CrossfadeSetting is not null)
            SettingsCrossfadeValue.Text = CrossfadeSetting.Value == 0 ? "Выключен" : $"{CrossfadeSetting.Value:0.#} с";
    }

    private async void SettingsClearOffline_Click(object sender, RoutedEventArgs e)
    {
        await ClearOfflineAsync();
        SettingsAutoFavorites.IsChecked = _autoFavoritesEnabled;
    }

    private bool CanApplySettingsControl => _settingsOpen && !_settingsInitializing && !_closed;

    private void SettingsSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!CanApplySettingsControl) return;
        if (ReferenceEquals(sender, SettingsTheme))
        {
            if (SettingsTheme.SelectedValue is not string mode) return;
            var previous = ThemeManager.Mode;
            ThemeManager.SetMode(mode);
            if (ThemeManager.Mode != previous) RememberPreferenceEdit(PreferenceField.Theme);
        }
        else if (ReferenceEquals(sender, SettingsQuality))
        {
            if (SettingsQuality.SelectedIndex is < 0 or > 2) return;
            var previous = SelectedQuality;
            var ready = _preferencesReady;
            try { _preferencesReady = false; QualityCombo.SelectedIndex = SettingsQuality.SelectedIndex; }
            finally { _preferencesReady = ready; }
            if (SelectedQuality != previous) RememberPreferenceEdit(PreferenceField.Quality);
        }
        else if (ReferenceEquals(sender, SettingsSpeed))
        {
            if (SettingsSpeed.SelectedItem is not double ratio) return;
            var previous = _player.SpeedRatio;
            _player.SetSpeed(ratio);
            if (_player.SpeedRatio != previous) RememberPreferenceEdit(PreferenceField.PlaybackSpeed);
        }
        else if (ReferenceEquals(sender, SettingsDownloads))
        {
            if (SettingsDownloads.SelectedItem is not int count) return;
            var previous = _concurrentDownloads;
            _concurrentDownloads = Math.Clamp(count, 1, 10);
            if (_concurrentDownloads != previous) RememberPreferenceEdit(PreferenceField.ConcurrentDownloads);
        }
        else return;
        PersistSettingsImmediately();
    }

    private void SettingsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!CanApplySettingsControl) return;
        if (ReferenceEquals(sender, SettingsAccentFromCover))
        {
            var previous = _accentFromCover;
            _accentFromCover = SettingsAccentFromCover.IsChecked == true;
            _coverAccent?.Configure(_accentFromCover, _staticAccentColor);
            if (_accentFromCover != previous) RememberPreferenceEdit(PreferenceField.AccentFromCover);
        }
        else if (ReferenceEquals(sender, SettingsNormalizeVolume))
        {
            var previous = _player.NormalizeVolume;
            _player.SetNormalization(SettingsNormalizeVolume.IsChecked == true);
            if (_player.NormalizeVolume != previous) RememberPreferenceEdit(PreferenceField.NormalizeVolume);
        }
        else if (ReferenceEquals(sender, SettingsTray))
        {
            var previous = _minimizeToTray;
            _minimizeToTray = SettingsTray.IsChecked == true;
            if (_shell is not null) { _shell.MinimizeToTray = _minimizeToTray; _minimizeToTray = _shell.MinimizeToTray; }
            if (_minimizeToTray != previous) RememberPreferenceEdit(PreferenceField.MinimizeToTray);
        }
        else if (ReferenceEquals(sender, SettingsAutoFavorites))
        {
            // The existing handler owns queue start/cancellation and persistence.
            var changed = AutoFavoritesCheckBox.IsChecked != SettingsAutoFavorites.IsChecked;
            AutoFavoritesCheckBox.IsChecked = SettingsAutoFavorites.IsChecked == true;
            if (!changed) FlushSettingsSave();
            return;
        }
        else return;
        PersistSettingsImmediately();
    }

    private void QueueSettingsSave()
    {
        _settingsSavePending = true;
        if (_settingsSaveTimer is null)
        {
            _settingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
            _settingsSaveTimer.Tick += SettingsSave_Tick;
            Closed += SettingsSave_Closed;
        }
        _settingsSaveTimer.Stop(); _settingsSaveTimer.Start();
    }

    private void SettingsSave_Tick(object? sender, EventArgs e) => FlushSettingsSave();
    private void SettingsSlider_Commit(object sender, RoutedEventArgs e) => FlushSettingsSave();
    private void SettingsSave_Closed(object? sender, EventArgs e)
    {
        // Closing has already attempted to save the current runtime preferences.
        _settingsSavePending = false;
        if (_settingsSaveTimer is not null)
        { _settingsSaveTimer.Stop(); _settingsSaveTimer.Tick -= SettingsSave_Tick; _settingsSaveTimer = null; }
        Closed -= SettingsSave_Closed;
    }
    private void FlushSettingsSave()
    {
        _settingsSaveTimer?.Stop();
        if (!_settingsSavePending) return;
        _settingsSavePending = false;
        if (!_closed) SavePreferences();
    }
    private void PersistSettingsImmediately()
    {
        _settingsSavePending = false; _settingsSaveTimer?.Stop();
        SavePreferences();
    }

    private void InitializeAccentSwatches()
    {
        if (SettingsAccentSwatches.Children.Count != 0) return;
        foreach (var (hex, name) in new[] { ("#E7BB59", "Золотистый"), ("#EA571C", "Оранжевый"), ("#E55B76", "Розовый"), ("#A88AF1", "Лиловый"),
            ("#6EADFA", "Голубой"), ("#51BDAD", "Бирюзовый"), ("#9DBF63", "Зелёный"), ("#ADB0BA", "Серый") })
        {
            var button = new Button { Tag = hex, ToolTip = name + " · " + hex, Width = 38, Height = 38, Padding = new Thickness(5), Margin = new Thickness(0, 0, 8, 8),
                BorderThickness = new Thickness(2), Background = System.Windows.Media.Brushes.Transparent,
                Content = new System.Windows.Shapes.Ellipse { Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), Width = 22, Height = 22 } };
            System.Windows.Automation.AutomationProperties.SetName(button, "Статичный акцент: " + name);
            button.Click += (_, _) => SettingsAccentHex.Text = hex;
            SettingsAccentSwatches.Children.Add(button);
        }
    }

    private void SettingsAccentHex_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateAccentDraft();
        if (!CanApplySettingsControl || !PreferencesStore.IsAccentColor(SettingsAccentHex.Text)) return;
        var previous = _staticAccentColor;
        _staticAccentColor = SettingsAccentHex.Text.ToUpperInvariant();
        _coverAccent?.Configure(_accentFromCover, _staticAccentColor);
        if (_staticAccentColor != previous) RememberPreferenceEdit(PreferenceField.StaticAccentColor);
        PersistSettingsImmediately();
    }
    private void UpdateAccentDraft()
    {
        if (SettingsAccentPreview is null || SettingsAccentError is null || SettingsAccentHex is null) return;
        var valid = PreferencesStore.IsAccentColor(SettingsAccentHex.Text);
        SettingsAccentError.Text = valid ? "" : "Цвет: # и шесть шестнадцатеричных цифр.";
        if (valid) SettingsAccentPreview.Background = new SolidColorBrush(CoverPalette.Readable((Color)ColorConverter.ConvertFromString(SettingsAccentHex.Text)));
        foreach (var button in SettingsAccentSwatches.Children.OfType<Button>())
        {
            if (valid && string.Equals((string)button.Tag, SettingsAccentHex.Text, StringComparison.OrdinalIgnoreCase))
                button.SetResourceReference(Control.BorderBrushProperty, "ForegroundBrush");
            else button.BorderBrush = System.Windows.Media.Brushes.Transparent;
        }
    }
}
