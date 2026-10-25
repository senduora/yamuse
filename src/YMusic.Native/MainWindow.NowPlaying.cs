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
    private Slider? _expandedPlayerSeek;
    private Grid? _expandedLyricsPanel;
    private ListBox? _expandedLyricsList;
    private string? _expandedCoverUri;
    private TrackRow? _expandedPlayerTrack;
    private bool _expandedSeekDragging, _expandedLyricsOpen;
    private int _expandedAccountGeneration;
    private DateTimeOffset _expandedLyricsFollowUntil;
    private int _expandedLyricsFollowIndex = -1;
    private IInputElement? _expandedPreviousFocus;
    private ExpandedWindowPlacement? _expandedWindowPlacement;
    private sealed record ExpandedWindowPlacement(WindowStyle Style, WindowState State, ResizeMode Resize, Rect Bounds);

    private bool IsExpandedPlayerOpen => ExpandedPlayerHost?.Visibility == Visibility.Visible;
    private void ExpandPlayer_Click(object sender, RoutedEventArgs e) => SetExpandedPlayerVisible(!IsExpandedPlayerOpen);
    private void CloseExpandedPlayer() => SetExpandedPlayerVisible(false);

    // The fullscreen:false route lets deterministic fixtures render the exact
    // production surface offscreen without maximizing a window on a user's monitor.
    private void SetExpandedPlayerVisible(bool open, bool fullscreen = true)
    {
        if (open == IsExpandedPlayerOpen || open && (_closed || _current is null)) return;
        if (open)
        {
            _expandedPreviousFocus = Keyboard.FocusedElement;
            SetSettingsVisible(false);
            SetLyricsVisible(false);
            BuildExpandedPlayer();
            _expandedAccountGeneration = _accountGeneration;
            if (fullscreen)
            {
                var bounds = RestoreBounds;
                if (bounds.IsEmpty) bounds = new Rect(Left, Top, Width, Height);
                _expandedWindowPlacement = new(WindowStyle, WindowState, ResizeMode, bounds);
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
            }
            ExpandedPlayerHost.Visibility = Visibility.Visible;
            RefreshExpandedPlayer();
            _expandedPlayerCloseButton?.Focus();
        }
        else
        {
            SetExpandedLyricsVisible(false, animate: false);
            _expandedSeekGesture?.Cancel();
            _expandedSeekDragging = false;
            ExpandedPlayerHost.Visibility = Visibility.Collapsed;
            if (_expandedPlayerCover is not null) Artwork.SetSourceUri(_expandedPlayerCover, null);
            if (_expandedPlayerSeek is not null) _expandedPlayerSeek.IsEnabled = false;
            if (_expandedPlayerCoverButton is not null) _expandedPlayerCoverButton.Tag = null;
            if (_expandedPlayerTitleButton is not null) _expandedPlayerTitleButton.Tag = null;
            if (_expandedPlayerArtistLinks is not null) { _expandedPlayerArtistLinks.Content = null; _expandedPlayerArtistLinks.ContextMenu = null; }
            _expandedCoverUri = null;
            _expandedPlayerTrack = null;
            if (_expandedWindowPlacement is { } previous)
            {
                _expandedWindowPlacement = null;
                WindowState = WindowState.Normal;
                WindowStyle = previous.Style;
                ResizeMode = previous.Resize;
                Left = previous.Bounds.Left; Top = previous.Bounds.Top;
                Width = previous.Bounds.Width; Height = previous.Bounds.Height;
                WindowState = previous.State;
            }
            if (_expandedPreviousFocus is UIElement { IsVisible: true } focus) focus.Focus();
            _expandedPreviousFocus = null;
        }
    }

    private void BuildExpandedPlayer()
    {
        if (_expandedPlayerSurface is not null) return;
        var surface = _expandedPlayerSurface = new Grid { Background = new SolidColorBrush(Color.FromRgb(20, 21, 25)) };
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
        _expandedPlayerCloseButton = ExpandedIconButton("ExpandedPlayerClose", "\uE73F", "Свернуть проигрыватель · Esc", 44, () => CloseExpandedPlayer());
        Grid.SetColumn(_expandedPlayerCloseButton, 2); header.Children.Add(_expandedPlayerCloseButton); surface.Children.Add(header);

        var body = new Grid { Margin = new Thickness(38, 12, 38, 0), MaxWidth = 1500 };
        body.ColumnDefinitions.Add(new()); body.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetRow(body, 1); surface.Children.Add(body);
        var player = new Grid { Margin = new Thickness(16, 4, 16, 0) };
        player.RowDefinitions.Add(new()); player.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.Children.Add(player);
        _expandedPlayerCover = new Image { Width = 560, Height = 560, Stretch = Stretch.UniformToFill, Clip = new RectangleGeometry(new Rect(0, 0, 560, 560), 24, 24) };
        Artwork.SetPixelSize(_expandedPlayerCover, 1024);
        var cover = new Grid { Width = 560, Height = 560 };
        cover.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(40, 34, 34)), CornerRadius = new CornerRadius(24) });
        cover.Children.Add(new TextBlock { Text = "\uE8D6", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 100, Foreground = ExpandedBrush("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
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
        transport.Children.Add(ExpandedForwardButton("ExpandedPlayerPrevious", PreviousButton, "\uE892", "Предыдущий трек", 46));
        var play = ExpandedForwardButton("ExpandedPlayerPlayPause", PlayPauseButton, "\uE768", "Воспроизвести / пауза", 64);
        play.SetResourceReference(Control.BackgroundProperty, "AccentBrush"); play.SetResourceReference(Control.ForegroundProperty, "AccentForegroundBrush");
        play.FontSize = 27; play.Margin = new Thickness(14, 0, 14, 0); transport.Children.Add(play);
        transport.Children.Add(ExpandedForwardButton("ExpandedPlayerNext", NextButton, "\uE893", "Следующий трек", 46));
        var like = ExpandedForwardButton("ExpandedPlayerLike", PlayerLikeButton, "\uEB51", "Мне нравится", 46);
        like.Margin = new Thickness(16, 0, 0, 0); like.SetBinding(Control.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = PlayerLikeButton }); transport.Children.Add(like);
        _expandedPlayerLyricsButton = ExpandedIconButton("ExpandedPlayerLyrics", "Тт", "Текст песни", 46, () => SetExpandedLyricsVisible(!_expandedLyricsOpen));
        _expandedPlayerLyricsButton.FontFamily = new FontFamily("Segoe UI"); _expandedPlayerLyricsButton.FontSize = 20;
        _expandedPlayerLyricsButton.Margin = new Thickness(5, 0, 0, 0); transport.Children.Add(_expandedPlayerLyricsButton);

        _expandedPlayerSeek = new Slider { Name = "ExpandedPlayerSeek", Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 0), SmallChange = 5, LargeChange = 15, IsMoveToPointEnabled = true };
        AutomationProperties.SetName(_expandedPlayerSeek, "Позиция воспроизведения"); details.Children.Add(_expandedPlayerSeek);
        _expandedPlayerSeek.ValueChanged += (_, args) => { if (_expandedSeekDragging && _expandedPlayerPosition is not null) _expandedPlayerPosition.Text = FormatTime(TimeSpan.FromSeconds(args.NewValue)); };
        _expandedSeekGesture = new SliderGesture(_expandedPlayerSeek, () =>
        { _expandedSeekDragging = true; _expandedSeekIgnoreKeyUp = false; _expandedSeekKeyboardGesture = _expandedSeekGesture?.IsKeyboardActive == true; }, FinishExpandedSeek);
        _expandedPlayerSeek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, args) => FinishExpandedSeek(!args.Canceled)), true);
        _expandedPlayerSeek.PreviewKeyUp += (_, args) =>
        {
            if (!IsExpandedPlayerOpen || _expandedAccountGeneration != _accountGeneration || !_expandedPlayerSeek.IsEnabled || !SliderGesture.SeekKey(args.Key)) return;
            if (_expandedSeekIgnoreKeyUp) { _expandedSeekIgnoreKeyUp = false; return; }
            PlaybackTimeline.Stop(_expandedPlayerSeek, useBaseValue: true); CommitSeek(_expandedPlayerSeek.Value); RefreshExpandedPlayer(); args.Handled = true;
        };
        var time = new DockPanel(); details.Children.Add(time);
        _expandedPlayerDuration = new TextBlock { Foreground = ExpandedBrush("MutedBrush"), FontSize = 12 }; DockPanel.SetDock(_expandedPlayerDuration, Dock.Right); time.Children.Add(_expandedPlayerDuration);
        _expandedPlayerPosition = new TextBlock { Foreground = ExpandedBrush("MutedBrush"), FontSize = 12 }; time.Children.Add(_expandedPlayerPosition);
        var volume = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 9, 0, 0) };
        volume.Children.Add(new TextBlock { Text = "\uE767", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Foreground = ExpandedBrush("MutedBrush") });
        var volumeSlider = new Slider { Name = "ExpandedPlayerVolume", Width = 150, Minimum = 0, Maximum = 100, SmallChange = 5, Margin = new Thickness(9, 0, 9, 0) };
        _expandedVolumeGesture = new SliderGesture(volumeSlider, () => { }, _ => FlushVolumePreferences());
        volumeSlider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Slider.Value)) { Source = VolumeSlider, Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(volumeSlider, "Громкость"); volume.Children.Add(volumeSlider);
        var volumeText = new TextBlock { Width = 37, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = ExpandedBrush("MutedBrush") };
        volumeText.SetBinding(TextBlock.TextProperty, new Binding(nameof(Slider.Value)) { Source = VolumeSlider, StringFormat = "{0:0}%" }); volume.Children.Add(volumeText); details.Children.Add(volume);

        BuildExpandedLyrics(body);
        body.SizeChanged += (_, _) => { if (_expandedLyricsOpen) AnimateExpandedLyrics(false); };
        var footer = new TextBlock { Text = "Пробел — пауза     ·     Esc — свернуть", Foreground = ExpandedBrush("MutedBrush"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 16) };
        Grid.SetRow(footer, 2); surface.Children.Add(footer);
        ExpandedPlayerHost.Content = surface;
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
        if (commit && IsExpandedPlayerOpen && _expandedAccountGeneration == _accountGeneration && _expandedPlayerSeek is { IsEnabled: true } seek) CommitSeek(seek.Value);
        RefreshExpandedPlayer();
    }
    private void RefreshExpandedPlayer()
    {
        if (!IsExpandedPlayerOpen) return;
        if (_current is null || _closed || _expandedAccountGeneration != _accountGeneration) { CloseExpandedPlayer(); return; }
        if (_expandedPlayerCover is null || _expandedPlayerSeek is null) return;
        var track = _current;
        if (!ReferenceEquals(_expandedPlayerTrack, track))
        { _expandedPlayerTrack = track; _expandedSeekGesture?.Cancel(); _expandedSeekDragging = false; PlaybackTimeline.Stop(_expandedPlayerSeek); _expandedLyricsFollowIndex = -1; _expandedLyricsFollowUntil = default; }
        if (_expandedCoverUri != track.CoverUri)
        { _expandedCoverUri = track.CoverUri; Artwork.SetSourceUri(_expandedPlayerCover, track.CoverUri); }
        _expandedPlayerTitle!.Text = track.Title; _expandedPlayerTitle.ToolTip = track.Title;
        _expandedPlayerArtist!.Text = track.Artist; _expandedPlayerArtist.ToolTip = track.Artist;
        if (!ReferenceEquals(_expandedPlayerArtistLinks!.Content, track))
        {
            _expandedPlayerArtistLinks.Content = track;
            _expandedPlayerArtistLinks.ContextMenu = track.Artists.Count > 0 ? CreateTrackArtistsMenu(_expandedPlayerArtistLinks, track.Artists) : null;
        }
        var hasArtistLinks = track.Artists.Count > 0 || !string.IsNullOrWhiteSpace(track.Artist);
        _expandedPlayerArtistLinks.Visibility = hasArtistLinks ? Visibility.Visible : Visibility.Collapsed;
        _expandedPlayerArtistLinks.IsEnabled = _session is not null;
        _expandedPlayerArtist.Visibility = hasArtistLinks ? Visibility.Collapsed : Visibility.Visible;
        _expandedPlayerCoverButton!.Tag = _expandedPlayerTitleButton!.Tag = track;
        _expandedPlayerCoverButton.IsEnabled = _expandedPlayerTitleButton.IsEnabled = _session is not null;
        _expandedPlayerSeek.IsEnabled = SeekSlider.IsEnabled;
        if (!_expandedPlayerSeek.IsEnabled) _expandedSeekDragging = false;
        if (_expandedSeekDragging) PlaybackTimeline.Stop(_expandedPlayerSeek, useBaseValue: true);
        if (!_expandedSeekDragging)
        {
            PlaybackTimeline.Update(_expandedPlayerSeek, _pendingSeek ?? SeekSlider.Value, SeekSlider.Maximum,
                _player.IsPlaying && _pendingSeek is null && !_loadingStream && !_closed, _player.SpeedRatio);
            _expandedPlayerPosition!.Text = PositionText.Text;
        }
        _expandedPlayerDuration!.Text = DurationText.Text;
        RefreshExpandedLyrics();
    }

    private void BuildExpandedLyrics(Grid body)
    {
        _expandedLyricsPanel = new Grid { Name = "ExpandedLyricsPanel", Width = 0, Opacity = 0, ClipToBounds = true, Visibility = Visibility.Collapsed };
        _expandedLyricsPanel.RowDefinitions.Add(new() { Height = GridLength.Auto }); _expandedLyricsPanel.RowDefinitions.Add(new());
        Grid.SetColumn(_expandedLyricsPanel, 1); body.Children.Add(_expandedLyricsPanel);
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
        _expandedLyricsList.SetBinding(Selector.SelectedIndexProperty, new Binding(nameof(Selector.SelectedIndex)) { Source = LyricsList, Mode = BindingMode.OneWay });
        _expandedLyricsList.SetBinding(ToolTipProperty, new Binding(nameof(ToolTip)) { Source = LyricsList });
        ScrollViewer.SetHorizontalScrollBarVisibility(_expandedLyricsList, ScrollBarVisibility.Disabled); ScrollViewer.SetVerticalScrollBarVisibility(_expandedLyricsList, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(_expandedLyricsList, true); VirtualizingPanel.SetIsVirtualizing(_expandedLyricsList, true); VirtualizingPanel.SetVirtualizationMode(_expandedLyricsList, VirtualizationMode.Recycling); VirtualizingPanel.SetScrollUnit(_expandedLyricsList, ScrollUnit.Pixel); SmoothScroll.SetEnabled(_expandedLyricsList, true);
        AutomationProperties.SetName(_expandedLyricsList, "Текст текущей песни");
        _expandedLyricsList.PreviewMouseWheel += (_, _) => PauseExpandedLyricsFollow();
        _expandedLyricsList.PreviewTouchDown += (_, _) => PauseExpandedLyricsFollow();
        _expandedLyricsList.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => PauseExpandedLyricsFollow()), true);
        _expandedLyricsList.PreviewKeyDown += (_, args) => { if (args.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) PauseExpandedLyricsFollow(); };
        _expandedLyricsList.PreviewMouseLeftButtonUp += (_, args) =>
        {
            if (!IsExpandedPlayerOpen || !_expandedLyricsOpen || args.ChangedButton != MouseButton.Left || !_player.HasSource || _expandedAccountGeneration != _accountGeneration) return;
            if (ItemsControl.ContainerFromElement(_expandedLyricsList, args.OriginalSource as DependencyObject) is not ListBoxItem item) return;
            var index = _expandedLyricsList.ItemContainerGenerator.IndexFromContainer(item);
            if (index < 0 || index >= _lyricLines.Count) return;
            args.Handled = true; _expandedLyricsFollowUntil = default; _expandedLyricsFollowIndex = -1;
            CommitSeek(_lyricLines[index].TimeSeconds); RefreshExpandedPlayer();
        };
        Grid.SetRow(_expandedLyricsList, 1); _expandedLyricsPanel.Children.Add(_expandedLyricsList);
    }
    private void SetExpandedLyricsVisible(bool visible, bool animate = true)
    {
        if (_expandedLyricsOpen == visible) return;
        _expandedLyricsOpen = visible;
        // Share the existing current-track request, fallback, cache and 100ms clock.
        // The ordinary lyrics panel remains hidden behind this inline surface.
        _lyricsOpen = visible;
        UpdateLyricsTimer();
        _expandedLyricsFollowUntil = default; _expandedLyricsFollowIndex = -1;
        if (_expandedPlayerLyricsButton is not null)
        {
            _expandedPlayerLyricsButton.Foreground = ExpandedBrush(visible ? "AccentBrush" : "MutedBrush");
            _expandedPlayerLyricsButton.ToolTip = visible ? "Скрыть текст песни" : "Текст песни";
            AutomationProperties.SetName(_expandedPlayerLyricsButton, visible ? "Скрыть текст песни" : "Текст песни");
        }
        AnimateExpandedLyrics(animate);
        if (visible && _lyricsCts is null) RefreshLyrics();
        RefreshExpandedLyrics();
    }
    private void AnimateExpandedLyrics(bool animate)
    {
        if (_expandedLyricsPanel is null || _expandedLyricsPanel.Parent is not Grid body) return;
        var panel = _expandedLyricsPanel;
        var target = _expandedLyricsOpen ? Math.Max(300, Math.Min(650, body.ActualWidth * .49)) : 0;
        var from = panel.ActualWidth; var opacity = panel.Opacity;
        panel.BeginAnimation(WidthProperty, null); panel.BeginAnimation(OpacityProperty, null);
        panel.Width = target; panel.Opacity = _expandedLyricsOpen ? 1 : 0;
        if (_expandedLyricsOpen) panel.Visibility = Visibility.Visible;
        var duration = animate && SystemParameters.ClientAreaAnimation ? TimeSpan.FromMilliseconds(320) : TimeSpan.Zero;
        if (duration == TimeSpan.Zero) { panel.Visibility = _expandedLyricsOpen ? Visibility.Visible : Visibility.Collapsed; return; }
        var width = new DoubleAnimation(from, target, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop };
        width.Completed += (_, _) =>
        {
            if (!_expandedLyricsOpen) panel.Visibility = Visibility.Collapsed;
            else { _expandedLyricsFollowIndex = -1; RefreshExpandedLyrics(); }
        };
        panel.BeginAnimation(WidthProperty, width);
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, _expandedLyricsOpen ? 1 : 0, duration) { FillBehavior = FillBehavior.Stop });
    }
    private void PauseExpandedLyricsFollow()
    {
        _expandedLyricsFollowUntil = DateTimeOffset.UtcNow.AddMilliseconds(3500);
        if (_expandedLyricsList is not null && FindViewer(_expandedLyricsList) is { } viewer) SmoothScroll.CancelAnimation(viewer);
    }
    private void RefreshExpandedLyrics()
    {
        if (!IsExpandedPlayerOpen || !_expandedLyricsOpen || _expandedLyricsList is null) return;
        if (_expandedAccountGeneration != _accountGeneration || _current is null) { CloseExpandedPlayer(); return; }
        var index = LyricsList.SelectedIndex;
        if (index < 0 || index >= _expandedLyricsList.Items.Count || DateTimeOffset.UtcNow < _expandedLyricsFollowUntil) return;
        if (_expandedLyricsFollowIndex == index && _expandedLyricsFollowUntil == default) return;
        _expandedLyricsFollowUntil = default;
        if (FindViewer(_expandedLyricsList) is not { } viewer) return;
        _expandedLyricsFollowIndex = index;
        if (_expandedLyricsList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement row)
        { _expandedLyricsList.ScrollIntoView(_expandedLyricsList.Items[index]); return; }
        var point = row.TranslatePoint(new Point(0, 0), viewer);
        SmoothScroll.ScrollTo(viewer, viewer.VerticalOffset + point.Y + row.ActualHeight / 2 - viewer.ViewportHeight * .45);
    }
}
