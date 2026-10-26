using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;

namespace YMusic.Native;

// A view of the main player's state. WPF animates its display between native
// observations; it owns no playback timer, queue, media engine or network.
internal sealed class MiniPlayerWindow : Window
{
    private const double HeaderHeight = 32;
    private static Brush Muted => ThemeManager.Brush("#989BA7");
    private readonly TextBlock _coverPlaceholder;
    private readonly TextBlock _playbackStatus;
    private readonly Border _artwork;
    private readonly ColumnDefinition _artworkColumn;
    private readonly StackPanel _metadata;
    private readonly Grid _layout, _info, _controls;
    private readonly StackPanel _volume, _transport;
    private Size? _artworkWindowSize, _autoCompactSize;
    private bool _layingOut;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly TextBlock _volumeText;
    private bool _hasTrack, _canSeek, _pointerSeeking, _keySeeking, _seekDirty, _syncingSeek, _syncingVolume;
    private double _position, _duration;
    private string _title = "", _artist = "";
    private string? _trackKey;
    private bool _isPlaying;
    private SliderGesture? _seekGesture, _volumeGesture;

    internal event Action<ShellAction>? Command;
    internal event Action<double>? SeekRequested;
    internal event Action<double>? VolumeRequested;
    internal event Action? ShowMainRequested;
    internal TextBlock TitleText { get; }
    internal TextBlock ArtistText { get; }
    internal Image CoverImage { get; }
    internal Button PlayButton { get; }
    internal Button LikeButton { get; }
    internal Slider SeekSlider { get; }
    internal Slider VolumeSlider { get; }
    internal TextBlock PositionText { get; }
    internal TextBlock DurationText { get; }
    internal ToggleButton PinButton { get; }
    internal Button ArtworkButton { get; }
    internal bool IsArtworkVisible { get; private set; } = true;
    internal bool IsSeeking => _pointerSeeking || _keySeeking;

