using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace YMusic.Native;

public partial class MainWindow
{
    private Grid? _expandedPlayerSurface;
    private Image? _expandedPlayerCover;
    private Button? _expandedPlayerCoverButton, _expandedPlayerTitleButton, _expandedPlayerLyricsButton, _expandedPlayerCloseButton;
    private TextBlock? _expandedPlayerTitle, _expandedPlayerArtist, _expandedPlayerPosition, _expandedPlayerDuration;
    private ContentControl? _expandedPlayerArtistLinks;
    private PlaybackSeekSlider? _expandedPlayerSeek;
    private Grid? _expandedLyricsPanel;
    private ListBox? _expandedLyricsList;
    private TrackRow? _expandedPlayerTrack;
    private bool _expandedSeekDragging, _expandedLyricsOpen, _expandedLyricsManualDragging;
    private int _expandedAccountGeneration;
    private DateTimeOffset _expandedLyricsFollowUntil;
    private int _expandedLyricsFollowIndex = -1;
    private IInputElement? _expandedPreviousFocus;
    private ExpandedWindowPlacement? _expandedWindowPlacement;
    private sealed record ExpandedWindowPlacement(WindowStyle Style, WindowState State, ResizeMode Resize, Rect Bounds);

    private bool IsExpandedPlayerOpen => ExpandedPlayerHost?.Visibility == Visibility.Visible;
    private void ExpandPlayer_Click(object sender, RoutedEventArgs e)
    {
        if (IsExpandedPlayerOpen && _expandedCollapse is null) RequestExpandedPlayerClose();
        else SetExpandedPlayerVisible(true);
    }
    // Navigation, account changes and lifetime events always use the immediate
    // route. Only an explicit user collapse waits for the finite transition.
    private void CloseExpandedPlayer() => SetExpandedPlayerVisible(false);

