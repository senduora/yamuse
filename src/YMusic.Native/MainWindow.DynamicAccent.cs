using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace YMusic.Native;

public partial class MainWindow
{
    private CoverAccentController? _coverAccent;

    private void InitializeDynamicAccent()
    {
        _coverAccent = new CoverAccentController(PlayerCover, Application.Current.Resources, () => _current);
        // InitializeComponent has already resolved some local DynamicResources.
        // Changing Application.Resources before this window is loaded does not
        // invalidate every cached BAML reference; local entries invalidate its
        // entire tree while keeping the same brushes used by the mini player.
        foreach (var (key, brush) in _coverAccent.Brushes) Resources[key] = brush;
        Resources["AccentForegroundBrush"] = Application.Current.Resources["AccentForegroundBrush"];
        Closed += (_, _) => { _coverAccent.Dispose(); _coverAccent = null; };
    }
}

/// <summary>One palette worker and six shared brushes; no frame timer or new artwork request.</summary>
internal sealed class CoverAccentController : IDisposable
{
    internal static readonly TimeSpan TransitionDuration = TimeSpan.FromSeconds(5);
    private readonly Image _cover;
    private readonly Func<object?> _identity;
    private readonly Func<BitmapSource, CancellationToken, Task<Color?>> _analyze;
    private readonly DependencyPropertyDescriptor _sourceChanged;
    private readonly DependencyPropertyDescriptor _uriChanged;
    private readonly DependencyPropertyDescriptor _loadStatusChanged;
    private readonly Dictionary<string, SolidColorBrush> _brushes = [];
    private readonly Dictionary<string, AnimationClock> _clocks = [];
    private CancellationTokenSource? _pending;
    private long _generation;
    private bool _disposed;
    private bool _fromCover = true;
    private Color _staticColor = CoverPalette.Fallback;
    internal Color TargetColor { get; private set; } = CoverPalette.Fallback;
    internal Task PendingAnalysis { get; private set; } = Task.CompletedTask;
    internal IReadOnlyDictionary<string, SolidColorBrush> Brushes => _brushes;
    internal IReadOnlyDictionary<string, AnimationClock> Clocks => _clocks;

    internal void Configure(bool fromCover, string staticColor)
    {
        _cover.VerifyAccess();
        _fromCover = fromCover;
        _staticColor = PreferencesStore.IsAccentColor(staticColor)
            ? Color.FromRgb(Convert.ToByte(staticColor.Substring(1, 2), 16), Convert.ToByte(staticColor.Substring(3, 2), 16), Convert.ToByte(staticColor.Substring(5, 2), 16))
            : CoverPalette.Fallback;
        OnCoverChanged(_cover, EventArgs.Empty);
    }

    internal CoverAccentController(Image cover, ResourceDictionary resources, Func<object?> identity,
        Func<BitmapSource, CancellationToken, Task<Color?>>? analyze = null)
    {
        _cover = cover; _identity = identity;
        _analyze = analyze ?? ((bitmap, token) => Task.Run(() => { token.ThrowIfCancellationRequested(); return CoverPalette.Extract(bitmap); }, token));
        foreach (var (key, color) in Palette(CoverPalette.Fallback))
        {
            // DynamicResource setters never seal/freeze these privately owned
            // brushes. Stable instances also update code-created player views.
            var brush = new SolidColorBrush(color);
            // ResourceDictionary may freeze even a program-created resource on
            // first lookup. A completed, held colour clock makes CanFreeze false
            // without a running frame animation or a cloned brush per control.
            HoldMutable(brush, color);
            _brushes.Add(key, brush); resources[key] = brush;
        }
        resources["AccentForegroundBrush"] = new SolidColorBrush(CoverPalette.Ink);
        _sourceChanged = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
        _uriChanged = DependencyPropertyDescriptor.FromProperty(Artwork.SourceUriProperty, typeof(Image));
        _loadStatusChanged = DependencyPropertyDescriptor.FromProperty(Artwork.LoadStatusProperty, typeof(Image));
        _sourceChanged.AddValueChanged(cover, OnCoverChanged);
        _uriChanged.AddValueChanged(cover, OnCoverChanged);
        _loadStatusChanged.AddValueChanged(cover, OnCoverChanged);
        OnCoverChanged(cover, EventArgs.Empty);
    }

