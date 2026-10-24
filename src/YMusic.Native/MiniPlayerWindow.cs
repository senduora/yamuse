using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace YMusic.Native;

// A view of the main player's state. No clock, queue, media engine or network.
internal sealed class MiniPlayerWindow : Window
{
    private static readonly Brush Muted = Brush("#989BA7");
    private static readonly Brush Accent = Brush("#FF705E");
    private readonly TextBlock _coverPlaceholder;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly TextBlock _volumeText;
    private bool _hasTrack, _canSeek, _pointerSeeking, _keySeeking, _seekDirty, _syncingSeek, _syncingVolume;
    private double _position, _duration;
    private string _title = "", _artist = "";
    private string? _trackKey;

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
    internal bool IsSeeking => _pointerSeeking || _keySeeking;

    internal MiniPlayerWindow()
    {
        Title = "Мини-плеер · " + AppBranding.Name; Icon = AppBranding.WindowIcon;
        Width = 440; Height = 258; MinWidth = 440; MinHeight = 258;
        ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
        UseLayoutRounding = true; Background = Brush("#17191E"); Foreground = Brush("#F4F1F0");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 12;
        var frame = new Border { Background = Background, BorderBrush = Brush("#353740"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14) };
        var root = new Grid { Margin = new Thickness(18, 11, 18, 13) };
        foreach (var height in new[] { 25d, 93, 54, 48 }) root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        frame.Child = root; Content = frame;

        var header = new Grid { Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Image { Source = AppBranding.WindowIcon, Width = 17, Height = 17, Margin = new Thickness(0, 0, 7, 0) });
        brand.Children.Add(new TextBlock { Text = AppBranding.Name, FontWeight = FontWeights.SemiBold, Foreground = Muted });
        header.Children.Add(brand);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        PinButton = new ToggleButton { Content = Glyph("\uE718", 12), Width = 29, Height = 25, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Muted, ToolTip = "Поверх других окон" };
        PinButton.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ToggleButton">
              <Border x:Name="Surface" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" CornerRadius="6" Background="{TemplateBinding Background}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
              <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="#30323B"/></Trigger><Trigger Property="IsChecked" Value="True"><Setter TargetName="Surface" Property="Background" Value="#352724"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        AutomationProperties.SetName(PinButton, "Поверх других окон");
        PinButton.Checked += (_, _) => { Topmost = true; PinButton.Foreground = Accent; };
        PinButton.Unchecked += (_, _) => { Topmost = false; PinButton.Foreground = Muted; };
        tools.Children.Add(PinButton);
        tools.Children.Add(IconButton("\uE8A7", "Открыть основное окно", () => ShowMainRequested?.Invoke(), 29, 25, 12));
        tools.Children.Add(IconButton("\uE8BB", "Закрыть мини-плеер", Close, 29, 25, 12));
        Grid.SetColumn(tools, 1); header.Children.Add(tools); root.Children.Add(header);
        header.MouseLeftButtonDown += (_, args) =>
        {
            for (var element = args.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, header); element = element is Visual ? VisualTreeHelper.GetParent(element) : (element as FrameworkContentElement)?.Parent)
                if (element is ButtonBase) return;
            if (args.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } }
        };

        var info = new Grid { Margin = new Thickness(0, 9, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        info.ColumnDefinitions.Add(new ColumnDefinition()); info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(37) });
        var cover = new Border { Width = 78, Height = 78, CornerRadius = new CornerRadius(11), Background = Brush("#292B33"), ClipToBounds = true };
        var coverLayers = new Grid();
        _coverPlaceholder = Glyph("\uE8D6", 27); _coverPlaceholder.Foreground = Muted;
        coverLayers.Children.Add(_coverPlaceholder);
        CoverImage = new Image { Width = 78, Height = 78, Stretch = Stretch.UniformToFill, Clip = new RectangleGeometry(new Rect(0, 0, 78, 78), 11, 11) };
        coverLayers.Children.Add(CoverImage); cover.Child = coverLayers; info.Children.Add(cover);
        var metadata = new StackPanel { Margin = new Thickness(14, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        TitleText = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        ArtistText = new TextBlock { FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 7, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        metadata.Children.Add(TitleText); metadata.Children.Add(ArtistText); Grid.SetColumn(metadata, 1); info.Children.Add(metadata);
        LikeButton = IconButton("\uEB51", "Добавить в Мне нравится", () => Command?.Invoke(ShellAction.ToggleLike), 34, 34, 19);
        Grid.SetColumn(LikeButton, 2); info.Children.Add(LikeButton); Grid.SetRow(info, 1); root.Children.Add(info);

        var timeline = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        timeline.RowDefinitions.Add(new RowDefinition { Height = new GridLength(23) }); timeline.RowDefinitions.Add(new RowDefinition());
        SeekSlider = Slider(0, 1); SeekSlider.IsMoveToPointEnabled = true; SeekSlider.SmallChange = 5; SeekSlider.LargeChange = 15;
        AutomationProperties.SetName(SeekSlider, "Позиция воспроизведения");
        timeline.Children.Add(SeekSlider);
        var times = new DockPanel { LastChildFill = false };
        PositionText = new TextBlock { Text = "0:00", Foreground = Muted, FontSize = 10 };
        DurationText = new TextBlock { Text = "0:00", Foreground = Muted, FontSize = 10 };
        DockPanel.SetDock(DurationText, Dock.Right); times.Children.Add(DurationText); times.Children.Add(PositionText);
        Grid.SetRow(times, 1); timeline.Children.Add(times); Grid.SetRow(timeline, 2); root.Children.Add(timeline);

        var controls = new Grid();
        controls.ColumnDefinitions.Add(new ColumnDefinition()); controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); controls.ColumnDefinitions.Add(new ColumnDefinition());
        var volume = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        volume.Children.Add(Glyph("\uE767", 14));
        VolumeSlider = Slider(0, 100); VolumeSlider.Width = 66; VolumeSlider.Margin = new Thickness(8, 0, 0, 0); VolumeSlider.SmallChange = 5; VolumeSlider.LargeChange = 10;
        AutomationProperties.SetName(VolumeSlider, "Громкость"); volume.Children.Add(VolumeSlider); controls.Children.Add(volume);
        var transport = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _previous = IconButton("\uE892", "Предыдущий трек", () => Command?.Invoke(ShellAction.Previous), 37, 39, 17);
        PlayButton = IconButton("\uE768", "Играть", () => Command?.Invoke(ShellAction.PlayPause), 44, 44, 18);
        PlayButton.Template = ButtonTemplate(22);
        PlayButton.Background = Accent; PlayButton.Foreground = Brush("#21191B"); PlayButton.Margin = new Thickness(7, 0, 7, 0);
        _next = IconButton("\uE893", "Следующий трек", () => Command?.Invoke(ShellAction.Next), 37, 39, 17);
        transport.Children.Add(_previous); transport.Children.Add(PlayButton); transport.Children.Add(_next); Grid.SetColumn(transport, 1); controls.Children.Add(transport);
        _volumeText = new TextBlock { Foreground = Muted, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_volumeText, 2); controls.Children.Add(_volumeText); Grid.SetRow(controls, 3); root.Children.Add(controls);

        SeekSlider.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, args) =>
        {
            if (args.ChangedButton != MouseButton.Left || !_canSeek) return;
            BeginSeek(false);
            // A point click is handled by Slider before its instance handlers.
            // Capture that gesture too, so release outside the rail finishes it.
            if (Mouse.LeftButton == MouseButtonState.Pressed && !InsideThumb(args.OriginalSource as DependencyObject)) SeekSlider.CaptureMouse();
        }), true);
        SeekSlider.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, args) =>
        {
            if (args.ChangedButton == MouseButton.Left) CompleteSeek(false);
        }), true);
        SeekSlider.LostMouseCapture += (_, _) => { if (_pointerSeeking && !SeekSlider.IsMouseCaptureWithin) CancelSeek(); };
        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => BeginSeek(false)), true);
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, args) => { if (args.Canceled) CancelSeek(); else CompleteSeek(false); }), true);
        SeekSlider.PreviewKeyDown += (_, args) => { if (SeekKey(args.Key)) BeginSeek(true); else if (args.Key == Key.Escape) CancelSeek(); };
        SeekSlider.PreviewKeyUp += (_, args) => { if (SeekKey(args.Key)) CompleteSeek(true); };
        SeekSlider.LostKeyboardFocus += (_, _) => { if (_keySeeking) CompleteSeek(true); };
        Deactivated += (_, _) => { if (_keySeeking) CompleteSeek(true); else CancelSeek(); };
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
        Closed += (_, _) => { CancelSeek(); Command = null; SeekRequested = null; VolumeRequested = null; ShowMainRequested = null; };
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
        _title = title; _artist = artist; _hasTrack = !string.IsNullOrWhiteSpace(title);
        TitleText.Text = _hasTrack ? title : "Музыка рядом"; TitleText.ToolTip = _hasTrack ? title : null;
        ArtistText.Text = _hasTrack && !string.IsNullOrWhiteSpace(artist) ? artist : "Выберите трек в основном окне";
        PlayButton.Content = Glyph(isPlaying ? "\uE769" : "\uE768", 18);
        PlayButton.ToolTip = isPlaying ? "Пауза" : "Играть"; AutomationProperties.SetName(PlayButton, isPlaying ? "Пауза" : "Играть");
        PlayButton.IsEnabled = _hasTrack; _next.IsEnabled = _hasTrack && canNext; _previous.IsEnabled = _hasTrack && canPrevious;
        if (!_hasTrack) UpdateDetails(null, 0, 0, false, false, false, VolumeSlider.Value);
    }

    internal void UpdateDetails(ImageSource? cover, double positionSeconds, double durationSeconds, bool canSeek, bool isLiked, bool canLike, double volumePercent, string? trackKey = null)
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
            SeekSlider.Maximum = Math.Max(1, duration);
            if (!IsSeeking) { SeekSlider.Value = _position; PositionText.Text = Time(_position); }
        }
        finally { _syncingSeek = false; }
        DurationText.Text = Time(duration);
        CoverImage.Source = _hasTrack ? cover : null; _coverPlaceholder.Visibility = CoverImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        LikeButton.IsEnabled = _hasTrack && canLike; LikeButton.Content = Glyph(isLiked && _hasTrack ? "\uEB52" : "\uEB51", 19);
        LikeButton.Foreground = isLiked && _hasTrack ? Accent : Foreground;
        LikeButton.ToolTip = isLiked ? "Убрать из Мне нравится" : "Добавить в Мне нравится"; AutomationProperties.SetName(LikeButton, LikeButton.ToolTip.ToString());
        _syncingVolume = true;
        try { VolumeSlider.Value = double.IsFinite(volumePercent) ? Math.Clamp(volumePercent, 0, 100) : 0; }
        finally { _syncingVolume = false; }
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
    private static Brush Brush(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
    private static TextBlock Glyph(string glyph, double size) => new() { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private static Slider Slider(double minimum, double maximum) => new()
    {
        Minimum = minimum, Maximum = maximum, Height = 22, VerticalAlignment = VerticalAlignment.Center,
        Background = Brush("#3A3C45"), Foreground = Accent, BorderThickness = new Thickness(0), IsSnapToTickEnabled = false,
        Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
              <Grid x:Name="Surface" Background="Transparent">
                <Border Height="4" Margin="5,0" CornerRadius="2" Background="#3A3C45" VerticalAlignment="Center"/>
                <Track x:Name="PART_Track" Orientation="Horizontal" IsDirectionReversed="False">
                  <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Background="#FF705E" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" Margin="5,0,0,0" CornerRadius="2" Background="{TemplateBinding Background}" VerticalAlignment="Center"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
                  <Track.Thumb><Thumb Width="10" Height="10"><Thumb.Template><ControlTemplate TargetType="Thumb"><Ellipse Fill="#FF705E"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                  <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
                </Track>
                <Border x:Name="Focus" CornerRadius="6" BorderBrush="#FF705E" BorderThickness="1" Opacity="0" IsHitTestVisible="False"/>
              </Grid>
              <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="Opacity" Value="0.7"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value="0.35"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """)
    };
    private static ControlTemplate ButtonTemplate(int radius) => (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Grid x:Name="Surface"><Border CornerRadius="RADIUS" Background="{TemplateBinding Background}"/><Border x:Name="Hover" CornerRadius="RADIUS" Background="White" Opacity="0" IsHitTestVisible="False"/><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Grid>
          <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Hover" Property="Opacity" Value="0.08"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="Hover" Property="Opacity" Value="0.15"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value="0.3"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
        """.Replace("RADIUS", radius.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    private static Button IconButton(string glyph, string label, Action action, double width, double height, double size)
    {
        var button = new Button { Content = Glyph(glyph, size), Width = width, Height = height, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Brush("#EEECEE"), ToolTip = label, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Template = ButtonTemplate(8) };
        AutomationProperties.SetName(button, label); button.Click += (_, _) => { if (button.IsEnabled) action(); }; return button;
    }
}
