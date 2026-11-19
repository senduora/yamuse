using System.ComponentModel;
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
    private int _activeLyricIndex = -1;
    private CancellationTokenSource? _lyricsCts;
    private (TrackRow Track, LyricsCacheKey Key)? _lyricsRequestOwner;
    private GeniusLyricsClient? _geniusLyrics;
    private LrclibLyricsClient? _lrclibLyrics = new();
    private readonly DispatcherTimer _lyricsTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private ListBox? _visibleLyricsList;
    private int _lyricsVisibilityEpoch;
    private bool _lyricsFollowAfterReveal;
    private DateTimeOffset _lyricsFollowUntil;
    private bool _lyricsManualDragging;
    private TrackRow? _lyricsCoverTrack;
    private Uri? _lyricsSource;
    private readonly Dictionary<LyricsCacheKey, CachedLyrics> _lyricsCache = new();
    private readonly record struct LyricsCacheKey(string TrackId, string RequestId, string? AlbumId, string Album,
        string Title, string Artist, string? PrimaryArtist, long DurationMs, string? Owner, bool Authorized, int AccountGeneration);
    private sealed record CachedLyrics(LyricsResult Result, Uri? Source, DateTimeOffset Loaded);
    private sealed class LyricDisplayRow(string text) : INotifyPropertyChanged
    {
        public string Text { get; } = text;
        public bool IsCurrent { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => Text;

        internal void SetCurrent(bool current)
        {
            IsCurrent = current;
            PropertyChanged?.Invoke(this, new(nameof(IsCurrent)));
        }
    }

    private LyricsCacheKey LyricsCacheIdentity(TrackRow track) => new(track.Id, track.RequestId, track.AlbumId, track.Album,
        track.Title, track.Artist, track.PrimaryArtistName ?? track.Artists.FirstOrDefault(artist => !string.IsNullOrWhiteSpace(artist.Name))?.Name,
        track.DurationMs, _account?.Uid, _session is not null, _accountGeneration);

    private async void Home_Click(object sender, RoutedEventArgs e) => await LoadHomeAsync();
    private async Task LoadHomeAsync()
    {
        if (_session is null) { SetStatus("Войдите в Яндекс, чтобы открыть рекомендации."); return; }
        SetSection("home", "Главная");
        await CatalogReadAsync(async (session, token, isCurrent) =>
        {
            var sections = await _api.GetHomeSectionsAsync(session, token);
            if (!isCurrent()) return;
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
        if (_lyricsOpen) RefreshLyrics();
    }
    private void InitializeLyrics()
    {
        _lyricsList = LyricsList; _lyricsHeading = LyricsHeading;
        _lyricsNote = LyricsNote; _lyricsSourceButton = LyricsSourceButton;
        _lyricsTimer.Tick += (_, _) => UpdateLyrics(DisplayPosition(_player.Position).TotalSeconds);
        StateChanged += (_, _) => UpdateLyricsTimer();
        IsVisibleChanged += (_, _) => UpdateLyricsTimer();
        LyricsList.IsVisibleChanged += (_, _) => UpdateLyricsTimer();
        LyricsList.PreviewMouseWheel += (_, _) => PauseLyricsFollow();
        LyricsList.PreviewTouchDown += (_, _) => PauseLyricsFollow();
        LyricsList.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => { _lyricsManualDragging = true; PauseLyricsFollow(); }), true);
        LyricsList.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { _lyricsManualDragging = false; PauseLyricsFollow(); }), true);
        RegisterLyricsPageScroll(LyricsList, held => _lyricsManualDragging = held, PauseLyricsFollow);
        RegisterLyricLineGesture(LyricsList, expanded: false);
        LyricsList.PreviewKeyDown += (_, args) =>
        {
            if (args.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) PauseLyricsFollow();
            if (args.Key == Key.Enter && (args.IsRepeat ||
                TrySeekLyricLine(LyricLineIndex(LyricsList, args.OriginalSource as DependencyObject, useSelection: true), expanded: false)))
                args.Handled = true;
        };
    }
    private void SetLyricsVisible(bool visible)
    {
        if (_lyricsOpen == visible) return;
        _lyricsOpen = visible;
        _lyricsManualDragging = false;
        UpdateMainContentVisibility();
        UpdateLyricsTimer();
        PlayerLyricsButton.Foreground = (Brush)FindResource(visible ? "AccentTextBrush" : "MutedBrush");
        PlayerLyricsButton.Background = visible ? (Brush)FindResource("AccentSoftBrush") : Brushes.Transparent;
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
        try { using var process = Process.Start(new ProcessStartInfo(source.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { SetStatus("Не удалось открыть страницу в браузере.", true); }
    }
    private void LyricsRetry_Click(object sender, RoutedEventArgs e)
    {
        LoadLyrics(refresh: true);
    }
    private void RegisterLyricLineGesture(ListBox list, bool expanded)
    {
        (int Index, LyricLine Line, int Playback, int Account, long Visibility)? pressed = null;
        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var index = LyricLineIndex(list, e.OriginalSource as DependencyObject, useSelection: false);
            pressed = e.ChangedButton == MouseButton.Left && index >= 0 && index < _lyricLines.Count
                ? (index, _lyricLines[index], _playbackGeneration, _accountGeneration, _lyricsVisibilityEpoch) : null;
        };
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var origin = pressed;
            pressed = null;
            if (e.ChangedButton != MouseButton.Left || origin is not { } start || start.Playback != _playbackGeneration
                || start.Account != _accountGeneration || start.Visibility != _lyricsVisibilityEpoch) return;
            var index = LyricLineIndex(list, e.OriginalSource as DependencyObject, useSelection: false);
            if (index == start.Index && index < _lyricLines.Count && ReferenceEquals(start.Line, _lyricLines[index]))
                TrySeekLyricLine(index, expanded); // ListBox releases its capture on the subsequent MouseUp.
        };
        list.MouseLeave += (_, _) => pressed = null;
        list.Unloaded += (_, _) => pressed = null;
        list.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, _) =>
        { if (!list.IsMouseCaptureWithin) pressed = null; }), handledEventsToo: true);
    }

    private static int LyricLineIndex(ListBox list, DependencyObject? source, bool useSelection) => source is not null
        && ItemsControl.ContainerFromElement(list, source) is ListBoxItem item
        ? list.ItemContainerGenerator.IndexFromContainer(item) : useSelection ? list.SelectedIndex : -1;

    private bool TrySeekLyricLine(int index, bool expanded)
    {
        if (!_player.HasSource || _player.Duration <= TimeSpan.Zero || index < 0 || index >= _lyricLines.Count) return false;
        if (expanded)
        {
            if (!IsExpandedPlayerOpen || !_expandedLyricsOpen || _expandedCollapse is not null || _expandedAccountGeneration != _accountGeneration)
                return false;
            _expandedLyricsFollowUntil = default;
            _expandedLyricsFollowIndex = -1;
        }
        else
        {
            if (!_lyricsOpen) return false;
            _lyricsFollowUntil = default;
        }
        CommitSeek(_lyricLines[index].TimeSeconds);
        return true;
    }
    private void UpdateLyricsTimer()
    {
        var visible = VisibleLyricsList();
        if (visible is not null && _lyricLines.Count > 0 && _player.IsPlaying) _lyricsTimer.Start();
        else _lyricsTimer.Stop();
        if (ReferenceEquals(_visibleLyricsList, visible)) return;

        var previous = _visibleLyricsList;
        _visibleLyricsList = visible;
        _lyricsVisibilityEpoch++;
        if (previous is not null && FindViewer(previous) is { } viewer) SmoothScroll.CancelProgrammaticAnimation(viewer);
        _lyricsManualDragging = false;
        _expandedLyricsManualDragging = false;
        _lyricsFollowAfterReveal = visible is not null && ReferenceEquals(visible, _lyricsList);
        _expandedLyricsFollowIndex = -1;
        if (visible is not null && _lyricLines.Count > 0) UpdateLyrics(DisplayPosition(_player.Position).TotalSeconds);
    }

    private ListBox? VisibleLyricsList()
    {
        if (_closed || !_lyricsOpen || _current is null || !IsVisible || WindowState == WindowState.Minimized) return null;
        if (!IsExpandedPlayerOpen) return _lyricsList is { IsVisible: true } inline ? inline : null;
        return _expandedLyricsOpen && _expandedFullscreenHost?.Window.WindowState != WindowState.Minimized
            && _expandedLyricsList is { IsVisible: true } expanded ? expanded : null;
    }
    private void PauseLyricsFollow()
    {
        _lyricsFollowUntil = DateTimeOffset.UtcNow.AddMilliseconds(3500);
        if (FindViewer(LyricsList) is { } viewer) SmoothScroll.CancelProgrammaticAnimation(viewer);
    }

    private static void RegisterLyricsPageScroll(ListBox list, Action<bool> setHeld, Action pauseFollow)
    {
        static bool IsPageButton(object source) => source is RepeatButton
        { Name: "PageBefore" or "PageAfter", TemplatedParent: ScrollBar { Orientation: Orientation.Vertical } };

        list.AddHandler(Mouse.GotMouseCaptureEvent, new MouseEventHandler((_, args) =>
        { if (IsPageButton(args.OriginalSource)) { setHeld(true); pauseFollow(); } }), true);
        list.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, args) =>
        { if (IsPageButton(args.OriginalSource)) { setHeld(false); pauseFollow(); } }), true);
        list.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, args) =>
        { if (IsPageButton(args.OriginalSource)) pauseFollow(); }), true);
    }
    private void SetLyricsRequest(TrackRow? track, CancellationTokenSource? cancellation)
    {
        _lyricsCts = cancellation;
        _lyricsRequestOwner = track is not null && cancellation is not null ? (track, LyricsCacheIdentity(track)) : null;
    }

    private void RefreshLyrics()
    {
        if (_current is { } track && _lyricsCts is { IsCancellationRequested: false } && _lyricsRequestOwner is { } owner
            && ReferenceEquals(owner.Track, track) && owner.Key == LyricsCacheIdentity(track)) return;
        LoadLyrics(refresh: false);
    }

    private async void LoadLyrics(bool refresh)
    {
        if (_closed) return;
        _lyricsCts?.Cancel(); SetLyricsRequest(null, null);
        SetLyricsRows([], null); _lyricsSource = null; _lyricsFollowUntil = default;
        UpdateLyricsTimer();
        if (FindViewer(LyricsList) is { } viewer) SmoothScroll.CancelProgrammaticAnimation(viewer);
        ResetExpandedLyricsFollow();
        if (_lyricsSourceButton is not null) _lyricsSourceButton.Visibility = Visibility.Collapsed;
        if (_lyricsHeading is not null) _lyricsHeading.Text = _current?.Title ?? "Текст песни";
        LyricsArtist.Text = _current?.Artist ?? "";
        var track = _current;
        SetLyricsArtwork(track);
        if (track is null) { if (_lyricsNote is not null) _lyricsNote.Text = "Запустите трек, чтобы открыть текст."; return; }
        var cacheKey = LyricsCacheIdentity(track);
        if (refresh) _lyricsCache.Remove(cacheKey);
        else if (_lyricsCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.Loaded < TimeSpan.FromMinutes(30))
        { ApplyLyrics(track, cached); return; }
        // Production preloads per track. Test/demo windows use isolated clients
        // only when the embedded lyrics view is explicitly opened.
        if (!_restore && !_lyricsOpen) return;
        if (_lyricsNote is not null) _lyricsNote.Text = track.Artist + "\nЗагружаем текст…";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        SetLyricsRequest(track, cts);
        bool Current() => !_closed && !cts.IsCancellationRequested && ReferenceEquals(_lyricsCts, cts)
            && cacheKey == LyricsCacheIdentity(track) && ReferenceEquals(_current, track);
        try
        {
            LyricsResult result = new("", [], false, null);
            Uri? source = null;
            var timedUnavailable = false;
            if (_session is not null)
            {
                var session = await EnsureSessionAsync(cts.Token);
                if (!Current()) return;
                result = await _api.GetLyricsAsync(session, track.Id, cts.Token);
                if (!Current()) return;
            }
            var retryYandexLyrics = result.RetryPreferredLyrics;
            if (!result.Synchronized && track.Title.Length > 0 && track.Artist.Length > 0 && _lrclibLyrics is not null)
            {
                if (_lyricsNote is not null) _lyricsNote.Text = "Ищем текст с таймкодами…";
                var lookup = await _lrclibLyrics.FindSynchronizedAsync(track, cts.Token);
                if (!Current()) return;
                timedUnavailable = lookup.TemporarilyUnavailable;
                if (lookup.Lyrics is { } timed) { result = timed; source = new Uri("https://lrclib.net"); }
            }
            var primaryArtist = cacheKey.PrimaryArtist;
            var artistName = primaryArtist ?? track.Artist;
            if (string.IsNullOrWhiteSpace(result.PlainText) && result.Lines.Count == 0 && track.Title.Length > 0 && artistName.Length > 0)
            {
                if (_lyricsNote is not null) _lyricsNote.Text = track.Artist + "\nИщем текст в Genius…";
                _geniusLyrics ??= new GeniusLyricsClient();
                var fallback = await _geniusLyrics.FindAsync(track.Title, artistName, cts.Token, refresh, artistIsExact: primaryArtist is not null);
                if (!Current()) return;
                if (fallback is not null) { result = new(fallback.PlainText, [], false, "Источник: Genius · текст без синхронизации"); source = fallback.Source; }
                else result = new("", [], false, timedUnavailable ? "Текст не найден в Яндекс Музыке и Genius. LRCLIB временно недоступен."
                    : "Текст не найден в Яндекс Музыке и Genius.");
            }
            var value = new CachedLyrics(result, source, DateTimeOffset.UtcNow);
            if (!timedUnavailable && !retryYandexLyrics)
            {
                if (_lyricsCache.Count >= 64 && !_lyricsCache.ContainsKey(cacheKey)) _lyricsCache.Remove(_lyricsCache.Keys.First());
                _lyricsCache[cacheKey] = value;
            }
            ApplyLyrics(track, value);
        }
        catch (Exception ex)
        {
            if (Current())
            {
                _diagnostics.LastErrorType = "Lyrics:" + ex.GetType().Name;
                if (_lyricsNote is not null) _lyricsNote.Text = track.Artist + "\n" + (ex is MusicException ? ex.Message : "Не удалось загрузить текст. Нажмите «Обновить текст», чтобы повторить.");
            }
        }
        finally { if (ReferenceEquals(_lyricsCts, cts)) SetLyricsRequest(null, null); cts.Dispose(); }
    }
    private void SetLyricsArtwork(TrackRow? track)
    {
        var trackChanged = !ReferenceEquals(_lyricsCoverTrack, track);
        _lyricsCoverTrack = track;
        Artwork.SetTrackSource(LyricsCover, track?.CoverUri, trackChanged);
    }
    private void ApplyLyrics(TrackRow track, CachedLyrics cached)
    {
        if (!ReferenceEquals(_current, track)) return;
        SetLyricsRows(cached.Result.Synchronized ? cached.Result.Lines : [],
            cached.Result.Synchronized ? cached.Result.Lines.Select(line => line.Text)
                : cached.Result.PlainText.Length == 0 ? [] : cached.Result.PlainText.Split('\n'));
        _lyricsSource = cached.Source;
        if (_lyricsHeading is not null) _lyricsHeading.Text = track.Title;
        if (_lyricsNote is not null)
        {
            var note = cached.Source is null && cached.Result.PlainText.Length > 0
                ? "Источник: Яндекс Музыка · " + (cached.Result.Synchronized ? "синхронизированный текст" : "текст без синхронизации")
                : cached.Result.Note;
            if (cached.Result.MayBePartial) note += "\nПолнота доступного обычного текста не подтверждена.";
            _lyricsNote.Text = track.Artist + "\n" + note;
        }
        if (_lyricsSourceButton is not null) _lyricsSourceButton.Visibility = cached.Source is null ? Visibility.Collapsed : Visibility.Visible;
        if (_lyricsSourceButton is not null) _lyricsSourceButton.Content = cached.Source?.Host == "lrclib.net" ? "Открыть LRCLIB ↗" : "Открыть на Genius ↗";
        if (_lyricsList is not null)
        {
            _lyricsList.ToolTip = cached.Result.Synchronized ? "Нажмите на строку, чтобы перейти к этому месту песни" : null;
        }
        UpdateLyricsTimer();
        if (_lyricLines.Count > 0) UpdateLyrics(DisplayPosition(_player.Position).TotalSeconds);
        if (ReferenceEquals(VisibleLyricsList(), LyricsList)) Motion.Enter(LyricsList, 4);
        RefreshExpandedLyrics();
    }
    private void SetLyricsRows(IReadOnlyList<LyricLine> timed, IEnumerable<string>? text)
    {
        SetActiveLyricIndex(-1);
        _lyricLines = timed;
        if (_lyricsList is not null) _lyricsList.ItemsSource = text?.Select(line => new LyricDisplayRow(line)).ToArray();
    }
    private bool SetActiveLyricIndex(int index)
    {
        if (_activeLyricIndex == index) return false;
        // Native selection and focus own the next seek candidate, independently of playback time.
        SetCurrent(_activeLyricIndex, false);
        _activeLyricIndex = index;
        SetCurrent(index, true);
        return true;

        void SetCurrent(int rowIndex, bool current)
        {
            if (_lyricsList is { } list && rowIndex >= 0 && rowIndex < list.Items.Count
                && list.Items[rowIndex] is LyricDisplayRow row) row.SetCurrent(current);
        }
    }
    private void UpdateLyrics(double position)
    {
        var visible = VisibleLyricsList();
        if (visible is null || _lyricsList is null || _lyricLines.Count == 0) return;
        var index = -1;
        var low = 0; var high = _lyricLines.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (_lyricLines[middle].TimeSeconds <= position) { index = middle; low = middle + 1; }
            else high = middle - 1;
        }
        var changed = SetActiveLyricIndex(index);
        if (changed && index < 0 && FindViewer(_lyricsList) is { } viewer)
            SmoothScroll.CancelProgrammaticAnimation(viewer);
        RefreshExpandedLyrics();
        if (!ReferenceEquals(visible, _lyricsList) || index < 0 || _lyricsManualDragging || DateTimeOffset.UtcNow < _lyricsFollowUntil) return;
        if (!changed && !_lyricsFollowAfterReveal && _lyricsFollowUntil == default) return;
        _lyricsFollowAfterReveal = false;
        _lyricsFollowUntil = default;
        CenterLyricLine(index);
    }
    private void CenterLyricLine(int index)
    {
        var track = _current;
        var lines = _lyricLines;
        var account = _accountGeneration;
        var playback = _playbackGeneration;
        var visibility = _lyricsVisibilityEpoch;
        bool StillCurrent() => ReferenceEquals(VisibleLyricsList(), LyricsList) && !_lyricsManualDragging &&
            DateTimeOffset.UtcNow >= _lyricsFollowUntil && _accountGeneration == account &&
            _playbackGeneration == playback && _lyricsVisibilityEpoch == visibility &&
            ReferenceEquals(_current, track) && ReferenceEquals(_lyricLines, lines) && _activeLyricIndex == index;

        void CenterIn(ScrollViewer viewer)
        {
            if (LyricsList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement row)
            {
                SmoothScroll.ScrollIntoView(viewer, LyricsList, index, StillCurrent);
                return;
            }
            var point = row.TranslatePoint(new Point(0, 0), viewer);
            var target = viewer.VerticalOffset + point.Y + row.ActualHeight / 2 - viewer.ViewportHeight * .45;
            SmoothScroll.ScrollTo(viewer, target, StillCurrent);
        }

        if (FindViewer(LyricsList) is { } viewer) CenterIn(viewer);
        else _ = Dispatcher.InvokeAsync(() =>
        {
            if (!StillCurrent()) return;
            LyricsList.ApplyTemplate();
            if (FindViewer(LyricsList) is { } realized) CenterIn(realized);
        }, DispatcherPriority.Loaded);
    }
    private TrackRow? ResetLyricsForAccountChange()
    {
        var expanded = IsExpandedPlayerOpen && _expandedLyricsOpen && _expandedLyricsPanel?.IsVisible == true;
        var visible = _lyricsOpen && (IsExpandedPlayerOpen ? expanded : LyricsPanel.IsVisible);
        var retainedTrack = visible && _account is null && _session is null && _player.SourceLocalPath is not null ? _current : null;
        if (retainedTrack is not null && expanded) _expandedAccountGeneration = _accountGeneration;
        ClearDiscoveryLyrics(closePanel: retainedTrack is null);
        return retainedTrack;
    }

    private void ReloadLyricsAfterAccountChange(TrackRow? retainedTrack)
    {
        if (retainedTrack is null) return;
        if (_lyricsOpen && ReferenceEquals(_current, retainedTrack)) RefreshLyrics();
        else CloseDiscovery();
    }

    private void CloseDiscovery() => ClearDiscoveryLyrics(closePanel: true);

    private void ClearDiscoveryLyrics(bool closePanel)
    {
        _lyricsCts?.Cancel(); SetLyricsRequest(null, null);
        if (closePanel)
        {
            SetExpandedLyricsVisible(false, animate: false);
            SetLyricsVisible(false);
            _lyricsTimer.Stop();
        }
        _lyricsCache.Clear(); SetLyricsRows([], null);
        UpdateLyricsTimer();
        _lyricsSource = null;
        if (_lyricsSourceButton is not null) _lyricsSourceButton.Visibility = Visibility.Collapsed;
        if (_lyricsNote is not null) _lyricsNote.Text = "";
        if (_lyricsHeading is not null) _lyricsHeading.Text = "Текст песни";
        LyricsArtist.Text = "";
        SetLyricsArtwork(null);
        _geniusLyrics?.Dispose(); _geniusLyrics = null;
        if (_closed) { _lrclibLyrics?.Dispose(); _lrclibLyrics = null; }
    }
}