    private void OnCoverChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        _generation++; _pending?.Cancel(); _pending = null;
        var source = _cover.Source as BitmapSource;
        // Artwork clears Source while the next cover loads (including a cached
        // previous track). Keep the visible palette until that request finishes.
        if (_fromCover && Artwork.GetLoadStatus(_cover) == ArtworkLoadStatus.Loading)
        { PendingAnalysis = Task.CompletedTask; return; }
        // Real covers are already frozen and small. A non-frozen foreign image
        // is deliberately ignored rather than touching its dispatcher in a worker.
        if (!_fromCover || source is null || !source.IsFrozen) { Retarget(_staticColor); PendingAnalysis = Task.CompletedTask; return; }
        var cancellation = new CancellationTokenSource(); _pending = cancellation;
        PendingAnalysis = AnalyzeAsync(source, _identity(), Artwork.GetSourceUri(_cover), _generation, cancellation);
    }

    private async Task AnalyzeAsync(BitmapSource source, object? identity, string? uri, long generation, CancellationTokenSource cancellation)
    {
        try
        {
            var color = await _analyze(source, cancellation.Token);
            if (!_disposed && !cancellation.IsCancellationRequested && generation == _generation
                && ReferenceEquals(_cover.Source, source) && ReferenceEquals(_identity(), identity) && Artwork.GetSourceUri(_cover) == uri)
                Retarget(color ?? _staticColor);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or ArgumentException or NotSupportedException or System.IO.IOException)
        {
            if (!_disposed && generation == _generation && ReferenceEquals(_cover.Source, source) && ReferenceEquals(_identity(), identity)) Retarget(_staticColor);
        }
        finally
        {
            if (ReferenceEquals(_pending, cancellation)) _pending = null;
            cancellation.Dispose();
        }
    }

    internal void Retarget(Color color)
    {
        _cover.VerifyAccess();
        if (_disposed) return;
        color = CoverPalette.Readable(color);
        if (TargetColor == color) return;
        TargetColor = color;
        foreach (var (key, target) in Palette(color))
        {
            var brush = _brushes[key]; var current = brush.Color;
            brush.Color = target;
            // Explicit user-requested five-second colour fade is independent
            // of render tier; Windows' reduced-animation preference still wins.
            if (!SystemParameters.ClientAreaAnimation || current == target)
            { HoldMutable(brush, target); _clocks.Remove(key); continue; }
            var animation = new ColorAnimation(current, target, new Duration(TransitionDuration))
            { FillBehavior = FillBehavior.Stop };
            var clock = animation.CreateClock(); _clocks[key] = clock;
            brush.ApplyAnimationClock(SolidColorBrush.ColorProperty, clock, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private static IEnumerable<(string Key, Color Color)> Palette(Color color)
    {
        yield return ("AccentBrush", color);
        yield return ("AccentHoverBrush", CoverPalette.Mix(color, Colors.White, .20));
        yield return ("AccentFocusBrush", CoverPalette.Mix(color, Colors.White, .58));
        yield return ("AccentSoftBrush", CoverPalette.Mix(Color.FromRgb(16, 17, 20), color, .12));
        yield return ("AccentSelectionBrush", CoverPalette.Mix(Color.FromRgb(16, 17, 20), color, .20));
        yield return ("AccentTextSelectionBrush", Color.FromArgb(110, color.R, color.G, color.B));
    }

    private static void HoldMutable(SolidColorBrush brush, Color color) => brush.BeginAnimation(SolidColorBrush.ColorProperty,
        new ColorAnimation(color, color, new Duration(TimeSpan.Zero)) { FillBehavior = FillBehavior.HoldEnd }, HandoffBehavior.SnapshotAndReplace);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++; _pending?.Cancel(); _pending = null;
        _sourceChanged.RemoveValueChanged(_cover, OnCoverChanged); _uriChanged.RemoveValueChanged(_cover, OnCoverChanged);
        _loadStatusChanged.RemoveValueChanged(_cover, OnCoverChanged);
        foreach (var brush in _brushes.Values)
        { var current = brush.Color; brush.Color = current; brush.ApplyAnimationClock(SolidColorBrush.ColorProperty, null); }
        _clocks.Clear();
    }
}
