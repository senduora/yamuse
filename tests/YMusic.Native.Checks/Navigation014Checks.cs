using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckNavigation014()
    {
        await Run("Navigation014: mouse Back and Forward restore playlist filter, selection, scroll and album header without changing playback", async () =>
        {
            using var f = new SaFixture("mouse-navigation"); await f.Activate(); var w = f.Window;
            var rows = Enumerable.Range(0, 60).Select(i => new TrackRow { Id = "nav-" + i, RequestId = "nav-" + i + ":same-title-b", Title = "Трек " + i,
                AlbumId = "same-title-b", Album = "Альбом", Available = true }).ToArray();
            SaInvoke(w, "ShowPlaylist", new PlaylistDetails("owner", "nav-list", "Исходный плейлист", "", 1, rows));
            Control<TextBox>(w, "FilterBox").Text = "Трек"; await Task.Delay(300);
            var list = Control<ListView>(w, "TracksList"); list.SelectedItem = rows[12]; list.ScrollIntoView(rows[12]); w.UpdateLayout();
            var scroll = SaVisuals<ScrollViewer>(list).First(); var offset = scroll.VerticalOffset;
            var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); queue.SetItems([rows[2], rows[3]], rows[2]); var version = queue.Version;
            await Call(w, "OpenCatalogAsync", new CatalogItem("same-title-b", "album", "Контрольный альбом", ""), true);
            NavMouse(w, MouseButton.XButton1); await Task.Delay(80); w.UpdateLayout();
            Assert(SaField<string>(w, "_section") == "playlist-detail" && ReferenceEquals(list.SelectedItem, rows[12])
                && Control<TextBox>(w, "FilterBox").Text == "Трек" && Math.Abs(scroll.VerticalOffset - offset) < 1, "mouse Back lost the playlist state");
            NavMouse(w, MouseButton.XButton2); await Task.Delay(80); w.UpdateLayout();
            Assert(SaField<string>(w, "_section") == "album-detail" && Control<ContentControl>(w, "CollectionHero").IsVisible
                && SaField<AlbumDetails?>(w, "_activeAlbumDetails")?.Id == "same-title-b", "mouse Forward lost the album metadata/header");
            Assert(queue.Version == version && ReferenceEquals(queue.Current, rows[2]) && !f.Player.HasSource, "history navigation changed the playback queue or opened audio");
            NavMouse(w, MouseButton.XButton1); SaClick(Control<Button>(w, "FavoritesNav")); NavMouse(w, MouseButton.XButton2);
            Assert(SaField<string>(w, "_section") == "favorites", "a new navigation did not invalidate Forward history");
            w.SignOut(); var section = SaField<string>(w, "_section"); NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton2);
            Assert(SaField<string>(w, "_section") == section && NavHistoryCount(w, "_browseHistory") == 0 && NavHistoryCount(w, "_browseForward") == 0,
                "history exposed the previous account after signout");
        });
        await Run("Navigation014: mouse handling leaves ordinary buttons alone and Back retains live Settings changes", async () =>
        {
            using var f = new SaFixture("mouse-overlay"); await f.Activate(); var w = f.Window;
            var left = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            w.RaiseEvent(left); Assert(!left.Handled, "history swallowed an ordinary mouse button");
            var crossfade = f.Player.CrossfadeSeconds; SaInvoke(w, "Settings_Click", null!, null!); Control<Slider>(w, "CrossfadeSetting").Value = 9.5;
            NavMouse(w, MouseButton.XButton1);
            Assert(!SaField<bool>(w, "_settingsOpen") && f.Player.CrossfadeSeconds == 9.5, "mouse Back rolled back live Settings");
            for (var i = 0; i < 30; i++) SaInvoke(w, "SetSection", i % 2 == 0 ? "favorites" : "offline", "Раздел " + i);
            Assert(NavHistoryCount(w, "_browseHistory") == 24, "navigation history is unbounded or clears all entries at the limit");
        });
        await Run("Navigation014: list title and album links plus player links open the exact album without playing audio", async () =>
        {
            foreach (var origin in new[] { "list-title", "list-album", "player-title", "player-cover" })
            {
                using var f = new SaFixture("album-affordance-" + origin); await f.Activate(); var w = f.Window;
                var track = new TrackRow { Id = "nav-track", RequestId = "nav-track:same-title-b", Title = "Точное название", Album = "Точный альбом", AlbumId = "same-title-b", Available = true };
                SaInvoke(w, "ShowPlaylist", new PlaylistDetails("owner", "source", "Плейлист", "", 1, [track]));
                UlSet(w, "_current", track); SaInvoke(w, "UpdateControls"); w.UpdateLayout();
                var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); queue.SetItems([track], track); var version = queue.Version;
                Button button;
                if (origin.StartsWith("player", StringComparison.Ordinal)) button = Control<Button>(w, origin.EndsWith("title", StringComparison.Ordinal) ? "PlayerTitleButton" : "PlayerCoverButton");
                else
                {
                    var list = Control<ListView>(w, "TracksList"); list.ScrollIntoView(track); w.UpdateLayout();
                    var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(track)!;
                    button = SaVisuals<Button>(row).Single(b => ReferenceEquals(b.Tag, track) && (origin.EndsWith("title", StringComparison.Ordinal)
                        ? SaVisuals<TextBlock>(b).Any(t => t.Text == track.Title) : SaVisuals<TextBlock>(b).Any(t => t.Text == track.Album)));
                }
                SaClick(button); await Until(() => SaField<string>(w, "_section") == "album-detail" && SaField<CancellationTokenSource?>(w, "_featureCts") is null);
                Assert(f.Handler.Seen.Any(c => c.Uri.AbsolutePath == "/albums/same-title-b/with-tracks") && !f.Handler.Seen.Any(c => c.Uri.AbsolutePath == "/search"), "album link guessed a title instead of using the exact ID");
                Assert(queue.Version == version && ReferenceEquals(queue.Current, track) && !f.Player.HasSource, "an album affordance started audio or changed the queue");
            }
        });
        await Run("Navigation014: returning to favorites preserves a confirmed unlike while away", async () =>
        {
            using var f = new SaFixture("favorites-live-history"); await f.Activate(); var w = f.Window;
            var removed = new TrackRow { Id = "removed", RequestId = "removed:1", Title = "Убранный", Available = true };
            var retained = new TrackRow { Id = "retained", RequestId = "retained:1", Title = "Оставшийся", Available = true };
            var includeRemoved = true;
            f.RespondOverride = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath == "/users/12345/likes/tracks"
                ? Json(includeRemoved
                    ? """{"result":{"library":{"tracks":[{"id":"removed","albumId":"1"},{"id":"retained","albumId":"1"}]}}}"""
                    : """{"result":{"library":{"tracks":[{"id":"retained","albumId":"1"}]}}}""")
                : null);
            SaField<List<TrackRow>>(w, "_favoritesCache").AddRange([removed, retained]);
            SaField<HashSet<string>>(w, "_favoriteTrackIds").UnionWith([removed.Id, retained.Id]);
            SaClick(Control<Button>(w, "FavoritesNav"));
            var list = Control<ListView>(w, "TracksList");
            list.SelectedItem = removed;
            await Call(w, "OpenCatalogAsync", new CatalogItem("1", "album", "Альбом", ""), true);
            includeRemoved = false;
            SaInvoke(w, "ApplyConfirmedTrackLike", removed, false, false);
            NavMouse(w, MouseButton.XButton1);
            Assert(SaField<string>(w, "_section") == "favorites" && list.Items.Cast<TrackRow>().Select(row => row.RequestId).SequenceEqual([retained.RequestId])
                && list.SelectedItem is null && !f.Player.HasSource, "Back resurrected a removed favorite or selected the row that took its place");
        });
        await Run("Navigation014: returning to artist tracks restores continuation and refresh targets that artist", async () =>
        {
            using var f = new SaFixture("artist-tracks-history"); await f.Activate(); var w = f.Window;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.RespondOverride = async (request, _) =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath != "/artists/artist-42/tracks") return null;
                var nextPage = uri.Query.Contains("page=1", StringComparison.Ordinal);
                if (nextPage) { entered.TrySetResult(); await release.Task; }
                var tracks = Enumerable.Range(nextPage ? 100 : 0, nextPage ? 1 : 100).Select(i => SaTrack("history-" + i));
                return Json(JsonSerializer.Serialize(new { result = new { tracks } }));
            };
            await Call(w, "OpenArtistAsync", "artist-42", "Первый исполнитель", true);
            var initialHistoryCount = NavHistoryCount(w, "_browseHistory");
            await Call(w, "LoadArtistTracksAsync", true);
            Assert(NavHistoryCount(w, "_browseHistory") == initialHistoryCount + 1, "opening all tracks pushed history twice");
            var list = Control<ListView>(w, "TracksList"); var more = Control<Button>(w, "MoreArtistButton");
            await Call(w, "OpenArtistAsync", "artist-99", "Другой исполнитель", true);
            NavMouse(w, MouseButton.XButton1);
            Assert(list.Items.Count == 100 && more.IsVisible, "Back lost the next artist tracks page");
            try
            {
                SaClick(more);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                SaClick(more);
            }
            finally { release.TrySetResult(); }
            await Until(() => SaField<CancellationTokenSource?>(w, "_featureCts") is null);
            Assert(list.Items.Count == 101 && list.Items.Cast<TrackRow>().Last().Id == "history-100" && !more.IsVisible,
                "More used the wrong artist, skipped a page or left an exhausted continuation visible");
            await Call(w, "RefreshCatalogViewAsync");
            Assert(list.Items.Count == 100 && more.IsVisible && f.Handler.Seen.Count(call => call.Uri.AbsolutePath == "/artists/artist-42/tracks") == 3
                && f.Handler.Seen.All(call => call.Uri.AbsolutePath != "/artists/artist-99/tracks") && !f.Player.HasSource,
                "Refresh reused the other artist or changed playback");
        });
        await Run("Navigation014: unavailable catalog track and details refresh preserve manual playback", NavCatalogTrackIntent);
        await Run("Navigation014: offline history and refresh preserve exact owner and wait for recovery", NavOfflinePlaylistRecovery);
        await Run("Navigation014: Home and artist transport cannot start hidden playlist rows", NavHiddenPlaybackFallback);
        await Run("Navigation014: signout from a private detail cannot restore it with Back or Forward", async () =>
        {
            using var f = new SaFixture("logout-history-detail"); await f.Activate(); var w = f.Window;
            await Call(w, "OpenArtistAsync", "artist-42", "Личная страница", true);
            Assert(NavHistoryCount(w, "_browseHistory") > 0, "fixture did not open the detail with existing history");
            w.SignOut();
            Assert(NavHistoryCount(w, "_browseHistory") == 0 && NavHistoryCount(w, "_browseForward") == 0,
                "logout captured the very view it had just removed from history");
            NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton2);
            Assert(SaField<string>(w, "_section") == "favorites", "history restored a private page after logout");
        });
        await Run("Navigation014: refresh and Back do not discard a confirmed playlist removal", async () =>
        {
            foreach (var timing in new[] { "refresh", "back-before", "back-after" })
                await NavPlaylistRemoval(timing);
        });
        await Run("Navigation014: confirmed insertion updates the open playlist and deletion removes its history", async () =>
        {
            using var f = new SaFixture("playlist-insertion-deletion"); await f.Activate(); var w = f.Window;
            const string path = "/users/12345/playlists/17";
            var revision = 1;
            var deleted = false;
            var deleteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deleteRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            object Playlist() => new
            {
                kind = "17", owner = new { uid = "12345" }, title = "Плейлист", visibility = "private", revision,
                trackCount = revision == 1 ? 0 : 1,
                tracks = revision == 1 ? Array.Empty<object>() : new object[] { new { id = "added", albumId = "1" } }
            };
            f.RespondOverride = async (request, _) => request.RequestUri!.AbsolutePath switch
            {
                "/users/12345/playlists/list" => Json(JsonSerializer.Serialize(new { result = deleted ? Array.Empty<object>() : new[] { Playlist() } })),
                path => Json(JsonSerializer.Serialize(new { result = Playlist() })),
                path + "/change" => AcceptChange(),
                path + "/delete" => await AcceptDeleteAsync(),
                _ => null
            };
            HttpResponseMessage AcceptChange() { revision++; return Json(JsonSerializer.Serialize(new { result = Playlist() })); }
            async Task<HttpResponseMessage> AcceptDeleteAsync()
            {
                deleteEntered.TrySetResult();
                await deleteRelease.Task.WaitAsync(TimeSpan.FromSeconds(5));
                deleted = true;
                return Json("""{"result":"ok"}""");
            }

            await Call(w, "LoadCollectionAsync", "playlists");
            var item = new CatalogItem("17", "playlist", "Плейлист", "", "12345", "17");
            await Call(w, "OpenCatalogAsync", item, true);
            await Call(w, "InsertTrackIntoPlaylistAsync", item, new LikedId("added", "1"));
            var list = Control<ListView>(w, "TracksList");
            var current = SaField<PlaylistDetails>(w, "_activePlaylist");
            Assert(current.Revision == 2 && list.Items.Cast<TrackRow>().Single().RequestId == "added:1",
                "successful insertion left a stale active playlist or stale rows");

            var deleting = Call(w, "DeletePlaylistFromCatalogAsync", current);
            await deleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SaClick(Control<Button>(w, "SettingsButton"));
            Control<Slider>(w, "CrossfadeSetting").Value = 7.5;
            deleteRelease.TrySetResult();
            await deleting;
            await Until(() => SaField<CancellationTokenSource?>(w, "_featureCts") is null);
            Assert(deleted && SaField<string>(w, "_section") == "playlists"
                && Control<Grid>(w, "SettingsPanel").IsVisible && Control<Slider>(w, "CrossfadeSetting").Value == 7.5,
                "confirmed deletion lost Settings or left the deleted playlist underneath");
            SaClick(Control<Button>(w, "SettingsBackButton"));
            NavMouse(w, MouseButton.XButton1);
            Assert(SaField<string>(w, "_section") == "playlists" && SaField<PlaylistDetails?>(w, "_activePlaylist") is null
                && Control<ListBox>(w, "CatalogList").Items.Count == 0 && Control<Button>(w, "CreatePlaylistButton").IsVisible
                && f.Handler.Seen.Count(call => call.Uri.AbsolutePath == path + "/delete") == 1,
                "Back revived a deleted playlist or hid playlist creation");
        });
        await Run("Navigation014: delayed catalog and detail reads preserve confirmed playlist changes", NavPlaylistLateReads);
        await Run("Navigation014: a newer service revision updates earlier playlist history", NavPlaylistReadHistory);
        await Run("Navigation014: playlist refresh and Back preserve duplicate occurrence selection", NavPlaylistOccurrenceSelection);
        await Run("Navigation014: evicted playlist tracks leave only revision and deletion metadata", NavPlaylistHistoryEviction);
        await Run("Navigation014: confirmed creation adds a card and Back returns to it", NavPlaylistCreation);
        await Run("Navigation014: playlist selection reserves the write and uses the session after the dialog", NavPlaylistAddSession);
    }

    private static async Task NavPlaylistLateReads()
    {
        using var f = new SaFixture("playlist-late-reads"); await f.Activate(); var w = f.Window;
        const string path = "/users/12345/playlists/17";
        const string oldTitle = "Было";
        const string newTitle = "Стало";
        var title = oldTitle;
        var revision = 1;
        var deleted = false;
        int? staleDetailRevision = null;
        var holdNextCollection = false;
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var searchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var searchRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        object Playlist(string name, int rev) => new { kind = "17", owner = new { uid = "12345" }, title = name, revision = rev,
            visibility = "private", trackCount = 0, tracks = Array.Empty<object>() };
        f.RespondOverride = async (request, _) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/search":
                    searchEntered.TrySetResult();
                    await searchRelease.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return Json(JsonSerializer.Serialize(new { result = new { playlists = new { total = 1, perPage = 1,
                        results = new[] { Playlist(oldTitle, 1) } } } }));
                case "/users/12345/playlists/list":
                    if (holdNextCollection)
                    {
                        holdNextCollection = false;
                        searchEntered.TrySetResult();
                        await searchRelease.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        return Json(JsonSerializer.Serialize(new { result = new[] { Playlist(oldTitle, 1) } }));
                    }
                    return Json(JsonSerializer.Serialize(new { result = deleted ? Array.Empty<object>() : new[] { Playlist(title, revision) } }));
                case path:
                    return Json(JsonSerializer.Serialize(new { result = staleDetailRevision is { } stale ? Playlist(oldTitle, stale) : Playlist(title, revision) }));
                case path + "/name":
                case path + "/delete":
                    writeEntered.TrySetResult();
                    await writeRelease.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (request.RequestUri.AbsolutePath.EndsWith("/delete", StringComparison.Ordinal))
                    {
                        deleted = true;
                        return Json("""{"result":"ok"}""");
                    }
                    revision = 2;
                    title = newTitle;
                    return Json(JsonSerializer.Serialize(new { result = Playlist(title, revision) }));
                default: return null;
            }
        };
        var item = new CatalogItem("17", "playlist", oldTitle, "", "12345", "17");
        var list = Control<ListBox>(w, "CatalogList");
        await Call(w, "OpenCatalogAsync", item, true);
        foreach (var deleting in new[] { false, true })
        {
            var shown = SaField<PlaylistDetails>(w, "_activePlaylist");
            var writing = deleting ? Call(w, "DeletePlaylistFromCatalogAsync", shown) : Call(w, "RenamePlaylistFromCatalogAsync", shown, newTitle);
            await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task reading;
            if (deleting)
            {
                holdNextCollection = true;
                reading = Call(w, "LoadCollectionAsync", "playlists");
            }
            else
            {
                SaInvoke(w, "SearchNav_Click", null!, null!);
                Control<TextBox>(w, "CatalogQuery").Text = oldTitle;
                reading = Call(w, "SearchCatalogViewAsync", false);
            }
            await searchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            writeRelease.TrySetResult();
            await writing;
            searchRelease.TrySetResult();
            await reading;
            Assert(SaField<string>(w, "_section") == (deleting ? "playlists" : "search"), "playlist confirmation replaced the current catalog");
            if (deleting) { Assert(list.Items.Count == 0, "delayed collection resurrected a deleted playlist"); break; }

            Assert(list.Items.Cast<CatalogItem>().Single().Title == newTitle, "delayed search resurrected an old playlist title");
            staleDetailRevision = 1;
            await Call(w, "OpenCatalogAsync", item, true);
            Assert(SaField<PlaylistDetails>(w, "_activePlaylist") is { Revision: 2, Title: newTitle }, "reopening allowed a stale detail to replace an accepted revision");
            staleDetailRevision = 2;
            await Call(w, "RefreshCatalogViewAsync");
            Assert(SaField<PlaylistDetails>(w, "_activePlaylist").Title == newTitle, "equal-revision detail restored the old title");
            staleDetailRevision = null;
            writeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            writeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            searchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            searchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private static async Task NavPlaylistReadHistory()
    {
        using var f = new SaFixture("playlist-newer-read-history"); await f.Activate(); var w = f.Window;
        var revision = 1;
        object Playlist() => new { kind = "17", owner = new { uid = "12345" }, title = "Версия " + revision,
            revision, trackCount = 0, tracks = Array.Empty<object>() };
        f.RespondOverride = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath switch
        {
            "/users/12345/playlists/list" => Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } })),
            "/users/12345/playlists/17" => Json(JsonSerializer.Serialize(new { result = Playlist() })),
            _ => null
        });
        var item = new CatalogItem("17", "playlist", "Версия 1", "", "12345", "17");
        await Call(w, "OpenCatalogAsync", item, true);
        await Call(w, "LoadCollectionAsync", "playlists");
        revision = 2;
        await Call(w, "OpenCatalogAsync", item, true);
        NavMouse(w, MouseButton.XButton1);
        NavMouse(w, MouseButton.XButton1);
        Assert(SaField<PlaylistDetails>(w, "_activePlaylist") is { Revision: 2, Title: "Версия 2" },
            "Back restored an earlier detail after the server supplied a newer revision");
    }

    private static async Task NavPlaylistCreation()
    {
        using var f = new SaFixture("playlist-creation-back"); await f.Activate(); var w = f.Window;
        const string playlist = """{"kind":"17","owner":{"uid":"12345"},"title":"Новый","revision":1,"visibility":"private","trackCount":0,"tracks":[]}""";
        f.RespondOverride = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath switch
        {
            "/users/12345/playlists/list" => Json("""{"result":[]}"""),
            "/users/12345/playlists/create" or "/users/12345/playlists/17" => Json("{\"result\":" + playlist + "}"),
            _ => null
        });
        await Call(w, "LoadCollectionAsync", "playlists");
        await Call(w, "CreatePlaylistFromCatalogAsync", "Новый", "private");
        Assert(SaField<PlaylistDetails>(w, "_activePlaylist") is { Kind: "17" }, "confirmed creation did not open its detail");
        NavMouse(w, MouseButton.XButton1);
        var list = Control<ListBox>(w, "CatalogList");
        Assert(SaField<string>(w, "_section") == "playlists" && list.Items.Cast<CatalogItem>().Single().Id == "17", "Back skipped the new playlist card");
        await Call(w, "LoadCollectionAsync", "playlists");
        Assert(list.Items.Cast<CatalogItem>().Single().Id == "17"
            && f.Handler.Seen.Count(call => call.Uri.AbsolutePath == "/users/12345/playlists/create") == 1, "stale list removed the confirmed new card");
    }

    private static async Task NavPlaylistOccurrenceSelection()
    {
        using var f = new SaFixture("playlist-occurrence-selection"); await f.Activate(); var w = f.Window;
        const string path = "/users/12345/playlists/17";
        var revision = 1;
        string[] order = ["repeat", "between", "repeat", "last"];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removedIndices = new List<int>();
        object Playlist() => new { kind = "17", owner = new { uid = "12345" }, title = "С повторами", visibility = "private", revision,
            trackCount = order.Length, tracks = order.Select(id => new { id, albumId = "1" }).ToArray() };
        f.RespondOverride = async (request, token) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/users/12345/playlists/list": return Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } }));
                case path: return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                case path + "/change":
                    var body = PlaylistWritesForm(await request.Content!.ReadAsStringAsync(token));
                    using (var diff = JsonDocument.Parse(body["diff"]))
                    {
                        var from = diff.RootElement[0].GetProperty("from").GetInt32();
                        entered.TrySetResult();
                        await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        removedIndices.Add(from);
                        order = order.Where((_, i) => i != from).ToArray();
                        revision++;
                    }
                    return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                default: return null;
            }
        };
        var item = new CatalogItem("17", "playlist", "С повторами", "", "12345", "17");
        var album = new CatalogItem("1", "album", "Альбом", "");
        var list = Control<ListView>(w, "TracksList");
        var remove = Control<Button>(w, "RemovePlaylistTrackButton");
        TrackRow[] Rows() => SaField<PlaylistDetails>(w, "_activePlaylist").Tracks.ToArray();

        await Call(w, "OpenCatalogAsync", item, true);
        var firstSelection = Rows()[2];
        list.SelectedItem = firstSelection;
        await Call(w, "RefreshCatalogViewAsync");
        Assert(list.SelectedItem is TrackRow { Number: 3, RequestId: "repeat:1" } refreshed && !ReferenceEquals(firstSelection, refreshed),
            "refresh lost the second duplicate or selected the first");

        foreach (var deleteSelection in new[] { false, true })
        {
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            list.SelectedItem = Rows()[1];
            SaClick(remove);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!deleteSelection) list.SelectedItem = Rows()[2];
            await Call(w, "OpenCatalogAsync", album, true);
            release.TrySetResult();
            await Until(() => !SaField<bool>(w, "_catalogWriting"));
            NavMouse(w, MouseButton.XButton1);
            if (deleteSelection)
                Assert(list.SelectedItem is null && Rows().Count(row => row.RequestId == "repeat:1") == 1,
                    "Back substituted the surviving duplicate for the deleted selected occurrence");
            else
                Assert(ReferenceEquals(list.SelectedItem, Rows()[1]) && Rows()[1].RequestId == "repeat:1",
                    "Back lost the selected second duplicate after an unrelated delayed removal");
        }
        Assert(removedIndices.SequenceEqual([1, 1]) && f.Handler.Seen.Count(call => call.Uri.AbsolutePath == path + "/change") == 2,
            "removing a selected occurrence did not send its exact source index");
    }

    private static async Task NavPlaylistHistoryEviction()
    {
        using var f = new SaFixture("playlist-history-eviction"); await f.Activate(); var w = f.Window;
        const string prefix = "/users/12345/playlists/";
        var stale = false;
        var deleted = false;
        object Playlist(string kind, int revision) => new { kind, owner = new { uid = "12345" }, title = $"Плейлист {kind}", revision,
            trackCount = 1, tracks = new[] { new { id = $"track-{kind}-{revision}", albumId = "1" } } };
        f.RespondOverride = (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == prefix + "list")
                return Task.FromResult<HttpResponseMessage?>(Json(JsonSerializer.Serialize(new { result = !deleted || stale
                    ? new[] { Playlist("17", 2), Playlist("18", 1) } : new[] { Playlist("17", 2) } })));
            if (path == prefix + "18/delete")
            {
                deleted = true;
                return Task.FromResult<HttpResponseMessage?>(Json("""{"result":"ok"}"""));
            }
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) return Task.FromResult<HttpResponseMessage?>(null);
            var kind = path[prefix.Length..];
            return Task.FromResult<HttpResponseMessage?>(Json(JsonSerializer.Serialize(new { result = Playlist(kind, kind == "17" && !stale ? 2 : 1) })));
        };
        Task Open(string kind) => Call(w, "OpenCatalogAsync", new CatalogItem(kind, "playlist", $"Плейлист {kind}", "", "12345", kind), true);
        await Open("17");
        await Open("18");
        await Call(w, "DeletePlaylistFromCatalogAsync", SaField<PlaylistDetails>(w, "_activePlaylist"));
        await Until(() => SaField<CancellationTokenSource?>(w, "_featureCts") is null);
        for (var i = 0; i < 30; i++) await Open($"other-{i}");

        var cache = SaField<System.Collections.IDictionary>(w, "_playlistCatalogState");
        Assert(cache.Count >= 32 && NavHistoryCount(w, "_browseHistory") == 24 && cache.Values.Cast<object>().All(state =>
            state.GetType().GetProperty("Header")?.GetValue(state) is PlaylistDetails { Tracks.Count: 0 }),
            "the account-wide playlist metadata retained track rows beyond the bounded history");
        stale = true;
        await Open("17");
        Assert(SaField<PlaylistDetails?>(w, "_activePlaylist") is null && Control<ListView>(w, "TracksList").Items.Count == 0
            && Control<TextBlock>(w, "StatusText").Text.Contains("более старую версию", StringComparison.Ordinal),
            "eviction allowed a stale playlist revision to become active");
        await Call(w, "LoadCollectionAsync", "playlists");
        Assert(Control<ListBox>(w, "CatalogList").Items.Cast<CatalogItem>().Select(item => item.Id).SequenceEqual(["17"]),
            "unrelated reads displaced deletion metadata and revived an old card");
        stale = false;
        await Open("17");
        Assert(SaField<PlaylistDetails>(w, "_activePlaylist") is { Revision: 2 }
            && Control<ListView>(w, "TracksList").Items.Cast<TrackRow>().Single().Id == "track-17-2",
            "a fresh read failed to reopen an evicted playlist");
    }

    private static async Task NavPlaylistAddSession()
    {
        using var f = new SaFixture("playlist-chooser-session"); await f.Activate(); var w = f.Window;
        const string path = "/users/12345/playlists/17";
        var newerSession = FakeSession with { AccessToken = "fixture-new-access" };
        var firstDetail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revision = 1;
        var detailReads = 0;
        object Playlist() => new { kind = "17", owner = new { uid = "12345" }, title = "Плейлист", revision, visibility = "private",
            trackCount = revision - 1, tracks = revision == 1 ? Array.Empty<object>() : new object[] { new { id = "track", albumId = "1" } } };
        f.RespondOverride = async (request, _) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/users/12345/playlists/list":
                    if (detailReads == 0)
                    {
                        firstList.TrySetResult();
                        await listRelease.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    return Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } }));
                case path:
                    if (++detailReads == 1)
                    {
                        firstDetail.TrySetResult();
                        await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                case path + "/change":
                    revision++;
                    return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                default: return null;
            }
        };
        var chooser = new DispatcherTimer(DispatcherPriority.Background, w.Dispatcher) { Interval = TimeSpan.FromMilliseconds(15) };
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chooser.Tick += (_, _) =>
        {
            if (w.OwnedWindows.OfType<PlaylistDialog>().FirstOrDefault(dialog => dialog.IsVisible) is not { } dialog) return;
            chooser.Stop();
            try
            {
                var list = ((Grid)dialog.Content).Children.OfType<ListBox>().Single();
                var source = PresentationSource.FromVisual(dialog) ?? throw new InvalidOperationException("playlist chooser has no presentation source");
                UlSet(w, "_session", newerSession);
                list.SelectedIndex = 0;
                AccessKeyManager.ProcessKey(source, "\r", false);
                Assert(!dialog.IsVisible && dialog.Selected?.Id == "17", "native Enter did not confirm the chosen playlist");
                confirmed.TrySetResult();
            }
            catch (Exception error) { confirmed.TrySetException(error); }
            finally { if (dialog.IsVisible) dialog.Close(); }
        };
        try
        {
            chooser.Start();
            var pending = Call(w, "AddTrackToPlaylistAsync", new TrackRow { Id = "track", RequestId = "track:1" });
            await firstList.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var competingPicker = Call(w, "AddTrackToPlaylistAsync", new TrackRow { Id = "second", RequestId = "second:1" });
            await competingPicker.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(f.Handler.Seen.Count(call => call.Uri.AbsolutePath == "/users/12345/playlists/list") == 1
                && Control<TextBlock>(w, "StatusText").Text.Contains("Выбор плейлиста уже выполняется", StringComparison.Ordinal),
                "a second command could open another chooser during the first playlist lookup");
            listRelease.TrySetResult();
            await Task.WhenAny(confirmed.Task, pending).WaitAsync(TimeSpan.FromSeconds(5));
            Assert(confirmed.Task.IsCompleted, "adding the track ended before the playlist chooser was confirmed");
            await confirmed.Task;
            await Task.WhenAny(firstDetail.Task, pending).WaitAsync(TimeSpan.FromSeconds(5));
            Assert(firstDetail.Task.IsCompleted, "adding the track ended before reading the chosen playlist");
            var selected = new CatalogItem("17", "playlist", "Плейлист", "", "12345", "17");
            Assert(SaField<bool>(w, "_catalogWriting"), "playlist detail read was outside the reserved write slot");
            await Call(w, "InsertTrackIntoPlaylistAsync", selected, new LikedId("second", "1"));
            Assert(Control<TextBlock>(w, "StatusText").Text.Contains("другого изменения", StringComparison.Ordinal), "busy write was silently discarded");
            release.TrySetResult();
            await pending;
            Assert(f.Handler.Seen.First(call => call.Uri.AbsolutePath == "/users/12345/playlists/list").Authorization == "OAuth " + FakeSession.AccessToken
                && f.Handler.Seen.Where(call => call.Uri.AbsolutePath == path).All(call => call.Authorization == "OAuth " + newerSession.AccessToken)
                && f.Handler.Seen.Count(call => call.Uri.AbsolutePath == path + "/change" && call.Authorization == "OAuth " + newerSession.AccessToken) == 1,
                "accepted command used the pre-dialog session or was replayed");
        }
        finally { chooser.Stop(); listRelease.TrySetResult(); release.TrySetResult(); }
    }

    private static async Task NavPlaylistRemoval(string timing)
    {
        using var f = new SaFixture("playlist-removal-" + timing); await f.Activate(); var w = f.Window;
        const string path = "/users/12345/playlists/17";
        var revision = 1;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        object Playlist()
        {
            var current = Volatile.Read(ref revision);
            return new { kind = "17", owner = new { uid = "12345" }, title = "Плейлист", visibility = "private", revision = current,
                trackCount = current == 1 ? 1 : 0,
                tracks = current == 1 ? new object[] { new { id = "remove-me", albumId = "1" } } : Array.Empty<object>() };
        }
        f.RespondOverride = async (request, _) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/users/12345/playlists/list":
                    return Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } }));
                case path:
                    return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                case path + "/change":
                    entered.TrySetResult();
                    await release.Task;
                    Volatile.Write(ref revision, 2);
                    return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                default:
                    return null;
            }
        };
        try
        {
            await Call(w, "OpenCatalogAsync", new CatalogItem("17", "playlist", "Плейлист", "", "12345", "17"), true);
            var list = Control<ListView>(w, "TracksList");
            list.SelectedIndex = 0;
            SaClick(Control<Button>(w, "RemovePlaylistTrackButton"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (timing == "refresh") await Call(w, "RefreshCatalogViewAsync");
            else
            {
                await Call(w, "OpenCatalogAsync", new CatalogItem("1", "album", "Альбом", ""), true);
                if (timing == "back-before") NavMouse(w, MouseButton.XButton1);
            }
            if (timing != "back-after")
                Assert(list.Items.Count == 1 && SaField<PlaylistDetails>(w, "_activePlaylist").Revision == 1, "fixture did not show the old revision");
            release.TrySetResult();
            await Until(() => !SaField<bool>(w, "_catalogWriting"));
            if (timing == "back-after")
            {
                Assert(SaField<string>(w, "_section") == "album-detail", "a write replaced the current album");
                NavMouse(w, MouseButton.XButton1);
            }
            Assert(list.Items.Count == 0 && list.SelectedItem is null && SaField<PlaylistDetails>(w, "_activePlaylist").Revision == 2
                && f.Handler.Seen.Count(call => call.Uri.AbsolutePath == path + "/change") == 1 && !f.Player.HasSource,
                "the confirmed removal was discarded or replayed after refresh or Back");
            Volatile.Write(ref revision, 1);
            await Call(w, "RefreshCatalogViewAsync");
            Assert(list.Items.Count == 0 && SaField<PlaylistDetails>(w, "_activePlaylist").Revision == 2,
                "a stale read rolled back the confirmed revision");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task NavCatalogTrackIntent()
    {
        using var f = new SaFixture("catalog-track-intent"); await f.Activate(); var w = f.Window;
        var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
        var prior = new TrackRow { Id = "prior", RequestId = "prior", Available = true };
        queue.SetItems([prior], prior);
        var version = queue.Version;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.RespondOverride = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath != "/tracks") return null;
            var body = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(token));
            if (body.Contains("not-available", StringComparison.Ordinal)) return Json("{\"result\":[]}");
            entered.TrySetResult();
            await release.Task;
            return Json(JsonSerializer.Serialize(new { result = new[] { SaTrack("pending-detail") } }));
        };
        try
        {
            await Call(w, "OpenCatalogAsync", new CatalogItem("not-available", "track", "Недоступный", ""), true);
            Assert(queue.Version == version && ReferenceEquals(queue.Current, prior) && !f.Player.HasSource,
                "unavailable metadata replaced the existing queue");
            var opening = Call(w, "OpenCatalogAsync", new CatalogItem("pending-detail", "track", "Отложенный", "", AlbumId: "1"), true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var manual = new TrackRow { Id = "manual", RequestId = "manual", Title = "Ручной выбор", Available = true,
                DurationMs = 30000, LocalPath = WriteWave("catalog-intent-manual.wav", 30) };
            await w.PlayTrackAsync(manual);
            Assert(f.Player.IsPlaying && ReferenceEquals(queue.Current, manual), "fixture failed to start newer manual playback");
            version = queue.Version;
            release.TrySetResult();
            await opening;
            await Call(w, "RefreshCatalogViewAsync");
            Assert(Control<ListView>(w, "TracksList").Items.Cast<TrackRow>().Single().Id == "pending-detail"
                && queue.Version == version && ReferenceEquals(queue.Current, manual) && f.Player.IsPlaying
                && f.Handler.Seen.Count(call => call.Uri.AbsolutePath == "/tracks") == 3,
                "a late catalog response or refresh replaced the newer track, or refresh skipped metadata");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task NavOfflinePlaylistRecovery()
    {
        using var f = new SaFixture("offline-history"); var w = f.Window;
        var offline = SaField<OfflineLibrary>(w, "_offline");
        PlaylistDetails Details(string owner, int revision) => new(owner, "77", owner + "-" + revision, "", revision,
            [new TrackRow { Id = owner + "-" + revision, RequestId = owner + "-" + revision, Title = "Офлайн-строка" }]);
        await offline.RememberPlaylistAsync(Details("first-owner", 1), AudioQuality.Optimal, default);
        await offline.RememberPlaylistAsync(Details("second-owner", 1), AudioQuality.Optimal, default);
        var state = typeof(OfflineLibrary).GetField("_directoryState", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(offline)!;
        var recovery = state.GetType().GetField("RecoveryTask", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recovery.SetValue(state, release.Task);
        var catalog = Control<ListBox>(w, "CatalogList");
        var list = Control<ListView>(w, "TracksList");
        try
        {
            var pending = Call(w, "ShowOfflinePlaylistsAsync");
            Assert(!pending.IsCompleted && SaField<string>(w, "_section") == "favorites", "navigation changes the view before recovery");
            release.TrySetResult();
            await pending;
            var first = catalog.Items.Cast<CatalogItem>().Single(item => item.Owner == "first-owner");
            var second = catalog.Items.Cast<CatalogItem>().Single(item => item.Owner == "second-owner");
            await Call(w, "OpenCatalogAsync", first, true);
            await Call(w, "OpenCatalogAsync", second, true);
            await offline.RememberPlaylistAsync(Details("first-owner", 2), AudioQuality.Optimal, default);
            NavMouse(w, MouseButton.XButton1);
            await Until(() => SaField<CancellationTokenSource?>(w, "_featureCts") is null);
            Assert(list.Items.Cast<TrackRow>().Single().Id == "first-owner-2" && Control<Button>(w, "RefreshButton").IsVisible,
                "Back reused stale tracks or the other owner with the same kind");
            await offline.RememberPlaylistAsync(Details("first-owner", 3), AudioQuality.Optimal, default);
            await Call(w, "RefreshCatalogViewAsync");
            Assert(list.Items.Cast<TrackRow>().Single().Id == "first-owner-3", "offline refresh lost the restored owner/kind");
            NavMouse(w, MouseButton.XButton2);
            await Until(() => SaField<CancellationTokenSource?>(w, "_featureCts") is null);
            Assert(list.Items.Cast<TrackRow>().Single().Id == "second-owner-1", "Forward reused the first owner");
            var duplicate = Details("second-owner", 2).Tracks.Single();
            await offline.RememberPlaylistAsync(Details("second-owner", 2) with { Tracks = [duplicate, duplicate] }, AudioQuality.Optimal, default);
            await Call(w, "RefreshCatalogViewAsync");
            list.SelectedIndex = 1;
            await Call(w, "OpenCatalogAsync", first, true);
            NavMouse(w, MouseButton.XButton1);
            await Until(() => SaField<CancellationTokenSource?>(w, "_featureCts") is null);
            Assert(list.SelectedItem is TrackRow selected && selected.RequestId == duplicate.RequestId && selected.Number == 2,
                "Back selected the first duplicate instead of the saved second occurrence");

            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            recovery.SetValue(state, release.Task);
            var canceled = Call(w, "ShowOffline");
            SaClick(Control<Button>(w, "FavoritesNav"));
            release.TrySetResult();
            await canceled;
            Assert(SaField<string>(w, "_section") == "favorites", "late offline read overwrote a newer view");

            File.WriteAllText(Path.Combine(offline.DirectoryPath, "playlists.index.json"), "{");
            await Call(w, "ShowOfflinePlaylist", first);
            Assert(SaField<string>(w, "_section") == "favorites" && f.Handler.Seen.Count == 0 && !f.Player.HasSource,
                "unreadable offline data changed views or attempted service playback");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task NavHiddenPlaybackFallback()
    {
        using var f = new SaFixture("hidden-playback-fallback"); await f.Activate(); var w = f.Window;
        var row = new TrackRow { Id = "hidden-playlist-row", RequestId = "hidden-playlist-row", Title = "Скрытая строка", Available = true };
        var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
        var button = Control<Button>(w, "PlayPauseButton");
        foreach (var section in new[] { "home", "artist-detail" })
        {
            SaInvoke(w, "ShowPlaylist", new PlaylistDetails("12345", "99", "Исходный плейлист", "", 1, [row]));
            Control<ListView>(w, "TracksList").SelectedItem = row;
            if (section == "home") await Call(w, "LoadHomeAsync");
            else await Call(w, "OpenArtistAsync", "artist-42", "Исполнитель", true);
            Assert(!button.IsEnabled && !Control<ListView>(w, "TracksList").IsVisible,
                "the lower Play remained enabled by an invisible playlist");
            SaClick(button);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, Environment.TickCount, Key.P)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            w.RaiseEvent(key);
            Assert(key.Handled && queue.Items.Count == 0 && !f.Player.HasSource
                && f.Handler.Seen.All(request => request.Uri.AbsolutePath != "/get-file-info"),
                "the lower Play or keyboard command opened an invisible playlist row");
        }
    }

    private static int NavHistoryCount(MainWindow window, string field)
    {
        var stack = typeof(MainWindow).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        return (int)stack.GetType().GetProperty("Count")!.GetValue(stack)!;
    }
    private static void NavMouse(MainWindow window, MouseButton button)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        window.RaiseEvent(args); Assert(args.Handled, "side mouse button was not handled by the real window route");
    }
}