    // The fullscreen:false route lets deterministic fixtures render the exact
    // production surface offscreen without maximizing a window on a user's monitor.
    private void SetExpandedPlayerVisible(bool open, bool fullscreen = true)
    {
        if (open && _expandedCollapse is not null) CloseExpandedPlayer();
        if (!open) CancelExpandedCollapse();
        if (open == IsExpandedPlayerOpen || open && (_closed || _current is null)) return;
        if (open)
        {
            _queuePress = null;
            var reveal = fullscreen ? CaptureExpandedRevealOrigin() : null;
            CancelExpandedReveal();
            _expandedPreviousFocus = Keyboard.FocusedElement;
            SetSettingsVisible(false);
            SetLyricsVisible(false);
            BuildExpandedPlayer();
            _expandedAccountGeneration = _accountGeneration;
            // Resolve the first track before preparing opacity. Its normal
            // track-change guard must not cancel this opening transition.
            RefreshExpandedPlayerCore(opening: true);
            if (fullscreen)
            {
                var bounds = RestoreBounds;
                if (bounds.IsEmpty) bounds = new Rect(Left, Top, Width, Height);
                _expandedWindowPlacement = new(WindowStyle, WindowState, ResizeMode, bounds);
                CreateExpandedFullscreenHost();
                PrepareExpandedReveal(reveal);
            }
            ExpandedPlayerHost.Visibility = Visibility.Visible;
            RefreshExpandedPlayer();
            if (!IsExpandedPlayerOpen) return;
            if (fullscreen)
            {
                try { ShowExpandedFullscreenHost(); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
                { CloseExpandedPlayer(); SetStatus("Не удалось открыть полноэкранный проигрыватель. Попробуйте ещё раз.", true); return; }
            }
            if (ShowActivated) _expandedPlayerCloseButton?.Focus();
            if (fullscreen) BeginPreparedExpandedReveal();
        }
        else
        {
            _fullscreenDisplayRequest?.SetRequired(false);
            CancelExpandedReveal();
            CloseExpandedFullscreenHost();
            SetExpandedLyricsVisible(false, animate: false);
            SetExpandedQueueVisible(false, animate: false);
            _expandedSeekGesture?.Cancel();
            _expandedSeekDragging = false;
            ExpandedPlayerHost.Visibility = Visibility.Collapsed;
            if (_expandedPlayerCover is not null) Artwork.SetSourceUri(_expandedPlayerCover, null);
            if (_expandedPlayerSeek is not null) _expandedPlayerSeek.IsEnabled = false;
            if (_expandedPlayerCoverButton is not null) _expandedPlayerCoverButton.Tag = null;
            if (_expandedPlayerTitleButton is not null) _expandedPlayerTitleButton.Tag = null;
            if (_expandedPlayerArtistLinks is not null) { _expandedPlayerArtistLinks.Content = null; _expandedPlayerArtistLinks.ContextMenu = null; }
            _expandedPlayerTrack = null;
            _expandedWindowPlacement = null;
            if (ShowActivated && _expandedPreviousFocus is UIElement { IsVisible: true } focus) focus.Focus();
            _expandedPreviousFocus = null;
        }
    }

    private void BuildExpandedPlayer()
    {
        if (_expandedPlayerSurface is not null) return;
        InitializeFullscreenPower();
        var surface = _expandedPlayerSurface = new Grid { Background = ThemeManager.Brush("#141519") };
        surface.RowDefinitions.Add(new() { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new());
        surface.RowDefinitions.Add(new() { Height = GridLength.Auto });
        KeyboardNavigation.SetTabNavigation(surface, KeyboardNavigationMode.Cycle);
        var header = new Grid { Margin = new Thickness(34, 20, 28, 0) };
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var heading = new TextBlock { Text = "СЕЙЧАС ИГРАЕТ", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ExpandedBrush("MutedBrush"), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(heading);
        var quality = new TextBlock { Name = "ExpandedPlayerQuality", Foreground = ExpandedBrush("MutedBrush"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
        quality.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = QualityText }); Grid.SetColumn(quality, 1); header.Children.Add(quality);
        _expandedPlayerCloseButton = ExpandedIconButton("ExpandedPlayerClose", "\uE73F", "Свернуть проигрыватель · Esc", 44, () => RequestExpandedPlayerClose());
        Grid.SetColumn(_expandedPlayerCloseButton, 2); header.Children.Add(_expandedPlayerCloseButton); surface.Children.Add(header);

        var body = new Grid { Name = "ExpandedPlayerBody", Margin = new Thickness(38, 12, 38, 0), MaxWidth = 1500 };
        body.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); body.ColumnDefinitions.Add(new()); body.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetRow(body, 1); surface.Children.Add(body);
        var player = new Grid { Margin = new Thickness(16, 4, 16, 0) };
        player.RowDefinitions.Add(new()); player.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetColumn(player, 1); body.Children.Add(player);
        _expandedPlayerCover = new Image { Width = 560, Height = 560, Stretch = Stretch.UniformToFill, Clip = new RectangleGeometry(new Rect(0, 0, 560, 560), 24, 24) };
        Artwork.SetPixelSize(_expandedPlayerCover, 1024);
        var cover = new Grid { Width = 560, Height = 560 };
        cover.Children.Add(new Border { Background = (Brush)FindResource("CoverPlaceholderBrush"), CornerRadius = new CornerRadius(24) });
        cover.Children.Add(new TextBlock { Text = "\uE8D6", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 100, Foreground = ExpandedBrush("AccentTextBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        cover.Children.Add(_expandedPlayerCover);
        _expandedPlayerCoverButton = new Button { Name = "ExpandedPlayerCover", Content = cover, Padding = new Thickness(0), Style = (Style)FindResource("ArtistLink"), ToolTip = "Открыть альбом", HorizontalContentAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(_expandedPlayerCoverButton, "Открыть альбом текущего трека");
        _expandedPlayerCoverButton.Click += ExpandedAlbum_Click;
        var viewbox = new Viewbox { Child = _expandedPlayerCoverButton, Stretch = Stretch.Uniform, MaxWidth = 560, MaxHeight = 560, Margin = new Thickness(0, 0, 0, 18) };
        player.Children.Add(viewbox);

        var details = new StackPanel { MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(details, 1); player.Children.Add(details);
        _expandedPlayerTitle = new TextBlock { FontSize = 27, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, MaxHeight = 70, TextWrapping = TextWrapping.Wrap };
        _expandedPlayerTitleButton = new Button { Name = "ExpandedPlayerTitle", Content = _expandedPlayerTitle, Style = (Style)FindResource("TrackTitleLink"), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center, ToolTip = "Открыть альбом" };
        _expandedPlayerTitleButton.Click += ExpandedAlbum_Click; details.Children.Add(_expandedPlayerTitleButton);
        _expandedPlayerArtist = new TextBlock { FontSize = 16, Foreground = ExpandedBrush("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 5, 0, 12) };
        details.Children.Add(_expandedPlayerArtist);
        _expandedPlayerArtistLinks = new ContentControl { Name = "ExpandedPlayerArtists", ContentTemplate = (DataTemplate)FindResource("TrackArtistLinks"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 12), Visibility = Visibility.Collapsed };
        _expandedPlayerArtistLinks.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, _) => CloseExpandedPlayer()), true);
        details.Children.Add(_expandedPlayerArtistLinks);
        var transport = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        details.Children.Add(transport);
        transport.Children.Add(ExpandedQueueModeButton("ExpandedPlayerShuffle", ShuffleButton, "\uE8B1", 40));
        transport.Children.Add(ExpandedForwardButton("ExpandedPlayerPrevious", PreviousButton, "\uE892", "Предыдущий трек", 46));
        var play = ExpandedForwardButton("ExpandedPlayerPlayPause", PlayPauseButton, "\uE768", "Воспроизвести / пауза", 64);
        play.SetResourceReference(Control.BackgroundProperty, "AccentBrush"); play.SetResourceReference(Control.ForegroundProperty, "AccentForegroundBrush");
        play.FontSize = 27; play.Margin = new Thickness(14, 0, 14, 0); transport.Children.Add(play);
        transport.Children.Add(ExpandedForwardButton("ExpandedPlayerNext", NextButton, "\uE893", "Следующий трек", 46));
        transport.Children.Add(ExpandedQueueModeButton("ExpandedPlayerRepeat", RepeatButton, "\uE8EE", 40));

        _expandedPlayerSeek = new PlaybackSeekSlider { Name = "ExpandedPlayerSeek", Style = (Style)FindResource(typeof(Slider)),
            Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 0), SmallChange = 5, LargeChange = 15, IsMoveToPointEnabled = true };
        AutomationProperties.SetName(_expandedPlayerSeek, "Позиция воспроизведения"); details.Children.Add(_expandedPlayerSeek);
        _expandedPlayerSeek.ValueChanged += (_, args) => { if (_expandedSeekDragging && _expandedPlayerPosition is not null) _expandedPlayerPosition.Text = FormatTime(TimeSpan.FromSeconds(args.NewValue)); };
        _expandedSeekGesture = new SliderGesture(_expandedPlayerSeek, () =>
        { _expandedSeekDragging = true; _expandedSeekIgnoreKeyUp = false; _expandedSeekKeyboardGesture = _expandedSeekGesture?.IsKeyboardActive == true; }, FinishExpandedSeek);
        _expandedPlayerSeek.AutomationSeekRequested += seconds =>
        {
            if (_expandedCollapse is not null || !IsExpandedPlayerOpen || _expandedAccountGeneration != _accountGeneration) return;
            _expandedSeekGesture.Cancel(); CommitSeek(seconds);
        };
        _expandedPlayerSeek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, args) => FinishExpandedSeek(!args.Canceled)), true);
        _expandedPlayerSeek.PreviewKeyUp += (_, args) =>
        {
            if (_expandedCollapse is not null || !IsExpandedPlayerOpen || _expandedAccountGeneration != _accountGeneration
                || !_expandedPlayerSeek.IsEnabled || !SliderGesture.SeekKey(args.Key)) return;
            if (_expandedSeekIgnoreKeyUp) { _expandedSeekIgnoreKeyUp = false; return; }
            PlaybackTimeline.Stop(_expandedPlayerSeek, useBaseValue: true); CommitSeek(_expandedPlayerSeek.Value); RefreshExpandedPlayer(); args.Handled = true;
        };
        var time = new DockPanel(); details.Children.Add(time);
        _expandedPlayerDuration = new TextBlock { Foreground = ExpandedBrush("MutedBrush"), FontSize = 12 }; DockPanel.SetDock(_expandedPlayerDuration, Dock.Right); time.Children.Add(_expandedPlayerDuration);
        _expandedPlayerPosition = new TextBlock { Foreground = ExpandedBrush("MutedBrush"), FontSize = 12 }; time.Children.Add(_expandedPlayerPosition);
        var volume = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        var like = ExpandedForwardButton("ExpandedPlayerLike", PlayerLikeButton, "\uEB51", "Мне нравится", 36);
        like.FontSize = 19; like.SetBinding(Control.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = PlayerLikeButton }); volume.Children.Add(like);
        _expandedPlayerLyricsButton = ExpandedIconButton("ExpandedPlayerLyrics", "Тт", "Текст песни", 36, () => SetExpandedLyricsVisible(!_expandedLyricsOpen));
        _expandedPlayerLyricsButton.FontFamily = new FontFamily("Segoe UI"); _expandedPlayerLyricsButton.FontSize = 17;
        var sourceButton = ExpandedIconButton("ExpandedPlayerSource", "", "Источник воспроизведения", 36, () =>
        {
            if (_expandedCollapse is not null || !HasPlaybackContext) return;
            CloseExpandedPlayer();
            PlaybackContextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        var sourceIcon = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource("PlaybackContextGeometry"),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = 18,
            Height = 18
        };
        sourceIcon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = sourceButton });
        sourceButton.Content = sourceIcon;
        sourceButton.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = PlaybackContextButton });
        sourceButton.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = PlaybackContextButton });
        sourceButton.SetBinding(ToolTipProperty, new Binding(nameof(ToolTip)) { Source = PlaybackContextButton });
        sourceButton.SetBinding(AutomationProperties.NameProperty, new Binding { Source = PlaybackContextButton, Path = new PropertyPath(AutomationProperties.NameProperty) });
        volume.Children.Add(sourceButton);
        _expandedPlayerQueueButton = ExpandedIconButton("ExpandedPlayerQueue", "\uE14C", "Очередь воспроизведения", 36, () => SetExpandedQueueVisible(!_expandedQueueOpen));
        _expandedPlayerQueueButton.FontSize = 18; _expandedPlayerQueueButton.Margin = new Thickness(0, 0, 9, 0); volume.Children.Add(_expandedPlayerQueueButton);
        var more = ExpandedIconButton("ExpandedPlayerMore", "⋯", "Действия с треком", 32, () => { });
        more.FontFamily = new FontFamily("Segoe UI"); more.FontSize = 22;
        more.Click += (_, _) => { if (_current is not null) OpenTrackActionsMenu(more, () => _current); };
        volume.Children.Add(more);
        volume.Children.Add(new TextBlock { Text = "\uE767", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Foreground = ExpandedBrush("MutedBrush") });
        var volumeSlider = new Slider { Name = "ExpandedPlayerVolume", Width = 110, Minimum = 0, Maximum = 100, SmallChange = 5, Margin = new Thickness(9, 0, 9, 0) };
        _expandedVolumeGesture = new SliderGesture(volumeSlider, () => { }, _ => FlushVolumePreferences());
        volumeSlider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Slider.Value)) { Source = VolumeSlider, Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(volumeSlider, "Громкость"); volume.Children.Add(volumeSlider);
        var volumeText = new TextBlock { Width = 37, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = ExpandedBrush("MutedBrush") };
        volumeText.SetBinding(TextBlock.TextProperty, new Binding(nameof(Slider.Value)) { Source = VolumeSlider, StringFormat = "{0:0}%" }); volume.Children.Add(volumeText); details.Children.Add(volume);
        _expandedPlayerLyricsButton.Margin = new Thickness(16, 0, 0, 0);
        volume.Children.Add(_expandedPlayerLyricsButton);

        BuildExpandedLyrics(body);
        BuildExpandedQueue(body);
        body.SizeChanged += (_, _) => LayoutExpandedPanes(false);
        surface.Unloaded += (_, _) => { if (_expandedFullscreenHost is null) { CancelExpandedCollapse(); CancelExpandedReveal(); ClearExpandedQueue(); } };
        Closed += ExpandedPlayerClosed;
        var footer = new TextBlock { Text = "Пробел — пауза     ·     Esc — свернуть", Foreground = ExpandedBrush("MutedBrush"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 10) };
        Grid.SetRow(footer, 2); surface.Children.Add(footer);
        // Keep the host's three-row contract for its existing ambient observer.
        // The moving foreground remains separate from the native backdrop.
        surface.Background = Brushes.Transparent;
        _expandedRevealStage = new Grid { Name = "ExpandedRevealStage", ClipToBounds = true, Background = ThemeManager.Brush("#141519") };
        _expandedRevealStage.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _expandedRevealStage.RowDefinitions.Add(new());
        _expandedRevealStage.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRowSpan(surface, 3);
        _expandedRevealStage.Children.Add(surface);
        ExpandedPlayerHost.Content = _expandedRevealStage;
    }

    private Brush ExpandedBrush(string key) => (Brush)FindResource(key);
    private Button ExpandedIconButton(string name, string glyph, string label, double size, Action action)
    {
        var button = new Button { Name = name, Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 22, Style = (Style)FindResource("PlayerButton"), Width = size, Height = size, ToolTip = label };
        AutomationProperties.SetName(button, label); button.Click += (_, _) => { if (button.IsEnabled) action(); }; return button;
    }
    private Button ExpandedForwardButton(string name, Button source, string glyph, string label, double size)
    {
        var button = ExpandedIconButton(name, glyph, label, size, () => source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        button.SetBinding(ContentControl.ContentProperty, new Binding(nameof(Button.Content)) { Source = source });
        button.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = source });
        button.SetBinding(ToolTipProperty, new Binding(nameof(ToolTip)) { Source = source });
        button.SetBinding(AutomationProperties.NameProperty, new Binding { Source = source, Path = new PropertyPath(AutomationProperties.NameProperty) });
        return button;
    }
    private void ExpandedAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TrackRow track, IsEnabled: true }) return;
        CloseExpandedPlayer(); TrackAlbum_Click(new Button { Tag = track }, e);
    }
    private void FinishExpandedSeek(bool commit)
    {
        if (!_expandedSeekDragging) return;
        if (!commit && _expandedSeekKeyboardGesture) _expandedSeekIgnoreKeyUp = true;
        _expandedSeekKeyboardGesture = false;
        _expandedSeekDragging = false;
        _expandedSeekGesture?.Cancel();
        if (commit && _expandedCollapse is null && IsExpandedPlayerOpen && _expandedAccountGeneration == _accountGeneration
            && _expandedPlayerSeek is { IsEnabled: true } seek) CommitSeek(seek.Value);
        RefreshExpandedPlayer();
    }
    private void RefreshExpandedPlayer()
        => RefreshExpandedPlayerCore(opening: false);

    private void RefreshExpandedPlayerCore(bool opening)
    {
        if (!opening && !IsExpandedPlayerOpen) return;
        if (_current is null || _closed || _expandedAccountGeneration != _accountGeneration) { CloseExpandedPlayer(); return; }
        if (_expandedCollapse is { } collapse && !ReferenceEquals(collapse.Track, _current)) { CloseExpandedPlayer(); return; }
        if (_expandedPlayerCover is null || _expandedPlayerSeek is null) return;
        var track = _current;
        var trackChanged = !ReferenceEquals(_expandedPlayerTrack, track);
        if (trackChanged)
        { CancelExpandedReveal(); _expandedPlayerTrack = track; _expandedSeekGesture?.Cancel(); _expandedSeekDragging = false; PlaybackTimeline.Stop(_expandedPlayerSeek); _expandedLyricsFollowIndex = -1; _expandedLyricsFollowUntil = default; }
        Artwork.SetTrackSource(_expandedPlayerCover, track.CoverUri, trackChanged);
        _expandedPlayerTitle!.Text = track.Title; _expandedPlayerTitle.ToolTip = track.Title;
        _expandedPlayerArtist!.Text = track.Artist; _expandedPlayerArtist.ToolTip = track.Artist;
        if (!ReferenceEquals(_expandedPlayerArtistLinks!.Content, track))
        {
            ClearTrackArtistsMenu(_expandedPlayerArtistLinks);
            _expandedPlayerArtistLinks.Content = track;
            if (track.Artists.Count > 0)
                _expandedPlayerArtistLinks.ContextMenu = CreateTrackArtistsMenu(_expandedPlayerArtistLinks, track.Artists,
                    () => ReferenceEquals(_expandedPlayerArtistLinks?.Content, track));
        }
        var hasArtistLinks = track.Artists.Count > 0 || !string.IsNullOrWhiteSpace(track.Artist);
        _expandedPlayerArtistLinks.Visibility = hasArtistLinks ? Visibility.Visible : Visibility.Collapsed;
        _expandedPlayerArtistLinks.IsEnabled = _session is not null;
        _expandedPlayerArtist.Visibility = hasArtistLinks ? Visibility.Collapsed : Visibility.Visible;
        _expandedPlayerCoverButton!.Tag = _expandedPlayerTitleButton!.Tag = track;
        _expandedPlayerCoverButton.IsEnabled = _expandedPlayerTitleButton.IsEnabled = _session is not null;
        _expandedPlayerSeek.IsEnabled = _expandedCollapse is null && SeekSlider.IsEnabled;
        if (!_expandedPlayerSeek.IsEnabled) _expandedSeekDragging = false;
        if (_expandedSeekDragging) PlaybackTimeline.Stop(_expandedPlayerSeek, useBaseValue: true);
        if (!_expandedSeekDragging)
        {
            PlaybackTimeline.Update(_expandedPlayerSeek, _pendingSeek ?? (double)SeekSlider.GetAnimationBaseValue(RangeBase.ValueProperty), SeekSlider.Maximum,
                CanAdvanceVisiblePlaybackPosition, _player.SpeedRatio);
            _expandedPlayerPosition!.Text = PositionText.Text;
        }
        _expandedPlayerDuration!.Text = DurationText.Text;
        RefreshExpandedQueue();
        RefreshExpandedLyrics();
        UpdateFullscreenPower();
    }

    private void BuildExpandedLyrics(Grid body)
    {
        _expandedLyricsPanel = new Grid { Name = "ExpandedLyricsPanel", Width = 0, Opacity = 0, ClipToBounds = true, Visibility = Visibility.Collapsed };
        _expandedLyricsPanel.RowDefinitions.Add(new() { Height = GridLength.Auto }); _expandedLyricsPanel.RowDefinitions.Add(new());
        Grid.SetColumn(_expandedLyricsPanel, 2); body.Children.Add(_expandedLyricsPanel);
        var info = new StackPanel { Margin = new Thickness(28, 4, 0, 16) };
        info.Children.Add(new TextBlock { Text = "ТЕКСТ ПЕСНИ", FontSize = 12, Foreground = ExpandedBrush("MutedBrush"), Margin = new Thickness(0, 0, 0, 10) });
        var note = new TextBlock { FontSize = 12, Foreground = ExpandedBrush("MutedBrush"), TextWrapping = TextWrapping.Wrap };
        note.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = LyricsNote }); info.Children.Add(note);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var source = new Button { Style = (Style)FindResource("QuietButton"), Padding = new Thickness(12, 5, 12, 5), FontSize = 12 };
        source.SetBinding(ContentControl.ContentProperty, new Binding(nameof(Button.Content)) { Source = LyricsSourceButton });
        source.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = LyricsSourceButton }); source.Click += LyricsSource_Click; actions.Children.Add(source);
        var retry = new Button { Content = "Обновить текст", Style = (Style)FindResource("QuietButton"), Padding = new Thickness(12, 5, 12, 5), FontSize = 12 }; retry.Click += LyricsRetry_Click; actions.Children.Add(retry); info.Children.Add(actions);
        _expandedLyricsPanel.Children.Add(info);
        _expandedLyricsList = new ListBox { Name = "ExpandedLyricsList", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = ExpandedBrush("MutedBrush"), FontSize = 25, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(20, 0, 0, 0), ItemTemplate = LyricsList.ItemTemplate, ItemContainerStyle = LyricsList.ItemContainerStyle };
        _expandedLyricsList.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ItemsControl.ItemsSource)) { Source = LyricsList });
        _expandedLyricsList.SetBinding(ToolTipProperty, new Binding(nameof(ToolTip)) { Source = LyricsList });
        _expandedLyricsList.IsVisibleChanged += (_, _) => UpdateLyricsTimer();
        ScrollViewer.SetHorizontalScrollBarVisibility(_expandedLyricsList, ScrollBarVisibility.Disabled); ScrollViewer.SetVerticalScrollBarVisibility(_expandedLyricsList, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(_expandedLyricsList, true); VirtualizingPanel.SetIsVirtualizing(_expandedLyricsList, true); VirtualizingPanel.SetVirtualizationMode(_expandedLyricsList, VirtualizationMode.Recycling); VirtualizingPanel.SetScrollUnit(_expandedLyricsList, ScrollUnit.Pixel); SmoothScroll.SetEnabled(_expandedLyricsList, true);
        AutomationProperties.SetName(_expandedLyricsList, "Текст текущей песни");
        _expandedLyricsList.PreviewMouseWheel += (_, _) => PauseExpandedLyricsFollow();
        _expandedLyricsList.PreviewTouchDown += (_, _) => PauseExpandedLyricsFollow();
        _expandedLyricsList.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => { _expandedLyricsManualDragging = true; PauseExpandedLyricsFollow(); }), true);
        _expandedLyricsList.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { _expandedLyricsManualDragging = false; PauseExpandedLyricsFollow(); }), true);
        RegisterLyricsPageScroll(_expandedLyricsList, held => _expandedLyricsManualDragging = held, PauseExpandedLyricsFollow);
        _expandedLyricsList.PreviewKeyDown += (_, args) =>
        {
            if (args.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) PauseExpandedLyricsFollow();
            if (args.Key == Key.Enter && (args.IsRepeat ||
                TrySeekLyricLine(LyricLineIndex(_expandedLyricsList, args.OriginalSource as DependencyObject, useSelection: true), expanded: true)))
                args.Handled = true;
        };
        RegisterLyricLineGesture(_expandedLyricsList, expanded: true);
        Grid.SetRow(_expandedLyricsList, 1); _expandedLyricsPanel.Children.Add(_expandedLyricsList);
    }
    private void SetExpandedLyricsVisible(bool visible, bool animate = true)
    {
        if (_expandedLyricsOpen == visible) return;
        CancelExpandedReveal();
        _expandedLyricsOpen = visible;
        _expandedLyricsManualDragging = false;
        // Share the existing current-track request, fallback, cache and 100ms clock.
        // The ordinary lyrics panel remains hidden behind this inline surface.
        _lyricsOpen = visible;
        UpdateLyricsTimer();
        _expandedLyricsFollowUntil = default; _expandedLyricsFollowIndex = -1;
        if (_expandedPlayerLyricsButton is not null)
        {
            _expandedPlayerLyricsButton.Foreground = ExpandedBrush(visible ? "AccentTextBrush" : "MutedBrush");
            _expandedPlayerLyricsButton.ToolTip = visible ? "Скрыть текст песни" : "Текст песни";
            AutomationProperties.SetName(_expandedPlayerLyricsButton, visible ? "Скрыть текст песни" : "Текст песни");
        }
        AnimateExpandedLyrics(animate);
        if (visible) RefreshLyrics();
        RefreshExpandedLyrics();
    }
    private void AnimateExpandedLyrics(bool animate)
    {
        LayoutExpandedPanes(animate);
    }
    private void PauseExpandedLyricsFollow()
    {
        _expandedLyricsFollowUntil = DateTimeOffset.UtcNow.AddMilliseconds(3500);
        if (_expandedLyricsList is not null && FindViewer(_expandedLyricsList) is { } viewer) SmoothScroll.CancelProgrammaticAnimation(viewer);
    }
    private void ResetExpandedLyricsFollow()
    {
        _expandedLyricsFollowIndex = -1;
        if (_expandedLyricsList is not null && FindViewer(_expandedLyricsList) is { } viewer)
            SmoothScroll.CancelProgrammaticAnimation(viewer);
    }
    private void RefreshExpandedLyrics()
    {
        if (!IsExpandedPlayerOpen || !_expandedLyricsOpen || _expandedLyricsList is null) return;
        if (_expandedAccountGeneration != _accountGeneration || _current is null) { CloseExpandedPlayer(); return; }
        if (!ReferenceEquals(VisibleLyricsList(), _expandedLyricsList)) return;
        var index = _activeLyricIndex;
        if (index < 0)
        {
            if (_expandedLyricsFollowIndex != -1) ResetExpandedLyricsFollow();
            return;
        }
        if (index >= _expandedLyricsList.Items.Count || _expandedLyricsManualDragging || DateTimeOffset.UtcNow < _expandedLyricsFollowUntil) return;
        if (_expandedLyricsFollowIndex == index && _expandedLyricsFollowUntil == default) return;
        _expandedLyricsFollowUntil = default;
        if (FindViewer(_expandedLyricsList) is not { } viewer) return;
        _expandedLyricsFollowIndex = index;
        var list = _expandedLyricsList;
        var track = _current;
        var account = _accountGeneration;
        var playback = _playbackGeneration;
        var visibility = _lyricsVisibilityEpoch;
        bool StillCurrent() => ReferenceEquals(VisibleLyricsList(), list) &&
            !_expandedLyricsManualDragging && DateTimeOffset.UtcNow >= _expandedLyricsFollowUntil &&
            _accountGeneration == account && _expandedAccountGeneration == account &&
            _playbackGeneration == playback && _lyricsVisibilityEpoch == visibility &&
            ReferenceEquals(_current, track) && ReferenceEquals(_expandedLyricsList, list) && _activeLyricIndex == index;
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement row)
        {
            SmoothScroll.ScrollIntoView(viewer, list, index, StillCurrent);
            return;
        }
        var point = row.TranslatePoint(new Point(0, 0), viewer);
        SmoothScroll.ScrollTo(viewer, viewer.VerticalOffset + point.Y + row.ActualHeight / 2 - viewer.ViewportHeight * .45, StillCurrent);
    }
}
