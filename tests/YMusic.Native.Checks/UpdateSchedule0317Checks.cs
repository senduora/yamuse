using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckUpdateSchedule0317()
    {
        await Run("Updates0317: all intervals calendar month restart and future checkpoints are bounded", U317Calendar);
        await Run("Updates0317: missing invalid preferences and corrupt checkpoint preserve safe defaults", U317Storage);
        await Run("Updates0317: Startup Off and periodic checks remain serialized and stop on close", U317Scheduler);
        await Run("Updates0317: persisted attempt survives restart and a failed request waits the selected interval", U317Restart);
        await Run("Updates0317: immediate frequency persistence and automatic status preserve current edits", U317SettingsDraft);
        await Run("Updates0317: first bind live apply invalid HEX debounce close and centered settings have no save footer", U317InstantSettings);
        await Run("Updates0317: portable check and explicit official links never create installer backend", U317Portable);
        await Run("Updates0317: portable HTTP metadata rejects redirects oversized malformed and canceled responses", U317PortableTrust);
        await Run("Updates0317: footer status and links fit the compact footer at both window sizes", U317Footer);
    }

    private static Task U317Calendar()
    {
        var jan = new DateTimeOffset(2028, 1, 31, 20, 15, 0, TimeSpan.Zero);
        foreach (var (frequency, expected) in new[]
        {
            (UpdateCheckFrequency.Hourly, jan.AddHours(1)), (UpdateCheckFrequency.Daily, jan.AddDays(1)),
            (UpdateCheckFrequency.Weekly, jan.AddDays(7)), (UpdateCheckFrequency.Monthly, new DateTimeOffset(2028, 2, 29, 20, 15, 0, TimeSpan.Zero))
        })
        {
            var checkpoint = new UpdateScheduleCheckpoint(jan, jan.AddMinutes(-1));
            Assert(UpdateSchedulePolicy.NextDue(frequency, checkpoint, jan) == expected, frequency + " interval drift");
            Assert(UpdateSchedulePolicy.NextDue(frequency, checkpoint, expected.AddTicks(-1)) > expected.AddTicks(-1), frequency + " checked early");
            Assert(UpdateSchedulePolicy.NextDue(frequency, checkpoint, expected) == expected, frequency + " lost due on restart");
            Assert(UpdateSchedulePolicy.NextDue(frequency, new(jan.AddYears(3)), jan) > jan, frequency + " future clock created retry loop");
            Assert(UpdateSchedulePolicy.NextDue(frequency, new(), jan) == jan, frequency + " first check is not due");
        }
        Assert(UpdateSchedulePolicy.NextDue(UpdateCheckFrequency.Off, new(), jan) is null
            && UpdateSchedulePolicy.NextDue(UpdateCheckFrequency.Startup, new(), jan) is null
            && UpdateSchedulePolicy.NextDue((UpdateCheckFrequency)999, new(), jan) is null, "disabled or invalid frequency scheduled a timer");
        Assert(UpdateSchedulePolicy.NextDue(UpdateCheckFrequency.Monthly, new(new(2027, 1, 31, 0, 0, 0, TimeSpan.Zero)), jan)
            == new DateTimeOffset(2027, 2, 28, 0, 0, 0, TimeSpan.Zero), "nonleap month boundary drift");
        return Task.CompletedTask;
    }

    private static Task U317Storage()
    {
        var directory = Path.Combine(_directory, "updates0317-store"); Directory.CreateDirectory(directory);
        var preferences = new PreferencesStore(directory);
        Assert(preferences.Load().UpdateCheckFrequency == UpdateCheckFrequency.Startup, "missing preference did not preserve Startup");
        File.WriteAllText(preferences.FilePath, "{\"volume\":0.23,\"updateCheckFrequency\":\"future-mode\"}");
        var recovered = preferences.Load();
        Assert(recovered.Volume == .23 && recovered.UpdateCheckFrequency == UpdateCheckFrequency.Startup, "invalid frequency reset unrelated preferences");
        foreach (var frequency in Enum.GetValues<UpdateCheckFrequency>())
        { preferences.Save(new() { UpdateCheckFrequency = frequency }); Assert(preferences.Load().UpdateCheckFrequency == frequency, "mode persistence drift"); }
        var store = new UpdateScheduleStore(directory); var now = DateTimeOffset.UtcNow;
        foreach (var corrupt in new[] { "{", "{}", new string('x', 5000), "{\"LastAttemptUtc\":\"bad-date\"}" })
        { File.WriteAllText(store.FilePath, corrupt); Assert(store.Load(now).LastAttemptUtc is null, "corrupt checkpoint survived"); }
        store.Save(new(now.AddDays(2), now.AddDays(1)));
        Assert(store.Load(now) == new UpdateScheduleCheckpoint(now, now), "future persisted clock was not reset");
        Assert(Directory.GetFiles(directory, "*.tmp").Length == 0, "checkpoint write left temporary files");
        return Task.CompletedTask;
    }

    private static async Task U317Scheduler()
    {
        using var f = new SaFixture("updates0317-scheduler"); var w = f.Window;
        var backend = new Su016Backend { HoldCheck = true }; Su016Service(w, "u317-scheduler", backend);
        var now = new DateTimeOffset(2028, 3, 1, 1, 0, 0, TimeSpan.Zero); U317Set(w, "_updateClock", (Func<DateTimeOffset>)(() => now));
        U317Set(w, "_updateCheckFrequency", UpdateCheckFrequency.Off);
        await Call(w, "CheckForAppUpdatesAsync"); await Call(w, "RunScheduledUpdateCheckAsync");
        Assert(backend.Checks == 0 && SaField<DispatcherTimer?>(w, "_updateScheduleTimer") is null, "Off started a check or timer");
        U317Set(w, "_updateCheckFrequency", UpdateCheckFrequency.Hourly);
        var first = Call(w, "RunScheduledUpdateCheckAsync"); await backend.CheckEntered.Task;
        await Call(w, "RunScheduledUpdateCheckAsync"); SaClick(Control<Button>(w, "FooterUpdateButton"));
        Assert(backend.Checks == 1 && !Control<Button>(w, "FooterUpdateButton").IsEnabled, "manual/timer requests overlapped");
        backend.CheckRelease.TrySetResult(); await first;
        Assert(Su016Find<Border>(w, "StartupUpdatePrompt") is null && backend.Downloads == 0 && backend.Applies == 0, "periodic check navigated, prompted, or installed");
        await Call(w, "RunScheduledUpdateCheckAsync"); Assert(backend.Checks == 1, "checked before due");
        now = now.AddHours(1); backend.HoldCheck = false;
        await Call(w, "RunScheduledUpdateCheckAsync"); Assert(backend.Checks == 2, "missed due hourly check");
        var timer = SaField<DispatcherTimer?>(w, "_updateScheduleTimer"); Assert(timer?.IsEnabled == true, "interval timer was not armed");
        w.Close(); now = now.AddHours(1); await Call(w, "RunScheduledUpdateCheckAsync");
        Assert(timer is { IsEnabled: false } && SaField<DispatcherTimer?>(w, "_updateScheduleTimer") is null && backend.Checks == 2,
            "timer or clock callback survived close");
    }

    private static async Task U317Restart()
    {
        var directory = Path.Combine(_directory, "updates0317-restart"); var store = new UpdateScheduleStore(directory);
        var now = new DateTimeOffset(2028, 5, 1, 3, 0, 0, TimeSpan.Zero);
        using (var first = new SaFixture("updates0317-restart1"))
        {
            var backend = new Su016Backend { CheckFailures = 1 }; Su016Service(first.Window, "u317-restart1", backend);
            U317Set(first.Window, "_updateCheckFrequency", UpdateCheckFrequency.Daily); U317Set(first.Window, "_updateScheduleStore", store);
            U317Set(first.Window, "_updateClock", (Func<DateTimeOffset>)(() => now));
            await Call(first.Window, "CheckForAppUpdatesAsync");
            Assert(backend.Checks == 1 && store.Load(now).LastAttemptUtc == now && store.Load(now).LastSuccessUtc is null,
                "failed check did not persist only its attempt");
        }
        using var second = new SaFixture("updates0317-restart2"); var retry = new Su016Backend(); Su016Service(second.Window, "u317-restart2", retry);
        U317Set(second.Window, "_updateCheckFrequency", UpdateCheckFrequency.Daily); U317Set(second.Window, "_updateScheduleStore", store);
        U317Set(second.Window, "_updateClock", (Func<DateTimeOffset>)(() => now));
        await Call(second.Window, "CheckForAppUpdatesAsync"); Assert(retry.Checks == 0, "restart retried a failed request too soon");
        now = now.AddDays(1); await Call(second.Window, "RunScheduledUpdateCheckAsync");
        Assert(retry.Checks == 1 && store.Load(now).LastSuccessUtc == now, "due restart did not record the successful check");
    }

    private static async Task U317SettingsDraft()
    {
        using var f = new SaFixture("updates0317-draft"); var w = f.Window;
        var backend = new Su016Backend { Version = null }; Su016Service(w, "u317-draft", backend);
        var store = new PreferencesStore(Path.Combine(_directory, "updates0317-draft-settings"));
        U317Set(w, "_preferenceStore", store); U317Set(w, "_preferencesReady", true);
        U317Set(w, "_updateCheckFrequency", UpdateCheckFrequency.Hourly); SaInvoke(w, "SavePreferences");
        SaClick(Control<Button>(w, "SettingsButton"));
        var frequency = SaField<ComboBox>(w, "_updateFrequencyCombo"); frequency.SelectedValue = UpdateCheckFrequency.Weekly;
        Control<TextBox>(w, "SettingsAccentHex").Text = "#2399BB";
        Control<ComboBox>(w, "SettingsSpeed").SelectedItem = 1.5;
        var before = File.ReadAllBytes(store.FilePath);
        Assert(store.Load().UpdateCheckFrequency == UpdateCheckFrequency.Weekly && f.Player.SpeedRatio == 1.5,
            "selection did not apply immediately");
        Control<TextBox>(w, "SettingsAccentHex").Text = "#23";
        var settingsRoot = Control<Grid>(w, "SettingsPanel"); var currentInput = Control<TextBox>(w, "SettingsAccentHex");
        System.Windows.Input.FocusManager.SetFocusedElement(settingsRoot, currentInput);
        await Call(w, "CheckForAppUpdatesAsync"); SaInvoke(w, "RefreshAppUpdateUi");
        Assert(File.ReadAllBytes(store.FilePath).SequenceEqual(before) && frequency.SelectedValue is UpdateCheckFrequency.Weekly
            && Control<TextBox>(w, "SettingsAccentHex").Text == "#23" && Equals(Control<ComboBox>(w, "SettingsSpeed").SelectedItem, 1.5)
            && ReferenceEquals(System.Windows.Input.FocusManager.GetFocusedElement(settingsRoot), currentInput),
            "automatic status/checkpoint persisted or replaced current input");
        SaInvoke(w, "SettingsClose_Click", Control<Button>(w, "SettingsBackButton"), new RoutedEventArgs());
        SaClick(Control<Button>(w, "SettingsButton"));
        Assert(frequency.SelectedValue is UpdateCheckFrequency.Weekly && store.Load().UpdateCheckFrequency == UpdateCheckFrequency.Weekly,
            "Back rolled back the current frequency");
        frequency.SelectedValue = UpdateCheckFrequency.Off;
        Assert(store.Load().UpdateCheckFrequency == UpdateCheckFrequency.Off && SaField<DispatcherTimer?>(w, "_updateScheduleTimer")?.IsEnabled != true,
            "Off did not persist immediately and stop the scheduler");
        frequency.SelectedValue = UpdateCheckFrequency.Hourly;
        Assert(store.Load().UpdateCheckFrequency == UpdateCheckFrequency.Hourly && SaField<DispatcherTimer?>(w, "_updateScheduleTimer")?.IsEnabled == true,
            "frequency change did not rearm and persist immediately");
        frequency.SelectedValue = UpdateCheckFrequency.Off;
        SaClick(Control<Button>(w, "FooterUpdateButton")); await Until(() => backend.Checks == 2 && !SaField<bool>(w, "_updateCheckInProgress"));
        Assert(store.Load().UpdateCheckFrequency == UpdateCheckFrequency.Off, "manual check silently enabled automatic updates");
    }

    private static async Task U317InstantSettings()
    {
        using var f = new SaFixture("updates0317-instant"); var w = f.Window;
        var directory = Path.Combine(_directory, "updates0317-instant-settings"); var store = new PreferencesStore(directory);
        store.Save(new() { Volume = .37, PlaybackSpeed = 1.25, CrossfadeSeconds = 6, AutoDownloadFavorites = true, StaticAccentColor = "#6EADFA" });
        var initial = File.ReadAllBytes(store.FilePath);
        U317Set(w, "_preferenceStore", store); SaInvoke(w, "LoadPreferences");
        var requestsBefore = f.Handler.Seen.Count;
        SaClick(Control<Button>(w, "SettingsButton"));
        Assert(File.ReadAllBytes(store.FilePath).SequenceEqual(initial) && f.Handler.Seen.Count == requestsBefore
            && SaField<Task?>(w, "_downloadTask") is null && f.Player.SpeedRatio == 1.25 && f.Player.CrossfadeSeconds == 6,
            "initial binding rewrote preferences or started downloads/network");
        var panel = Control<Grid>(w, "SettingsPanel");
        Assert(w.FindName("SettingsApplyButton") is null && w.FindName("SettingsCancelButton") is null && panel.RowDefinitions.Count == 2,
            "removed Save/Cancel footer remains in XAML");
        foreach (var (width, height) in new[] { (1050, 680), (1280, 860), (1600, 1000) })
        {
            w.Width = width; w.Height = height; w.UpdateLayout(); await Task.Delay(350);
            var root = Control<Grid>(w, "LayoutRoot"); var bounds = panel.TransformToAncestor(root).TransformBounds(new Rect(panel.RenderSize));
            var sidebar = Control<Border>(w, "Sidebar"); var expectedCenter = sidebar.ActualWidth + (root.ActualWidth - sidebar.ActualWidth) / 2;
            Assert(Math.Abs(bounds.Left + bounds.Width / 2 - expectedCenter) <= 1 && panel.ActualWidth <= 800.5
                && Control<ScrollViewer>(w, "SettingsScroll").ViewportHeight > 200, "Settings content/header is not centered or former footer still consumes space");
            App.Capture(w, Path.Combine(_directory, $"updates0317-settings-{width}x{height}.png"));
        }
        Control<ComboBox>(w, "SettingsSpeed").SelectedItem = 1.5;
        Assert(f.Player.SpeedRatio == 1.5 && store.Load().PlaybackSpeed == 1.5, "speed did not apply/save immediately");
        var beforeFailure = File.ReadAllBytes(store.FilePath);
        using (var blocked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Control<ComboBox>(w, "SettingsSpeed").SelectedItem = 2d;
            Assert(f.Player.SpeedRatio == 2 && Equals(Control<ComboBox>(w, "SettingsSpeed").SelectedItem, 2d)
                && File.ReadAllBytes(store.FilePath).SequenceEqual(beforeFailure)
                && SaField<Diagnostics>(w, "_diagnostics").LastErrorType?.StartsWith("PreferencesSave:", StringComparison.Ordinal) == true,
                "failed atomic save corrupted disk, reset live input, or hid the failure");
        }
        Control<ComboBox>(w, "SettingsSpeed").SelectedItem = 1.25;
        Assert(store.Load().PlaybackSpeed == 1.25, "retry after failed atomic save did not persist");
        Control<TextBox>(w, "SettingsAccentHex").Text = "#123456";
        var valid = File.ReadAllBytes(store.FilePath);
        Control<TextBox>(w, "SettingsAccentHex").Text = "#12";
        Assert(Control<TextBox>(w, "SettingsAccentHex").Text == "#12" && File.ReadAllBytes(store.FilePath).SequenceEqual(valid)
            && SaField<string>(w, "_staticAccentColor") == "#123456", "incomplete HEX replaced the last valid color or lost input");
        var slider = Control<Slider>(w, "CrossfadeSetting");
        var beforeSlider = File.ReadAllBytes(store.FilePath);
        for (var i = 0; i < 10000; i++) slider.Value = i % 2 == 0 ? 3 : 7;
        Assert(f.Player.CrossfadeSeconds == 7 && File.ReadAllBytes(store.FilePath).SequenceEqual(beforeSlider), "slider did not apply runtime immediately or wrote per change");
        await Task.Delay(350);
        Assert(store.Load().CrossfadeSeconds == 7 && SaField<DispatcherTimer?>(w, "_settingsSaveTimer")?.IsEnabled != true,
            "slider debounce did not persist and stop");
        slider.Value = 8; w.Close();
        Assert(store.Load().CrossfadeSeconds == 8 && SaField<DispatcherTimer?>(w, "_settingsSaveTimer") is null, "close lost a pending slider value or retained its timer");
    }

    private static UpdateSupport U317PortableService(MainWindow window, Func<CancellationToken, Task<string?>> check)
    {
        var directory = Path.Combine(_directory, "updates0317-portable-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "feed.json"); File.WriteAllText(config, "{\"githubRepository\":\"https://github.com/senduora/yamuse\"}");
        var service = new UpdateSupport(config, false, _ => throw new InvalidOperationException("portable created installer backend"), check);
        U317Set(window, "_appUpdatesOverride", service); return service;
    }

    private static async Task U317Portable()
    {
        using var f = new SaFixture("updates0317-portable"); var w = f.Window; var checks = 0; var opened = new List<Uri>();
        var service = U317PortableService(w, token => { token.ThrowIfCancellationRequested(); checks++; return Task.FromResult<string?>("9.1.0"); });
        U317Set(w, "_updateLinkLauncher", (Action<Uri>)(uri => opened.Add(uri)));
        await Call(w, "CheckForAppUpdatesAsync");
        Assert(checks == 1 && service.State.Status == AppUpdateStatus.Available && Su016Find<Border>(w, "StartupUpdatePrompt") is null
            && opened.Count == 0, "portable startup did not check quietly or forced navigation");
        await service.DownloadAsync(); await Throws<InvalidOperationException>(() => { service.ApplyAndRestart(); return Task.CompletedTask; });
        Assert(service.State.Status == AppUpdateStatus.Available, "portable download/apply changed the service");
        SaClick(Control<Button>(w, "FooterUpdateButton"));
        SaClick(Control<Button>(w, "GithubButton")); SaClick(Control<Button>(w, "ChangelogButton"));
        Assert(opened.Select(uri => uri.AbsoluteUri).SequenceEqual(new[] { "https://github.com/senduora/yamuse/releases", "https://github.com/senduora/yamuse", "https://github.com/senduora/yamuse/releases" }),
            "portable click opened an untrusted page or installer");
        SaClick(Control<Button>(w, "SettingsButton"));
        var settingsPanel = (StackPanel)Control<ContentControl>(w, "SettingsUpdates").Content;
        SaClick(settingsPanel.Children.OfType<Button>().Single());
        Assert(opened.Count == 4 && opened[^1].AbsoluteUri == "https://github.com/senduora/yamuse/releases" && checks == 1,
            "portable settings action used an installer or repeated metadata check");
        using var late = new SaFixture("updates0317-portable-late"); var held = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = U317PortableService(late.Window, _ => held.Task);
        var pending = Call(late.Window, "CheckForAppUpdatesAsync"); late.Window.Close(); held.TrySetResult("9.1.0"); await pending;
        Assert(canceled.State.Status == AppUpdateStatus.Portable && Su016Find<Border>(late.Window, "StartupUpdatePrompt") is null, "late portable check published stale state");
    }

    private static async Task U317PortableTrust()
    {
        async Task<string?> Read(string json, HttpStatusCode code = HttpStatusCode.OK)
            => await UpdateSupport.CheckPortableReleaseAsync("0.3.17", CancellationToken.None, new FakeHandler((request, _) =>
            {
                Assert(request.RequestUri!.AbsoluteUri == "https://api.github.com/repos/senduora/yamuse/releases/latest"
                    && request.Method == HttpMethod.Get && request.Headers.Authorization is null && request.Headers.UserAgent.Count != 0,
                    "portable request lost fixed HTTPS/no credentials contract");
                return Task.FromResult(Json(json, code));
            }));
        Assert(await Read("{\"tag_name\":\"v0.3.18\",\"draft\":false,\"prerelease\":false}") == "0.3.18", "valid version was not detected");
        Assert(await Read("{\"tag_name\":\"v0.3.17\",\"draft\":false,\"prerelease\":false}") is null, "equal version offered update");
        Assert(await Read("{\"tag_name\":\"v0.3.16\",\"draft\":false,\"prerelease\":false}") is null, "older version offered downgrade");
        await Throws<HttpRequestException>(() => Read("{}", HttpStatusCode.Redirect));
        foreach (var json in new[] { "{", "{}", "{\"tag_name\":\"https://evil.invalid/a.exe\",\"draft\":false,\"prerelease\":false}",
            "{\"tag_name\":\"9.1.0\",\"draft\":true,\"prerelease\":false}", "{\"tag_name\":\"9.1.0\",\"draft\":false,\"prerelease\":true}" })
        {
            try { await Read(json); throw new InvalidOperationException("invalid metadata accepted"); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { }
        }
        await Throws<InvalidDataException>(() => Read(new string(' ', 65537)));
        await Throws<InvalidDataException>(() => UpdateSupport.CheckPortableReleaseAsync("0.3.17", CancellationToken.None,
            new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new U317UnknownLengthContent() }))));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Throws<OperationCanceledException>(() => UpdateSupport.CheckPortableReleaseAsync("0.3.17", cancel.Token,
            new FakeHandler((_, token) => { token.ThrowIfCancellationRequested(); throw new InvalidOperationException("canceled HTTP executed"); })));
    }

    private static async Task U317Footer()
    {
        using var f = new SaFixture("updates0317-footer"); var w = f.Window; var backend = new Su016Backend { Version = null };
        Su016Service(w, "u317-footer", backend); await Call(w, "CheckForAppUpdatesAsync");
        Assert(Equals(Control<Button>(w, "FooterUpdateButton").Content, "✓"), "up to date status was not truthful");
        foreach (var (width, height) in new[] { (1280, 860), (1050, 680) })
        {
            w.Width = width; w.Height = height; w.UpdateLayout();
            var footer = Control<Border>(w, "MetricsBar");
            foreach (var name in new[] { "VersionText", "FooterUpdateButton", "MemoryText", "CpuText", "GithubButton", "ChangelogButton" })
            {
                var element = (FrameworkElement)w.FindName(name);
                var bounds = element.TransformToAncestor(footer).TransformBounds(new Rect(element.RenderSize));
                Assert(bounds.Left >= 0 && bounds.Top >= -.5 && bounds.Right <= footer.ActualWidth + .5 && bounds.Bottom <= footer.ActualHeight + .5,
                    name + " escaped compact footer");
            }
            App.Capture(w, Path.Combine(_directory, $"updates0317-footer-{width}x{height}.png"));
        }
        backend.CheckFailures = 1; SaClick(Control<Button>(w, "FooterUpdateButton"));
        await Until(() => !SaField<bool>(w, "_updateCheckInProgress"));
        Assert(Equals(Control<Button>(w, "FooterUpdateButton").Content, "!") && Control<Button>(w, "FooterUpdateButton").IsEnabled,
            "failed check claimed success or lost retry");
    }

    private static void U317Set(MainWindow window, string name, object? value)
        => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);

    private sealed class U317UnknownLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[65537]).AsTask();
    }
}
