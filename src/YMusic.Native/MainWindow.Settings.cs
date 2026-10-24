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
        try { _preferenceStore.Save(new() { Quality = SelectedQuality, Volume = VolumeSlider.Value / 100, PlaybackSpeed = _player.SpeedRatio, MinimizeToTray = _minimizeToTray, AutoDownloadFavorites = _autoFavoritesEnabled, ConcurrentDownloads = _concurrentDownloads }); }
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
                    case ShellAction.Exit: _shell.RequestExit(); break;
                }
            };
        }
        catch (Exception ex) { _diagnostics.LastErrorType = "DesktopShell:" + ex.GetType().Name; }
    }
    private void MiniPlayer_Click(object sender, RoutedEventArgs e) => _shell?.ShowMiniPlayer();
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Настройки воспроизведения", FontSize = 23, Margin = new Thickness(0, 0, 0, 20) });
        panel.Children.Add(new TextBlock { Text = "Скорость", Margin = new Thickness(0, 0, 0, 8) });
        var speed = new ComboBox { ItemsSource = new double[] { 0.5, 0.75, 1, 1.25, 1.5, 2 }, SelectedItem = _player.SpeedRatio };
        panel.Children.Add(speed);
        var tray = new CheckBox { Content = "Сворачивать в трей при закрытии окна", IsChecked = _minimizeToTray, Margin = new Thickness(0, 22, 0, 18) };
        panel.Children.Add(tray);
        var auto = new CheckBox { Content = "Автоматически скачивать Мне нравится", IsChecked = _autoFavoritesEnabled, Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(auto);
        panel.Children.Add(new TextBlock { Text = "Одновременные скачивания", Margin = new Thickness(0, 0, 0, 8) });
        var downloads = new ComboBox { ItemsSource = Enumerable.Range(1, 10), SelectedItem = _concurrentDownloads };
        panel.Children.Add(downloads);
        panel.Children.Add(new TextBlock { Text = "От 1 до 10 треков. Изменение применяется к следующей загрузке плейлиста или избранного; текущая продолжится с прежним значением.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 18) });
        var clear = new Button { Content = "Удалить всё скачанное", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 18) };
        clear.Click += async (_, _) => { await ClearOfflineAsync(); auto.IsChecked = _autoFavoritesEnabled; };
        panel.Children.Add(clear);
        panel.Children.Add(CreateUpdateSettingsPanel());
        panel.Children.Add(new TextBlock { Text = "Пробел — пауза. Enter — открыть выбранное. Медиаклавиши работают через Windows.\nМузыка сохраняется в Документы\\YMusic Native\\Music по кнопке скачивания или при включённом автоскачивании избранного.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
        var apply = new Button { Content = "Сохранить" }; panel.Children.Add(apply);
        var dialog = new Window { Title = "Настройки · YMusic Native", Owner = this, Width = 520, Height = Math.Min(680, SystemParameters.WorkArea.Height), MinWidth = 420, MinHeight = Math.Min(360, SystemParameters.WorkArea.Height), WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Style = (Style)FindResource(typeof(Window)) };
        apply.Click += (_, _) =>
        {
            _player.SetSpeed(speed.SelectedItem is double ratio ? ratio : 1);
            _minimizeToTray = tray.IsChecked == true;
            if (_shell is not null) { _shell.MinimizeToTray = _minimizeToTray; _minimizeToTray = _shell.MinimizeToTray; }
            var autoChanged = _autoFavoritesEnabled != (auto.IsChecked == true);
            _concurrentDownloads = downloads.SelectedItem is int count ? Math.Clamp(count, 1, 10) : PlayerPreferences.DefaultConcurrentDownloads;
            AutoFavoritesCheckBox.IsChecked = auto.IsChecked == true;
            if (!autoChanged) SavePreferences();
            dialog.Close();
        };
        dialog.ContentRendered += (_, _) => Motion.Enter(panel, 8);
        dialog.ShowDialog();
    }
}
