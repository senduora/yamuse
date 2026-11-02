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
        await Run("QaFixes061: update aborted by real cache maintenance keeps paused checkpoint and explicit continuation", Qf061A10UpdateMaintenance);
        await Run("QaFixes061: failed update apply and checkpoint replacement preserve recoverable downloads", Qf061A10UpdateErrors);
        await Run("QaFixes061: update close cancellation logout and same-account login preserve download isolation", Qf061A10UpdateBoundaries);
        await Run("QaFixes061: pending restore and superseding admission prevent stale update checkpoint writes", Qf061A10UpdateAdmission);
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

    private static async Task Qf061A10UpdateMaintenance()
    {
        using var f = new Qf061A10UpdateFixture("maintenance");
        var previousCache = Artwork.Cache;
        using var cache = new ArtworkCache(Path.Combine(f.DirectoryPath, "artwork"), new R4NoNetwork());
        var gate = R4Get<SemaphoreSlim>(cache, "_diskGate");
        await gate.WaitAsync();
        Task? cleanup = null;
        try
        {
            Artwork.Cache = cache;
            var update = f.BeginUpdate();
            Assert(!update.IsCompleted && R4Get<bool>(f.Window, "_appUpdateActionInProgress")
                && Control<Button>(f.Window, "SettingsClearCache").IsEnabled,
                "update did not enter its worker wait or blocked the real cache entry");
            cleanup = R4Task(f.Window, "ClearCachesAsync");
            Assert(R4Get<bool>(f.Window, "_cacheMaintenance") && !cleanup.IsCompleted,
                "real cache cleanup did not overlap the pending update");
            f.FinishWorker(); await update.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(f.Backend.Applies == 0 && !R4Get<bool>(f.Window, "_startupUpdateRestartRequested"),
                "late maintenance allowed an update apply");
            f.AssertPausedCheckpoint();
        }
        finally
        {
            gate.Release();
            try { if (cleanup is not null) await cleanup.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { Artwork.Cache = previousCache; }
        }
        f.AssertPausedCheckpoint();
        Assert(f.Queue.CanResume && !f.Queue.Running && R4Get<bool>(f.Window, "_downloadsAutoSuspended")
            && R4Get<Task?>(f.Window, "_downloadTask") is null && f.Requests == 0,
            "aborted update resumed downloads automatically or disabled explicit continuation");
        await R4Task(f.Window, "ResumeDownloadQueue");
        Assert(f.Queue.Entries.All(entry => entry.State == DownloadEntryState.Unavailable) && !f.Queue.Running && f.Requests == 0,
            "explicit continuation did not process the paused fixture queue without network");
    }

    private static async Task Qf061A10UpdateErrors()
    {
        using (var f = new Qf061A10UpdateFixture("apply-error"))
        {
            f.Backend.FailApply = true;
            var update = f.BeginUpdate(); f.FinishWorker();
            await update.WaitAsync(TimeSpan.FromSeconds(5));
            f.AssertPausedCheckpoint();
            Assert(f.Backend.Applies == 1 && !R4Get<bool>(f.Window, "_startupUpdateRestartRequested") && f.Queue.CanResume
                && R4Get<bool>(f.Window, "_downloadsAutoSuspended") && f.Requests == 0,
                "failed fake apply discarded or automatically resumed the paused queue");
        }
        using (var f = new Qf061A10UpdateFixture("checkpoint-error"))
        {
            var before = File.ReadAllBytes(f.SnapshotPath);
            using (var locked = new FileStream(f.SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var update = f.BeginUpdate();
                try { await update.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { f.FinishWorker(); }
                Assert(f.Backend.Applies == 0 && before.SequenceEqual(File.ReadAllBytes(f.SnapshotPath))
                    && R4Get<Diagnostics>(f.Window, "_diagnostics").LastErrorType?.StartsWith("DownloadSave:", StringComparison.Ordinal) == true,
                    "failed checkpoint replacement allowed apply or damaged the previous snapshot");
            }
            f.AssertPausedCheckpoint();
            Assert(f.Queue.CanResume && f.Requests == 0, "checkpoint failure stranded the live paused queue");
        }
    }

    private static async Task Qf061A10UpdateBoundaries()
    {
        foreach (var action in new[] { "close", "cancel", "logout", "same-account" })
        {
            using var f = new Qf061A10UpdateFixture(action);
            var update = f.BeginUpdate();
            Assert(!update.IsCompleted, action + ": fixture did not enter the update wait");
            if (action == "close") f.Window.Close();
            else if (action == "cancel") R4Get<CancellationTokenSource>(f.Window, "_lifetime").Cancel();
            else
            {
                f.Window.SignOut();
                if (action == "same-account")
                {
                    R4Set(f.Window, "_account", new Account(Qf061A10UpdateFixture.AccountUid, "Fixture relogin", false));
                    R4Set(f.Window, "_session", FakeSession);
                }
            }
            f.FinishWorker(); await update.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(f.Backend.Applies == 0 && f.Requests == 0, action + ": stale update applied or accessed the service");
            if (action is "close" or "cancel") f.AssertPausedCheckpoint();
            else Assert(!File.Exists(f.SnapshotPath) && f.Queue.Suspended
                && R4Get<bool>(f.Window, "_downloadPersistenceInvalidated"),
                action + ": old account checkpoint or resumable entries were revived");
        }
    }

    private static async Task Qf061A10UpdateAdmission()
    {
        using var f = new Qf061A10UpdateFixture("new-admission");
        var update = f.BeginUpdate();
        f.FinishWorker();
        // Worker completion queues the update continuation. A new user batch can
        // be admitted before that continuation runs on this same dispatcher.
        f.Queue.Replace([new TrackRow { Id = "replacement", RequestId = "replacement", Title = "New queue", Available = false }], "New queue");
        R4Call(f.Window, "BeginDownloadQueuePersistence");
        f.Store.Save(DownloadQueueStore.Capture(Qf061A10UpdateFixture.AccountUid, f.Queue, AudioQuality.Optimal, false, null));
        var replacement = File.ReadAllBytes(f.SnapshotPath);
        await update.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(f.Backend.Applies == 0 && replacement.SequenceEqual(File.ReadAllBytes(f.SnapshotPath))
            && f.Store.Load(Qf061A10UpdateFixture.AccountUid)?.Entries.Single().Track.Id == "replacement",
            "superseded update applied or rewrote the newly admitted download checkpoint");

        using var restoring = new Qf061A10UpdateFixture("pending-restore");
        var snapshot = restoring.Store.Load(Qf061A10UpdateFixture.AccountUid)!;
        restoring.Queue.EndRun(true); restoring.Queue.Replace([], "Restoring");
        R4Set(restoring.Window, "_downloadQueueAccountUid", null); R4Set(restoring.Window, "_downloadPersistenceAccountUid", null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new TaskCompletionSource<DownloadQueueSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        restoring.Window.DownloadQueueRestoreReadAsync = _ => { entered.TrySetResult(); return read.Task; };
        var restore = R4Task(restoring.Window, "RestoreDownloadQueueAsync", Qf061A10UpdateFixture.AccountUid);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var admission = R4Get<long>(restoring.Window, "_downloadQueueAdmissionRevision");
            await R4Task(restoring.Window, "InstallStartupUpdateAsync").WaitAsync(TimeSpan.FromSeconds(5));
            Assert(restoring.Backend.Applies == 0 && !restore.IsCompleted
                && R4Get<long>(restoring.Window, "_downloadQueueAdmissionRevision") == admission,
                "update superseded an in-flight initial restore before it could load the saved queue");
        }
        finally
        {
            read.TrySetResult(snapshot);
            await restore.WaitAsync(TimeSpan.FromSeconds(5));
        }
        restoring.AssertPausedCheckpoint();
        Assert(restoring.Queue.CanResume && !restoring.Queue.Running && restoring.Requests == 0,
            "rejected update left the initial restored queue unloaded or started its downloads");
    }

    private sealed class Qf061A10UpdateFixture : IDisposable
    {
        internal const string AccountUid = "qa-fixes061-update";
        internal string DirectoryPath { get; }
        internal string SnapshotPath => Path.Combine(DirectoryPath, DownloadQueueStore.FileName);
        internal MainWindow Window { get; }
        internal DownloadQueueState Queue => R4Get<DownloadQueueState>(Window, "_downloadQueue");
        internal DownloadQueueStore Store => R4Get<DownloadQueueStore>(Window, "_downloadQueueStore");
        internal Qf061A10UpdateBackend Backend { get; } = new();
        internal int Requests { get; private set; }
        private readonly OfflineLibrary _offline;
        private readonly CancellationTokenSource _workerCancellation;
        private TaskCompletionSource? _worker;
        internal Qf061A10UpdateFixture(string name)
        {
            DirectoryPath = Path.Combine(_directory, "qa-fixes061-update-" + name); Directory.CreateDirectory(DirectoryPath);
            var feed = Path.Combine(DirectoryPath, "feed.json");
            File.WriteAllText(feed, "{\"githubRepository\":\"https://github.com/example/qa-update-fixture\"}");
            var updates = new UpdateSupport(feed, true, _ => Backend);
            updates.CheckAsync().GetAwaiter().GetResult();
            _offline = new OfflineLibrary(Path.Combine(DirectoryPath, "music"),
                new AudioBuffer(Path.Combine(DirectoryPath, "download-buffer"), new R4NoNetwork()));
            var player = new NativePlayer(new AudioBuffer(Path.Combine(DirectoryPath, "player-buffer"), new R4NoNetwork()));
            var preferences = new PreferencesStore(DirectoryPath); preferences.Save(new() { Volume = 0, AutoDownloadFavorites = true });
            Window = new MainWindow(restore: false,
                api: new MusicApi(new FakeHandler((_, _) => { Requests++; throw new InvalidOperationException("Update fixture forbids network"); })),
                store: new SessionStore(DirectoryPath), diagnosticsDirectory: DirectoryPath, offline: _offline, player: player,
                preferences: preferences, audioOutputDevices: new PlaybackOptionsFakeOutputs());
            R4Set(Window, "_restore", true); R4Set(Window, "_account", new Account(AccountUid, "Fixture", false));
            R4Set(Window, "_session", FakeSession); R4Set(Window, "_appUpdatesOverride", updates);
            R4Call(Window, "InitializeDownloadPersistence"); R4Call(Window, "BeginDownloadQueuePersistence");
            R4Set(Window, "_downloadQueueAccountUid", AccountUid); R4Set(Window, "_downloadQueueQuality", AudioQuality.Optimal);
            Queue.Replace(Enumerable.Range(1, 2).Select(id => new TrackRow
            { Id = "pending-" + id, RequestId = "pending-" + id, Title = "Pending fixture", Available = false }), "Update fixture");
            Queue.BeginRun(false); // A live batch can yield in preflight before workers inspect availability.
            Store.Save(DownloadQueueStore.Capture(AccountUid, Queue, AudioQuality.Optimal, false, null));
            _workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(R4Get<CancellationTokenSource>(Window, "_lifetime").Token);
        }
        internal Task BeginUpdate()
        {
            _worker = new(TaskCreationOptions.RunContinuationsAsynchronously);
            R4Set(Window, "_downloadTask", _worker.Task); R4Set(Window, "_downloadCts", _workerCancellation);
            return R4Task(Window, "InstallStartupUpdateAsync");
        }
        internal void FinishWorker()
        {
            if (_worker is null || _worker.Task.IsCompleted) return;
            if (R4Get<int>(Window, "_downloadQueueAccountGeneration") != R4Get<int>(Window, "_accountGeneration")) Queue.Suspend();
            Queue.EndRun(true);
            R4Set(Window, "_downloadCts", null); R4Set(Window, "_downloadTask", null);
            _worker.TrySetResult();
        }
        internal void AssertPausedCheckpoint()
        {
            var snapshot = Store.Load(AccountUid);
            Assert(snapshot is not null, "update lost the paused download checkpoint");
            var restored = new DownloadQueueState(); restored.Restore(DownloadQueueStore.PrepareRestore(snapshot!, _offline));
            Assert(restored.Entries.Count == 2 && restored.CanResume && !restored.Running
                && restored.Entries.All(entry => entry.State == DownloadEntryState.Paused),
                "update checkpoint did not restore unfinished downloads paused and resumable");
        }
        public void Dispose()
        {
            FinishWorker();
            if (!R4Get<bool>(Window, "_closed")) Window.Close();
            _offline.Dispose(); _workerCancellation.Dispose();
        }
    }
    private sealed class Qf061A10UpdateBackend : IAppUpdateBackend
    {
        internal int Applies;
        internal bool FailApply;
        public string? PendingVersion => "9.1.0";
        public Task<string?> CheckAsync() => throw new InvalidOperationException("Fake pending update must not check the service");
        public Task DownloadAsync(Action<int> progress, CancellationToken token) => throw new InvalidOperationException("Fake pending update must not download");
        public void ApplyAndRestart()
        { Applies++; if (FailApply) throw new IOException("Fixture apply failure"); }
    }

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
            R4Set(Window, "_pendingConfirmedFavorites", new Dictionary<string, TrackRow>(StringComparer.Ordinal));
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
