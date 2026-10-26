using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace YMusic.Native;

public partial class MainWindow
{
    private DependencyPropertyDescriptor? _ambientExpandedContent;
    private AmbientBackdrop? _waveAmbient, _expandedAmbient;

    private void InitializeAmbientBackdrops()
    {
        if (_waveAmbient is not null) return;
        if (WaveSurface.Child is Grid wave)
        {
            // Only the old decorative ellipse and stroked wave paths are replaced.
            // Cover, metadata and buttons retain their original parents and handlers.
            foreach (var ornament in wave.Children.OfType<Shape>().Where(shape => !shape.IsHitTestVisible).ToArray()) wave.Children.Remove(ornament);
            WaveSurface.Background = ThemeManager.Brush(Color.FromRgb(17, 21, 28));
            WaveSurface.SetResourceReference(Border.BorderBrushProperty, "AccentSoftBrush");
            _waveAmbient = new AmbientBackdrop { Name = "WaveAmbientBackdrop", CornerRadius = 23, IsMotionEnabled = _player.IsPlaying };
            wave.Children.Insert(0, _waveAmbient);
        }
        _ambientExpandedContent = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl));
        _ambientExpandedContent.AddValueChanged(ExpandedPlayerHost, OnAmbientExpandedContentChanged);
        ExpandedPlayerHost.IsVisibleChanged += OnAmbientExpandedVisibilityChanged;
        OnAmbientExpandedContentChanged(ExpandedPlayerHost, EventArgs.Empty);
        Closed += OnAmbientWindowClosed;
    }

    private void OnAmbientExpandedContentChanged(object? sender, EventArgs args)
    {
        if (ExpandedPlayerHost.Content is not Grid surface || surface.Children.OfType<AmbientBackdrop>().Any()) return;
        _expandedAmbient = new AmbientBackdrop { Name = "ExpandedAmbientBackdrop", IsMotionEnabled = _player.IsPlaying };
        Grid.SetRowSpan(_expandedAmbient, Math.Max(1, surface.RowDefinitions.Count));
        Grid.SetColumnSpan(_expandedAmbient, Math.Max(1, surface.ColumnDefinitions.Count));
        surface.Children.Insert(0, _expandedAmbient);
        surface.ClipToBounds = true;
    }

    private void OnAmbientExpandedVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        // The wave page remains in the visual tree beneath this opaque overlay.
        // Hide its decorative layer too, so only the visible atmosphere runs.
        if (_waveAmbient is not null) _waveAmbient.Visibility = ExpandedPlayerHost.IsVisible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateAmbientMotion()
    {
        // Playback changes already refresh controls. Decorative clocks have no
        // reason to keep the visible idle window rendering at the display rate.
        var playing = !_closed && _player.IsPlaying;
        if (_waveAmbient is not null) _waveAmbient.IsMotionEnabled = playing;
        if (_expandedAmbient is not null) _expandedAmbient.IsMotionEnabled = playing;
    }

    private void OnAmbientWindowClosed(object? sender, EventArgs args)
    {
        _waveAmbient?.Release(); _expandedAmbient?.Release();
        _ambientExpandedContent?.RemoveValueChanged(ExpandedPlayerHost, OnAmbientExpandedContentChanged);
        _ambientExpandedContent = null;
        ExpandedPlayerHost.IsVisibleChanged -= OnAmbientExpandedVisibilityChanged;
        Closed -= OnAmbientWindowClosed;
    }
}
