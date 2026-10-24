using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private bool _lyricsOpen;
    private ListBox? _lyricsList;
    private TextBlock? _lyricsNote;
    private TextBlock? _lyricsHeading;
    private Button? _lyricsSourceButton;
    private IReadOnlyList<LyricLine> _lyricLines = [];
    private CancellationTokenSource? _lyricsCts;
    private GeniusLyricsClient? _geniusLyrics;
    private LrclibLyricsClient? _lrclibLyrics = new();
    private readonly DispatcherTimer _lyricsTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DateTimeOffset _lyricsFollowUntil;
    private Uri? _lyricsSource;
    private readonly Dictionary<string, CachedLyrics> _lyricsCache = new(StringComparer.Ordinal);
    private sealed record CachedLyrics(LyricsResult Result, Uri? Source, DateTimeOffset Loaded);

    private async void Home_Click(object sender, RoutedEventArgs e) => await LoadHomeAsync();
    private async Task LoadHomeAsync()
    {
        if (_session is null) { SetStatus("Войдите в Яндекс, чтобы открыть рекомендации."); return; }
        SetSection("home", "Главная");
        await CatalogReadAsync(async (session, token) =>
        {
            var sections = await _api.GetHomeSectionsAsync(session, token);
            token.ThrowIfCancellationRequested();
            ShowHomeSections(sections);
            var notes = sections.Select(s => s.Note).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();
            SetStatus($"Подборки Яндекса · {sections.Count:N0} разделов" + (notes.Length > 0 ? " · " + string.Join(" · ", notes) : ""));
        });
    }
    private void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        SetSettingsVisible(false);
        SetLyricsVisible(!_lyricsOpen);
        if (_lyricsOpen && _lyricsCts is null) RefreshLyrics();
    }
    private void InitializeLyrics()
    {
        _lyricsList = LyricsList; _lyricsHeading = LyricsHeading;
        _lyricsNote = LyricsNote; _lyricsSourceButton = LyricsSourceButton;
        _lyricsTimer.Tick += (_, _) => UpdateLyrics(_player.Position.TotalSeconds);
        LyricsList.PreviewMouseWheel += (_, _) => PauseLyricsFollow();
        LyricsList.PreviewTouchDown += (_, _) => PauseLyricsFollow();
        LyricsList.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => PauseLyricsFollow()), true);
        LyricsList.PreviewKeyDown += (_, args) =>
        { if (args.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) PauseLyricsFollow(); };
    }
    private void SetLyricsVisible(bool visible)
    {
        if (_lyricsOpen == visible) return;
        _lyricsOpen = visible;
        UpdateMainContentVisibility();
        UpdateLyricsTimer();
        PlayerLyricsButton.Foreground = (Brush)FindResource(visible ? "AccentBrush" : "MutedBrush");
        PlayerLyricsButton.Background = visible ? new SolidColorBrush(Color.FromRgb(51, 37, 34)) : Brushes.Transparent;
        PlayerLyricsButton.ToolTip = visible ? "Скрыть текст песни · Esc" : "Текст песни";
        System.Windows.Automation.AutomationProperties.SetName(PlayerLyricsButton, visible ? "Скрыть текст песни" : "Открыть текст песни");
        if (!_closed) Motion.Enter(CurrentContent());
    }
    private void LyricsClose_Click(object sender, RoutedEventArgs e)
    {
        SetLyricsVisible(false);
        PlayerLyricsButton.Focus();
    }
    private void LyricsSource_Click(object sender, RoutedEventArgs e)
    {
        if (_lyricsSource is not { Scheme: "https", Host: "genius.com" or "www.genius.com" or "lrclib.net" } source) return;
        Process.Start(new ProcessStartInfo(source.AbsoluteUri) { UseShellExecute = true });
    }
    private void LyricsRetry_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not null) _lyricsCache.Remove(_current.Id);
        RefreshLyrics();
    }
    private void LyricsLine_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var index = LyricsList.SelectedIndex;
        if (index >= 0 && index < _lyricLines.Count && _player.HasSource) CommitSeek(_lyricLines[index].TimeSeconds);
    }
    private void LyricsLine_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_player.HasSource) return;
        if (ItemsControl.ContainerFromElement(LyricsList, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;
        var index = LyricsList.ItemContainerGenerator.IndexFromContainer(item);
        if (index < 0 || index >= _lyricLines.Count) return;
        e.Handled = true; _lyricsFollowUntil = default;
        CommitSeek(_lyricLines[index].TimeSeconds);
    }
    private void UpdateLyricsTimer()
    {
        if (!_closed && _lyricsOpen && _player.IsPlaying) _lyricsTimer.Start();
        else _lyricsTimer.Stop();
    }
    private void PauseLyricsFollow()
    {
        _lyricsFollowUntil = DateTimeOffset.UtcNow.AddMilliseconds(3500);
        if (FindViewer(LyricsList) is { } viewer) SmoothScroll.CancelAnimation(viewer);
    }
    private async void RefreshLyrics()
    {
        _lyricsCts?.Cancel(); _lyricLines = []; _lyricsSource = null; _lyricsFollowUntil = default;
        if (_lyricsSourceButton is not null) _lyricsSourceButton.Visibility = Visibility.Collapsed;
        if (_lyricsList is not null) _lyricsList.ItemsSource = null;
        if (_lyricsHeading is not null) _lyricsHeading.Text = _current?.Title ?? "Текст песни";
        LyricsArtist.Text = _current?.Artist ?? "";
        Artwork.SetSourceUri(LyricsCover, _current?.CoverUri);
        var track = _current;
        if (track is null) { if (_lyricsNote is not null) _lyricsNote.Text = "Запустите трек, чтобы открыть текст."; return; }
        if (_lyricsCache.TryGetValue(track.Id, out var cached) && DateTimeOffset.UtcNow - cached.Loaded < TimeSpan.FromMinutes(30))
        { ApplyLyrics(track, cached); return; }
        // Production preloads per track. Test/demo windows use isolated clients
        // only when the embedded lyrics view is explicitly opened.
        if (!_restore && !_lyricsOpen) return;
        if (_lyricsNote is not null) _lyricsNote.Text = track.Artist + "\nЗагружаем текст…";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _lyricsCts = cts;
        var generation = _accountGeneration;
        try
        {
            LyricsResult result = new("", [], false, null);
            Uri? source = null;
            if (_session is not null)
            {
                var session = await EnsureSessionAsync(cts.Token);
                result = await _api.GetLyricsAsync(session, track.Id, cts.Token);
            }
            if (!result.Synchronized && track.Title.Length > 0 && track.Artist.Length > 0 && _lrclibLyrics is not null)
            {
                if (_lyricsNote is not null && ReferenceEquals(_current, track)) _lyricsNote.Text = "Ищем текст с таймкодами…";
                var timed = await _lrclibLyrics.FindSynchronizedAsync(track, cts.Token);
                if (timed is not null) { result = timed; source = new Uri("https://lrclib.net"); }
            }
            if (string.IsNullOrWhiteSpace(result.PlainText) && result.Lines.Count == 0 && track.Title.Length > 0 && track.Artist.Length > 0)
            {
                if (_lyricsNote is not null && ReferenceEquals(_current, track)) _lyricsNote.Text = track.Artist + "\nИщем текст в Genius…";
                _geniusLyrics ??= new GeniusLyricsClient();
                var fallback = await _geniusLyrics.FindAsync(track.Title, track.Artist, cts.Token);
                if (fallback is not null) { result = new(fallback.PlainText, [], false, "Источник: Genius · текст без синхронизации"); source = fallback.Source; }
                else result = new("", [], false, "Текст не найден в Яндекс Музыке и Genius.");
            }
            cts.Token.ThrowIfCancellationRequested();
            if (generation != _accountGeneration || !ReferenceEquals(_current, track) || _closed) return;
            var value = new CachedLyrics(result, source, DateTimeOffset.UtcNow);
            if (_lyricsCache.Count >= 64 && !_lyricsCache.ContainsKey(track.Id)) _lyricsCache.Remove(_lyricsCache.Keys.First());
            _lyricsCache[track.Id] = value;
            ApplyLyrics(track, value);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_lyricsCts, cts) && ReferenceEquals(_current, track) && !_closed)
            {
                _diagnostics.LastErrorType = "Lyrics:" + ex.GetType().Name;
                if (_lyricsNote is not null) _lyricsNote.Text = track.Artist + "\n" + (ex is MusicException ? ex.Message : "Не удалось загрузить текст. Нажмите «Обновить текст», чтобы повторить.");
            }
        }
        finally { if (ReferenceEquals(_lyricsCts, cts)) _lyricsCts = null; cts.Dispose(); RefreshExpandedLyrics(); }
    }
    private void ApplyLyrics(TrackRow track, CachedLyrics cached)
    {
        if (!ReferenceEquals(_current, track)) return;
        _lyricLines = cached.Result.Synchronized ? cached.Result.Lines : [];
        _lyricsSource = cached.Source;
        if (_lyricsHeading is not null) _lyricsHeading.Text = track.Title;
        if (_lyricsNote is not null)
        {
            var note = cached.Source is null && cached.Result.PlainText.Length > 0
                ? "Источник: Яндекс Музыка · " + (cached.Result.Synchronized ? "синхронизированный текст" : "текст без синхронизации")
                : cached.Result.Note;
            _lyricsNote.Text = track.Artist + "\n" + note;
        }
        if (_lyricsSourceButton is not null) _lyricsSourceButton.Visibility = cached.Source is null ? Visibility.Collapsed : Visibility.Visible;
        if (_lyricsSourceButton is not null) _lyricsSourceButton.Content = cached.Source?.Host == "lrclib.net" ? "Открыть LRCLIB ↗" : "Открыть на Genius ↗";
        if (_lyricsList is not null)
        {
            _lyricsList.ItemsSource = cached.Result.Synchronized ? cached.Result.Lines.Select(line => line.Text).ToArray() : cached.Result.PlainText.Split('\n');
            _lyricsList.ToolTip = cached.Result.Synchronized ? "Нажмите на строку, чтобы перейти к этому месту песни" : null;
        }
        UpdateLyrics(_player.Position.TotalSeconds);
        if (_lyricsOpen) Motion.Enter(LyricsList, 4);
        RefreshExpandedLyrics();
    }
    private void UpdateLyrics(double position)
    {
        if (_lyricsList is null || _lyricLines.Count == 0) { RefreshExpandedLyrics(); return; }
        var index = -1;
        var low = 0; var high = _lyricLines.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (_lyricLines[middle].TimeSeconds <= position) { index = middle; low = middle + 1; }
            else high = middle - 1;
        }
        var changed = _lyricsList.SelectedIndex != index;
        if (changed) _lyricsList.SelectedIndex = index;
        RefreshExpandedLyrics();
        if (!_lyricsOpen || index < 0 || DateTimeOffset.UtcNow < _lyricsFollowUntil) return;
        if (!changed && _lyricsFollowUntil == default) return;
        _lyricsFollowUntil = default;
        CenterLyricLine(index);
    }
    private void CenterLyricLine(int index)
    {
        if (FindViewer(LyricsList) is not { } viewer) return;
        if (LyricsList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement row)
        {
            LyricsList.ScrollIntoView(LyricsList.Items[index]);
            return;
        }
        var point = row.TranslatePoint(new Point(0, 0), viewer);
        var target = viewer.VerticalOffset + point.Y + row.ActualHeight / 2 - viewer.ViewportHeight * .45;
        SmoothScroll.ScrollTo(viewer, target);
    }
    private void CloseDiscovery()
    {
        _lyricsCts?.Cancel(); SetLyricsVisible(false); _lyricsTimer.Stop(); _lyricsCache.Clear(); _lyricLines = [];
        LyricsList.ItemsSource = null; Artwork.SetSourceUri(LyricsCover, null);
        _geniusLyrics?.Dispose(); _geniusLyrics = null;
        if (_closed) { _lrclibLyrics?.Dispose(); _lrclibLyrics = null; }
    }
}
