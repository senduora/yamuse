using System.Windows;
using System.Windows.Controls;

namespace YMusic.Native;

public partial class MainWindow
{
    private PreferencesStore? _preferenceStore;
    private bool _preferencesReady;
    private bool _minimizeToTray;
    private bool _autoFavoritesEnabled;
    private int _concurrentDownloads = PlayerPreferences.DefaultConcurrentDownloads;
    private DesktopShell? _shell;
    private void LoadPreferences()
    {
        if (_restore && !_demo) _preferenceStore = new PreferencesStore(App.DataDirectory);
        try
        {
            var preferences = _preferenceStore?.Load() ?? new PlayerPreferences();
            QualityCombo.SelectedIndex = preferences.Quality switch { AudioQuality.Excellent => 0, AudioQuality.Optimal => 1, _ => 2 };
            VolumeSlider.Value = preferences.Volume * 100;
            _player.SetSpeed(preferences.PlaybackSpeed);
            _player.SetCrossfade(preferences.CrossfadeSeconds);
            _minimizeToTray = preferences.MinimizeToTray;
            _autoFavoritesEnabled = preferences.AutoDownloadFavorites;
            _concurrentDownloads = preferences.ConcurrentDownloads;
            AutoFavoritesCheckBox.IsChecked = _autoFavoritesEnabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _diagnostics.LastErrorType = "PreferencesLoad:" + ex.GetType().Name; }
        _preferencesReady = true;
    }
    private void SavePreferences()
    {
        if (!_preferencesReady || _preferenceStore is null) return;
        try { _preferenceStore.Save(new() { Quality = SelectedQuality, Volume = VolumeSlider.Value / 100, PlaybackSpeed = _player.SpeedRatio, MinimizeToTray = _minimizeToTray, AutoDownloadFavorites = _autoFavoritesEnabled, ConcurrentDownloads = _concurrentDownloads, CrossfadeSeconds = _player.CrossfadeSeconds }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _diagnostics.LastErrorType = "PreferencesSave:" + ex.GetType().Name; SetStatus("Не удалось сохранить настройки на диске.", true); }
    }
    private void InitializeShell()
    {
        try
        {
            _shell = new DesktopShell(this) { MinimizeToTray = _minimizeToTray };
            _shell.Command += async action =>
            {
                switch (action)
                {
                    case ShellAction.PlayPause: await TogglePlaybackAsync(); break;
                    case ShellAction.Next: await AdvanceAsync(1, false); break;
                    case ShellAction.Previous:
                        if (_player.Position.TotalSeconds > 3) { CommitSeek(0); }
                        else await AdvanceAsync(-1, false);
                        break;
                    case ShellAction.ToggleLike: await ToggleCurrentLikeAsync(); break;
                    case ShellAction.Exit: _shell.RequestExit(); break;
                }
            };
            _shell.SeekRequested += seconds => { if (!_closed && SeekSlider.IsEnabled) { CommitSeek(seconds); UpdateShellState(); } };
            _shell.VolumeRequested += percent => { if (!_closed && double.IsFinite(percent)) VolumeSlider.Value = Math.Clamp(percent, 0, 100); };
            if (System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image)) is { } coverSource)
            {
                EventHandler coverChanged = (_, _) => UpdateShellState();
                coverSource.AddValueChanged(PlayerCover, coverChanged);
                Closed += (_, _) => coverSource.RemoveValueChanged(PlayerCover, coverChanged);
            }
            UpdateShellState();
        }
        catch (Exception ex) { _diagnostics.LastErrorType = "DesktopShell:" + ex.GetType().Name; }
    }
    private void MiniPlayer_Click(object sender, RoutedEventArgs e) => _shell?.ShowMiniPlayer();
    private bool _settingsOpen;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _settingsOpen) return;
        SettingsQuality.SelectedIndex = QualityCombo.SelectedIndex;
        SettingsSpeed.ItemsSource ??= new double[] { 0.5, 0.75, 1, 1.25, 1.5, 2 };
        SettingsSpeed.SelectedItem = _player.SpeedRatio;
        CrossfadeSetting.Value = _player.CrossfadeSeconds;
        ShowSettingsCrossfade();
        SettingsTray.IsChecked = _minimizeToTray;
        SettingsAutoFavorites.IsChecked = _autoFavoritesEnabled;
        SettingsDownloads.ItemsSource ??= Enumerable.Range(1, 10).ToArray();
        SettingsDownloads.SelectedItem = _concurrentDownloads;
        SettingsUpdates.Content ??= CreateUpdateSettingsPanel();
        SetLyricsVisible(false);
        SetSettingsVisible(true);
        SettingsScroll.ScrollToTop();
        SettingsBackButton.Focus();
    }

    private void SetSettingsVisible(bool visible)
    {
        if (_settingsOpen == visible) return;
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

    private void SettingsCrossfade_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ShowSettingsCrossfade();
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

    private void SettingsApply_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsOpen) return;
        _player.SetSpeed(SettingsSpeed.SelectedItem is double ratio ? ratio : 1);
        _player.SetCrossfade(CrossfadeSetting.Value);
        QualityCombo.SelectedIndex = SettingsQuality.SelectedIndex;
        _minimizeToTray = SettingsTray.IsChecked == true;
        if (_shell is not null) { _shell.MinimizeToTray = _minimizeToTray; _minimizeToTray = _shell.MinimizeToTray; }
        var autoChanged = _autoFavoritesEnabled != (SettingsAutoFavorites.IsChecked == true);
        _concurrentDownloads = SettingsDownloads.SelectedItem is int count ? Math.Clamp(count, 1, 10) : PlayerPreferences.DefaultConcurrentDownloads;
        AutoFavoritesCheckBox.IsChecked = SettingsAutoFavorites.IsChecked == true;
        if (!autoChanged) SavePreferences();
        SetSettingsVisible(false);
        SettingsButton.Focus();
    }
}
