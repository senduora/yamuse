using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private DispatcherTimer? _statusDismissTimer;
    private int _statusNotificationVersion;
    private bool _statusNotificationError;

    private void ShowStatusNotification(bool error)
    {
        if (StatusNotification is null || _closed) return;
        if (string.IsNullOrWhiteSpace(StatusText.Text)) { HideStatusNotification(); return; }
        if (_statusDismissTimer is null)
        {
            _statusDismissTimer = new DispatcherTimer();
            _statusDismissTimer.Tick += (_, _) =>
            {
                _statusDismissTimer.Stop();
                if (_busy == 0) HideStatusNotification();
            };
            Closed += (_, _) =>
            {
                _statusDismissTimer.Stop();
                _statusNotificationVersion++;
                StatusNotification.BeginAnimation(OpacityProperty, null);
                StatusNotificationOffset.BeginAnimation(TranslateTransform.YProperty, null);
            };
        }
        _statusNotificationError = error;
        AnimateStatusNotification(true);
        UpdateStatusNotificationLifetime();
    }

    private void UpdateStatusNotificationLifetime()
    {
        if (_statusDismissTimer is null || _closed) return;
        _statusDismissTimer.Stop();
        if (_busy == 0 && StatusNotification.Visibility == Visibility.Visible)
        {
            _statusDismissTimer.Interval = TimeSpan.FromSeconds(_statusNotificationError ? 6 : 3.2);
            _statusDismissTimer.Start();
        }
    }

    private void HideStatusNotification()
    {
        _statusDismissTimer?.Stop();
        if (StatusNotification is not null) AnimateStatusNotification(false);
    }

    private void AnimateStatusNotification(bool shown)
    {
        var version = ++_statusNotificationVersion;
        var opacity = StatusNotification.Opacity;
        var y = StatusNotificationOffset.Y;
        StatusNotification.BeginAnimation(OpacityProperty, null);
        StatusNotificationOffset.BeginAnimation(TranslateTransform.YProperty, null);
        StatusNotification.Visibility = Visibility.Visible;
        StatusNotification.Opacity = shown ? 1 : 0;
        StatusNotificationOffset.Y = shown ? 0 : 14;
        if (!SystemParameters.ClientAreaAnimation || !IsVisible || WindowState == WindowState.Minimized)
        {
            if (!shown) StatusNotification.Visibility = Visibility.Collapsed;
            return;
        }
        var duration = TimeSpan.FromMilliseconds(shown ? 200 : 180);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(opacity, shown ? 1 : 0, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        if (!shown) fade.Completed += (_, _) =>
        {
            if (version == _statusNotificationVersion) StatusNotification.Visibility = Visibility.Collapsed;
        };
        StatusNotification.BeginAnimation(OpacityProperty, fade);
        StatusNotificationOffset.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(y, shown ? 0 : 14, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
    }
}
