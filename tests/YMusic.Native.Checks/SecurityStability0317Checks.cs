using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckSecurityStability0317()
    {
        await Run("Security stability0317: active offline and cache cleanup reject restart without changing preferences, then retry once", R4ActiveMaintenance);
        await Run("Security stability0317: cleanup starting during a pending download prevents restart and preference writes", R4MaintenanceDuringWait);
        await Run("Security stability0317: lifetime cancellation and closed window prevent a pending restart", R4CanceledRestart);
        await Run("Security stability0317: both update controls follow maintenance without replacing the settings draft", R4MaintenanceUpdateControls);
        await Run("Security stability0317: deleting one local fixture track disables restart then restores retry controls", R4SingleTrackMaintenanceControls);
    }

    private static async Task R4ActiveMaintenance()
    {
        foreach (var flag in new[] { "_offlineMaintenance", "_cacheMaintenance" })
        {
            using var f = new R4UpdateFixture("active-" + flag);
            R4Set(f.Window, flag, true);
            var applied = await R4Task<bool>(f.Window, "ApplyAppUpdateAsync", f.Updates);
            await R4Task(f.Window, "InstallStartupUpdateAsync");
            Assert(!applied && f.Backend.Applies == 0 && f.PreferencesUnchanged && R4Get<bool>(f.Window, "_autoFavoritesEnabled")
                && !R4Get<DownloadQueueState>(f.Window, "_downloadQueue").Suspended,
                "known cleanup allowed apply or changed preferences/download state");
            R4Set(f.Window, flag, false); R4Call(f.Window, "RefreshMaintenanceActions");
            await R4Task(f.Window, "InstallStartupUpdateAsync");
            await R4Task(f.Window, "InstallStartupUpdateAsync");
            Assert(f.Backend.Applies == 1 && R4Get<bool>(f.Window, "_startupUpdateRestartRequested")
                && R4Get<bool>(f.Window, "_autoFavoritesEnabled"), "retry did not apply exactly once or changed automatic downloads");
        }
    }

    private static async Task R4MaintenanceDuringWait()
    {
        foreach (var flag in new[] { "_offlineMaintenance", "_cacheMaintenance" })
        {
            using var f = new R4UpdateFixture("late-" + flag);
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            R4Set(f.Window, "_downloadTask", pending.Task);
            var restart = R4Task(f.Window, "InstallStartupUpdateAsync");
            Assert(!restart.IsCompleted && f.Backend.Applies == 0 && f.PreferencesUnchanged, "restart did not wait without saving preferences");
            R4Set(f.Window, flag, true); R4Call(f.Window, "RefreshMaintenanceActions");
            pending.TrySetResult(); await restart.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(f.Backend.Applies == 0 && f.PreferencesUnchanged && !R4Get<bool>(f.Window, "_startupUpdateRestartRequested")
                && R4Get<bool>(f.Window, "_autoFavoritesEnabled"), "cleanup that began during download wait was bypassed");
            R4Set(f.Window, flag, false); R4Set(f.Window, "_downloadTask", null); R4Call(f.Window, "RefreshMaintenanceActions");
            await R4Task(f.Window, "InstallStartupUpdateAsync"); await R4Task(f.Window, "InstallStartupUpdateAsync");
            Assert(f.Backend.Applies == 1, "late-blocked update was not retriable exactly once");
        }
    }

    private static async Task R4CanceledRestart()
    {
        foreach (var closed in new[] { false, true })
        {
            using var f = new R4UpdateFixture(closed ? "closed" : "lifetime");
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            R4Set(f.Window, "_downloadTask", pending.Task);
            var restart = R4Task(f.Window, "InstallStartupUpdateAsync");
            Assert(!restart.IsCompleted, "fixture did not enter pending restart wait");
            if (closed) { R4Set(f.Window, "_closed", true); pending.TrySetResult(); }
            else f.Lifetime.Cancel();
            await restart.WaitAsync(TimeSpan.FromSeconds(5)); pending.TrySetResult();
            Assert(f.Backend.Applies == 0 && f.PreferencesUnchanged && R4Get<bool>(f.Window, "_autoFavoritesEnabled"),
                "closed/canceled window applied an update or saved new preferences");
        }
    }

    private static async Task R4MaintenanceUpdateControls()
    {
        using var f = new R4UpdateFixture("controls");
        var panel = (StackPanel)R4Call(f.Window, "CreateUpdateSettingsPanelCore", f.Updates)!;
        var button = panel.Children.OfType<Button>().Single();
        var text = panel.Children.OfType<TextBlock>().Last();
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        try
        {
            foreach (var flag in new[] { "_offlineMaintenance", "_cacheMaintenance" })
            {
                R4Set(f.Window, flag, true); R4Call(f.Window, "RefreshMaintenanceActions");
                Assert(!button.IsEnabled && !f.StartupButton.IsEnabled && text.Text.Contains("Дождитесь", StringComparison.Ordinal)
                    && f.StartupText.Text.Contains("повторите", StringComparison.Ordinal), "maintenance left an enabled update button or no explanation");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await R4Task(f.Window, "InstallStartupUpdateAsync");
                Assert(f.Backend.Applies == 0 && f.PreferencesUnchanged, "disabled UI handler bypassed cleanup guard");
                R4Set(f.Window, flag, false); R4Call(f.Window, "RefreshMaintenanceActions");
                Assert(button.IsEnabled && f.StartupButton.IsEnabled && R4Get<bool>(f.Window, "_settingsOpen")
                    && R4Get<TextBox>(f.Window, "SettingsAccentHex").Text == "#20A0B0", "cleanup completion did not enable retry or replaced the settings draft");
            }
        }
        finally { panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
    }

    private static async Task R4SingleTrackMaintenanceControls()
    {
        using var f = new R4UpdateFixture("single-track");
        var music = Path.Combine(f.DirectoryPath, "Music"); Directory.CreateDirectory(music);
        var bytes = new byte[4096]; File.WriteAllBytes(Path.Combine(music, "fixture.mp3"), bytes);
        var item = new OfflineItem("fixture", "fixture:album", "Fixture", "Fixture", "Fixture", 3000, "fixture.mp3", "mp3", 320,
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(music, "library.index.json"), JsonSerializer.Serialize(new[] { item }));
        using var library = new OfflineLibrary(music, new AudioBuffer(Path.Combine(f.DirectoryPath, "offline-buffer"), new R4NoNetwork()));
        var track = new TrackRow { Id = item.Id, RequestId = item.RequestId, Title = item.Title };
        var list = new ListView(); list.Items.Add(track); list.SelectedItem = track;
        R4Set(f.Window, "TracksList", list); R4Set(f.Window, "_offline", library);
        R4Set(f.Window, "_tracks", new TrackRows()); R4Set(f.Window, "_favoritesCache", new List<TrackRow>());
        R4Set(f.Window, "_waveTracks", new List<TrackRow>()); R4Set(f.Window, "_playbackQueue", new PlaybackQueue());
        R4Set(f.Window, "_catalogItems", new ObservableCollection<CatalogItem>()); R4Set(f.Window, "_homeSections", Array.Empty<HomeSection>());
        R4Set(f.Window, "_deletedUntilFavoritesRefresh", new HashSet<string>(StringComparer.Ordinal));
        foreach (var name in new[] { "StatusText", "WaveStatus", "HomeStatus", "ArtistStatus" }) R4Set(f.Window, name, new TextBlock());
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        R4Set(f.Window, "_downloadTask", pending.Task);
        R4Call(f.Window, "DeleteOfflineTrack_Click", f.Window, new RoutedEventArgs());
        Assert(R4Get<bool>(f.Window, "_offlineMaintenance") && !f.StartupButton.IsEnabled,
            "single-track deletion did not immediately disable update restart");
        R4Call(f.Window, "DeleteOfflineTrack_Click", f.Window, new RoutedEventArgs()); // Active-operation early return.
        Assert(!await R4Task<bool>(f.Window, "ApplyAppUpdateAsync", f.Updates), "single-track deletion allowed SDK apply");
        pending.TrySetResult();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (R4Get<bool>(f.Window, "_offlineMaintenance"))
        {
            Assert(DateTime.UtcNow < deadline, "single-track fixture did not complete"); await Task.Delay(10);
        }
        Assert(library.Read().Count == 0 && !File.Exists(Path.Combine(music, "fixture.mp3")) && f.StartupButton.IsEnabled
            && R4Get<bool>(f.Window, "_settingsOpen") && R4Get<TextBox>(f.Window, "SettingsAccentHex").Text == "#20A0B0"
            && f.Backend.Applies == 0 && f.PreferencesUnchanged,
            "single-track deletion failed or did not restore update retry without changing the settings draft");
    }

    private static FieldInfo R4Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void R4Set(object target, string name, object? value) => R4Field(target, name).SetValue(target, value);
    private static T R4Get<T>(object target, string name) => (T)R4Field(target, name).GetValue(target)!;
    private static object? R4Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static Task R4Task(object target, string name, params object[] args) => (Task)R4Call(target, name, args)!;
    private static Task<T> R4Task<T>(object target, string name, params object[] args) => (Task<T>)R4Call(target, name, args)!;

    // The product constructor reads several optional stores before a fixture
    // could replace them. This private fixture never calls it or shows a window.
    private sealed class R4UpdateFixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal CancellationTokenSource Lifetime { get; } = new();
        internal R4UpdateBackend Backend { get; } = new();
        internal UpdateSupport Updates { get; }
        internal Button StartupButton { get; } = new();
        internal TextBlock StartupText { get; } = new();
        internal string DirectoryPath { get; }
        private readonly NativePlayer _player;
        private readonly string _preferencesPath;
        private readonly byte[] _preferencesBefore;
        internal bool PreferencesUnchanged => File.ReadAllBytes(_preferencesPath).SequenceEqual(_preferencesBefore);

        internal R4UpdateFixture(string name)
        {
            var directory = DirectoryPath = Path.Combine(_directory, "security-stability0317-" + name); Directory.CreateDirectory(directory);
            var feed = Path.Combine(directory, "feed.json");
            File.WriteAllText(feed, "{\"githubRepository\":\"https://github.com/example/r4-fixture\"}");
            Updates = new UpdateSupport(feed, true, _ => Backend);
            Updates.CheckAsync().GetAwaiter().GetResult(); // PendingVersion makes this synchronous and local.
            var preferences = new PreferencesStore(directory);
            preferences.Save(new PlayerPreferences { Volume = .21, AutoDownloadFavorites = true });
            _preferencesPath = Directory.GetFiles(directory, "*.json").Single(path => Path.GetFileName(path) != "feed.json");
            _preferencesBefore = File.ReadAllBytes(_preferencesPath);
            _player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer"), new R4NoNetwork()));
            Window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            R4Set(Window, "_lifetime", Lifetime); R4Set(Window, "_downloadQueue", new DownloadQueueState());
            R4Set(Window, "_appUpdatesOverride", Updates); R4Set(Window, "_player", _player);
            R4Set(Window, "_preferencesReady", true); R4Set(Window, "_preferenceStore", preferences);
            R4Set(Window, "_autoFavoritesEnabled", true); R4Set(Window, "_concurrentDownloads", 3);
            R4Set(Window, "_accentFromCover", true); R4Set(Window, "_staticAccentColor", "#EA571C");
            R4Set(Window, "QualityCombo", new ComboBox { SelectedIndex = 1 }); R4Set(Window, "VolumeSlider", new Slider { Maximum = 100, Value = 67 });
            R4Set(Window, "SettingsButton", new Button()); R4Set(Window, "SettingsClearOffline", new Button());
            R4Set(Window, "SettingsClearCache", new Button()); R4Set(Window, "SettingsUninstall", new Button());
            R4Set(Window, "_startupUpdatePrompt", new Border()); R4Set(Window, "_startupUpdateText", StartupText);
            R4Set(Window, "_startupUpdateButton", StartupButton); R4Set(Window, "_startupUpdateLater", new Button());
            R4Set(Window, "_settingsOpen", true); R4Set(Window, "SettingsAccentHex", new TextBox { Text = "#20A0B0" });
        }
        public void Dispose() { Lifetime.Cancel(); _player.Dispose(); Lifetime.Dispose(); }
    }
    private sealed class R4UpdateBackend : IAppUpdateBackend
    {
        internal int Applies;
        public string? PendingVersion => "9.1.0";
        public Task<string?> CheckAsync() => throw new InvalidOperationException("Pending fake package must not check the network");
        public Task DownloadAsync(Action<int> progress, CancellationToken token) => throw new InvalidOperationException("Fake package is already downloaded");
        public void ApplyAndRestart() => Applies++;
    }
    private sealed class R4NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("Security stability fixture forbids network requests");
    }
}