    internal MiniPlayerWindow()
    {
        ThemeManager.Initialize(Application.Current);
        WindowAppearance.Apply(this);
        Title = "Мини-плеер · " + AppBranding.Name; Icon = AppBranding.WindowIcon;
        Width = 484; Height = 300; MinWidth = 320; MinHeight = 224;
        ResizeMode = ResizeMode.CanResize; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(14), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false
        });
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
        UseLayoutRounding = true; Background = Brushes.Transparent; Foreground = Brush("#F4F1F0");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 12;
        var frame = new Border { Background = Brush("#181A20"), BorderBrush = Brush("#353740"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14) };
        frame.SizeChanged += (_, _) => frame.Clip = new RectangleGeometry(new Rect(0, 0, frame.ActualWidth, frame.ActualHeight), 14, 14);
        var root = _layout = new Grid { Margin = new Thickness(14, 8, 14, 10) };
        root.ColumnDefinitions.Add(new ColumnDefinition()); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        frame.Child = root; Content = frame;

        var header = new Grid { Background = Brushes.Transparent };
        Grid.SetColumnSpan(header, 2);
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Image { Source = AppBranding.WindowIcon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) });
        brand.Children.Add(new TextBlock { Text = AppBranding.Name, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Muted });
        header.Children.Add(brand);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        ArtworkButton = IconButton("\uE91B", "Скрыть обложку", () => SetArtworkVisible(!IsArtworkVisible), 30, 30, 14);
        ArtworkButton.Foreground = Muted; ArtworkButton.Margin = new Thickness(0, 0, 3, 0);
        AutomationProperties.SetHelpText(ArtworkButton, "Обложка показана");
        tools.Children.Add(ArtworkButton);
        PinButton = new ToggleButton { Content = Glyph("\uE718", 13), Width = 30, Height = 30, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Muted, ToolTip = "Поверх других окон", Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
        PinButton.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ToggleButton">
              <Border x:Name="Surface" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" CornerRadius="8" BorderThickness="1" Background="{TemplateBinding Background}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
              <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="{DynamicResource HoverBrush}"/></Trigger><Trigger Property="IsChecked" Value="True"><Setter TargetName="Surface" Property="Background" Value="{DynamicResource AccentSoftBrush}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        AutomationProperties.SetName(PinButton, "Поверх других окон");
        PinButton.Checked += (_, _) => { Topmost = true; PinButton.SetResourceReference(ForegroundProperty, "AccentTextBrush"); };
        PinButton.Unchecked += (_, _) => { Topmost = false; PinButton.Foreground = Muted; };
        tools.Children.Add(PinButton);
        tools.Children.Add(IconButton("\uE8A7", "Открыть основное окно", () => ShowMainRequested?.Invoke(), 30, 30, 13));
        tools.Children.Add(IconButton("\uE8BB", "Закрыть мини-плеер", Close, 30, 30, 12));
        Grid.SetColumn(tools, 1); header.Children.Add(tools); root.Children.Add(header);
        header.MouseLeftButtonDown += (_, args) =>
        {
            for (var element = args.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, header); element = element is Visual ? VisualTreeHelper.GetParent(element) : (element as FrameworkContentElement)?.Parent)
                if (element is ButtonBase) return;
            if (args.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } }
        };

        var info = _info = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
        info.RowDefinitions.Add(new RowDefinition()); info.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        _artworkColumn = new ColumnDefinition { Width = GridLength.Auto };
        info.ColumnDefinitions.Add(_artworkColumn);
        info.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 0 }); info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        _artwork = new Border { Width = 100, Height = 100, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(12), Background = Brush("#292B33"), ClipToBounds = true };
        var coverLayers = new Grid();
        _coverPlaceholder = Glyph("\uE8D6", 29); _coverPlaceholder.Foreground = Muted;
        coverLayers.Children.Add(_coverPlaceholder);
        CoverImage = new Image { Stretch = Stretch.UniformToFill };
        coverLayers.Children.Add(CoverImage); _artwork.Child = coverLayers; info.Children.Add(_artwork);
        _artwork.SizeChanged += (_, _) => coverLayers.Clip = new RectangleGeometry(new Rect(0, 0, _artwork.ActualWidth, _artwork.ActualHeight), 12, 12);
        _metadata = new StackPanel { Margin = new Thickness(16, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        _playbackStatus = new TextBlock { FontSize = 9, Foreground = Muted, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 7, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        TitleText = new TextBlock { FontSize = 17, LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = 42, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        ArtistText = new TextBlock { FontSize = 12, LineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = 30, TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _metadata.Children.Add(_playbackStatus); _metadata.Children.Add(TitleText); _metadata.Children.Add(ArtistText); Grid.SetColumn(_metadata, 1); info.Children.Add(_metadata);
        LikeButton = IconButton("\uEB51", "Добавить в Мне нравится", () => Command?.Invoke(ShellAction.ToggleLike), 36, 36, 19);
        LikeButton.Background = Brush("#23262E"); LikeButton.Template = ButtonTemplate(12);
        Grid.SetColumn(LikeButton, 2); info.Children.Add(LikeButton); Grid.SetRow(info, 1); root.Children.Add(info);

        var timeline = new Grid();
        Grid.SetColumnSpan(timeline, 2);
        timeline.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) }); timeline.RowDefinitions.Add(new RowDefinition());
        SeekSlider = Slider(0, 1); SeekSlider.IsMoveToPointEnabled = true; SeekSlider.SmallChange = 5; SeekSlider.LargeChange = 15;
        AutomationProperties.SetName(SeekSlider, "Позиция воспроизведения");
        timeline.Children.Add(SeekSlider);
        var times = new DockPanel { LastChildFill = false };
        PositionText = new TextBlock { Text = "0:00", Foreground = Muted, FontSize = 10, Margin = new Thickness(4, 0, 0, 0) };
        DurationText = new TextBlock { Text = "0:00", Foreground = Muted, FontSize = 10, Margin = new Thickness(0, 0, 4, 0) };
        DockPanel.SetDock(DurationText, Dock.Right); times.Children.Add(DurationText); times.Children.Add(PositionText);
        Grid.SetRow(times, 1); timeline.Children.Add(times); Grid.SetRow(timeline, 3); root.Children.Add(timeline);

        var controls = _controls = new Grid();
        controls.RowDefinitions.Add(new RowDefinition()); controls.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        controls.ColumnDefinitions.Add(new ColumnDefinition()); controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); controls.ColumnDefinitions.Add(new ColumnDefinition());
        var volume = _volume = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var volumeGlyph = Glyph("\uE767", 14); volumeGlyph.Foreground = Muted; volume.Children.Add(volumeGlyph);
        VolumeSlider = Slider(0, 100); VolumeSlider.Width = 66; VolumeSlider.Margin = new Thickness(8, 0, 0, 0); VolumeSlider.SmallChange = 5; VolumeSlider.LargeChange = 10;
        AutomationProperties.SetName(VolumeSlider, "Громкость"); volume.Children.Add(VolumeSlider); controls.Children.Add(volume);
        var transport = _transport = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _previous = IconButton("\uE892", "Предыдущий трек", () => Command?.Invoke(ShellAction.Previous), 38, 40, 17);
        PlayButton = IconButton("\uE768", "Играть", () => Command?.Invoke(ShellAction.PlayPause), 50, 50, 18);
        PlayButton.Template = ButtonTemplate(25);
        PlayButton.SetResourceReference(BackgroundProperty, "AccentBrush"); PlayButton.SetResourceReference(ForegroundProperty, "AccentForegroundBrush"); PlayButton.Margin = new Thickness(6, 0, 6, 0);
        _next = IconButton("\uE893", "Следующий трек", () => Command?.Invoke(ShellAction.Next), 38, 40, 17);
        transport.Children.Add(_previous); transport.Children.Add(PlayButton); transport.Children.Add(_next); Grid.SetColumn(transport, 1); controls.Children.Add(transport);
        _volumeText = new TextBlock { Foreground = Muted, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_volumeText, 2); controls.Children.Add(_volumeText); Grid.SetRow(controls, 2); root.Children.Add(controls);

        SizeChanged += (_, _) => UpdateResponsiveLayout();
        UpdateResponsiveLayout();

        _seekGesture = new SliderGesture(SeekSlider, () => BeginSeek(_seekGesture?.IsKeyboardActive == true),
            commit => { if (commit) CompleteSeek(_keySeeking); else CancelSeek(); }, registerPointer: false);
        SeekSlider.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(_seekGesture.Start), true);
        SeekSlider.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(_seekGesture.End), true);
        _volumeGesture = new SliderGesture(VolumeSlider, () => { }, _ => { });
        Deactivated += (_, _) => { if (_keySeeking) _seekGesture.Complete(true); else _seekGesture.Cancel(); };
        SeekSlider.ValueChanged += (_, _) =>
        {
            if (_syncingSeek || !IsSeeking) return;
            _seekDirty = true; PositionText.Text = Time(SeekSlider.Value);
        };
        VolumeSlider.ValueChanged += (_, _) =>
        {
            _volumeText.Text = $"{Math.Round(VolumeSlider.Value):0}%";
            if (!_syncingVolume) VolumeRequested?.Invoke(VolumeSlider.Value);
        };
        Closed += (_, _) => { _seekGesture.Cancel(); _volumeGesture.Cancel(); PlaybackTimeline.Stop(SeekSlider); Command = null; SeekRequested = null; VolumeRequested = null; ShowMainRequested = null; };
        Update("", "", false, false, false); UpdateDetails(null, 0, 0, false, false, false, 70);
    }

    internal void Update(string title, string artist, bool isPlaying, bool canNext, bool canPrevious)
    {
        VerifyAccess();
        if (_title != title || _artist != artist)
        {
            CancelSeek(); _canSeek = false; SeekSlider.IsEnabled = false;
            CoverImage.Source = null; _coverPlaceholder.Visibility = Visibility.Visible;
        }
        _title = title; _artist = artist; _hasTrack = !string.IsNullOrWhiteSpace(title); _isPlaying = isPlaying;
        if (!isPlaying) PlaybackTimeline.Stop(SeekSlider);
        TitleText.Text = _hasTrack ? title : "Музыка рядом"; TitleText.ToolTip = _hasTrack ? title : null;
        ArtistText.Text = _hasTrack && !string.IsNullOrWhiteSpace(artist) ? artist : "Выберите трек в основном окне";
        ArtistText.ToolTip = ArtistText.Text;
        _playbackStatus.Text = !_hasTrack ? "ВАША МУЗЫКА" : isPlaying ? "СЕЙЧАС ИГРАЕТ" : "НА ПАУЗЕ";
        PlayButton.Content = Glyph(isPlaying ? "\uE769" : "\uE768", 18);
        PlayButton.ToolTip = isPlaying ? "Пауза" : "Играть"; AutomationProperties.SetName(PlayButton, isPlaying ? "Пауза" : "Играть");
        PlayButton.IsEnabled = _hasTrack; _next.IsEnabled = _hasTrack && canNext; _previous.IsEnabled = _hasTrack && canPrevious;
        if (!_hasTrack) UpdateDetails(null, 0, 0, false, false, false, VolumeSlider.Value);
    }

    internal void UpdateDetails(ImageSource? cover, double positionSeconds, double durationSeconds, bool canSeek, bool isLiked, bool canLike, double volumePercent, string? trackKey = null, double speedRatio = 1, bool advancePlayhead = false)
    {
        VerifyAccess();
        if (!string.Equals(_trackKey, trackKey, StringComparison.Ordinal)) { CancelSeek(); _trackKey = trackKey; }
        var duration = _hasTrack && double.IsFinite(durationSeconds) ? Math.Max(0, durationSeconds) : 0;
        var allowSeek = _hasTrack && canSeek && duration > 0;
        if (IsSeeking && (!allowSeek || Math.Abs(_duration - duration) > .01)) CancelSeek();
        _duration = duration; _position = double.IsFinite(positionSeconds) ? Math.Clamp(positionSeconds, 0, duration) : 0;
        _canSeek = allowSeek; SeekSlider.IsEnabled = allowSeek;
        _syncingSeek = true;
        try
        {
            if (!IsSeeking) { PlaybackTimeline.Update(SeekSlider, _position, duration, _isPlaying && allowSeek && advancePlayhead, speedRatio); PositionText.Text = Time(_position); }
        }
        finally { _syncingSeek = false; }
        DurationText.Text = Time(duration);
        CoverImage.Source = _hasTrack ? cover : null; _coverPlaceholder.Visibility = CoverImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        LikeButton.IsEnabled = _hasTrack && canLike; LikeButton.Content = Glyph(isLiked && _hasTrack ? "\uEB52" : "\uEB51", 19);
        if (isLiked && _hasTrack) LikeButton.SetResourceReference(ForegroundProperty, "AccentTextBrush"); else LikeButton.Foreground = Foreground;
        LikeButton.ToolTip = isLiked ? "Убрать из Мне нравится" : "Добавить в Мне нравится"; AutomationProperties.SetName(LikeButton, LikeButton.ToolTip.ToString());
        _syncingVolume = true;
        try { if (_volumeGesture?.IsActive != true) VolumeSlider.Value = double.IsFinite(volumePercent) ? Math.Clamp(volumePercent, 0, 100) : 0; }
        finally { _syncingVolume = false; }
    }

    private void SetArtworkVisible(bool visible)
    {
        if (visible == IsArtworkVisible) return;
        var width = double.IsFinite(Width) ? Width : ActualWidth;
        var height = double.IsFinite(Height) ? Height : ActualHeight;
        var restore = visible && _autoCompactSize is { } compact && Math.Abs(compact.Width - width) < 2 && Math.Abs(compact.Height - height) < 2;
        if (!visible) _artworkWindowSize = new Size(width, height);
        IsArtworkVisible = visible;
        _artwork.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MinWidth = visible ? 320 : 280;
        MinHeight = visible ? 224 : width >= 620 ? 156 : 184;
        if (!visible)
        {
            Height = Math.Min(height, MinHeight);
            _autoCompactSize = new Size(Width, Height);
        }
        else if (restore && _artworkWindowSize is { } expanded)
        {
            Width = expanded.Width; Height = expanded.Height;
        }
        else
        {
            Width = Math.Max(width, MinWidth); Height = Math.Max(height, MinHeight);
        }
        UpdateResponsiveLayout();
        ArtworkButton.Content = Glyph(visible ? "\uE91B" : "\uEB9F", 14);
        ArtworkButton.ToolTip = visible ? "Скрыть обложку" : "Показать обложку";
        AutomationProperties.SetName(ArtworkButton, (string)ArtworkButton.ToolTip);
        AutomationProperties.SetHelpText(ArtworkButton, visible ? "Обложка показана" : "Обложка скрыта");
    }

    private void UpdateResponsiveLayout()
    {
        if (_layingOut) return;
        _layingOut = true;
        try
        {
            var width = ActualWidth > 0 ? ActualWidth : Width;
            var height = ActualHeight > 0 ? ActualHeight : Height;
            var inline = !IsArtworkVisible && width >= 620;
            var portrait = IsArtworkVisible && height >= 340 && (width < 420 || height >= width * .82);
            MinHeight = IsArtworkVisible ? 224 : inline ? 156 : 184;
            var contentWidth = Math.Max(250, width - 30);
            var controlsHeight = IsArtworkVisible ? 52 : 42;
            var infoHeight = Math.Max(54, height - 20 - HeaderHeight - 36 - (inline ? 0 : controlsHeight));

            // A wide coverless window becomes a slim bar; narrow windows keep
            // separate transport and timeline rows. The timeline always stretches.
            _layout.ColumnDefinitions[1].Width = new GridLength(inline ? 224 : 0);
            _layout.RowDefinitions[2].Height = new GridLength(inline ? 0 : controlsHeight);
            Grid.SetColumn(_info, 0); Grid.SetColumnSpan(_info, inline ? 1 : 2);
            Grid.SetRow(_controls, inline ? 1 : 2); Grid.SetColumn(_controls, inline ? 1 : 0); Grid.SetColumnSpan(_controls, inline ? 1 : 2);
            _info.Margin = new Thickness(0, IsArtworkVisible ? 4 : 0, inline ? 12 : 0, IsArtworkVisible ? 4 : 0);
            _artworkColumn.Width = IsArtworkVisible && !portrait ? GridLength.Auto : new GridLength(0);
            _info.RowDefinitions[1].Height = portrait ? GridLength.Auto : new GridLength(0);
            Grid.SetRow(_artwork, 0); Grid.SetColumn(_artwork, 0); Grid.SetColumnSpan(_artwork, portrait ? 3 : 1);
            _artwork.HorizontalAlignment = portrait ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            _artwork.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(_metadata, portrait ? 1 : 0); Grid.SetColumn(_metadata, IsArtworkVisible && !portrait ? 1 : 0);
            Grid.SetColumnSpan(_metadata, portrait ? 3 : IsArtworkVisible ? 1 : 2);
            _metadata.Margin = portrait ? new Thickness(0, 10, 0, 0) : new Thickness(IsArtworkVisible ? 12 : 0, 0, 9, 0);
            TitleText.TextAlignment = ArtistText.TextAlignment = portrait ? TextAlignment.Center : TextAlignment.Left;
            _playbackStatus.Visibility = IsArtworkVisible && !portrait && width >= 420 && height >= 280 ? Visibility.Visible : Visibility.Collapsed;
            TitleText.FontSize = portrait ? 18 : IsArtworkVisible ? 17 : 16;
            TitleText.LineHeight = 21; TitleText.MaxHeight = IsArtworkVisible ? 42 : 21;
            TitleText.TextWrapping = IsArtworkVisible ? TextWrapping.Wrap : TextWrapping.NoWrap;
            LikeButton.VerticalAlignment = portrait ? VerticalAlignment.Top : VerticalAlignment.Center;

            var coverSize = Math.Round(portrait ? Math.Clamp(Math.Min(contentWidth - 80, infoHeight - 90), 72, 240)
                : Math.Clamp(Math.Min(contentWidth * .24, infoHeight - 16), 64, 180));
            _artwork.Width = _artwork.Height = coverSize;
            CoverImage.Width = CoverImage.Height = coverSize;
            // Grid's auto/star measurement may first offer infinite width to
            // the metadata stack. Bound the text to the final slot so it wraps
            // during measure instead of being ellipsized only during arrange.
            var metadataWidth = portrait ? contentWidth : IsArtworkVisible ? contentWidth - coverSize - 57
                : contentWidth - (inline ? 224 + 12 : 0) - 45;
            TitleText.MaxWidth = ArtistText.MaxWidth = Math.Max(100, metadataWidth);
            var playSize = IsArtworkVisible ? 48d : 40d;
            PlayButton.Width = PlayButton.Height = playSize;
            _previous.Width = _next.Width = IsArtworkVisible ? 34 : 30;
            _previous.Height = _next.Height = 38;
            var transportWidth = playSize + _previous.Width + _next.Width + 12;
            VolumeSlider.Width = inline ? 110 : Math.Round(Math.Clamp((contentWidth - transportWidth) / 2 - 24, 38, 110));
            _controls.RowDefinitions[1].Height = new GridLength(inline ? 24 : 0);
            _controls.ColumnDefinitions[1].Width = inline ? new GridLength(0) : GridLength.Auto;
            _controls.ColumnDefinitions[2].Width = inline ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            Grid.SetRow(_transport, 0); Grid.SetColumn(_transport, inline ? 0 : 1); Grid.SetColumnSpan(_transport, inline ? 3 : 1);
            _transport.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(_volume, inline ? 1 : 0); Grid.SetColumn(_volume, 0); Grid.SetColumnSpan(_volume, inline ? 2 : 1);
            Grid.SetRow(_volumeText, inline ? 1 : 0);
            SeekSlider.Width = double.NaN; SeekSlider.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        finally { _layingOut = false; }
    }

    private void BeginSeek(bool keyboard)
    {
        if (!_canSeek || (keyboard ? _keySeeking : _pointerSeeking)) return;
        if (!IsSeeking) _seekDirty = false;
        if (keyboard) _keySeeking = true; else _pointerSeeking = true;
    }

    private void CompleteSeek(bool keyboard)
    {
        if (!(keyboard ? _keySeeking : _pointerSeeking)) return;
        // Move-to-point may set Value in Slider's class handler before our
        // preview handler starts the gesture; compare against the player too.
        var commit = _canSeek && _hasTrack && (_seekDirty || Math.Abs(SeekSlider.Value - _position) > .001);
        var position = Math.Clamp(SeekSlider.Value, 0, _duration);
        _pointerSeeking = _keySeeking = _seekDirty = false;
        if (ReferenceEquals(Mouse.Captured, SeekSlider)) SeekSlider.ReleaseMouseCapture();
        if (commit) { _position = position; SeekRequested?.Invoke(position); }
    }

    private void CancelSeek()
    {
        _pointerSeeking = _keySeeking = _seekDirty = false;
        if (SeekSlider is null || PositionText is null) return;
        if (_seekGesture?.IsActive == true) _seekGesture.Cancel();
        PlaybackTimeline.Stop(SeekSlider);
        if (ReferenceEquals(Mouse.Captured, SeekSlider)) SeekSlider.ReleaseMouseCapture();
        _syncingSeek = true;
        try { SeekSlider.Value = Math.Clamp(_position, 0, SeekSlider.Maximum); PositionText.Text = Time(_position); }
        finally { _syncingSeek = false; }
    }

    private static bool SeekKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;
    private bool InsideThumb(DependencyObject? source)
    {
        for (var element = source; element is not null && !ReferenceEquals(element, SeekSlider); element = element is Visual ? VisualTreeHelper.GetParent(element) : (element as FrameworkContentElement)?.Parent)
            if (element is Thumb) return true;
        return false;
    }
    private static string Time(double seconds)
    {
        var whole = Math.Floor(Math.Max(0, seconds));
        return whole >= 3600 ? $"{Math.Floor(whole / 3600):0}:{Math.Floor(whole / 60) % 60:00}:{whole % 60:00}" : $"{Math.Floor(whole / 60):0}:{whole % 60:00}";
    }
    private static Brush Brush(string color) => ThemeManager.Brush(color);
    private static TextBlock Glyph(string glyph, double size) => new() { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private static Slider Slider(double minimum, double maximum)
    {
        var slider = new Slider
        {
        Minimum = minimum, Maximum = maximum, Height = 22, VerticalAlignment = VerticalAlignment.Center,
        Background = Brush("#3A3C45"), BorderThickness = new Thickness(0), IsSnapToTickEnabled = false, FocusVisualStyle = null,
        Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
              <Grid x:Name="Surface" Background="Transparent">
                <Border Height="4" Margin="5,0" CornerRadius="2" Background="{DynamicResource TrackBrush}" VerticalAlignment="Center"/>
                <Track x:Name="PART_Track" Orientation="Horizontal" IsDirectionReversed="False">
                  <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Background="{DynamicResource AccentBrush}" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" Margin="5,0,0,0" CornerRadius="2" Background="{TemplateBinding Background}" VerticalAlignment="Center"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
                  <Track.Thumb><Thumb Width="10" Height="10"><Thumb.Template><ControlTemplate TargetType="Thumb"><Ellipse Fill="{DynamicResource AccentBrush}"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                  <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
                </Track>
              </Grid>
              <ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value="0.35"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """)
        };
        slider.SetResourceReference(ForegroundProperty, "AccentBrush");
        return slider;
    }
    private static ControlTemplate ButtonTemplate(int radius) => (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Grid x:Name="Surface"><Border CornerRadius="RADIUS" Background="{TemplateBinding Background}"/><Border x:Name="Hover" CornerRadius="RADIUS" Background="{DynamicResource HoverOverlayBrush}" Opacity="0" IsHitTestVisible="False"/><Border x:Name="Focus" CornerRadius="RADIUS" BorderBrush="{DynamicResource AccentBrush}" BorderThickness="1" Opacity="0" IsHitTestVisible="False"/><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Grid>
          <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Hover" Property="Opacity" Value="0.08"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="Hover" Property="Opacity" Value="0.15"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="Opacity" Value="0.8"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value="0.3"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
        """.Replace("RADIUS", radius.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    private static Button IconButton(string glyph, string label, Action action, double width, double height, double size)
    {
        var button = new Button { Content = Glyph(glyph, size), Width = width, Height = height, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Brush("#EEECEE"), ToolTip = label, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Template = ButtonTemplate(8) };
        AutomationProperties.SetName(button, label); button.Click += (_, _) => { if (button.IsEnabled) action(); }; return button;
    }
}
