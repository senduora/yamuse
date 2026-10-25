using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckStartupUpdates016()
    {
        await Run("Startup updates016: one startup check offers an inline update at both sizes and Later preserves navigation and settings draft", Su016OfferAndLater);
        await Run("Startup updates016: no-update offline unconfigured and portable startup stay quiet", Su016QuietStartup);
        await Run("Startup updates016: explicit update downloads once with progress and waits for offline writes before one restart", Su016ExplicitUpdate);
        await Run("Startup updates016: an already downloaded package still waits for an explicit click", Su016PendingPackage);
        await Run("Startup updates016: failed download and failed apply show a safe retriable message", Su016Retry);
        await Run("Startup updates016: closing during a stale check or download never presents or applies an update", Su016CloseInFlight);
        await Run("Startup updates016: closing while an offline write is pending cancels the restart wait", Su016CloseBeforeRestart);
    }

    private static async Task Su016OfferAndLater()
    {
        using var f = new SaFixture("startup016-offer"); await f.Activate(); var w = f.Window;
        var backend = new Su016Backend { HoldCheck = true }; var updates = Su016Service(w, "offer", backend);
        SaClick(Control<Button>(w, "SettingsButton"));
        Control<TextBox>(w, "SettingsAccentHex").Text = "#20A0B0";
        Control<ComboBox>(w, "SettingsSpeed").SelectedItem = 1.5;
        var section = SaField<string>(w, "_section"); var session = SaField<Session?>(w, "_session");
        var root = Control<Grid>(w, "LayoutRoot"); var rowCount = root.RowDefinitions.Count;
        var playerRow = Grid.GetRow(Control<Border>(w, "PlayerBar"));
        var first = Call(w, "CheckForAppUpdatesAsync"); await backend.CheckEntered.Task;
        await Call(w, "CheckForAppUpdatesAsync");
        Assert(backend.Checks == 1 && Su016Find<Border>(w, "StartupUpdatePrompt") is null, "duplicate startup check or premature invitation");
        backend.CheckRelease.TrySetResult(); await first; w.UpdateLayout();
        var prompt = Su016Find<Border>(w, "StartupUpdatePrompt")!;
        Assert(prompt is { IsVisible: true } && updates.State.Status == AppUpdateStatus.Available
            && backend.Downloads == 0 && backend.Applies == 0, "startup did not offer the newer package without downloading/applying it");
        Assert(root.RowDefinitions.Count == rowCount && Grid.GetRow(Control<Border>(w, "PlayerBar")) == playerRow
            && Grid.GetRowSpan(Control<Border>(w, "Sidebar")) == 2 && Grid.GetRowSpan(Control<ContentControl>(w, "ExpandedPlayerHost")) == 3,
            "invitation changed the sidebar/player/expanded layout contract");
        foreach (var (width, height) in new[] { (1280, 860), (1050, 680) })
        {
            w.Width = width; w.Height = height; w.UpdateLayout(); await Task.Delay(90);
            var bounds = prompt.TransformToAncestor(root).TransformBounds(new Rect(prompt.RenderSize));
            var sidebar = Control<Border>(w, "Sidebar");
            var player = Control<Border>(w, "PlayerBar");
            var playerTop = player.TranslatePoint(new Point(0, 0), root).Y;
            Assert(bounds.Left >= sidebar.ActualWidth && bounds.Right <= root.ActualWidth + .5 && bounds.Top >= 0
                && bounds.Bottom < playerTop && prompt.ActualHeight > 80, "inline invitation is clipped or overlaps the sidebar/player");
            App.Capture(w, Path.Combine(_directory, $"startup-update016-{width}x{height}.png"));
        }
        Assert(SaField<string>(w, "_section") == section && ReferenceEquals(SaField<Session?>(w, "_session"), session)
            && Control<TextBox>(w, "SettingsAccentHex").Text == "#20A0B0" && Equals(Control<ComboBox>(w, "SettingsSpeed").SelectedItem, 1.5),
            "startup check replaced navigation/account or the unsaved settings draft");
        SaClick(Su016Find<Button>(w, "StartupUpdateLater")!);
        SaInvoke(w, "RefreshAppUpdateUi"); SaClick(Control<Button>(w, "SearchNav")); SaInvoke(w, "RefreshAppUpdateUi");
        await Call(w, "CheckForAppUpdatesAsync");
        Assert(prompt.Visibility == Visibility.Collapsed && backend.Checks == 1 && backend.Downloads == 0 && backend.Applies == 0,
            "Later repeated the invitation/check or started an update during navigation");
        Assert(Control<TextBox>(w, "SettingsAccentHex").Text == "#20A0B0" && !f.Player.HasSource,
            "Later reset the settings draft or started playback");
        Assert(Control<Button>(w, "SettingsButton").ToolTip?.ToString()?.Contains("9.1.0", StringComparison.Ordinal) == true,
            "Later removed access to the available update in settings");
    }

    private static async Task Su016QuietStartup()
    {
        foreach (var kind in new[] { "no-update", "offline", "portable", "unconfigured" })
        {
            using var f = new SaFixture("startup016-quiet-" + kind); var w = f.Window;
            var backend = new Su016Backend { Version = kind == "no-update" ? null : "9.1.0", CheckFailures = kind == "offline" ? 1 : 0 };
            var updates = Su016Service(w, "quiet-" + kind, backend, installed: kind != "portable", configured: kind != "unconfigured");
            var status = Control<TextBlock>(w, "StatusText").Text;
            await Call(w, "CheckForAppUpdatesAsync"); await Call(w, "CheckForAppUpdatesAsync");
            Assert(Su016Find<Border>(w, "StartupUpdatePrompt") is null && backend.Downloads == 0 && backend.Applies == 0,
                kind + " startup displayed or applied an update");
            Assert(backend.Checks == (kind is "portable" or "unconfigured" ? 0 : 1)
                && Control<TextBlock>(w, "StatusText").Text == status, kind + " startup retried or displayed an unsolicited error");
            if (kind == "offline") Assert(updates.State.Status == AppUpdateStatus.Error, "offline result was hidden from manual settings");
        }
    }

    private static async Task Su016ExplicitUpdate()
    {
        using var f = new SaFixture("startup016-download"); var w = f.Window;
        var backend = new Su016Backend { HoldDownload = true }; var updates = Su016Service(w, "download", backend);
        await Call(w, "CheckForAppUpdatesAsync");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(MainWindow).GetField("_downloadTask", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w, pending.Task);
        typeof(MainWindow).GetField("_autoFavoritesEnabled", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w, true);
        var button = Su016Find<Button>(w, "StartupUpdateButton")!;
        SaClick(button); await backend.DownloadEntered.Task; SaClick(button);
        await Until(() => Su016Find<TextBlock>(w, "StartupUpdateText")!.Text.Contains("42%", StringComparison.Ordinal));
        Assert(backend.Checks == 1 && backend.Downloads == 1 && backend.Applies == 0 && !button.IsEnabled
            && !Su016Find<Button>(w, "StartupUpdateLater")!.IsEnabled, "explicit download has no progress/guard or applied early");
        backend.DownloadRelease.TrySetResult();
        await Until(() => updates.State.Status == AppUpdateStatus.Downloaded && SaField<bool>(w, "_appUpdateActionInProgress")
            && Su016Find<TextBlock>(w, "StartupUpdateText")!.Text == "Готовим перезапуск…");
        Assert(backend.Applies == 0 && !button.IsEnabled, "restart did not wait for the pending offline write");
        pending.TrySetResult(); await Until(() => backend.Applies == 1 && !SaField<bool>(w, "_appUpdateActionInProgress"));
        backend.LastProgress?.Invoke(3);
        SaClick(button); SaInvoke(w, "RefreshAppUpdateUi");
        Assert(updates.State.Status == AppUpdateStatus.Downloaded && backend.Downloads == 1 && backend.Applies == 1 && !button.IsEnabled && SaField<bool>(w, "_autoFavoritesEnabled")
            && !f.Player.HasSource, "one update click restarted twice or changed the stored auto-download/media state");
    }

    private static async Task Su016PendingPackage()
    {
        using var f = new SaFixture("startup016-pending"); var w = f.Window;
        var backend = new Su016Backend { PendingVersion = "9.1.0" }; var updates = Su016Service(w, "pending", backend);
        await Call(w, "CheckForAppUpdatesAsync");
        Assert(updates.State.Status == AppUpdateStatus.Downloaded && backend.Checks == 0 && backend.Downloads == 0 && backend.Applies == 0,
            "pending update was downloaded/applied during startup");
        SaClick(Su016Find<Button>(w, "StartupUpdateButton")!);
        await Until(() => backend.Applies == 1 && !SaField<bool>(w, "_appUpdateActionInProgress"));
        Assert(backend.Downloads == 0, "already downloaded package was downloaded again");
    }

    private static async Task Su016Retry()
    {
        foreach (var failure in new[] { "download", "apply" })
        {
            using var f = new SaFixture("startup016-retry-" + failure); var w = f.Window;
            var backend = new Su016Backend { DownloadFailures = failure == "download" ? 1 : 0, ApplyFailures = failure == "apply" ? 1 : 0 };
            Su016Service(w, "retry-" + failure, backend); await Call(w, "CheckForAppUpdatesAsync");
            var button = Su016Find<Button>(w, "StartupUpdateButton")!; SaClick(button);
            await Until(() => !SaField<bool>(w, "_appUpdateActionInProgress") && button.IsEnabled);
            var error = Su016Find<TextBlock>(w, "StartupUpdateText")!.Text;
            Assert(error.Contains("Не удалось", StringComparison.Ordinal) && !error.Contains("fixture-private", StringComparison.Ordinal)
                && backend.Applies == (failure == "apply" ? 1 : 0), "failed explicit update did not provide a safe retriable error");
            SaClick(button); await Until(() => SaField<bool>(w, "_startupUpdateRestartRequested"));
            Assert(backend.Downloads == (failure == "download" ? 2 : 1) && backend.Applies == (failure == "apply" ? 2 : 1)
                && !button.IsEnabled, "retry downloaded an existing package or applied more than once");
        }
    }

    private static async Task Su016CloseInFlight()
    {
        using (var f = new SaFixture("startup016-close-check"))
        {
            var w = f.Window; var backend = new Su016Backend { HoldCheck = true }; var updates = Su016Service(w, "close-check", backend);
            var check = Call(w, "CheckForAppUpdatesAsync"); await backend.CheckEntered.Task;
            w.Close(); backend.CheckRelease.TrySetResult(); await check;
            Assert(Su016Find<Border>(w, "StartupUpdatePrompt") is null && updates.State.Status == AppUpdateStatus.Ready
                && backend.Downloads == 0 && backend.Applies == 0, "late canceled check presented or applied a stale update");
        }
        using (var f = new SaFixture("startup016-close-download"))
        {
            var w = f.Window; var backend = new Su016Backend { HoldDownload = true }; var updates = Su016Service(w, "close-download", backend);
            await Call(w, "CheckForAppUpdatesAsync"); SaClick(Su016Find<Button>(w, "StartupUpdateButton")!); await backend.DownloadEntered.Task;
            w.Close(); backend.DownloadRelease.TrySetResult();
            await Until(() => !SaField<bool>(w, "_appUpdateActionInProgress"));
            Assert(backend.Canceled && backend.Applies == 0 && updates.State.Status != AppUpdateStatus.Downloaded,
                "download ignored lifetime cancellation and restarted after close");
        }
    }

    private static async Task Su016CloseBeforeRestart()
    {
        using var f = new SaFixture("startup016-close-write"); var w = f.Window;
        var backend = new Su016Backend { PendingVersion = "9.1.0" }; Su016Service(w, "close-write", backend);
        await Call(w, "CheckForAppUpdatesAsync");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(MainWindow).GetField("_downloadTask", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w, pending.Task);
        SaClick(Su016Find<Button>(w, "StartupUpdateButton")!);
        Assert(SaField<bool>(w, "_appUpdateActionInProgress") && backend.Applies == 0, "restart did not wait for the offline task");
        w.Close(); await Until(() => !SaField<bool>(w, "_appUpdateActionInProgress"));
        pending.TrySetResult(); await Task.Yield();
        Assert(backend.Applies == 0, "closing the client did not cancel a pending restart wait");
    }

    private static UpdateSupport Su016Service(MainWindow window, string name, Su016Backend backend, bool installed = true, bool configured = true)
    {
        var directory = Path.Combine(_directory, "startup016-" + name); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "feed.json");
        File.WriteAllText(path, configured ? "{\"githubRepository\":\"https://github.com/example/startup-fixture\"}" : "{}");
        var service = new UpdateSupport(path, installed, _ => backend);
        typeof(MainWindow).GetField("_appUpdatesOverride", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, service);
        return service;
    }

    private static T? Su016Find<T>(MainWindow window, string name) where T : FrameworkElement
        => SaVisuals<T>(window).FirstOrDefault(element => element.Name == name);

    private sealed class Su016Backend : IAppUpdateBackend
    {
        internal int Checks, Downloads, Applies, CheckFailures, DownloadFailures, ApplyFailures;
        internal string? Version = "9.1.0";
        internal bool HoldCheck, HoldDownload, Canceled;
        internal Action<int>? LastProgress;
        internal TaskCompletionSource CheckEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CheckRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DownloadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DownloadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? PendingVersion { get; set; }
        public async Task<string?> CheckAsync()
        {
            Checks++; CheckEntered.TrySetResult(); if (HoldCheck) await CheckRelease.Task;
            if (CheckFailures-- > 0) throw new IOException("fixture-private-check-failure");
            return Version;
        }
        public async Task DownloadAsync(Action<int> progress, CancellationToken token)
        {
            Downloads++; LastProgress = progress; progress(42); DownloadEntered.TrySetResult();
            using var registration = token.Register(() => Canceled = true);
            // Deliberately complete a late callback even after cancellation, to
            // verify the service and UI do not turn it into a restart.
            if (HoldDownload) await DownloadRelease.Task;
            if (DownloadFailures-- > 0) throw new IOException("fixture-private-download-failure");
            PendingVersion = Version; progress(100);
        }
        public void ApplyAndRestart()
        { Applies++; if (ApplyFailures-- > 0) throw new IOException("fixture-private-apply-failure"); }
    }
}
