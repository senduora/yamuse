using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckMenuHoverPreview3()
    {
        await Run("MenuHoverPreview3: pointer highlight fades in out and retargets without changing menu hit areas", async () =>
        {
            using var fixture = new MenuHoverPreview3Fixture("pointer");
            await fixture.Open();
            var first = Ta0317Item(fixture.Menu, "Next");
            var second = Ta0317Item(fixture.Menu, "Enqueue");
            var firstHover = MenuHoverPreview3Part(first);
            var secondHover = MenuHoverPreview3Part(second);
            var originalSize = first.RenderSize;
            var icon = (ContentPresenter)first.Template.FindName("ItemIcon", first);
            var originalIcon = icon.TranslatePoint(new Point(), first);
            var dpi = VisualTreeHelper.GetDpi(first);
            var roundedInset = Math.Round(first.Padding.Left * dpi.DpiScaleX) / dpi.DpiScaleX;
            var samples = new List<object>();

            Library017MouseOver(first);
            Assert(first.IsHighlighted && Motion.GetOpacity(firstHover) == 1,
                "pointer did not target the first menu highlight");
            samples.Add(new { phase = "fade-in", opacity = await MenuHoverPreview3Mid(firstHover) });
            await Task.Delay(220);
            Assert(firstHover.Opacity == 1, "first menu highlight did not settle");

            Library017MouseOver(second);
            Assert(!first.IsHighlighted && second.IsHighlighted
                && Motion.GetOpacity(firstHover) == 0 && Motion.GetOpacity(secondHover) == 1,
                "moving between actions left the old item highlighted");
            samples.Add(new { phase = "fade-out", opacity = await MenuHoverPreview3Mid(firstHover) });
            // Reverse a transition before its 170 ms deadline, then leave again.
            Library017MouseOver(first);
            await Task.Delay(35);
            Library017MouseOver(second);
            await Task.Delay(250);
            Assert(firstHover.Opacity == 0 && secondHover.Opacity == 1,
                "rapid pointer changes left a stale or unfinished highlight");
            var finalIcon = icon.TranslatePoint(new Point(), first);
            File.WriteAllText(Path.Combine(_directory, "menu-hover-preview3-geometry.json"), JsonSerializer.Serialize(new
            { beforeSize = originalSize.ToString(), afterSize = first.RenderSize.ToString(), beforeIcon = originalIcon.ToString(),
                afterIcon = finalIcon.ToString(), padding = first.Padding.ToString(), dpiX = dpi.DpiScaleX, dpiY = dpi.DpiScaleY,
                roundedInset }));
            Assert(first.RenderSize == originalSize && finalIcon == originalIcon
                && first.Padding == new Thickness(9, 7, 9, 7) && Math.Abs(originalIcon.X - roundedInset) < .000001,
                $"the hover layer changed action geometry or icon alignment: {originalSize}/{first.RenderSize}, {originalIcon}/{finalIcon}, inset={roundedInset}, DPI={dpi.DpiScaleX}");
            Assert(!firstHover.IsHitTestVisible && Library017Within(
                VisualTreeHelper.HitTest(first, new Point(first.ActualWidth - 2, first.ActualHeight / 2))?.VisualHit, first),
                "the hover layer intercepted input or removed the action's edge hit area");
            File.WriteAllText(Path.Combine(_directory, "menu-hover-preview3-motion.json"), JsonSerializer.Serialize(new
            { animationAssertionsRun = Motion.CanAnimate, samples, rapidRetarget = true, geometryPreserved = true,
                localWpfHoverOnly = true, nativeInputSent = false }));
        });

        await Run("MenuHoverPreview3: keyboard submenu and disabled states share the highlight without sticky selection", async () =>
        {
            using var fixture = new MenuHoverPreview3Fixture("keyboard-submenu");
            await fixture.Open();
            var first = Ta0317Item(fixture.Menu, "Next");
            var second = Ta0317Item(fixture.Menu, "Enqueue");
            var artists = Ta0317Item(fixture.Menu, "Artists");
            Library017MouseOver(null);
            fixture.EnableLocalKeyboardFocus();
            Assert(first.Focus(), "isolated menu could not focus an action for keyboard navigation");
            await Dispatcher.Yield(DispatcherPriority.Input);
            Assert(first.IsHighlighted && !first.IsMouseOver && Motion.GetOpacity(MenuHoverPreview3Part(first)) == 1,
                "keyboard focus did not highlight the menu item without a pointer");
            var down = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(first), Environment.TickCount, Key.Down)
            { RoutedEvent = Keyboard.KeyDownEvent };
            first.RaiseEvent(down);
            await Dispatcher.Yield(DispatcherPriority.Input);
            Assert(down.Handled && ReferenceEquals(Keyboard.FocusedElement, second) && second.IsHighlighted
                && !first.IsHighlighted && Motion.GetOpacity(MenuHoverPreview3Part(first)) == 0
                && Motion.GetOpacity(MenuHoverPreview3Part(second)) == 1,
                "keyboard navigation did not transfer the animated highlight");
            // WPF resumes pointer selection at Background priority after a key.
            await Dispatcher.Yield(DispatcherPriority.Background);
            Library017MouseOver(artists);
            artists.IsSubmenuOpen = true;
            await Task.Delay(250);
            var children = artists.Items.OfType<MenuItem>().ToArray();
            Assert(children.Length == 1 && fixture.Menu.IsOpen && artists.IsSubmenuOpen,
                $"submenu fixture lost its captured artist: children={children.Length}, menuOpen={fixture.Menu.IsOpen}, submenuOpen={artists.IsSubmenuOpen}");
            var child = children[0];
            Library017MouseOver(child);
            await Task.Delay(250);
            Assert(artists.IsSubmenuOpen && Motion.GetOpacity(MenuHoverPreview3Part(artists)) == 1
                && MenuHoverPreview3Part(artists).Opacity == 1 && child.IsHighlighted
                && Motion.GetOpacity(MenuHoverPreview3Part(child)) == 1,
                "entering an artist submenu lost its parent highlight or child animation");
            Library017MouseOver(first);
            artists.IsSubmenuOpen = false;
            await Task.Delay(250);
            Assert(Motion.GetOpacity(MenuHoverPreview3Part(artists)) == 0 && MenuHoverPreview3Part(artists).Opacity == 0,
                "closing the submenu left a sticky parent highlight");
            Assert(second.Focus(), "menu could not refocus an action for disabled-state coverage");
            second.IsEnabled = false;
            await Task.Delay(250);
            var secondSurface = (Border)second.Template.FindName("ItemSurface", second);
            Assert(Motion.GetOpacity(MenuHoverPreview3Part(second)) == 0 && MenuHoverPreview3Part(second).Opacity == 0
                && secondSurface.Opacity == .4,
                "disabled item retained a highlight or lost its disabled opacity");
            fixture.WriteKeyboardEvidence(down.Handled);
        });

        await Run("MenuHoverPreview3: open track menu follows live theme brushes and preserves action glyphs", async () =>
        {
            var previousMode = ThemeManager.Mode;
            using var fixture = new MenuHoverPreview3Fixture("theme");
            try
            {
                await fixture.Open();
                var selected = Ta0317Item(fixture.Menu, "Next");
                Library017MouseOver(selected);
                await Task.Delay(250);
                var hover = MenuHoverPreview3Part(selected);
                var brush = hover.Background;
                foreach (var mode in new[] { "Dark", "Light" })
                {
                    ThemeManager.SetMode(mode);
                    await Dispatcher.Yield(DispatcherPriority.DataBind);
                    fixture.Menu.UpdateLayout();
                    Assert(ReferenceEquals(hover.Background, brush)
                        && ReferenceEquals(hover.Background, Application.Current.FindResource("HoverBrush"))
                        && hover.Opacity == 1 && Motion.GetOpacity(hover) == 1,
                        "an open highlighted menu stopped following its shared theme brush");
                    Assert(ReferenceEquals(selected.Foreground, Application.Current.FindResource("AccentTextBrush"))
                        && ReferenceEquals(((System.Windows.Shapes.Path)selected.Icon).Stroke, selected.Foreground),
                        "highlighted action text and glyph no longer follow the same accent");
                    Assert(ReferenceEquals(((System.Windows.Shapes.Path)Ta0317Item(fixture.Menu, "Download").Icon).Stroke,
                        Application.Current.FindResource("DownloadBrush")), "hover replaced the offline action's green glyph");
                    MenuHoverPreview3Render(fixture.Menu, "menu-hover-preview3-" + mode.ToLowerInvariant() + ".png");
                }
            }
            finally { ThemeManager.SetMode(previousMode); }
        });
    }

    private static Border MenuHoverPreview3Part(MenuItem item) =>
        (Border)item.Template.FindName("ItemHover", item);

    private static async Task<double> MenuHoverPreview3Mid(Border hover)
    {
        Assert(!Motion.CanAnimate || hover.HasAnimatedProperties, "menu highlight changed without an animation");
        // A WPF clock starts on the following render tick, not at SetValue.
        await Task.Delay(40);
        var opacity = hover.Opacity;
        Assert(!Motion.CanAnimate || opacity > 0 && opacity < 1,
            $"menu highlight did not render an intermediate opacity: {opacity}");
        return opacity;
    }

    private static void MenuHoverPreview3Render(ContextMenu menu, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(menu);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_directory, name)); encoder.Save(output);
    }

    private sealed class MenuHoverPreview3Fixture : IDisposable
    {
        private readonly Library017Fixture _fixture;
        private readonly IInputElement? _previousMouseOver = Mouse.PrimaryDevice.DirectlyOver;
        private KeyboardFocusChangedEventHandler? _localKeyboardFocus;
        private DependencyObject? _previousKeyboardFocus;
        private readonly List<string> _focusTransitions = [];
        internal ContextMenu Menu { get; }
        internal MenuHoverPreview3Fixture(string name)
        {
            _fixture = new Library017Fixture("menu-hover-preview3-" + name);
            var row = new TrackRow { Id = "1234", RequestId = "1234:91", AlbumId = "91", Title = "Трек", Artist = "Исполнитель",
                Available = true, Artists = [new ArtistReference("55", "Исполнитель")] };
            Menu = Ta0317Menu(_fixture.Window, Control<Button>(_fixture.Window, "PlayerMoreButton"), () => row);
        }
        internal async Task Open()
        {
            Library017MouseOver(null);
            Menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            Menu.UpdateLayout();
            await Task.Delay(250);
            Assert(Menu.IsVisible && Menu.ActualHeight > 200 && Ta0317Item(Menu, "Next").IsEnabled,
                "private track menu fixture did not open and realize its action rows");
        }
        internal void EnableLocalKeyboardFocus()
        {
            Assert(!_fixture.Window.IsActive && Mouse.Captured is null,
                "keyboard fixture must start inactive with no mouse capture");
            _previousKeyboardFocus = Keyboard.FocusedElement as DependencyObject;
            _localKeyboardFocus = (_, args) =>
            {
                if (args.Handled || args.NewFocus is not MenuItem { IsEnabled: true, IsVisible: true, Focusable: true } item) return;
                // An inactive desktop cannot acquire native focus/capture. Feed
                // WPF's own device transitions, just as Library017MouseOver does:
                // its real focus events, menu selection and arrow navigation run,
                // while the OS input providers are never asked to activate it.
                LocalCapture(Menu);
                LocalFocus(item);
                args.Handled = true;
                _focusTransitions.Add(item.Name.Length > 0 ? item.Name : item.Header?.ToString() ?? "");
            };
            Menu.AddHandler(Keyboard.PreviewGotKeyboardFocusEvent, _localKeyboardFocus);
        }
        internal void WriteKeyboardEvidence(bool downHandled)
        {
            Assert(!_fixture.Window.IsActive, "local keyboard navigation activated the fixture owner");
            File.WriteAllText(Path.Combine(_directory, "menu-hover-preview3-keyboard.json"), JsonSerializer.Serialize(new
            { focusTransitions = _focusTransitions, downHandled, ownerActivated = _fixture.Window.IsActive,
                focusMethod = "WPF KeyboardDevice.ChangeFocus via scoped PreviewGotKeyboardFocus", captureMethod = "WPF MouseDevice.ChangeMouseCapture",
                nativeInputSent = false, nativeFocusAcquisitionRequested = false, highlightOrSelectionPropertiesAssigned = false }));
        }
        private static void LocalFocus(DependencyObject? target)
        {
            var change = typeof(KeyboardDevice).GetMethod("ChangeFocus", BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(DependencyObject), typeof(int)], null)
                ?? throw new InvalidOperationException("WPF local keyboard focus hook unavailable");
            change.Invoke(Keyboard.PrimaryDevice, [target, Environment.TickCount]);
        }
        private static void LocalCapture(IInputElement? target)
        {
            var change = typeof(MouseDevice).GetMethod("ChangeMouseCapture", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("WPF local menu capture hook unavailable");
            change.Invoke(Mouse.PrimaryDevice, [target, null, target is null ? CaptureMode.None : CaptureMode.SubTree, Environment.TickCount]);
        }
        public void Dispose()
        {
            Library017MouseOver(null);
            if (_localKeyboardFocus is not null)
            {
                Menu.RemoveHandler(Keyboard.PreviewGotKeyboardFocusEvent, _localKeyboardFocus);
                LocalFocus(_previousKeyboardFocus);
                LocalCapture(null);
            }
            Menu.IsOpen = false;
            _fixture.Dispose();
            Library017MouseOver(_previousMouseOver);
        }
    }
}
