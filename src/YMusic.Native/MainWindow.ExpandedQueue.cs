using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using ShapePath = System.Windows.Shapes.Path;

namespace YMusic.Native;

public partial class MainWindow
{
    private Button? _expandedPlayerQueueButton;
    private Grid? _expandedQueuePanel;
    private ListBox? _expandedQueueList;
    private TextBlock? _expandedQueueNote;
    private bool _expandedQueueOpen;
    private bool _expandedQueueRevealPending;
    private long _expandedQueueLayoutGeneration;
    private long _expandedQueueRevealQueue;
    private int _expandedQueueAccountGeneration, _expandedQueueCurrentIndex = -1;
    private long _expandedQueueVersion = -1;
    private long _expandedQueueShownReplacement;
    private long? _expandedQueueShownCursor;
    private IReadOnlyList<TrackRow>? _expandedQueueItems;
    private ExpandedQueueRow[] _expandedQueueRows = [];
    private ExpandedQueuePress? _expandedQueuePress;
    private sealed record ExpandedQueuePress(int Index, long Token, long Version, Point Origin);

    private void PlayerQueue_Click(object sender, RoutedEventArgs e) => ShowQueue();

    // The two transports share commands and live accent resources. Never reparent
    // the compact button's Grid: a WPF element has a single visual owner.
    private Button ExpandedQueueModeButton(string name, Button source, string glyph, double size)
    {
        var button = ExpandedIconButton(name, glyph, "", size, () => source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        button.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = source });
        button.SetBinding(Control.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = source });
        button.SetBinding(ToolTipProperty, new Binding(nameof(ToolTip)) { Source = source });
        button.SetBinding(AutomationProperties.NameProperty, new Binding { Source = source, Path = new PropertyPath(AutomationProperties.NameProperty) });
        var root = new Grid { Width = 24, Height = 28 };
        var repeat = name == "ExpandedPlayerRepeat";
        var path = new ShapePath { Data = Geometry.Parse(repeat
            ? "M 4,14 V 10 Q 4,6 8,6 H 20 M 17,3 L 20,6 L 17,9 M 20,14 V 18 Q 20,22 16,22 H 4 M 7,19 L 4,22 L 7,25"
            : "M 3,8 L 6,8 C 10,8 13,20 17,20 L 21,20 M 17,16 L 21,20 L 17,24 M 3,20 L 6,20 C 10,20 13,8 17,8 L 21,8 M 17,4 L 21,8 L 17,12"),
            StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Width = 20, Height = 24, Stretch = Stretch.Uniform };
        path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = source }); root.Children.Add(path);
        var indicator = new Ellipse { Width = 3, Height = 3, VerticalAlignment = VerticalAlignment.Bottom };
        indicator.SetResourceReference(Shape.FillProperty, "AccentBrush");
        indicator.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = repeat ? RepeatIndicator : ShuffleIndicator }); root.Children.Add(indicator);
        if (repeat)
        {
            var one = new ShapePath { Data = Geometry.Parse("M 10,12 L 12,10 V 18 M 10,18 H 14"), StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            one.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = source });
            one.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = RepeatOneLabel }); root.Children.Add(one);
        }
        button.Content = root;
        return button;
    }

    private void BuildExpandedQueue(Grid body)
    {
        var panel = _expandedQueuePanel = new Grid { Name = "ExpandedQueuePanel", Width = 0, Opacity = 0, Visibility = Visibility.Collapsed,
            ClipToBounds = true, Margin = new Thickness(0, 0, 0, 0) };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new());
        Grid.SetColumn(panel, 0); body.Children.Add(panel);
        var header = new Grid { Margin = new Thickness(0, 4, 14, 16) };
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(new TextBlock { Text = "ОЧЕРЕДЬ", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ExpandedBrush("MutedBrush") });
        _expandedQueueNote = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0), Foreground = ExpandedBrush("MutedBrush") };
        caption.Children.Add(_expandedQueueNote); header.Children.Add(caption);
        var close = ExpandedIconButton("ExpandedQueueClose", "\uE711", "Скрыть очередь", 30, () => SetExpandedQueueVisible(false));
        close.FontSize = 14; close.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(close, 1); header.Children.Add(close); panel.Children.Add(header);
        var list = _expandedQueueList = new ListBox { Name = "ExpandedQueueList", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 14, 0), ItemTemplate = ExpandedQueueTemplate() };
        var style = new Style(typeof(ListBoxItem), (Style)FindResource("FlatListItem"));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 8, 7, 8)));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 54d));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
        style.Setters.Add(new Setter(OpacityProperty, new Binding("Track.RowOpacity")));
        style.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(ExpandedQueueRow.AccessibleName))));
        var active = new DataTrigger { Binding = new Binding(nameof(ExpandedQueueRow.IsCurrent)), Value = true };
        active.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("AccentTextBrush")));
        active.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("AccentSoftBrush"))); style.Triggers.Add(active);
        list.ItemContainerStyle = style;
        AutomationProperties.SetName(list, "Очередь воспроизведения: выбрать трек и нажать Enter");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled); ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(list, true); VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling); VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel); SmoothScroll.SetEnabled(list, true);
        // Opening owns the reveal only until the user starts browsing the list or its scrollbar.
        list.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) => _expandedQueueRevealPending = false), true);
        list.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => _expandedQueueRevealPending = false), true);
        list.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (IsTrackRevealNavigationKey(args.Key))
                _expandedQueueRevealPending = false;
        }), true);
        list.PreviewMouseLeftButtonDown += (_, args) =>
            RememberExpandedQueuePress(list, args.OriginalSource as DependencyObject, args.GetPosition(list), args.ClickCount);
        list.PreviewMouseMove += (_, args) =>
        {
            if (_expandedQueuePress is { } press && ExceededExpandedQueueClickDistance(press.Origin, args.GetPosition(list)))
                _expandedQueuePress = null;
        };
        list.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, _) =>
        { if (!list.IsMouseCaptureWithin) _expandedQueuePress = null; }), true);
        list.PreviewMouseLeftButtonUp += async (_, args) =>
        {
            if (args.ChangedButton == MouseButton.Left) await ReleaseExpandedQueuePressAsync(list, args.GetPosition(list));
        };
        list.PreviewKeyDown += async (_, args) =>
        {
            var keyboard = args.KeyboardDevice;
            if (args.Key != Key.Enter || HasTransportModifier(keyboard.Modifiers,
                keyboard.IsKeyDown(Key.LWin) || keyboard.IsKeyDown(Key.RWin))) return;
            var row = ReferenceEquals(args.OriginalSource, list) ? list.SelectedItem as ExpandedQueueRow
                : ExpandedQueueRowAt(list, args.OriginalSource as DependencyObject);
            if (row is null) return;
            args.Handled = true;
            if (!args.IsRepeat) await PlayExpandedQueueRowAsync(row);
        };
        Grid.SetRow(list, 1); panel.Children.Add(list);
    }

    private void RememberExpandedQueuePress(ListBox list, DependencyObject? source, Point origin, int clickCount)
    {
        _expandedQueuePress = clickCount == 1 && _expandedQueueVersion == _playbackQueue.Version
            && ExpandedQueueRowAt(list, source) is { } row && ReferenceEquals(row.Items, _playbackQueue.Items)
            ? new(row.Index, row.Token, _expandedQueueVersion, origin) : null;
    }

    private Task ReleaseExpandedQueuePressAsync(ListBox list, Point point)
    {
        var press = _expandedQueuePress;
        _expandedQueuePress = null;
        if (press is null || press.Version != _playbackQueue.Version || !ReferenceEquals(list, _expandedQueueList)
            || ExceededExpandedQueueClickDistance(press.Origin, point)
            || ExpandedQueueRowAt(list, list.InputHitTest(point) as DependencyObject) is not { } row
            || row.Index != press.Index || row.Token != press.Token) return Task.CompletedTask;
        return PlayExpandedQueueRowAsync(row);
    }

    private static ExpandedQueueRow? ExpandedQueueRowAt(ListBox list, DependencyObject? source) =>
        source is not null && source is not ButtonBase && !FromButton(source)
        && ItemsControl.ContainerFromElement(list, source) is ListBoxItem { Content: ExpandedQueueRow row } ? row : null;

    private static bool ExceededExpandedQueueClickDistance(Point from, Point to) =>
        Math.Abs(from.X - to.X) >= SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(from.Y - to.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private static DataTemplate ExpandedQueueTemplate()
    {
        var root = new FrameworkElementFactory(typeof(DockPanel)); root.SetValue(FrameworkElement.HeightProperty, 38d);
        var number = new FrameworkElementFactory(typeof(TextBlock)); number.SetValue(DockPanel.DockProperty, Dock.Left);
        number.SetValue(FrameworkElement.WidthProperty, 29d); number.SetValue(TextBlock.FontSizeProperty, 11d);
        number.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); number.SetBinding(TextBlock.TextProperty, new Binding(nameof(ExpandedQueueRow.Position)));
        root.AppendChild(number);
        var time = new FrameworkElementFactory(typeof(TextBlock)); time.SetValue(DockPanel.DockProperty, Dock.Right);
        time.SetValue(FrameworkElement.MarginProperty, new Thickness(7, 0, 0, 0)); time.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        time.SetValue(TextBlock.FontSizeProperty, 11d); time.SetBinding(TextBlock.TextProperty, new Binding("Track.Duration")); root.AppendChild(time);
        var text = new FrameworkElementFactory(typeof(StackPanel)); text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetValue(TextBlock.FontSizeProperty, 13d);
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        title.SetBinding(TextBlock.TextProperty, new Binding("Track.Title")); text.AppendChild(title);
        var artist = new FrameworkElementFactory(typeof(TextBlock)); artist.SetValue(TextBlock.FontSizeProperty, 11d);
        artist.SetValue(UIElement.OpacityProperty, .72d); artist.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
        artist.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); artist.SetBinding(TextBlock.TextProperty, new Binding("Track.Artist")); text.AppendChild(artist);
        root.AppendChild(text); return new DataTemplate { VisualTree = root };
    }

    private void SetExpandedQueueVisible(bool visible, bool animate = true)
    {
        if (visible == _expandedQueueOpen || visible && (!IsExpandedPlayerOpen || _current is null || _closed)) return;
        CancelExpandedReveal();
        _expandedQueueOpen = visible;
        _expandedQueueRevealPending = visible;
        if (visible)
        {
            _expandedQueueAccountGeneration = _accountGeneration;
            _expandedQueueRevealQueue = _playbackQueue.ReplacementVersion;
        }
        if (_expandedPlayerQueueButton is not null)
        {
            _expandedPlayerQueueButton.SetResourceReference(Control.ForegroundProperty, visible ? "AccentTextBrush" : "MutedBrush");
            _expandedPlayerQueueButton.ToolTip = visible ? "Скрыть очередь" : "Очередь воспроизведения";
            AutomationProperties.SetName(_expandedPlayerQueueButton, (string)_expandedPlayerQueueButton.ToolTip);
        }
        if (visible) RefreshExpandedQueue(); else ClearExpandedQueue();
        LayoutExpandedPanes(animate);
    }

    private void RequestExpandedQueueReveal()
    {
        if (!_expandedQueueRevealPending || _expandedQueueList is null) return;
        var layout = _expandedQueueLayoutGeneration;
        RequestCurrentTrackReveal(_expandedQueueList, () =>
        {
            RefreshExpandedQueue();
            return _playbackQueue.CurrentIndex;
        }, () => _expandedQueueRevealPending && _expandedQueueOpen && IsExpandedPlayerOpen && layout == _expandedQueueLayoutGeneration
            && _expandedQueueAccountGeneration == _accountGeneration
            && _expandedQueueRevealQueue == _playbackQueue.ReplacementVersion,
            () => _expandedQueueRevealPending = false);
    }

    private void RefreshExpandedQueue() => RefreshExpandedQueueCore(null);

    private void RefreshExpandedQueueCore(long? beforeAppend)
    {
        if (!_expandedQueueOpen || _expandedQueueList is not { } list) return;
        if (_closed || _expandedQueueAccountGeneration != _accountGeneration || _current is null) { SetExpandedQueueVisible(false, false); return; }
        if (_expandedQueueRevealQueue != _playbackQueue.ReplacementVersion) _expandedQueueRevealPending = false;
        if (_expandedQueueVersion == _playbackQueue.Version) return;
        var press = _expandedQueuePress;
        _expandedQueuePress = null;
        var replacement = _playbackQueue.ReplacementVersion;
        var cursor = _playbackQueue.CurrentOccurrenceToken;
        var preserveSelection = _expandedQueueItems is not null && _expandedQueueShownReplacement == replacement
            && _expandedQueueShownCursor == cursor;
        // Only a passive append can renew a press, and the pressed occurrence
        // must still occupy the same row. Release checks the new hit separately.
        var preservePress = press is not null && beforeAppend == press.Version && _expandedQueueVersion == press.Version
            && preserveSelection && press.Index < _playbackQueue.Items.Count && _playbackQueue.OccurrenceTokenAt(press.Index) == press.Token;
        var selection = (_expandedQueueList.SelectedItem as ExpandedQueueRow)?.Token;
        if (!ReferenceEquals(_expandedQueueItems, _playbackQueue.Items))
        {
            _expandedQueueItems = _playbackQueue.Items;
            _expandedQueueRows = _expandedQueueItems.Select((track, index) =>
                new ExpandedQueueRow(_expandedQueueItems, track, index, _playbackQueue.OccurrenceTokenAt(index))).ToArray();
            _expandedQueueList.ItemsSource = _expandedQueueRows; _expandedQueueCurrentIndex = -1;
        }
        if (_expandedQueueCurrentIndex >= 0 && _expandedQueueCurrentIndex < _expandedQueueRows.Length) _expandedQueueRows[_expandedQueueCurrentIndex].IsCurrent = false;
        _expandedQueueCurrentIndex = _playbackQueue.CurrentIndex;
        if (_expandedQueueCurrentIndex >= 0 && _expandedQueueCurrentIndex < _expandedQueueRows.Length)
            _expandedQueueRows[_expandedQueueCurrentIndex].IsCurrent = true;
        _expandedQueueList.SelectedIndex = preserveSelection
            ? selection is long token ? Array.FindIndex(_expandedQueueRows, row => row.Token == token) : -1
            : _expandedQueueCurrentIndex;
        _expandedQueueShownReplacement = replacement;
        _expandedQueueShownCursor = cursor;
        _expandedQueueVersion = _playbackQueue.Version;
        if (preservePress && press is not null && _expandedQueueOpen && ReferenceEquals(list, _expandedQueueList))
            _expandedQueuePress = press with { Version = _expandedQueueVersion };
        _expandedQueueNote!.Text = (_playbackQueue.Shuffle ? "Порядок очереди · перемешивание включено" : "Порядок воспроизведения") + $" · {_expandedQueueItems!.Count:N0}";
    }

    private async Task PlayExpandedQueueRowAsync(ExpandedQueueRow row)
    {
        // The displayed occurrence must still belong to this account and exact
        // snapshot. SelectAt keeps duplicate instances and the queue origin intact.
        bool OwnsRow() => _expandedCollapse is null && _expandedQueueOpen && IsExpandedPlayerOpen && !_closed
            && _expandedQueueAccountGeneration == _accountGeneration && _expandedQueueVersion == _playbackQueue.Version
            && ReferenceEquals(row.Items, _playbackQueue.Items) && row.Index >= 0 && row.Index < _playbackQueue.Items.Count
            && ReferenceEquals(row.Track, _playbackQueue.Items[row.Index]);
        if (!OwnsRow()) return;
        if (!CanNavigateToTrack(row.Track, requireExactOfflineVariant: true))
        {
            await RetryExactOfflineRowAsync(row.Track, allowUnavailableCatalog: true, OwnsRow, PlayAsync);
            return;
        }
        await PlayAsync(null);

        async Task PlayAsync(NativePlayer.LocalPlaybackAdmission? admission)
        {
            if (!OwnsRow() || !CanSelectWhileRemovingAudio(row.Track, requireExactOfflineVariant: true)
                || !_playbackQueue.SelectAt(row.Index)) return;
            await PlayTrackCoreAsync(row.Track, null, false, requireExactOfflineVariant: true, localAdmission: admission);
        }
    }

    private void ClearExpandedQueue()
    {
        _expandedQueuePress = null;
        _expandedQueueRevealPending = false;
        if (_expandedQueueList is not null)
        { if (FindViewer(_expandedQueueList) is { } viewer) SmoothScroll.CancelAnimation(viewer); _expandedQueueList.ItemsSource = null; }
        _expandedQueueItems = null; _expandedQueueRows = []; _expandedQueueVersion = -1; _expandedQueueCurrentIndex = -1;
    }

    private void LayoutExpandedPanes(bool animate)
    {
        if (_expandedLyricsPanel?.Parent is not Grid body || _expandedQueuePanel is null) return;
        var both = _expandedLyricsOpen && _expandedQueueOpen;
        var lyricsWidth = _expandedLyricsOpen ? Math.Min(650, body.ActualWidth * (both ? .32 : .49)) : 0;
        var queueWidth = _expandedQueueOpen ? Math.Min(440, body.ActualWidth * (both ? .28 : .40)) : 0;
        AnimateExpandedPane(_expandedLyricsPanel, lyricsWidth, _expandedLyricsOpen, animate);
        AnimateExpandedPane(_expandedQueuePanel, queueWidth, _expandedQueueOpen, animate);
    }

    private void AnimateExpandedPane(Grid panel, double target, bool visible, bool animate)
    {
        var queue = ReferenceEquals(panel, _expandedQueuePanel);
        var layout = queue ? ++_expandedQueueLayoutGeneration : 0;
        var from = panel.ActualWidth; var opacity = panel.Opacity;
        panel.BeginAnimation(WidthProperty, null); panel.BeginAnimation(OpacityProperty, null);
        panel.Width = target; panel.Opacity = visible ? 1 : 0;
        if (visible) panel.Visibility = Visibility.Visible;
        if (!animate || !Motion.CanAnimate)
        {
            panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (queue && visible) RequestExpandedQueueReveal();
            return;
        }
        // Measuring a row during the width animation can realize it in a temporary viewport.
        var duration = TimeSpan.FromMilliseconds(320);
        var width = new DoubleAnimation(from, target, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop };
        width.Completed += (_, _) =>
        {
            if (queue && layout != _expandedQueueLayoutGeneration) return;
            var stillVisible = queue ? _expandedQueueOpen : _expandedLyricsOpen;
            if (!stillVisible) panel.Visibility = Visibility.Collapsed;
            else if (queue) RequestExpandedQueueReveal();
            else { _expandedLyricsFollowIndex = -1; RefreshExpandedLyrics(); }
        };
        panel.BeginAnimation(WidthProperty, width);
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, visible ? 1 : 0, duration) { FillBehavior = FillBehavior.Stop });
    }

    private sealed class ExpandedQueueRow(IReadOnlyList<TrackRow> items, TrackRow track, int index, long token) : INotifyPropertyChanged
    {
        public IReadOnlyList<TrackRow> Items { get; } = items;
        public TrackRow Track { get; } = track;
        public int Index { get; } = index;
        public long Token { get; } = token;
        public string Position => IsCurrent ? "▶" : (Index + 1).ToString();
        public string AccessibleName => $"{Index + 1}. {Track.Title} · {Track.Artist}" + (IsCurrent ? " · Сейчас играет" : "");
        private bool _isCurrent;
        public bool IsCurrent
        {
            get => _isCurrent;
            set { if (_isCurrent == value) return; _isCurrent = value; PropertyChanged?.Invoke(this, new(nameof(IsCurrent))); PropertyChanged?.Invoke(this, new(nameof(Position))); PropertyChanged?.Invoke(this, new(nameof(AccessibleName))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
