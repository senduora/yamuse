using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckPlaylistSettings()
    {
        await Run("Playlist settings: delayed opening preserves live settings and persistence", () => PlaylistSettingsResponse(refresh: false));
        await Run("Playlist settings: delayed refresh preserves live settings and Back retains them", () => PlaylistSettingsResponse(refresh: true));
        await Run("Playlist settings: explicit navigation cancels a pending response without restoring the old page", PlaylistSettingsNavigateAway);
        await Run("Playlist settings: playlist title dialog validates initial and edited values", PlaylistTitleDialogValidation);
        await Run("Playlist settings: native Enter accepts a selected playlist and Escape cancels", PlaylistPickerAccessKeys);
    }

    private static Task PlaylistTitleDialogValidation()
    {
        var owner = new Window { ShowActivated = false, ShowInTaskbar = false };
        TextDialog? dialog = null;
        try
        {
            _ = new WindowInteropHelper(owner).EnsureHandle();
            dialog = new TextDialog(owner, "Название плейлиста", new string('я', 201));
            var panel = (StackPanel)dialog.Content;
            var text = panel.Children.OfType<TextBox>().Single();
            var save = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single(button => button.IsDefault);
            Assert(!save.IsEnabled, "an oversized initial title could be submitted");
            text.Text = "плей\tлист";
            Assert(!save.IsEnabled, "a title containing a control character could be submitted");
            text.Text = "   ";
            Assert(!save.IsEnabled, "a blank title could be submitted");
            text.Text = " " + new string('я', 200) + " ";
            Assert(save.IsEnabled && dialog.Value.Length == 200, "a valid title at the limit could not be submitted");
        }
        finally { dialog?.Close(); owner.Close(); }
        return Task.CompletedTask;
    }

    private static Task PlaylistPickerAccessKeys()
    {
        var owner = new Window { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            _ = new WindowInteropHelper(owner).EnsureHandle();
            var playlist = new CatalogItem("fixture-playlist", "playlist", "Проверочный плейлист", "");
            foreach (var confirm in new[] { true, false })
            {
                var dialog = new PlaylistDialog(owner, [playlist])
                {
                    ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = 0
                };
                var list = ((Grid)dialog.Content).Children.OfType<ListBox>().Single();
                var noSelectionStayedOpen = false;
                var closedByAccessKey = false;
                dialog.Loaded += (_, _) =>
                {
                    try
                    {
                        var source = PresentationSource.FromVisual(dialog)!;
                        AccessKeyManager.ProcessKey(source, "\r", false);
                        noSelectionStayedOpen = dialog.IsVisible;
                        if (!noSelectionStayedOpen) return;
                        list.SelectedIndex = 0;
                        AccessKeyManager.ProcessKey(source, confirm ? "\r" : "\u001B", false);
                        closedByAccessKey = !dialog.IsVisible;
                    }
                    finally { if (dialog.IsVisible) dialog.Close(); }
                };
                Assert(dialog.ShowDialog() == confirm && noSelectionStayedOpen && closedByAccessKey && ReferenceEquals(dialog.Selected, playlist),
                    "playlist picker did not honor the modal access key or submitted without selection");
            }
        }
        finally { owner.Close(); }
        return Task.CompletedTask;
    }

    private static async Task PlaylistSettingsResponse(bool refresh)
    {
        using var fixture = new PlaylistSettingsFixture(refresh ? "refresh" : "open");
        var window = fixture.Window;
        if (refresh) SaInvoke(window, "ShowPlaylist", new PlaylistDetails("12345", "17", "До обновления", "", 1, []));
        var pending = refresh ? Call(window, "RefreshCatalogViewAsync") : Call(window, "OpenCatalogAsync", fixture.Item, true);
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SaClick(Control<Button>(window, "SettingsButton"));
        Control<Slider>(window, "CrossfadeSetting").Value = 7.5;
        Control<ComboBox>(window, "SettingsSpeed").SelectedItem = 1.25;
        Control<ComboBox>(window, "SettingsQuality").SelectedIndex = 1;
        Control<ComboBox>(window, "SettingsDownloads").SelectedItem = 7;
        window.UpdateLayout();
        var baseline = File.ReadAllBytes(fixture.Preferences.FilePath);
        var version = SaField<PlaybackQueue>(window, "_playbackQueue").Version;
        Assert(Control<Grid>(window, "SettingsPanel").IsVisible, "Settings did not open during the pending request");
        fixture.Release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        window.UpdateLayout();
        var visible = Control<Grid>(window, "SettingsPanel").IsVisible;
        var draft = Control<Slider>(window, "CrossfadeSetting").Value;
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "response-facts.json"), JsonSerializer.Serialize(new
        {
            refresh, settingsVisible = visible, draftCrossfade = draft, appliedCrossfade = fixture.Player.CrossfadeSeconds,
            section = SaField<string>(window, "_section"), playlistTitle = Control<TextBlock>(window, "CollectionTitle").Text,
            requestCount = fixture.Requests
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert(visible && !Control<Grid>(window, "ContentGrid").IsVisible, "a delayed playlist response closed Settings");
        Assert(draft == 7.5 && (double)Control<ComboBox>(window, "SettingsSpeed").SelectedItem == 1.25
            && Control<ComboBox>(window, "SettingsQuality").SelectedIndex == 1 && (int)Control<ComboBox>(window, "SettingsDownloads").SelectedItem == 7,
            "a delayed response reset the Settings draft");
        Assert(fixture.Player.CrossfadeSeconds == 7.5 && fixture.Player.SpeedRatio == 1.25
            && baseline.SequenceEqual(File.ReadAllBytes(fixture.Preferences.FilePath)), "the response reset live settings or rewrote their persisted state");
        Assert(SaField<PlaylistDetails?>(window, "_activePlaylist") is { Kind: "17", Revision: 2, Title: "Плейлист после ответа" }
            && fixture.Requests == 1 && SaField<PlaybackQueue>(window, "_playbackQueue").Version == version,
            "the underlying playlist did not update once or changed playback");
        SaClick(Control<Button>(window, "SettingsBackButton"));
        window.UpdateLayout();
        var preferences = fixture.Preferences.Load();
        Assert(!Control<Grid>(window, "SettingsPanel").IsVisible && Control<Grid>(window, "ContentGrid").IsVisible
            && Control<TextBlock>(window, "CollectionTitle").Text == "Плейлист после ответа", "return did not reveal the loaded playlist");
        Assert(preferences.CrossfadeSeconds == 7.5 && preferences.PlaybackSpeed == 1.25
            && preferences.Quality == AudioQuality.Optimal && preferences.ConcurrentDownloads == 7
            && baseline.SequenceEqual(File.ReadAllBytes(fixture.Preferences.FilePath)), "Back lost retained live values or changed the persisted package");
        SaClick(Control<Button>(window, "SettingsButton"));
        Assert(Control<Slider>(window, "CrossfadeSetting").Value == 7.5, "reopen lost the live value");
        SaClick(Control<Button>(window, "SettingsBackButton"));
    }

    private static async Task PlaylistSettingsNavigateAway()
    {
        using var fixture = new PlaylistSettingsFixture("navigate-away");
        var window = fixture.Window;
        var pending = Call(window, "OpenCatalogAsync", fixture.Item, true);
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SaClick(Control<Button>(window, "SettingsButton"));
        Control<Slider>(window, "CrossfadeSetting").Value = 7.5;
        var expected = fixture.Preferences.Load() with { CrossfadeSeconds = 7.5 };
        SaClick(Control<Button>(window, "FavoritesNav"));
        Assert(!Control<Grid>(window, "SettingsPanel").IsVisible && fixture.CancellationObserved, "explicit navigation did not close Settings and cancel the request");
        fixture.Release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        window.UpdateLayout();
        Assert(SaField<string>(window, "_section") == "favorites" && Control<TextBlock>(window, "CollectionTitle").Text == "Мне нравится"
            && SaField<PlaylistDetails?>(window, "_activePlaylist") is null, "a canceled late reply restored the playlist page");
        Assert(fixture.Preferences.Load() == expected && fixture.Player.CrossfadeSeconds == 7.5, "navigation lost the live setting or changed unrelated preferences");
        SaClick(Control<Button>(window, "SettingsButton"));
        Assert(Control<Slider>(window, "CrossfadeSetting").Value == 7.5, "navigation rolled back the live setting");
        SaClick(Control<Button>(window, "SettingsBackButton"));
    }

    private sealed class PlaylistSettingsFixture : IDisposable
    {
        internal string DirectoryPath { get; }
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal PreferencesStore Preferences { get; }
        internal CatalogItem Item { get; } = new("17", "playlist", "Открываемый плейлист", "", "12345", "17");
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Requests { get; private set; }
        internal bool CancellationObserved { get; private set; }

        internal PlaylistSettingsFixture(string suffix)
        {
            DirectoryPath = Path.Combine(_directory, "playlist-settings-" + suffix);
            Directory.CreateDirectory(DirectoryPath);
            var api = new FakeHandler(async (request, token) =>
            {
                Assert(request.Method == HttpMethod.Get && request.RequestUri is { Host: "api.music.yandex.net", AbsolutePath: "/users/12345/playlists/17" }, "unexpected fixture request");
                Requests++;
                using var registration = token.Register(() => CancellationObserved = true);
                Entered.TrySetResult();
                // Deliver even after cancellation to check that the UI rejects a late reply.
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return Json("""{"result":{"kind":"17","owner":{"uid":"12345"},"title":"Плейлист после ответа","revision":2,"trackCount":0,"tracks":[]}}""");
            });
            Player = new NativePlayer(new AudioBuffer(Path.Combine(DirectoryPath, "buffer"),
                new FakeHandler((_, _) => throw new InvalidOperationException("browse must not request audio"))));
            var offline = new OfflineLibrary(Path.Combine(DirectoryPath, "Music"), new AudioBuffer(Path.Combine(DirectoryPath, "download-buffer"),
                new FakeHandler((_, _) => throw new InvalidOperationException("browse must not download music"))));
            Window = new MainWindow(api: new MusicApi(api), store: new SessionStore(Path.Combine(DirectoryPath, "session")),
                restore: false, diagnosticsDirectory: DirectoryPath, offline: offline, player: Player)
            { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = 0 };
            Window.Show();
            typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, FakeSession);
            Control<Slider>(Window, "VolumeSlider").Value = 0;
            Preferences = new PreferencesStore(Path.Combine(DirectoryPath, "settings"));
            Preferences.Save(new PlayerPreferences { Volume = 0 });
            typeof(MainWindow).GetField("_preferenceStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, Preferences);
        }

        public void Dispose() { Release.TrySetResult(); Window.Close(); }
    }
}
