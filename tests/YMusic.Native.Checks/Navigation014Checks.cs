using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
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
        await Run("Navigation014: Back retries a pending initial catalog read without submitting the search draft", NavRetryInitialCatalogRead);
        await Run("Navigation014: catalog and artist double-click require the exact left-clicked row", NavExactDoubleClick);
        await Run("Navigation014: offline Back preserves the selected saved quality by path", NavOfflineSavedVersion);
        await Run("Navigation014: committed downloads refresh the selected offline card without overtaking navigation", NavOfflineDownloadCommit);
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
        await Run("Navigation014: playlist refresh and Back preserve duplicate occurrence selection", () => NavPlaylistOccurrenceSelection());
        await Run("Navigation014: confirmed removal preserves the surviving duplicate and a pending offline view", async () =>
        {
            await NavPlaylistOccurrenceSelection(keepSurvivor: true, offline: false);
            await NavPlaylistOccurrenceSelection(keepSurvivor: true, offline: true);
        });
        await Run("Navigation014: deletion respects offline navigation and leaves no tombstone if it fails", async () =>
        {
            await NavPlaylistDeletionDuringOffline(failOffline: false);
            await NavPlaylistDeletionDuringOffline(failOffline: true);
        });
        await Run("Navigation014: evicted playlist tracks leave only revision and deletion metadata", NavPlaylistHistoryEviction);
        await Run("Navigation014: confirmed creation adds a card and Back returns to it", NavPlaylistCreation);
        await Run("Navigation014: a pending player link outranks automatic opening after creation", NavPlaylistCreationAndPlayerLink);
        await Run("Navigation014: an obsolete playlist chooser releases admission for the new view", NavPlaylistChooserAndPlayerLink);
        await Run("Navigation014: playlist selection reserves the write and uses the session after the dialog", async () =>
        {
            await NavPlaylistAddSession("17");
            await NavPlaylistAddSession("19");
        });
        await Run("Navigation014: chooser insertion preserves exact known rows from history when metadata fails", NavPlaylistInsertKnownRows);
        await Run("Navigation014: sparse refresh preserves exact known metadata and explicit unavailability", NavPlaylistRefreshKnownRows);
        await Run("Navigation014: confirmed duplicate edits preserve the playing occurrence live and in history", () => NavPlaylistInsertDuplicate());
        await Run("Navigation014: only the exact write revision inherits metadata learned during playlist removal", NavPlaylistMetadataDuringChange);
        await Run("Navigation014: only an exact successor refreshed during write metadata restores the playing duplicate", async () =>
        {
            await NavPlaylistInsertDuplicate(refreshDuringRemoval: true);
            await NavPlaylistInsertDuplicate(refreshDuringRemoval: true, completeEnrichment: true);
            await NavPlaylistInsertDuplicate(refreshDuringRemoval: true, externalRevision: true);
        });
        await Run("Navigation014: duplicate identity needs a proven revision across rename, insertion and ordinary refresh", NavPlaylistDuplicateIdentityProof);
        await Run("Navigation014: equal-order refresh preserves the predecessor of a verified duplicate removal",
            () => NavPlaylistInsertDuplicate(refreshDuringRemoval: true, refreshBeforeRemoval: true));
        await Run("Navigation014: later playlist readback clears inherited duplicate selection and preserves a new choice", NavPlaylistLateSelection);
        await Run("Navigation014: a verified edit restores the choice hidden by an early successor refresh", NavPlaylistDeferredSelection);
        await Run("Navigation014: own list and chooser reconcile only newer versioned playlist cards", NavVersionedPlaylistCards);
        await Run("Navigation014: detail first learned after a card revision updates saved playlist views", NavPlaylistCardDetailHistory);
        await Run("Navigation014: late write acknowledgement cannot supersede a newer card detail already in flight",
            () => NavPlaylistDetailDuringWrite("card", previousMutation: true));
        await Run("Navigation014: the first write leaves a newer card and its held detail intact", async () =>
        {
            await NavPlaylistDetailDuringWrite("card");
            await NavPlaylistDetailDuringWrite("list");
        });
        await Run("Navigation014: a held refresh reconciles with a completed write and its original view", async () =>
        {
            await NavPlaylistDetailDuringWrite("refresh");
            await NavPlaylistDetailDuringWrite("stale");
            await NavPlaylistDetailDuringWrite("leave");
        });
        await Run("Navigation014: first private-playlist insertion preserves newer cards in either completion order", NavFirstInsertWithNewerCard);
        await Run("Navigation014: an unopened owned playlist keeps its first proven revision through history", NavUnopenedVersionedCardHistory);
        await Run("Navigation014: older confirmed rows appear before the newer detail on both history and source return", async () =>
        {
            await NavPlaylistDetailDuringWrite("history");
            await NavPlaylistDetailDuringWrite("source");
        });
        await Run("Navigation014: return before write publication reads the known newer card in both response orders", async () =>
        {
            foreach (var route in new[] { "history-before", "history-newer", "source-before", "source-newer" })
                await NavPlaylistDetailDuringWrite(route);
        });
        await Run("Navigation014: a manual viewport cancels the delayed reveal of a newer playlist composition",
            () => NavPlaylistDetailDuringWrite("source-cancel"));
        await Run("Navigation014: clearing the source selection cancels passive reselection on the same token",
            () => NavPlaylistDetailDuringWrite("source-deselect"));
        await Run("Navigation014: a deferred duplicate cannot replace a proven new playing occurrence after Back or Forward", async () =>
        {
            await NavDeferredChoiceAfterPlaybackMove(move: false);
            await NavDeferredChoiceAfterPlaybackMove(move: true);
            await NavDeferredChoiceAfterPlaybackMove(move: true, forward: true);
        });
        await Run("Navigation014: rename and delete recheck the observed owned revision immediately before dispatch", NavLatePlaylistWriteAdmission);
        await Run("Navigation014: canceling the playlist chooser retains the freshness gate for the current edit buttons", NavChooserUpdatesWriteAvailability);
        await Run("Navigation014: a same-occurrence audio open preserves exact removal selection while a transition still follows playback",
            () => NavPlaylistSelectionDuringAudioOpen());
        await Run("Navigation014: Back follows the exact duplicate after playback moves away from a reconciled selection", async () =>
        {
            await NavPlaylistSelectionDuringAudioOpen(leaveBeforeAdvance: true);
            await NavPlaylistSelectionDuringAudioOpen(leaveBeforeAdvance: true, keepOther: true);
        });
        await Run("Navigation014: history restores versioned playlist cards by owner and kind after re-sorting", NavPlaylistCardSelectionHistory);
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

    private static async Task NavPlaylistCreationAndPlayerLink()
    {
        using var f = new SaFixture("creation-held-link"); await f.Activate(); var w = f.Window;
        const string path = "/users/12345/playlists/17";
        const string playlist = """{"kind":"17","owner":{"uid":"12345"},"title":"Новый","revision":1,"visibility":"private","trackCount":0,"tracks":[]}""";
        var createEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var createRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var linkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var linkRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.RespondOverride = async (request, _) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/users/12345/playlists/list": return Json("""{"result":[]}""");
                case "/users/12345/playlists/create":
                    createEntered.TrySetResult();
                    await createRelease.Task;
                    return Json("{\"result\":" + playlist + "}");
                case path: return Json("{\"result\":" + playlist + "}");
                case "/tracks":
                    linkEntered.TrySetResult();
                    await linkRelease.Task;
                    return Json(JsonSerializer.Serialize(new { result = new[] { SaTrack("held-link") } }));
                default: return null;
            }
        };
        try
        {
            await Call(w, "LoadCollectionAsync", "playlists");
            var create = Call(w, "CreatePlaylistFromCatalogAsync", "Новый", "private");
            await createEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var link = Call(w, "OpenTrackAlbumAsync", new TrackRow { Id = "held-link", RequestId = "held-link:1" });
            await linkEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            createRelease.TrySetResult();
            await create;
            Assert(SaField<string>(w, "_section") == "playlists"
                && Control<ListBox>(w, "CatalogList").Items.OfType<CatalogItem>().Single().Id == "17",
                "confirmation overtook the newer link or omitted the created playlist from the catalog");
            linkRelease.TrySetResult();
            await link;
            Assert(SaField<AlbumDetails?>(w, "_activeAlbumDetails")?.Id == "1" && !f.Player.HasSource,
                "the pending player link lost its exact album or started audio");
        }
        finally { createRelease.TrySetResult(); linkRelease.TrySetResult(); }
    }

    private static async Task NavPlaylistChooserAndPlayerLink()
    {
        using var f = new SaFixture("chooser-held-link"); await f.Activate(); var w = f.Window;
        var chooserEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chooserRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var linkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var linkRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listReads = 0;
        CancellationToken formerChooserRead = default;
        f.RespondOverride = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/users/12345/playlists/list")
            {
                if (Interlocked.Increment(ref listReads) == 1)
                {
                    formerChooserRead = token;
                    chooserEntered.TrySetResult(); await chooserRelease.Task;
                }
                return Json("""{"result":[{"kind":"17","title":"Плейлист","owner":{"uid":"12345"}}]}""");
            }
            if (request.RequestUri.AbsolutePath != "/tracks") return null;
            linkEntered.TrySetResult(); await linkRelease.Task;
            return Json(JsonSerializer.Serialize(new { result = new[] { SaTrack("chooser-link") } }));
        };
        var observedDialogs = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (w.OwnedWindows.OfType<PlaylistDialog>().FirstOrDefault(candidate => candidate.IsVisible) is not { } dialog) return;
            observedDialogs++;
            dialog.Close();
        };
        try
        {
            var chooser = Call(w, "AddTrackToPlaylistAsync", new TrackRow { Id = "chooser-source", RequestId = "chooser-source:1" });
            await chooserEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Call(w, "AddTrackToPlaylistAsync", new TrackRow { Id = "repeat-click", RequestId = "repeat-click:1" });
            Assert(listReads == 1, "a second click in the same view admitted a concurrent chooser");
            var link = Call(w, "OpenTrackAlbumAsync", new TrackRow { Id = "chooser-link", RequestId = "chooser-link:1" });
            await linkEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(formerChooserRead.IsCancellationRequested && observedDialogs == 0 && SaField<string>(w, "_section") == "favorites",
                "the former chooser still owned its HTTP read or displaced the resolving link");
            linkRelease.TrySetResult();
            await link;
            Assert(SaField<AlbumDetails?>(w, "_activeAlbumDetails")?.Id == "1" && !f.Player.HasSource,
                "the newer link did not finish independently");
            timer.Start();
            await Call(w, "AddTrackToPlaylistAsync", new TrackRow { Id = "new-chooser", RequestId = "new-chooser:1" });
            Assert(listReads == 2 && observedDialogs == 1, "the held obsolete chooser blocked the new view's dialog");
            chooserRelease.TrySetResult();
            await chooser;
            Assert(listReads == 2 && observedDialogs == 1 && SaField<AlbumDetails?>(w, "_activeAlbumDetails")?.Id == "1",
                "the canceled chooser opened after the replacement had finished");
        }
        finally { timer.Stop(); chooserRelease.TrySetResult(); linkRelease.TrySetResult(); }
    }

    private static async Task NavPlaylistInsertKnownRows()
    {
        using var f = new SaFixture("chooser-known-rows"); var w = f.Window;
        const int audioSeconds = 20;
        const int audioDurationMs = audioSeconds * 1000;
        var media = NavSavePlaylistAudio(w, "inserted", audioSeconds);
        await f.Activate();
        const string path = "/users/12345/playlists/17";
        var revision = 1;
        string[] order = ["duplicate", "duplicate"];
        var failMetadata = false;
        var attemptedBeforeWrite = false;
        var changes = 0;
        object Playlist() => new { kind = "17", owner = new { uid = "12345" }, title = "С известными треками", visibility = "private",
            revision, trackCount = order.Length, tracks = order.Select(id => new { id, albumId = "1" }).ToArray() };
        f.RespondOverride = async (request, token) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/users/12345/playlists/list": return Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } }));
                case path: return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                case "/tracks" when failMetadata:
                    attemptedBeforeWrite |= changes == 0;
                    return Json("{}", System.Net.HttpStatusCode.ServiceUnavailable);
                case path + "/change":
                    var body = PlaylistWritesForm(await request.Content!.ReadAsStringAsync(token));
                    using (var diff = JsonDocument.Parse(body["diff"]))
                        Assert(diff.RootElement[0].GetProperty("at").GetInt32() == 2, "the insertion lost the authoritative append index");
                    changes++;
                    order = ["duplicate", "duplicate", "inserted"];
                    revision++;
                    return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                default: return null;
            }
        };
        var item = new CatalogItem("17", "playlist", "С известными треками", "", "12345", "17");
        await Call(w, "OpenCatalogAsync", item, true);
        var initial = SaField<PlaylistDetails>(w, "_activePlaylist");
        var known = initial.Tracks.ToArray();
        Assert(known.Length == 2 && known.All(row => row.Available && row.DurationMs > 0), "fixture did not load known metadata");
        var list = Control<ListView>(w, "TracksList");
        list.SelectedItem = known[1];
        SaClick(Control<Button>(w, "FavoritesNav"));
        // A competing active snapshot can outlive the catalog read that created it.
        var activeSecond = new TrackRow { Number = 2, Id = "duplicate", RequestId = "duplicate:1", AlbumId = "1",
            Title = "Активное второе вхождение", DurationMs = 190000, Available = true };
        SaInvoke(w, "ShowPlaylist", initial with { Tracks = [new TrackRow { Number = 1, Id = "duplicate",
            RequestId = "duplicate:1", HasTrackMetadata = false }, activeSecond] });
        list.SelectedItem = activeSecond;
        failMetadata = true;
        var inserted = new TrackRow { Number = 3, Id = "inserted", RequestId = "inserted:1", AlbumId = "1",
            Title = "Известный добавленный трек", DurationMs = audioDurationMs, Available = true };
        SaInvoke(w, "AddWaveBatch", new RadioBatch("playlist-wave-source", "fixture-batch", [inserted]) { Station = new object() });
        Assert((bool)typeof(MainWindow).GetMethod("IsContinuableWaveTrack", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(w, [inserted])!, "the source did not belong to the active wave");
        await Call(w, "InsertTrackIntoPlaylistCoreAsync", item, new LikedId(inserted.Id, inserted.AlbumId), inserted);
        Assert(changes == 1 && !attemptedBeforeWrite && SaField<string>(w, "_section") == "playlist-detail",
            "chooser depended on enrichment before the single confirmed write or changed the view");
        AssertKnownRows();
        NavMouse(w, MouseButton.XButton1);
        AssertKnownRows();
        var appended = SaField<PlaylistDetails>(w, "_activePlaylist").Tracks[2];
        list.SelectedItem = appended;
        await Call(w, "PlaySelectedAsync");
        Assert(f.Player.HasSource && appended.DownloadedPath == media
            && Control<TextBlock>(w, "QualityText").Text.StartsWith("Офлайн · ", StringComparison.Ordinal)
            && ReferenceEquals(SaField<PlaybackQueue>(w, "_playbackQueue").Current, appended),
            "the newly inserted playlist occurrence did not play the exact saved audio");
        await Call(w, "ExtendWaveQueueAsync");
        Assert(SaField<PlaybackQueue>(w, "_playbackQueue").Items.Count == 3
            && !f.Handler.Seen.Any(call => call.Uri.AbsolutePath.StartsWith("/rotor/session/", StringComparison.Ordinal)),
            "playing the playlist occurrence reported feedback or requested recommendations from the source wave");

        void AssertKnownRows()
        {
            var updated = SaField<PlaylistDetails>(w, "_activePlaylist");
            Assert(updated.Revision == 2 && updated.Tracks.Select(row => row.RequestId).SequenceEqual(["duplicate:1", "duplicate:1", "inserted:1"])
                && updated.Tracks.Select(row => row.Number).SequenceEqual([1, 2, 3])
                && ReferenceEquals(updated.Tracks[0], known[0]) && ReferenceEquals(updated.Tracks[1], activeSecond)
                && updated.Tracks[2].Title == inserted.Title && updated.Tracks[2].DurationMs == audioDurationMs
                && updated.Tracks[2].CanPlay && !ReferenceEquals(updated.Tracks[2], inserted)
                && ReferenceEquals(list.SelectedItem, updated.Tracks[1]),
                "the active or saved view lost known per-occurrence metadata, order, selection or the independent inserted track");
        }
    }

    private static async Task NavPlaylistRefreshKnownRows()
    {
        using var f = new SaFixture("playlist-sparse-refresh"); await f.Activate(); var w = f.Window;
        const string path = "/users/12345/playlists/17";
        var revision = 1;
        var title = "Исходный плейлист";
        var suppressMetadata = false;
        var sparseMetadata = false;
        var renames = 0;
        var edgeAlbum = "1";
        object Playlist() => new { kind = "17", owner = new { uid = "12345" }, title, revision, trackCount = 2,
            tracks = new[] { new { id = "omitted", albumId = "1" }, new { id = "edge", albumId = edgeAlbum } } };
        f.RespondOverride = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath switch
        {
            "/users/12345/playlists/list" => Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } })),
            path => Json(JsonSerializer.Serialize(new { result = Playlist() })),
            path + "/name" => Rename(),
            "/tracks" when suppressMetadata => Json("{}", System.Net.HttpStatusCode.ServiceUnavailable),
            "/tracks" when sparseMetadata => Json(JsonSerializer.Serialize(new { result = new[] { new
                { id = "edge", title = "Строка недоступна", available = false, albums = new[] { new { id = edgeAlbum } } } } })),
            _ => null
        });

        HttpResponseMessage Rename()
        {
            renames++; revision++; title = "Переименован";
            return Json(JsonSerializer.Serialize(new { result = Playlist() }));
        }

        var item = new CatalogItem("17", "playlist", title, "", "12345", "17");
        await Call(w, "OpenCatalogAsync", item, true);
        var original = SaField<PlaylistDetails>(w, "_activePlaylist");
        suppressMetadata = true;
        await Call(w, "RenamePlaylistFromCatalogAsync", original, "Переименован");
        Assert(renames == 1 && SaField<PlaylistDetails>(w, "_activePlaylist") is { Revision: 2, Title: "Переименован" },
            "the setup did not confirm the rename before refreshing");
        suppressMetadata = false;
        SaClick(Control<Button>(w, "FavoritesNav"));
        await Call(w, "OpenCatalogAsync", item, true);
        sparseMetadata = true;
        await Call(w, "RefreshCatalogViewAsync");
        AssertFreshMetadata();
        NavMouse(w, MouseButton.XButton1);
        NavMouse(w, MouseButton.XButton1);
        AssertFreshMetadata();
        edgeAlbum = "other-edition";
        await Call(w, "RefreshCatalogViewAsync");
        var changed = SaField<PlaylistDetails>(w, "_activePlaylist");
        Assert(changed.Tracks[0] is { RequestId: "omitted:1", HasTrackMetadata: false }
            && changed.Tracks[1].RequestId == "edge:other-edition",
            "refresh imported old metadata without proving the full exact order and edition");

        void AssertFreshMetadata()
        {
            var refreshed = SaField<PlaylistDetails>(w, "_activePlaylist");
            Assert(refreshed is { Revision: 2, Title: "Переименован" }
                && refreshed.Tracks[0].Title == original.Tracks[0].Title && refreshed.Tracks[0].DurationMs == original.Tracks[0].DurationMs
                && refreshed.Tracks[0].Available && refreshed.Tracks[0].HasTrackMetadata
                && refreshed.Tracks[1] is { Available: false, HasTrackMetadata: true, Title: "Строка недоступна" },
                "sparse refresh removed known metadata or ignored the explicit fresh unavailability");
        }
    }

    private static string NavSavePlaylistAudio(MainWindow window, string id, int seconds = 20, int bitrate = 320, bool append = false)
    {
        var directory = SaField<OfflineLibrary>(window, "_offline").DirectoryPath;
        Directory.CreateDirectory(directory);
        var fileName = id + ".mp3";
        var path = Path.Combine(directory, fileName);
        var bytes = SilentMp3Seconds(seconds, bitrate);
        File.WriteAllBytes(path, bytes);
        var item = new OfflineItem(id, id + ":1", id, "", "", seconds * 1000, fileName, "mp3", bitrate,
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UtcNow, ConfirmedBitrate: bitrate);
        var index = Path.Combine(directory, "library.index.json");
        var previous = append ? JsonSerializer.Deserialize<OfflineItem[]>(File.ReadAllText(index))! : [];
        File.WriteAllText(index, JsonSerializer.Serialize(previous.Append(item)));
        return path;
    }

    private static async Task NavPlaylistInsertDuplicate(bool refreshDuringRemoval = false, bool externalRevision = false,
        bool completeEnrichment = false, bool refreshBeforeRemoval = false)
    {
        var fixtureName = $"playlist-insert-duplicate-{refreshDuringRemoval}-{externalRevision}-{completeEnrichment}-{refreshBeforeRemoval}";
        using var f = new SaFixture(fixtureName); var w = f.Window;
        var media = NavSavePlaylistAudio(w, "repeat");
        await f.Activate();
        const string path = "/users/12345/playlists/17";
        var revision = 1;
        var count = 2;
        var changes = 0;
        var waitingForMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        object Playlist() => new { kind = "17", owner = new { uid = "12345" }, title = "Плейлист с повторами", revision,
            trackCount = count, tracks = Enumerable.Repeat(new { id = "repeat", albumId = "1" }, count).ToArray() };
        f.RespondOverride = async (request, token) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/users/12345/playlists/list": return Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } }));
                case path: return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                case "/tracks" when refreshDuringRemoval && changes == 2:
                    if (waitingForMetadata.TrySetResult())
                    {
                        await releaseMetadata.Task;
                        return completeEnrichment
                            ? Json("""{"result":[{"id":"repeat","title":"Сведения операции","available":true,"durationMs":20000,"albums":[{"id":"1"}]}]}""")
                            : Json("{}", System.Net.HttpStatusCode.ServiceUnavailable);
                    }
                    return Json("""{"result":[{"id":"repeat","title":"Свежие сведения","available":false,"durationMs":20000,"albums":[{"id":"1"}]}]}""");
                case "/tracks": return Json("""{"result":[{"id":"repeat","title":"Повтор","available":true,"durationMs":20000,"albums":[{"id":"1"}]}]}""");
                case path + "/change":
                    var form = PlaylistWritesForm(await request.Content!.ReadAsStringAsync(token));
                    using (var json = JsonDocument.Parse(form["diff"]))
                    {
                        var operation = json.RootElement[0];
                        if (changes == 0)
                        {
                            Assert(operation.GetProperty("at").GetInt32() == 2, "the duplicate was not inserted at the confirmed append position");
                            count++;
                        }
                        else
                        {
                            Assert(operation.GetProperty("from").GetInt32() == 0 && operation.GetProperty("to").GetInt32() == 1,
                                "the duplicate removal did not target the first exact occurrence");
                            count--;
                        }
                    }
                    changes++; revision++;
                    return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                default: return null;
            }
        };

        var item = new CatalogItem("17", "playlist", "Плейлист с повторами", "", "12345", "17");
        var list = Control<ListView>(w, "TracksList");
        await Call(w, "OpenCatalogAsync", item, true);
        var original = SaField<PlaylistDetails>(w, "_activePlaylist").Tracks;
        var playing = original[1];
        list.SelectedItem = original[0];
        await Call(w, "PlaySelectedAsync");
        var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
        var removedToken = queue.CurrentOccurrenceToken;
        Assert(f.Player.HasSource && ReferenceEquals(queue.Current, original[0]), "the fixture did not start the first occurrence");
        // Previous follows listening history, so visit the first occurrence before moving to the second.
        await Call(w, "AdvanceAsync", 1, false);
        var playbackToken = queue.CurrentOccurrenceToken;
        var queueVersion = queue.Version;
        var nativeSlot = Pc0317Get<object>(f.Player, "_current");
        Assert(f.Player.HasSource && ReferenceEquals(queue.Current, playing), "the fixture did not start the second occurrence");
        SaClick(Control<Button>(w, "FavoritesNav"));
        await Call(w, "OpenCatalogAsync", item, true);
        await Call(w, "InsertTrackIntoPlaylistAsync", item, new LikedId("repeat", "1"));
        AssertSelection(2, 1);
        NavMouse(w, MouseButton.XButton1);
        NavMouse(w, MouseButton.XButton1);
        AssertSelection(2, 1);
        NavMouse(w, MouseButton.XButton2);
        NavMouse(w, MouseButton.XButton2);
        AssertSelection(2, 1);
        if (refreshBeforeRemoval)
        {
            revision++;
            SaClick(Control<Button>(w, "RefreshButton"));
            await Until(() => SaField<PlaylistDetails>(w, "_activePlaylist").Revision == revision
                && SaField<CancellationTokenSource?>(w, "_featureCts") is null);
            AssertSelection(revision, 1);
        }
        list.SelectedIndex = 0;
        try
        {
            SaClick(Control<Button>(w, "RemovePlaylistTrackButton"));
            if (refreshDuringRemoval)
            {
                await waitingForMetadata.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (externalRevision) revision++;
                SaClick(Control<Button>(w, "RefreshButton"));
                await Until(() => SaField<PlaylistDetails>(w, "_activePlaylist").Revision == revision
                    && SaField<CancellationTokenSource?>(w, "_featureCts") is null);
                Assert(SaField<bool>(w, "_catalogWriting") && w.QueuePlayingIndex == -1 && queue.CurrentOccurrenceToken == playbackToken,
                    "the refresh did not publish the successor while the write was still enriching its own readback");
            }
        }
        finally { releaseMetadata.TrySetResult(); }
        await Until(() => !SaField<bool>(w, "_catalogWriting"));
        var expectedIndex = externalRevision ? -1 : 0;
        Assert(w.QueuePlayingIndex == expectedIndex, "the playing marker did not respect the revision of the confirmed removal");
        if (refreshDuringRemoval) AssertSelection(revision, expectedIndex, expectedChanges: 2, selectPlaying: false);
        SaClick(Control<Button>(w, "FavoritesNav"));
        NavMouse(w, MouseButton.XButton1);
        // Back preserves the empty choice after live deletion; the unchanged playing occurrence remains marked.
        AssertSelection(revision, expectedIndex, expectedChanges: 2, selectPlaying: false);
        NavMouse(w, MouseButton.XButton2);
        SaClick(Control<Button>(w, "PlaybackContextButton"));
        AssertSelection(revision, expectedIndex, expectedChanges: 2);

        await Call(w, "AdvanceAsync", -1, false);
        Assert(f.Player.HasSource && queue.CurrentOccurrenceToken == removedToken && ReferenceEquals(queue.Current, original[0]),
            "the fixture did not return to the deleted occurrence in the original playback queue");
        SaClick(Control<Button>(w, "PlaybackContextButton"));
        Assert(w.QueuePlayingIndex == -1 && list.SelectedItem is null
            && SaField<PlaylistDetails>(w, "_activePlaylist").Tracks.All(row => !row.IsPlaying),
            "the original queue token of a deleted occurrence marked another playlist row");

        void AssertSelection(int expectedRevision, int expectedIndex, int expectedChanges = 1, bool selectPlaying = true)
        {
            var shown = SaField<PlaylistDetails>(w, "_activePlaylist");
            Assert(changes == expectedChanges && shown.Revision == expectedRevision && shown.Tracks.Count == count
                && shown.Tracks.All(row => row.RequestId == "repeat:1" && row.HasTrackMetadata)
                && f.Player.SourceLocalPath == media && queue.CurrentOccurrenceToken == playbackToken && queue.Version == queueVersion
                && ReferenceEquals(Pc0317Get<object>(f.Player, "_current"), nativeSlot)
                && w.QueuePlayingIndex == expectedIndex, "a duplicate edit changed playback or ignored the revision required to mark a row");
            if (expectedIndex < 0)
                Assert(list.SelectedItem is null && shown.Tracks.All(row => !row.IsPlaying), "a newer unproven revision marked or selected a duplicate");
            else
                Assert(!ReferenceEquals(shown.Tracks[expectedIndex], playing)
                    && ReferenceEquals(list.SelectedItem, selectPlaying ? shown.Tracks[expectedIndex] : null)
                    && list.SelectedItems.Count == (selectPlaying ? 1 : 0)
                    && shown.Tracks.Where(row => row.IsPlaying).SequenceEqual([shown.Tracks[expectedIndex]]),
                    "a confirmed duplicate edit did not mark and select the correct new row");
            if (refreshDuringRemoval && expectedChanges == 2)
                Assert(shown.Tracks.All(row => row.HasTrackMetadata && row.Available == completeEnrichment
                    && row.Title == (completeEnrichment ? "Сведения операции" : "Свежие сведения")),
                    "the delayed enrichment did not preserve the authoritative metadata for each exact position");
        }
    }

    private static async Task NavPlaylistMetadataDuringChange()
    {
        foreach (var exactRevision in new[] { true, false })
        {
            using var f = new SaFixture("playlist-metadata-during-change-" + exactRevision); await f.Activate(); var w = f.Window;
            const string path = "/users/12345/playlists/17";
            var revision = 1;
            string[] order = ["repeat", "learned", "repeat", "edge"];
            var allMetadata = false;
            var changes = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            object Playlist() => new { kind = "17", owner = new { uid = "12345" }, title = "Сведения во время изменения", revision,
                trackCount = order.Length, tracks = order.Select(id => new { id, albumId = "1" }).ToArray() };
            f.RespondOverride = async (request, token) =>
            {
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/users/12345/playlists/list": return Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } }));
                    case path: return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                    case "/tracks" when revision == 1 && !allMetadata:
                        return Json(JsonSerializer.Serialize(new { result = new[] { SaTrack("repeat"), SaTrack("edge") } }));
                    case "/tracks" when revision > 1 && exactRevision:
                        return Json("{}", System.Net.HttpStatusCode.ServiceUnavailable);
                    case "/tracks" when revision > 1:
                        return Json("""{"result":[{"id":"edge","title":"Недоступен","available":false,"albums":[{"id":"1"}]}]}""");
                    case path + "/change":
                        var form = PlaylistWritesForm(await request.Content!.ReadAsStringAsync(token));
                        using (var json = JsonDocument.Parse(form["diff"]))
                            Assert(json.RootElement[0].GetProperty("from").GetInt32() == 0, "the change did not target the first duplicate");
                        changes++;
                        entered.TrySetResult();
                        await release.Task;
                        order = ["learned", "repeat", "edge"];
                        revision = exactRevision ? 2 : 4;
                        return Json("""{"result":{"revision":2}}""");
                    default: return null;
                }
            };

            var list = Control<ListView>(w, "TracksList");
            var item = new CatalogItem("17", "playlist", "Сведения во время изменения", "", "12345", "17");
            PlaylistDetails Shown() => SaField<PlaylistDetails>(w, "_activePlaylist");
            try
            {
                await Call(w, "OpenCatalogAsync", item, true);
                list.SelectedItem = Shown().Tracks[2];
                SaClick(Control<Button>(w, "FavoritesNav"));
                await Call(w, "OpenCatalogAsync", item, true);
                Assert(!Shown().Tracks[1].HasTrackMetadata, "the initial playlist fixture unexpectedly knew the missing row");
                list.SelectedItem = Shown().Tracks[0];
                SaClick(Control<Button>(w, "RemovePlaylistTrackButton"));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                allMetadata = true;
                await Call(w, "RefreshCatalogViewAsync");
                var learned = Shown().Tracks[1];
                Assert(Shown().Revision == 1 && learned is { HasTrackMetadata: true, Number: 2, Available: true },
                    "the held write did not let the ordinary refresh learn the exact missing row");
                list.SelectedItem = Shown().Tracks[2];
                release.TrySetResult();
                await Until(() => !SaField<bool>(w, "_catalogWriting"));
                AssertResult();
                NavMouse(w, MouseButton.XButton1);
                NavMouse(w, MouseButton.XButton1);
                AssertResult();
                NavMouse(w, MouseButton.XButton2);
                NavMouse(w, MouseButton.XButton2);
                AssertResult();

                void AssertResult()
                {
                    var updated = Shown();
                    Assert(changes == 1 && updated.Revision == revision
                        && updated.Tracks.Select(row => row.RequestId).SequenceEqual(["learned:1", "repeat:1", "edge:1"])
                        && updated.Tracks.Select(row => row.Number).SequenceEqual([1, 2, 3]),
                        "the edit replayed or displayed the wrong exact sequence");
                    if (exactRevision)
                        Assert(updated.Tracks[0].Title == learned.Title && updated.Tracks[0].DurationMs == learned.DurationMs
                            && updated.Tracks[0].Available && learned.Number == 2 && ReferenceEquals(list.SelectedItem, updated.Tracks[1]),
                            "the exact write revision lost metadata learned during the write or the selected survivor");
                    else
                        Assert(updated.Tracks.Take(2).All(row => !row.HasTrackMetadata && !row.Available)
                            && updated.Tracks[2] is { HasTrackMetadata: true, Title: "Недоступен", Available: false }
                            && list.SelectedItem is null,
                            "a later revision inherited an ambiguous duplicate's metadata or selection");
                }
            }
            finally { release.TrySetResult(); }
        }
    }

    private static async Task NavPlaylistDuplicateIdentityProof()
    {
        foreach (var (rename, exactRevision) in new[] { (true, true), (true, false), (false, true), (false, false) })
        {
            var fixtureName = $"playlist-duplicate-proof-{rename}-{exactRevision}";
            using var f = new SaFixture(fixtureName); var w = f.Window;
            NavSavePlaylistAudio(w, "repeat");
            await f.Activate();
            const string path = "/users/12345/playlists/17";
            var title = "Повторы";
            var revision = 1;
            string[] order = ["repeat", "repeat"];
            var writes = 0;
            var metadataError = !rename && exactRevision;
            object Playlist() => new { kind = "17", owner = new { uid = "12345" }, title, revision, trackCount = order.Length,
                tracks = order.Select(id => new { id, albumId = "1" }).ToArray() };
            f.RespondOverride = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath switch
            {
                "/users/12345/playlists/list" => Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } })),
                path => Json(JsonSerializer.Serialize(new { result = Playlist() })),
                path + "/name" or path + "/change" => Write(),
                "/tracks" when metadataError && writes > 0 => Json("""{"error":{"name":"fixture-metadata-only"}}"""),
                "/tracks" => Json(JsonSerializer.Serialize(new { result = order.Distinct().Select(id => new
                    { id, title = $"{id} · {revision}", available = true, durationMs = 20000, albums = new[] { new { id = "1" } } }) })),
                _ => null
            });

            HttpResponseMessage Write()
            {
                writes++;
                if (rename) title = "Новое название";
                else order = [.. order, "other"];
                revision = exactRevision ? 2 : 4;
                return Json(JsonSerializer.Serialize(new { result = new { revision = rename ? revision : 2 } }));
            }

            var list = Control<ListView>(w, "TracksList");
            var item = new CatalogItem("17", "playlist", title, "", "12345", "17");
            PlaylistDetails Shown() => SaField<PlaylistDetails>(w, "_activePlaylist");
            await Call(w, "OpenCatalogAsync", item, true);
            var previous = Shown();
            list.SelectedItem = previous.Tracks[1];
            await Call(w, "PlaySelectedAsync");
            var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
            var token = queue.CurrentOccurrenceToken;
            var nativeSlot = Pc0317Get<object>(f.Player, "_current");
            Assert(token is not null && nativeSlot is not null && ReferenceEquals(queue.Current, previous.Tracks[1]), "the initial duplicate did not play");
            if (rename) await Call(w, "RenamePlaylistFromCatalogAsync", previous, "Новое название");
            else await Call(w, "InsertTrackIntoPlaylistAsync", item, new LikedId("other", "1"));
            AssertResult(exactRevision);
            if (metadataError)
            {
                var status = Control<TextBlock>(w, "StatusText").Text;
                Assert(Shown().Tracks[^1] is { RequestId: "other:1", HasTrackMetadata: false }
                    && status.StartsWith("Изменение сохранено.", StringComparison.Ordinal),
                    "optional API error concealed the committed insertion or prompted a repeat");
            }
            SaClick(Control<Button>(w, "FavoritesNav"));
            SaClick(Control<Button>(w, "PlaybackContextButton"));
            AssertResult(exactRevision);

            if (rename && exactRevision)
            {
                order = ["repeat", "other"];
                revision++;
                await Call(w, "RefreshCatalogViewAsync");
                AssertResult(false);
                order = ["repeat", "other", "repeat"];
                revision++;
                await Call(w, "RefreshCatalogViewAsync");
                AssertResult(false);
                SaClick(Control<Button>(w, "FavoritesNav"));
                NavMouse(w, MouseButton.XButton1);
                AssertResult(false);
                SaClick(Control<Button>(w, "FavoritesNav"));
                SaClick(Control<Button>(w, "PlaybackContextButton"));
                AssertResult(false);
            }

            void AssertResult(bool proven)
            {
                var shown = Shown();
                Assert(writes == 1 && shown.Revision == revision && shown.Title == title
                    && shown.Tracks.Select(row => row.Id).SequenceEqual(order)
                    && queue.CurrentOccurrenceToken == token && ReferenceEquals(Pc0317Get<object>(f.Player, "_current"), nativeSlot),
                    "the confirmed list, unchanged audio or original queue token was lost");
                if (proven)
                    Assert(ReferenceEquals(list.SelectedItem, shown.Tracks[1]) && w.QueuePlayingIndex == 1 && shown.Tracks[1].IsPlaying,
                        "the proven revision lost the playing duplicate");
                else
                    Assert(list.SelectedItem is null && w.QueuePlayingIndex == -1 && shown.Tracks.All(row => !row.IsPlaying),
                        "an unproven revision selected or marked a duplicate by its former ordinal");
            }
        }
    }

    private static async Task NavPlaylistOccurrenceSelection(bool keepSurvivor = false, bool offline = false)
    {
        using var f = new SaFixture("playlist-occurrence-selection-" + keepSurvivor + "-" + offline); await f.Activate(); var w = f.Window;
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

        if (keepSurvivor)
        {
            var offlineEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var offlineRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                list.SelectedItem = Rows()[0];
                SaClick(remove);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                list.SelectedItem = Rows()[2];
                Task? opening = null;
                if (offline)
                {
                    w.OfflineViewAfterRead = _ => { offlineEntered.TrySetResult(); return offlineRelease.Task; };
                    opening = Call(w, "ShowOffline");
                    await offlineEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var requestCount = f.Handler.Seen.Count(call => call.Uri.AbsolutePath == path);
                    SaClick(Control<Button>(w, "RefreshButton"));
                    Assert(f.Handler.Seen.Count(call => call.Uri.AbsolutePath == path) == requestCount, "refresh read the outgoing playlist while offline was loading");
                }
                else await Call(w, "OpenCatalogAsync", album, true);
                release.TrySetResult();
                await Until(() => !SaField<bool>(w, "_catalogWriting"));
                if (opening is not null)
                {
                    AssertSurvivor();
                    offlineRelease.TrySetResult();
                    await opening;
                    Assert(SaField<string>(w, "_section") == "offline", "the confirmed mutation canceled the newer offline view");
                }
                NavMouse(w, MouseButton.XButton1);
                AssertSurvivor();
                Assert(removedIndices.SequenceEqual([0]) && !f.Player.HasSource, "the wrong duplicate was removed or navigation played audio");
                return;
            }
            finally { release.TrySetResult(); offlineRelease.TrySetResult(); }

            void AssertSurvivor()
            {
                Assert(SaField<string>(w, "_section") == "playlist-detail" && Rows().Length == 3
                    && ReferenceEquals(list.SelectedItem, Rows()[1]) && Rows()[1].RequestId == "repeat:1",
                    "the surviving exact duplicate lost its selection after the confirmed deletion");
            }
        }

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

    private static async Task NavPlaylistDeletionDuringOffline(bool failOffline)
    {
        using var f = new SaFixture("playlist-delete-during-offline-" + failOffline); await f.Activate(); var w = f.Window;
        const string path = "/users/12345/playlists/17";
        const string playlist = """{"kind":"17","owner":{"uid":"12345"},"title":"Плейлист","revision":1,"visibility":"private","trackCount":0,"tracks":[]}""";
        var deleteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offlineEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offlineRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = false;
        f.RespondOverride = async (request, _) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/users/12345/playlists/list": return Json(deleted ? """{"result":[]}""" : "{\"result\":[" + playlist + "]}");
                case path: return Json("{\"result\":" + playlist + "}");
                case path + "/delete":
                    deleteEntered.TrySetResult();
                    await deleteRelease.Task;
                    deleted = true;
                    return Json("""{"result":"ok"}""");
                default: return null;
            }
        };
        try
        {
            await Call(w, "OpenCatalogAsync", new CatalogItem("17", "playlist", "Плейлист", "", "12345", "17"), true);
            var deletion = Call(w, "DeletePlaylistFromCatalogAsync", SaField<PlaylistDetails>(w, "_activePlaylist"));
            await deleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            w.OfflineViewAfterRead = _ => { offlineEntered.TrySetResult(); return offlineRelease.Task; };
            var opening = Call(w, "ShowOffline");
            await offlineEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            deleteRelease.TrySetResult();
            await deletion;
            Assert(deleted && SaField<string>(w, "_section") == "playlist-detail"
                && SaField<PlaylistDetails?>(w, "_activePlaylist") is null, "deletion reopened the online collection or left the deleted detail active");
            if (failOffline) offlineRelease.TrySetException(new IOException("Fixture offline read failed."));
            else offlineRelease.TrySetResult();
            await opening;
            if (failOffline)
            {
                await Until(() => SaField<CancellationTokenSource?>(w, "_featureCts") is null);
                Assert(SaField<string>(w, "_section") == "playlists" && Control<ListBox>(w, "CatalogList").Items.Count == 0,
                    "failed offline navigation left a deleted playlist page on screen");
            }
            else Assert(SaField<string>(w, "_section") == "offline", "the outbound delete replaced the pending offline view");
            NavMouse(w, MouseButton.XButton1);
            Assert(SaField<string>(w, "_section") == "favorites" && !f.Player.HasSource, "Back captured the deleted outgoing playlist");
        }
        finally { deleteRelease.TrySetResult(); offlineRelease.TrySetResult(); }
    }

    private static async Task NavPlaylistLateSelection()
    {
        foreach (var explicitChoice in new int?[] { null, 0, 1 })
        {
            using var f = new SaFixture("playlist-late-selection-" + (explicitChoice?.ToString() ?? "inherited"));
            await f.Activate(); var w = f.Window;
            const string path = "/users/12345/playlists/17";
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var revision = 1;
            var writes = 0;
            object Playlist() => new { kind = "17", owner = new { uid = "12345" }, revision,
                title = revision == 1 ? "Повторы" : "Новое имя", trackCount = 2,
                tracks = Enumerable.Repeat(new { id = "repeat", albumId = "1" }, 2).ToArray() };
            f.RespondOverride = async (request, _) =>
            {
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/users/12345/playlists/list": return Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } }));
                    case path: return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                    case path + "/name":
                        writes++; revision = 4;
                        return Json("""{"result":{"revision":2}}""");
                    case "/tracks" when writes > 0 && entered.TrySetResult():
                        await release.Task;
                        return Json("{}", System.Net.HttpStatusCode.ServiceUnavailable);
                    case "/tracks": return Json(JsonSerializer.Serialize(new { result = new[] { SaTrack("repeat") } }));
                    default: return null;
                }
            };
            var item = new CatalogItem("17", "playlist", "Повторы", "", "12345", "17");
            var list = Control<ListView>(w, "TracksList");
            PlaylistDetails Shown() => SaField<PlaylistDetails>(w, "_activePlaylist");
            try
            {
                await Call(w, "OpenCatalogAsync", item, true);
                list.SelectedItem = Shown().Tracks[1];
                SaClick(Control<Button>(w, "FavoritesNav"));
                await Call(w, "OpenCatalogAsync", item, true);
                list.SelectedItem = Shown().Tracks[1];
                var writing = Call(w, "RenamePlaylistFromCatalogAsync", Shown(), "Новое имя");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                SaClick(Control<Button>(w, "RefreshButton"));
                await Until(() => Shown().Revision == 4 && SaField<CancellationTokenSource?>(w, "_featureCts") is null);
                Assert(ReferenceEquals(list.SelectedItem, Shown().Tracks[1]), "the independent refresh did not inherit the original choice before write reconciliation");
                if (explicitChoice == 0) list.SelectedIndex = 0;
                else if (explicitChoice == 1)
                {
                    list.ScrollIntoView(list.SelectedItem); w.UpdateLayout();
                    var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(1)!;
                    row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = row });
                }
                release.TrySetResult();
                await writing;
                AssertCurrent();
                NavMouse(w, MouseButton.XButton1);
                NavMouse(w, MouseButton.XButton1);
                Assert(Shown().Revision == 4 && list.SelectedItem is null, "Back reinstated an inherited duplicate choice after the unproven rename");
                NavMouse(w, MouseButton.XButton2);
                NavMouse(w, MouseButton.XButton2);
                AssertCurrent();
            }
            finally { release.TrySetResult(); }

            void AssertCurrent()
            {
                var shown = Shown();
                var selected = explicitChoice is int index ? shown.Tracks[index] : null;
                Assert(writes == 1 && shown is { Revision: 4, Title: "Новое имя" }
                    && ReferenceEquals(list.SelectedItem, selected) && !f.Player.HasSource,
                    "the unproven rename replayed, preserved an inherited choice, or discarded an explicit new choice");
            }
        }
    }

    private static async Task NavPlaylistDeferredSelection()
    {
        foreach (var (remove, choice) in new[] { (false, "keep"), (true, "keep"), (true, "new"), (true, "clear"), (true, "deleted"),
            (false, "newer"), (false, "multi"), (true, "multi"), (true, "play"), (true, "reveal"), (true, "history"), (true, "history-empty"),
            (true, "live"), (true, "live-empty"), (true, "early-live"), (true, "early-live-empty"),
            (true, "early-replace"), (true, "early-clear") })
        {
            using var f = new SaFixture($"playlist-deferred-choice-{remove}-{choice}"); var w = f.Window;
            var replaceChoice = choice is "early-replace" or "early-clear";
            var refreshBeforeConfirmation = replaceChoice || choice is "early-live" or "early-live-empty";
            var applyInLiveView = refreshBeforeConfirmation || choice is "live" or "live-empty";
            var goingBack = applyInLiveView || choice is "history" or "history-empty";
            var usesPlayback = goingBack || choice is "play" or "reveal";
            if (usesPlayback)
                NavSavePlaylistAudio(w, "other");
            await f.Activate();
            const string path = "/users/12345/playlists/17";
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var revision = 1;
            var writes = 0;
            string[] order = choice == "early-live" || replaceChoice ? ["other", "repeat", "unique"]
                : usesPlayback ? ["other", "repeat", "repeat"] : ["repeat", "repeat", "other"];
            var initialDuplicate = usesPlayback ? 1 : 0;
            var survivor = initialDuplicate + 1;
            object Playlist() => new { kind = "17", owner = new { uid = "12345" }, revision, title = "Повторы", trackCount = order.Length,
                tracks = order.Select(id => new { id, albumId = "1" }).ToArray() };
            f.RespondOverride = async (request, token) =>
            {
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/users/12345/playlists/list": return Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } }));
                    case path: return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                    case path + "/change":
                        var form = PlaylistWritesForm(await request.Content!.ReadAsStringAsync(token));
                        using (var json = JsonDocument.Parse(form["diff"]))
                            Assert(json.RootElement[0].GetProperty(remove ? "from" : "at").GetInt32() == (remove ? initialDuplicate : 3),
                                "the operation targeted the wrong occurrence or insertion position");
                        writes++;
                        order = remove ? order.Where((_, index) => index != initialDuplicate).ToArray() : [.. order, "repeat"];
                        revision = choice == "newer" ? 4 : 2;
                        return Json("""{"result":{"revision":2}}""");
                    case "/tracks" when writes > 0 && entered.TrySetResult():
                        await release.Task;
                        return Json("{}", System.Net.HttpStatusCode.ServiceUnavailable);
                    case "/tracks": return Json(JsonSerializer.Serialize(new { result = order.Distinct().Select(id => new
                        { id, title = id, available = true, durationMs = 20000, albums = new[] { new { id = "1" } } }) }));
                    default: return null;
                }
            };
            var item = new CatalogItem("17", "playlist", "Повторы", "", "12345", "17");
            var list = Control<ListView>(w, "TracksList");
            PlaylistDetails Shown() => SaField<PlaylistDetails>(w, "_activePlaylist");
            try
            {
                await Call(w, "OpenCatalogAsync", item, true);
                list.SelectedIndex = choice == "deleted" ? initialDuplicate : survivor;
                if (choice == "multi")
                {
                    list.SelectedItems.Add(Shown().Tracks.Single(row => row.Id == "other"));
                    var original = list.SelectedItems.OfType<TrackRow>().ToArray();
                    await Call(w, "RefreshCatalogViewAsync");
                    var refreshed = new[] { Shown().Tracks[survivor], Shown().Tracks.Single(row => row.Id == "other") };
                    Assert(list.SelectedItems.OfType<TrackRow>().SequenceEqual(refreshed) && !ReferenceEquals(original[0], refreshed[0]),
                        "an ordinary exact refresh lost part of extended selection or used an old row object");
                }
                SaClick(Control<Button>(w, "FavoritesNav"));
                await Call(w, "OpenCatalogAsync", item, true);
                var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
                long? playback = null;
                if (goingBack || choice == "reveal") await PlayOther();
                list.SelectedIndex = remove ? initialDuplicate : survivor;
                if (!remove && choice == "multi") list.SelectedItems.Add(Shown().Tracks.Single(row => row.Id == "other"));
                Task? writing = null;
                if (remove) SaClick(Control<Button>(w, "RemovePlaylistTrackButton"));
                else writing = Call(w, "InsertTrackIntoPlaylistAsync", item, new LikedId("repeat", "1"));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (goingBack)
                {
                    var keepsSelection = choice is "history" or "live" or "early-live" or "early-replace";
                    if (replaceChoice)
                        list.SelectedItem = Shown().Tracks[survivor];
                    else
                    {
                        if (keepsSelection) list.SelectedItems.Add(Shown().Tracks[survivor]);
                        Assert(list.SelectedItems.Count == (keepsSelection ? 2 : 1)
                            && list.SelectedItems.Contains(Shown().Tracks[initialDuplicate]), "the saved selection did not include the removed occurrence");
                    }
                    if (refreshBeforeConfirmation)
                    {
                        await Call(w, "RefreshCatalogViewAsync");
                        var selectedBeforeClear = keepsSelection || replaceChoice;
                        Assert(Shown().Revision == 2 && list.SelectedItems.Count == (selectedBeforeClear ? 1 : 0)
                            && ReferenceEquals(list.SelectedItem, selectedBeforeClear ? Shown().Tracks[1] : null),
                            "the early read exposed a guessed playing or deleted row selection");
                        if (choice == "early-clear") list.UnselectAll();
                    }
                    if (!applyInLiveView) SaClick(Control<Button>(w, "FavoritesNav"));
                    release.TrySetResult();
                    await Until(() => !SaField<bool>(w, "_catalogWriting"));
                    if (applyInLiveView)
                    {
                        AssertReconciledSelection("publication");
                        await Call(w, "RefreshCatalogViewAsync");
                        AssertReconciledSelection("passive refresh");
                        SaClick(Control<Button>(w, "FavoritesNav"));
                    }
                    for (var visit = 0; visit < 3; visit++)
                    {
                        if (visit > 0) NavMouse(w, MouseButton.XButton2);
                        NavMouse(w, MouseButton.XButton1);
                        AssertReconciledSelection($"Back visit {visit + 1}");
                    }
                    SaClick(Control<Button>(w, "PlaybackContextButton"));
                    Assert(list.SelectedItems.Count == 1 && ReferenceEquals(list.SelectedItem, Shown().Tracks[0])
                        && queue.CurrentOccurrenceToken == playback, "explicit reveal did not follow the same live occurrence after Back");
                    continue;

                    void AssertReconciledSelection(string transition)
                    {
                        Assert(writes == 1 && Shown().Revision == 2 && Shown().Tracks.Select(row => row.Id).SequenceEqual(order)
                            && list.SelectedItems.Count == (keepsSelection ? 1 : 0)
                            && ReferenceEquals(list.SelectedItem, keepsSelection ? Shown().Tracks[1] : null)
                            && w.QueuePlayingIndex == 0 && queue.CurrentOccurrenceToken == playback && f.Player.HasSource,
                            $"{transition} replaced the reconciled selection with the current playback row");
                    }
                }
                if (remove && choice != "deleted")
                {
                    list.SelectedIndex = survivor;
                    if (choice == "multi") list.SelectedItems.Add(Shown().Tracks.Single(row => row.Id == "other"));
                }
                SaClick(Control<Button>(w, "RefreshButton"));
                await Until(() => Shown().Revision == revision && SaField<CancellationTokenSource?>(w, "_featureCts") is null);
                Assert(choice == "multi" ? list.SelectedItems.OfType<TrackRow>().Select(row => row.Id).SequenceEqual(["other"])
                    : list.SelectedItem is null, "the independent successor refresh displayed an unconfirmed selection or hid a unique choice");
                if (choice is "new" or "clear") list.SelectedItem = Shown().Tracks.Single(row => row.Id == "other");
                if (choice == "clear") list.UnselectAll();
                if (choice == "play") await PlayOther();
                if (choice == "reveal")
                {
                    SaClick(Control<Button>(w, "PlaybackContextButton"));
                    Assert(ReferenceEquals(list.SelectedItem, Shown().Tracks[0]) && list.SelectedItems.Count == 1,
                        "the explicit source reveal did not select the playing occurrence");
                }
                release.TrySetResult();
                if (writing is not null) await writing;
                else await Until(() => !SaField<bool>(w, "_catalogWriting"));
                if (usesPlayback)
                {
                    Assert(writes == 1 && Shown().Revision == 2 && queue.CurrentOccurrenceToken == playback
                        && playback is not null && queue.Current is { Id: "other" }
                        && list.SelectedItems.Count == 1 && ReferenceEquals(list.SelectedItem, Shown().Tracks.Single(row => row.Id == "other")),
                        "the late confirmation overrode or extended the explicit playback-source selection");
                    continue;
                }
                AssertSelection(saved: false);
                NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton1);
                AssertSelection(saved: true);
                NavMouse(w, MouseButton.XButton2); NavMouse(w, MouseButton.XButton2);
                AssertSelection(saved: false);

                async Task PlayOther()
                {
                    if (choice != "play") list.SelectedItem = Shown().Tracks.Single(row => row.Id == "other");
                    SaClick(Control<Button>(w, "PlaySelectionButton"));
                    await Until(() => f.Player.HasSource && queue.Current is { Id: "other" } && !SaField<bool>(w, "_loadingStream"));
                    playback = queue.CurrentOccurrenceToken;
                }
            }
            finally { release.TrySetResult(); }

            void AssertSelection(bool saved)
            {
                var shown = Shown();
                var expected = choice is "deleted" or "newer" || !saved && choice == "clear" ? null
                    : !saved && choice == "new" ? shown.Tracks.Single(row => row.Id == "other") : shown.Tracks[remove ? 0 : 1];
                TrackRow[] expectedSelection = expected is null ? [] : choice == "multi"
                    ? [expected, shown.Tracks.Single(row => row.Id == "other")] : [expected];
                Assert(writes == 1 && shown.Revision == revision && shown.Tracks.Select(row => row.Id).SequenceEqual(order)
                    && list.SelectedItems.OfType<TrackRow>().SequenceEqual(expectedSelection) && !f.Player.HasSource,
                    "the exact successor lost the surviving choice, restored a deleted choice, or overrode explicit input");
            }
        }
    }

    private static async Task NavVersionedPlaylistCards()
    {
        using var f = new SaFixture("playlist-card-revision"); await f.Activate(); var w = f.Window;
        const string path = "/users/12345/playlists/17";
        var detailTitle = "Исходное имя";
        var detailRevision = 1;
        var listTitle = detailTitle;
        int? listRevision = detailRevision;
        var detailReads = 0;
        var writes = 0;
        HttpResponseMessage Detail()
        {
            return Json(JsonSerializer.Serialize(new { result = new
            {
                owner = new { uid = "12345" }, kind = "17", title = detailTitle, revision = detailRevision, trackCount = 0, tracks = Array.Empty<object>()
            } }));
        }
        f.RespondOverride = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath switch
        {
            "/users/12345/playlists/list" => Json(JsonSerializer.Serialize(new { result = new[]
                { new { kind = "17", owner = new { uid = "12345" }, title = listTitle, revision = listRevision } } })),
            path => CountedRead(),
            path + "/name" => Rename(),
            _ => null
        });
        HttpResponseMessage CountedRead() { detailReads++; return Detail(); }
        HttpResponseMessage Rename() { writes++; detailRevision++; detailTitle = "Локальное имя"; return Detail(); }
        var card = new CatalogItem("17", "playlist", detailTitle, "", "12345", "17");
        await Call(w, "OpenCatalogAsync", card, true);
        await Call(w, "RenamePlaylistFromCatalogAsync", SaField<PlaylistDetails>(w, "_activePlaylist"), "Локальное имя");
        var before = detailReads;

        listTitle = "Новое имя из коллекции"; listRevision = 3;
        await Call(w, "LoadCollectionAsync", "playlists");
        AssertCard("Новое имя из коллекции", 3);
        listRevision = 2; listTitle = "Старый ответ";
        await AssertChooser("Новое имя из коллекции");
        listRevision = 4; listTitle = "Новое имя из выбора";
        await AssertChooser("Новое имя из выбора");
        listRevision = null; listTitle = "Ответ без ревизии";
        await Call(w, "LoadCollectionAsync", "playlists");
        AssertCard("Новое имя из выбора", 4);
        await AssertChooser("Новое имя из выбора");
        Assert(writes == 1 && detailReads == before, "updated card headers depended on re-opening a detail or a second write");

        void AssertCard(string title, int revision)
        {
            var shown = Control<ListBox>(w, "CatalogList").Items.OfType<CatalogItem>().Single();
            Assert(shown.Title == title && shown.PlaylistRevision == revision && shown.PlaylistKind == "17", "a stale card displaced a newer exact header");
        }

        async Task AssertChooser(string title)
        {
            string? label = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                if (w.OwnedWindows.OfType<PlaylistDialog>().FirstOrDefault(candidate => candidate.IsVisible) is not { } dialog) return;
                var list = ((Grid)dialog.Content).Children.OfType<ListBox>().Single();
                list.UpdateLayout();
                if (list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item)
                    label = System.Windows.Automation.AutomationProperties.GetName(item);
                dialog.Close();
            };
            timer.Start();
            try { await Call(w, "AddTrackToPlaylistAsync", new TrackRow { Id = "fixture-track", RequestId = "fixture-track:1" }); }
            finally { timer.Stop(); }
            Assert(label == title, "chooser displayed an outdated verified playlist title");
        }
    }

    private static async Task NavPlaylistCardDetailHistory()
    {
        using var f = new SaFixture("playlist-card-first-detail"); await f.Activate(); var w = f.Window;
        const string path = "/users/12345/playlists/17";
        var title = "Исходный плейлист";
        var revision = 1;
        var detailReads = 0;
        string[] order = ["first", "second"];
        object Playlist() => new { kind = "17", owner = new { uid = "12345" }, title, revision,
            trackCount = order.Length, tracks = order.Select(id => new { id, albumId = "1" }).ToArray() };
        f.RespondOverride = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath switch
        {
            "/users/12345/playlists/list" => Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } })),
            path => Read(),
            path + "/name" => Rename(),
            _ => null
        });
        HttpResponseMessage Read() { detailReads++; return Json(JsonSerializer.Serialize(new { result = Playlist() })); }
        HttpResponseMessage Rename() { revision++; title = "Локальное имя"; return Json(JsonSerializer.Serialize(new { result = Playlist() })); }

        var item = new CatalogItem("17", "playlist", title, "", "12345", "17");
        await Call(w, "OpenCatalogAsync", item, true);
        await Call(w, "RenamePlaylistFromCatalogAsync", SaField<PlaylistDetails>(w, "_activePlaylist"), "Локальное имя");
        title = "Новое имя"; revision = 3; order = ["second", "first"];
        await Call(w, "LoadCollectionAsync", "playlists");
        var card = Control<ListBox>(w, "CatalogList").Items.OfType<CatalogItem>().Single();
        Assert(card is { PlaylistRevision: 3, Title: "Новое имя" }, "the test did not learn the header from the owned card first");
        await Call(w, "OpenCatalogAsync", card, true);
        AssertDetails(["second", "first"]);
        NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton1);
        AssertDetails(["second", "first"]);
        NavMouse(w, MouseButton.XButton2); NavMouse(w, MouseButton.XButton2);
        AssertDetails(["second", "first"]);
        order = ["alternate", "first"];
        await Call(w, "RefreshCatalogViewAsync");
        AssertDetails(["alternate", "first"]);
        var readsBeforeBack = detailReads;
        NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton1);
        AssertDetails(["second", "first"]);
        Assert(detailReads == readsBeforeBack, "history required re-reading a complete snapshot");

        void AssertDetails(string[] expected)
        {
            var shown = SaField<PlaylistDetails>(w, "_activePlaylist");
            Assert(shown is { Revision: 3, Title: "Новое имя" } && shown.Tracks.Select(row => row.Id).SequenceEqual(expected),
                "the first detail did not reconcile history or a later incompatible same-revision read overwrote it");
        }
    }

    private static async Task NavPlaylistDetailDuringWrite(string route, bool previousMutation = false)
    {
        using var f = new SaFixture($"playlist-detail-during-write-{route}-{previousMutation}"); var w = f.Window;
        var sourceReturn = route.StartsWith("source", StringComparison.Ordinal);
        var returnToPlaylist = sourceReturn || route.StartsWith("history", StringComparison.Ordinal);
        var detailFirst = route.EndsWith("-newer", StringComparison.Ordinal);
        var returnBeforeWrite = detailFirst || route.EndsWith("-before", StringComparison.Ordinal);
        var deselect = route == "source-deselect";
        var cancelReveal = deselect || route == "source-cancel";
        var longDetail = cancelReveal || route is "source-before" or "source-newer";
        var newerOrder = longDetail ? Enumerable.Range(1, 190).Select(index => $"new-{index}").Append("second").ToArray()
            : ["second", "external"];
        if (returnToPlaylist) NavSavePlaylistAudio(w, "second");
        await f.Activate();
        const string path = "/users/12345/playlists/17";
        var writeMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detailEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDetail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revision = 1;
        var title = "Исходный плейлист";
        var writtenRevision = previousMutation ? 3 : 2;
        var newerRevision = writtenRevision + 1;
        var holdDetail = false;
        var uid = "12345";
        string[] order = ["first", "second"];
        object Playlist() => new { kind = "17", owner = new { uid }, title, revision, trackCount = order.Length,
            tracks = order.Select(id => new { id, albumId = "1" }).ToArray() };
        f.RespondOverride = async (request, token) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/users/12345/playlists/list":
                    return Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } }));
                case path:
                    var response = Json(JsonSerializer.Serialize(new { result = Playlist() }));
                    if (holdDetail && detailEntered.TrySetResult()) await releaseDetail.Task;
                    return response;
                case path + "/name":
                    title = "Локальное имя";
                    revision = 2;
                    return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                case path + "/change":
                    revision = writtenRevision;
                    order = ["second"];
                    return Json(JsonSerializer.Serialize(new { result = new { revision } }));
                case "/tracks" when revision == writtenRevision && writeMetadata.TrySetResult():
                    await releaseWrite.Task.WaitAsync(token);
                    return Json("{}", System.Net.HttpStatusCode.ServiceUnavailable);
                case "/tracks" when returnToPlaylist:
                    var form = PlaylistWritesForm(await request.Content!.ReadAsStringAsync(token));
                    return Json(JsonSerializer.Serialize(new { result = form["track-ids"].Split(',').Select(id => id.Split(':')[0]).Distinct().Select(id => new
                        { id, title = id, available = true, durationMs = 20000, albums = new[] { new { id = "1" } } }) }));
                default: return null;
            }
        };

        var item = new CatalogItem("17", "playlist", title, "", "12345", "17");
        await Call(w, "OpenCatalogAsync", item, true);
        if (previousMutation)
            await Call(w, "RenamePlaylistFromCatalogAsync", SaField<PlaylistDetails>(w, "_activePlaylist"), "Локальное имя");
        var playlist = SaField<PlaylistDetails>(w, "_activePlaylist");
        Assert(playlist.Revision == writtenRevision - 1 && playlist.Tracks.Count == 2, "the write's predecessor was not shown");
        var list = Control<ListView>(w, "TracksList");
        var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
        if (returnToPlaylist)
        {
            list.SelectedIndex = 1;
            await Call(w, "PlaySelectedAsync");
            Assert(f.Player.HasSource && queue.Current?.Id == "second", "the original playing occurrence did not open");
        }
        var tokenBeforeWrite = queue.CurrentOccurrenceToken;
        list.SelectedIndex = 0;
        SaClick(Control<Button>(w, "RemovePlaylistTrackButton"));
        try
        {
            await writeMetadata.Task.WaitAsync(TimeSpan.FromSeconds(5));
            revision = route == "stale" ? 1 : newerRevision;
            title = route == "stale" ? playlist.Title : "Новое имя";
            order = route == "stale" ? ["first", "second"] : newerOrder;
            Task? reading = null;
            if (returnToPlaylist || route is "card" or "list")
            {
                await Call(w, "LoadCollectionAsync", "playlists");
                var card = ReadCard();
                if (route == "card")
                {
                    holdDetail = true;
                    reading = Call(w, "OpenCatalogAsync", card, true);
                }
            }
            else
            {
                holdDetail = true;
                reading = Call(w, "RefreshCatalogViewAsync");
            }
            if (reading is not null) await detailEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (route == "leave") SaClick(Control<Button>(w, "FavoritesNav"));
            if (returnBeforeWrite)
            {
                await EnterReturnedView();
                if (detailFirst)
                {
                    releaseDetail.TrySetResult();
                    await Until(() => SaField<CancellationTokenSource?>(w, "_featureCts") is null);
                    AssertDetails();
                }
            }
            releaseWrite.TrySetResult();
            await Until(() => !SaField<bool>(w, "_catalogWriting"));
            if (returnToPlaylist)
            {
                if (!returnBeforeWrite) { ReadCard(); await EnterReturnedView(); }
                if (!detailFirst)
                {
                    var intermediate = SaField<PlaylistDetails>(w, "_activePlaylist");
                    Assert(intermediate.Revision == writtenRevision && intermediate.Tracks.Select(row => row.Id).SequenceEqual(["second"])
                        && w.QueuePlayingIndex == 0 && queue.CurrentOccurrenceToken == tokenBeforeWrite,
                        "a return exposed the withdrawn row or lost the confirmed occurrence");
                    if (!returnBeforeWrite)
                        Assert(list.SelectedIndex == (sourceReturn ? 0 : -1), "the intermediate return used the wrong selection intent");
                    if (route == "history")
                    {
                        Assert(!Control<Button>(w, "RenamePlaylistButton").IsEnabled && !Control<Button>(w, "DeletePlaylistButton").IsEnabled,
                            "the intermediate view still offered revision-unconditioned writes");
                        await Call(w, "RenamePlaylistFromCatalogAsync", intermediate, "Устаревшая команда");
                        await Call(w, "DeletePlaylistFromCatalogAsync", intermediate);
                        Assert(f.Handler.Seen.All(request => request.Uri.AbsolutePath != path + "/name" && request.Uri.AbsolutePath != path + "/delete"),
                            "the stale UI submitted an unconditional write");
                    }
                    if (cancelReveal)
                    {
                        await Cr040Idle(w);
                        var viewer = Cr040Viewer(list);
                        viewer.ScrollToTop();
                        list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                            { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                        if (deselect)
                        {
                            Assert(list.SelectedIndex == 0 && list.SelectedItems.Count == 1, "the exact interim row was not revealed");
                            list.SelectedItems.Remove(list.SelectedItem);
                            Assert(list.SelectedItems.Count == 0, "the simulated native toggle did not clear the choice");
                        }
                    }
                }
                releaseDetail.TrySetResult();
                await Until(() => SaField<CancellationTokenSource?>(w, "_featureCts") is null);
                AssertDetails();
                var currentIndex = Array.IndexOf(newerOrder, "second");
                Assert(w.QueuePlayingIndex == currentIndex && queue.CurrentOccurrenceToken == tokenBeforeWrite && f.Player.HasSource
                    && (!sourceReturn || list.SelectedIndex == (deselect ? -1 : currentIndex)),
                    "the exact newer detail changed playback or discarded the return's selection");
                if (cancelReveal)
                {
                    await Cr040Idle(w);
                    Assert(Cr040Viewer(list).VerticalOffset < 1, "manual input did not revoke the second reveal");
                    if (deselect)
                    {
                        Assert(list.SelectedItems.Count == 0, "the later detail selected a row on the same token");
                        SaClick(Control<Button>(w, "PlaybackContextButton"));
                        Assert(list.SelectedIndex == currentIndex && queue.CurrentOccurrenceToken == tokenBeforeWrite,
                            "explicit source return did not reclaim the choice for the same occurrence");
                    }
                }
                else if (longDetail)
                {
                    await Until(() =>
                    {
                        w.UpdateLayout();
                        var viewer = Cr040Viewer(list);
                        return list.ItemContainerGenerator.ContainerFromIndex(currentIndex) is ListViewItem row
                            && row.TranslatePoint(new Point(0, row.ActualHeight), viewer).Y > 0
                            && row.TranslatePoint(new Point(), viewer).Y < viewer.ViewportHeight;
                    });
                }
                return;
            }
            if (route == "list")
            {
                await Call(w, "OpenCatalogAsync", ReadCard(), true);
                AssertDetails();
                return;
            }
            Assert(route == "leave" || SaField<CancellationTokenSource?>(w, "_featureCts") is { IsCancellationRequested: false },
                "the earlier publication canceled the live detail read");
            releaseDetail.TrySetResult();
            await reading!;
            if (route == "leave")
            {
                Assert(SaField<string>(w, "_section") == "favorites" && SaField<PlaylistDetails?>(w, "_activePlaylist") is null,
                    "a read reopened the playlist after the user left");
                return;
            }
            if (route == "stale")
            {
                var shown = SaField<PlaylistDetails>(w, "_activePlaylist");
                Assert(shown.Revision == writtenRevision && shown.Title == playlist.Title
                    && shown.Tracks.Select(row => row.Id).SequenceEqual(["second"]), "a stale response replaced the confirmed write");
                return;
            }
            AssertDetails();
            if (route != "card") return;
            NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton1);
            AssertDetails();
            NavMouse(w, MouseButton.XButton2); NavMouse(w, MouseButton.XButton2);
            AssertDetails();

            order = ["wrong"];
            revision = writtenRevision;
            await Call(w, "RefreshCatalogViewAsync"); AssertDetails();
            revision = newerRevision; title = "Несовпадающее имя";
            await Call(w, "RefreshCatalogViewAsync"); AssertDetails();
            title = "Новое имя"; uid = "999";
            await Call(w, "RefreshCatalogViewAsync"); AssertDetails();
        }
        finally { releaseWrite.TrySetResult(); releaseDetail.TrySetResult(); }

        async Task EnterReturnedView()
        {
            holdDetail = true;
            if (sourceReturn) SaClick(Control<Button>(w, "PlaybackContextButton"));
            else NavMouse(w, MouseButton.XButton1);
            await detailEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        CatalogItem ReadCard()
        {
            var card = Control<ListBox>(w, "CatalogList").Items.OfType<CatalogItem>().Single();
            Assert(card.PlaylistRevision == newerRevision && card.Title == "Новое имя", "the visible card lost its newer versioned header");
            return card;
        }

        void AssertDetails()
        {
            var shown = SaField<PlaylistDetails?>(w, "_activePlaylist");
            Assert(shown is { Owner: "12345", Kind: "17", Title: "Новое имя" } && shown.Revision == newerRevision
                && shown.Tracks.Select(row => row.Id).SequenceEqual(newerOrder),
                "a late write or incompatible read superseded the newer exact detail");
        }
    }

    private static async Task NavDeferredChoiceAfterPlaybackMove(bool move, bool forward = false)
    {
        using var f = new SaFixture($"playlist-deferred-choice-after-token-{move}-{forward}"); var w = f.Window;
        NavSavePlaylistAudio(w, "repeat");
        var nextPath = NavSavePlaylistAudio(w, "unique", append: true);
        await f.Activate();
        const string path = "/users/12345/playlists/17";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revision = 1;
        var writes = 0;
        string[] order = ["repeat", "unique", "repeat"];
        object Playlist() => new { kind = "17", owner = new { uid = "12345" }, title = "Два дубля", revision,
            trackCount = order.Length, tracks = order.Select(id => new { id, albumId = "1" }) };
        f.RespondOverride = async (request, _) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/users/12345/playlists/list": return Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } }));
                case path: return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                case path + "/change":
                    writes++; revision = 2; order = ["unique", "repeat"];
                    return Json("""{"result":{"revision":2}}""");
                case "/tracks" when writes > 0 && entered.TrySetResult():
                    await release.Task;
                    return Json("{}", System.Net.HttpStatusCode.ServiceUnavailable);
                case "/tracks": return Json(JsonSerializer.Serialize(new { result = order.Distinct().Select(id => new
                    { id, title = id, available = true, durationMs = 20000, albums = new[] { new { id = "1" } } }) }));
                default: return null;
            }
        };
        SaClick(Control<Button>(w, "FavoritesNav"));
        await Call(w, "OpenCatalogAsync", new CatalogItem("17", "playlist", "Два дубля", "", "12345", "17"), true);
        var list = Control<ListView>(w, "TracksList");
        var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
        list.SelectedIndex = 0;
        await Call(w, "PlaySelectedAsync");
        var original = queue.CurrentOccurrenceToken;
        var next = queue.OccurrenceTokenAt(1);
        try
        {
            SaClick(Control<Button>(w, "RemovePlaylistTrackButton"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            list.SelectedIndex = 2;
            await Call(w, "RefreshCatalogViewAsync");
            Assert(list.Items.Count == 2 && list.SelectedIndex == -1 && queue.CurrentOccurrenceToken == original,
                "an unproven swap displayed a guessed duplicate");
            if (forward) NavMouse(w, MouseButton.XButton1);
            else SaClick(Control<Button>(w, "FavoritesNav"));
            if (move)
            {
                await Call(w, "AdvanceAsync", 1, false);
                await Call(w, "TogglePlaybackAsync");
            }
            var position = f.Player.Position;
            var enter = forward ? MouseButton.XButton2 : MouseButton.XButton1;
            var leave = forward ? MouseButton.XButton1 : MouseButton.XButton2;
            NavMouse(w, enter); AssertChoice(confirmed: false);
            NavMouse(w, leave); NavMouse(w, enter); AssertChoice(confirmed: false);
            release.TrySetResult();
            await Until(() => !SaField<bool>(w, "_catalogWriting"));
            AssertChoice(confirmed: true);
            NavMouse(w, leave); NavMouse(w, enter); AssertChoice(confirmed: true);

            void AssertChoice(bool confirmed)
            {
                Assert(writes == 1 && f.Player.HasSource && queue.CurrentOccurrenceToken == (move ? next : original),
                    "the read or confirmation changed the existing playback occurrence");
                if (move)
                    Assert(list.SelectedIndex == 0 && list.SelectedItem is TrackRow { Id: "unique" }
                        && w.QueuePlayingIndex == 0 && f.Player.SourceLocalPath == nextPath && !f.Player.IsPlaying
                        && Math.Abs((f.Player.Position - position).TotalSeconds) < .4, "the old candidate replaced the proven new playing occurrence");
                else
                    Assert(list.SelectedIndex == (confirmed ? 1 : -1) && (confirmed ? list.SelectedItem is TrackRow { Id: "repeat" } : list.SelectedItem is null)
                        && w.QueuePlayingIndex == -1, "same-token reconciliation guessed or lost the surviving duplicate");
            }
        }
        finally { release.TrySetResult(); }
    }

    private static async Task NavLatePlaylistWriteAdmission()
    {
        foreach (var remove in new[] { false, true })
        foreach (var newer in new[] { false, true })
        {
            using var f = new SaFixture($"playlist-post-admission-{remove}-{newer}"); await f.Activate(); var w = f.Window;
            const string path = "/users/12345/playlists/17";
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var revision = 2;
            var cardRevision = 2;
            var title = "Точный список";
            var exists = true;
            var reads = 0;
            var writes = 0;
            object Header(int number, string name) => new { owner = new { uid = "12345" }, kind = "17", title = name,
                revision = number, trackCount = 0, tracks = Array.Empty<object>() };
            f.RespondOverride = async (request, token) =>
            {
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/users/12345/playlists/list":
                        return Json(JsonSerializer.Serialize(new { result = exists
                            ? new[] { Header(cardRevision, cardRevision > revision ? "Из другого окна" : title) } : Array.Empty<object>() }));
                    case path:
                        var response = Json(JsonSerializer.Serialize(new { result = Header(revision, title) }));
                        if (++reads == 2)
                        {
                            requested.TrySetResult();
                            await release.Task.WaitAsync(token);
                        }
                        return response;
                    case path + "/name":
                        writes++; cardRevision = ++revision; title = "Изменённое название";
                        return Json(JsonSerializer.Serialize(new { result = Header(revision, title) }));
                    case path + "/delete":
                        writes++; exists = false;
                        return Json("""{"result":"ok"}""");
                    default: return null;
                }
            };
            await Call(w, "OpenCatalogAsync", new CatalogItem("17", "playlist", title, "", "12345", "17"), true);
            var playlist = SaField<PlaylistDetails>(w, "_activePlaylist");
            var writing = remove ? Call(w, "DeletePlaylistFromCatalogAsync", playlist)
                : Call(w, "RenamePlaylistFromCatalogAsync", playlist, "Изменённое название");
            try
            {
                await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (newer) cardRevision = 3;
                await Call(w, "LoadCollectionAsync", "playlists");
                Assert(Control<ListBox>(w, "CatalogList").Items.OfType<CatalogItem>().Single().PlaylistRevision == cardRevision,
                    "the independent catalog read did not publish the observed revision");
                release.TrySetResult();
                await writing;
                Assert(writes == (newer ? 0 : 1) && (newer || !remove ? exists : !exists),
                    "final admission ignored a newer observed card or canceled an unchanged verified write");
            }
            finally { release.TrySetResult(); }
        }
    }

    private static async Task NavChooserUpdatesWriteAvailability()
    {
        foreach (var same in new[] { false, true })
        {
            using var f = new SaFixture($"playlist-write-availability-from-chooser-{same}"); await f.Activate(); var w = f.Window;
            const string path = "/users/12345/playlists/17";
            var revision = 2;
            object Header(string kind, int number) => new { owner = new { uid = "12345" }, kind, title = "Точный список",
                revision = number, trackCount = 1, tracks = new[] { new { id = "track", albumId = "1" } } };
            f.RespondOverride = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath switch
            {
                "/users/12345/playlists/list" => Json(JsonSerializer.Serialize(new { result = new[] { Header(same ? "17" : "18", 3) } })),
                path => Json(JsonSerializer.Serialize(new { result = Header("17", revision) })),
                _ => null
            });
            await Call(w, "OpenCatalogAsync", new CatalogItem("17", "playlist", "Точный список", "", "12345", "17"), true);
            var playlist = SaField<PlaylistDetails>(w, "_activePlaylist");
            var sawChooser = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                if (w.OwnedWindows.OfType<PlaylistDialog>().FirstOrDefault(candidate => candidate.IsVisible) is not { } chooser) return;
                sawChooser = true;
                chooser.Close();
            };
            timer.Start();
            try { await Call(w, "AddTrackToPlaylistAsync", playlist.Tracks[0]); }
            finally { timer.Stop(); }
            Assert(sawChooser && ReferenceEquals(playlist, SaField<PlaylistDetails>(w, "_activePlaylist")) && ButtonsEnabled() == !same,
                "canceling the chooser did not retain the exact shown playlist and earlier freshness observation");
            if (!same) continue;
            revision = 3;
            await Call(w, "RefreshCatalogViewAsync");
            Assert(ButtonsEnabled(), "the exact newer detail did not re-enable owner writes");

            bool ButtonsEnabled() => new[] { "RenamePlaylistButton", "DeletePlaylistButton", "RemovePlaylistTrackButton" }
                .All(name => Control<Button>(w, name).IsEnabled);
        }
    }

    private static async Task NavFirstInsertWithNewerCard()
    {
        foreach (var listFirst in new[] { true, false })
        {
            using var f = new SaFixture($"playlist-first-insert-newer-card-{listFirst}"); await f.Activate(); var w = f.Window;
            const string path = "/users/12345/playlists/17";
            const string firstTitle = "Приватный исходный список";
            const string newerTitle = "Новое имя приватного списка";
            var writeMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var listRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var revision = 1;
            int? listRevision = 1;
            var listTitle = firstTitle;
            var writes = 0;
            var detailReads = 0;
            f.RespondOverride = async (request, token) =>
            {
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/users/12345/playlists/list":
                        var cards = new[] { new { kind = "17", owner = new { uid = "12345" }, title = listTitle, revision = listRevision } };
                        if (listRevision == 3 && !listFirst && listRequested.TrySetResult()) await releaseList.Task.WaitAsync(token);
                        return Json(JsonSerializer.Serialize(new { result = cards }));
                    case path:
                        detailReads++;
                        string[] ids = revision == 1 ? ["first"] : ["first", "inserted"];
                        return Json(JsonSerializer.Serialize(new { result = new
                        {
                            kind = "17", owner = new { uid = "12345" }, title = revision == 3 ? newerTitle : firstTitle, revision,
                            visibility = "private", trackCount = ids.Length, tracks = ids.Select(id => new { id, albumId = "1" })
                        } }));
                    case path + "/change":
                        var form = PlaylistWritesForm(await request.Content!.ReadAsStringAsync(token));
                        using (var json = JsonDocument.Parse(form["diff"]))
                            Assert(form["revision"] == "1" && json.RootElement[0].GetProperty("at").GetInt32() == 1,
                                "first insertion did not use the authoritative raw predecessor");
                        writes++; revision = 2;
                        return Json("""{"result":{"revision":2}}""");
                    case "/tracks" when revision == 2 && writeMetadata.TrySetResult():
                        await releaseWrite.Task.WaitAsync(token);
                        return Json("{}", System.Net.HttpStatusCode.ServiceUnavailable);
                    default: return null;
                }
            };

            var item = new CatalogItem("17", "playlist", firstTitle, "", "12345", "17");
            var writing = Call(w, "InsertTrackIntoPlaylistAsync", item, new LikedId("inserted", "1"));
            try
            {
                await writeMetadata.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(detailReads == 3 && SaField<PlaylistDetails?>(w, "_activePlaylist") is null,
                    "fixture opened the previously unvisited playlist instead of using raw edit reads");
                revision = 3;
                listRevision = revision;
                listTitle = newerTitle;
                var listing = Call(w, "LoadCollectionAsync", "playlists");
                if (listFirst)
                {
                    await listing;
                    AssertCard();
                    releaseWrite.TrySetResult();
                    await writing;
                }
                else
                {
                    await listRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    releaseWrite.TrySetResult();
                    await writing;
                    releaseList.TrySetResult();
                    await listing;
                }
                var shown = AssertCard();
                Assert(writes == 1 && detailReads == 3, "card reconciliation hid a retry or relied on detail IO");

                foreach (var stale in new int?[] { 2, 3, null })
                {
                    listRevision = stale;
                    listTitle = "Неподтверждённое имя";
                    await Call(w, "LoadCollectionAsync", "playlists");
                    AssertCard();
                }
                await Call(w, "OpenCatalogAsync", shown, true);
                Assert(SaField<PlaylistDetails?>(w, "_activePlaylist") is { Owner: "12345", Kind: "17", Revision: 3, Title: newerTitle } details
                    && details.Tracks.Select(row => row.Id).SequenceEqual(["first", "inserted"]) && detailReads == 4,
                    "the first complete detail at the known card revision was not accepted");
            }
            finally { releaseWrite.TrySetResult(); releaseList.TrySetResult(); }

            CatalogItem AssertCard()
            {
                var card = Control<ListBox>(w, "CatalogList").Items.OfType<CatalogItem>().Single();
                Assert(card is { Owner: "12345", PlaylistKind: "17", PlaylistRevision: 3, Title: newerTitle },
                    "an older completed write displaced the newer card or blocked its admission");
                return card;
            }
        }
    }

    private static async Task NavUnopenedVersionedCardHistory()
    {
        using var f = new SaFixture("unopened-versioned-card-history"); await f.Activate(); var w = f.Window;
        const string path = "/users/12345/playlists/17";
        var detailReads = 0;
        var revision = 1;
        var title = "Старое название";
        int? cardRevision = revision;
        var cardTitle = title;
        f.RespondOverride = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath switch
        {
            "/users/12345/playlists/list" => Json(JsonSerializer.Serialize(new { result = new[]
                { new { kind = "17", owner = new { uid = "12345" }, title = cardTitle, revision = cardRevision } } })),
            path => Read(),
            _ => null
        });
        await Call(w, "LoadCollectionAsync", "playlists");
        AssertCard();
        SaClick(Control<Button>(w, "FavoritesNav"));
        cardRevision = revision = 3;
        cardTitle = title = "Новое название";
        await Call(w, "LoadCollectionAsync", "playlists");
        AssertCard();
        NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton1);
        AssertCard();
        foreach (var stale in new int?[] { 1, 3, null })
        {
            cardRevision = stale; cardTitle = "Неподтверждённое название";
            await Call(w, "LoadCollectionAsync", "playlists");
            AssertCard();
        }
        Assert(detailReads == 0, "card revision protection depended on opening a detail");
        var list = Control<ListBox>(w, "CatalogList");
        await Call(w, "OpenCatalogAsync", list.Items.OfType<CatalogItem>().Single(), true);
        Assert(SaField<PlaylistDetails>(w, "_activePlaylist") is { Revision: 3, Title: "Новое название", Tracks.Count: 1 },
            "the first complete detail for the observed header did not hydrate the playlist");
        cardRevision = revision = 4;
        cardTitle = title = "Следующее название";
        await Call(w, "LoadCollectionAsync", "playlists");
        AssertCard();

        void AssertCard()
        {
            var card = Control<ListBox>(w, "CatalogList").Items.OfType<CatalogItem>().Single();
            Assert(card.Owner == "12345" && card.PlaylistKind == "17" && card.Title == title && card.PlaylistRevision == revision,
                "history or an opaque or older response replaced a proven card");
        }
        HttpResponseMessage Read()
        {
            detailReads++;
            return Json(JsonSerializer.Serialize(new { result = new { kind = "17", owner = new { uid = "12345" }, title, revision,
                trackCount = 1, tracks = new[] { new { id = "track", albumId = "1" } } } }));
        }
    }

    private static async Task NavPlaylistSelectionDuringAudioOpen(bool leaveBeforeAdvance = false, bool keepOther = false)
    {
        using var f = new SaFixture($"playlist-selection-held-audio-{leaveBeforeAdvance}-{keepOther}"); var w = f.Window;
        var media = NavSavePlaylistAudio(w, "playing", bitrate: 128);
        await f.Activate();
        const string path = "/users/12345/playlists/17";
        var openEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revision = 1;
        string[] order = keepOther ? ["remove", "playing", "playing", "keep"] : ["remove", "playing", "playing"];
        object Playlist() => new { kind = "17", owner = new { uid = "12345" }, title = "Подборка", revision,
            trackCount = order.Length, tracks = order.Select(id => new { id, albumId = "1" }) };
        f.RespondOverride = async (request, _) =>
        {
            switch (Uri.UnescapeDataString(request.RequestUri!.AbsolutePath))
            {
                case "/users/12345/playlists/list": return Json(JsonSerializer.Serialize(new { result = new[] { Playlist() } }));
                case path: return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                case path + "/change":
                    revision = 2; order = order[1..];
                    return Json("""{"result":{"revision":2}}""");
                case "/tracks":
                    return Json(JsonSerializer.Serialize(new { result = order.Distinct().Select(id => new
                        { id, title = id, available = true, durationMs = 20000, albums = new[] { new { id = "1" } } }) }));
                case "/get-file-info":
                    if (openEntered.TrySetResult()) await releaseOpen.Task;
                    return Json("{}", System.Net.HttpStatusCode.ServiceUnavailable);
                case "/tracks/playing:1/download-info" or "/tracks/keep:1/download-info":
                    return Json("{}", System.Net.HttpStatusCode.ServiceUnavailable);
                default: return null;
            }
        };
        await Call(w, "OpenCatalogAsync", new CatalogItem("17", "playlist", "Подборка", "", "12345", "17"), true);
        var list = Control<ListView>(w, "TracksList");
        var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
        list.SelectedIndex = 1;
        var opening = Call(w, "PlaySelectedAsync");
        try
        {
            await openEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var token = queue.CurrentOccurrenceToken;
            Assert(token is not null && SaField<bool>(w, "_loadingStream") && !f.Player.HasSource,
                "fixture did not suspend the actual opening of the current occurrence");
            list.SelectedIndex = 0;
            if (keepOther) list.SelectedItems.Add(list.Items[list.Items.Count - 1]);
            Assert(list.SelectedItem is TrackRow { Id: "remove" }, "the fixture did not target the selected row for removal");
            SaClick(Control<Button>(w, "RemovePlaylistTrackButton"));
            await Until(() => !SaField<bool>(w, "_catalogWriting"));
            Assert(list.Items.Count == order.Length && KeepsChoice(), "exact removal did not reconcile the selected rows");
            releaseOpen.TrySetResult();
            await opening;
            Assert(f.Player.HasSource && f.Player.SourceLocalPath == media, "the refused quality upgrade did not open the verified local file");
            Assert(w.QueuePlayingIndex == 0 && queue.CurrentOccurrenceToken == token, "the open lost the exact playing occurrence");
            Assert(KeepsChoice(), "completion of the already selected token overwrote the reconciled choice");

            queue.Repeat = RepeatMode.One;
            await Call(w, "AdvanceAsync", 1, true);
            Assert(f.Player.HasSource && f.Player.SourceLocalPath == media && w.QueuePlayingIndex == 0 && queue.CurrentOccurrenceToken == token
                && KeepsChoice(), "repeat of the same token overwrote the reconciled choice");

            queue.Repeat = RepeatMode.Off;
            var next = queue.OccurrenceTokenAt(2);
            if (leaveBeforeAdvance)
            {
                SaClick(Control<Button>(w, "FavoritesNav"));
                NavMouse(w, MouseButton.XButton1);
                Assert(KeepsChoice() && queue.CurrentOccurrenceToken == token, "Back lost the choice without a playback transition");
                NavMouse(w, MouseButton.XButton2);
            }
            await Call(w, "AdvanceAsync", 1, false);
            if (leaveBeforeAdvance)
            {
                Assert(SaField<string>(w, "_section") == "favorites" && queue.CurrentOccurrenceToken == next,
                    "the other occurrence did not commit while browsing another view");
                NavMouse(w, MouseButton.XButton1);
            }
            AssertFollowsNext();
            if (leaveBeforeAdvance)
            {
                NavMouse(w, MouseButton.XButton2); NavMouse(w, MouseButton.XButton1);
                AssertFollowsNext();
            }

            bool KeepsChoice() => keepOther ? list.SelectedItems.Count == 1 && list.SelectedItem is TrackRow { Id: "keep" }
                : list.SelectedItems.Count == 0;
            void AssertFollowsNext()
            {
                Assert(f.Player.HasSource && f.Player.SourceLocalPath == media && queue.CurrentOccurrenceToken == next && w.QueuePlayingIndex == 1
                    && list.SelectedIndex == 1 && list.SelectedItems.Count == 1, "a different occurrence stopped following playback");
            }
        }
        finally { releaseOpen.TrySetResult(); }
    }

    private static async Task NavPlaylistCardSelectionHistory()
    {
        using var f = new SaFixture("playlist-card-history-selection"); await f.Activate(); var w = f.Window;
        const string prefix = "/users/12345/playlists/";
        var revision = 1;
        var title = "Альфа";
        object Owned(string kind, string name, int currentRevision) => new
            { kind, title = name, owner = new { uid = "12345" }, revision = currentRevision, trackCount = 0, tracks = Array.Empty<object>() };
        f.RespondOverride = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath switch
        {
            prefix + "17" => Json(JsonSerializer.Serialize(new { result = Owned("17", title, revision) })),
            prefix + "18" => Json(JsonSerializer.Serialize(new { result = Owned("18", "Бета", 1) })),
            prefix + "17/name" => Rename(),
            prefix + "list" => Json(JsonSerializer.Serialize(new { result = new[] { Owned("17", title, revision), Owned("18", "Бета", 1) } })),
            _ => null
        });
        HttpResponseMessage Rename()
        {
            title = "Альфа 2";
            revision = 2;
            return Json(JsonSerializer.Serialize(new { result = Owned("17", title, revision) }));
        }
        var card = new CatalogItem("17", "playlist", title, "", "12345", "17");
        await Call(w, "OpenCatalogAsync", card, true);
        await Call(w, "RenamePlaylistFromCatalogAsync", SaField<PlaylistDetails>(w, "_activePlaylist"), "Альфа 2");
        await Call(w, "LoadCollectionAsync", "playlists");
        SaInvoke(w, "ChangeListSort", new ListSortRule(ListSortField.Title));
        var list = Control<ListBox>(w, "CatalogList");
        list.SelectedIndex = 0;
        Assert(list.SelectedItem is CatalogItem { PlaylistKind: "17" }, "the initial sort did not put the target first");
        SaClick(Control<Button>(w, "FavoritesNav"));
        title = "Янтарь"; revision = 3;
        await Call(w, "LoadCollectionAsync", "playlists");
        NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton1);
        AssertCard();
        NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton2);
        AssertCard();
        await Call(w, "OpenSelectedCatalogAsync");
        Assert(SaField<PlaylistDetails>(w, "_activePlaylist") is { Kind: "17", Revision: 3 },
            "opening after restoring selection targeted the neighbouring card");

        void AssertCard() => Assert(list.SelectedIndex == 1
            && list.SelectedItem is CatalogItem { Owner: "12345", PlaylistKind: "17", PlaylistRevision: 3, Title: "Янтарь" },
            "history selected the old index instead of the exact reordered playlist");
    }

    private static async Task NavPlaylistHistoryEviction()
    {
        using var f = new SaFixture("playlist-history-eviction"); await f.Activate(); var w = f.Window;
        const string prefix = "/users/12345/playlists/";
        var stale = false;
        var deleted = false;
        var renamed = false;
        var allowSparseMetadata = true;
        object Playlist(string kind, int revision) => new { kind, owner = new { uid = "12345" },
            title = kind == "17" && renamed ? "Обновлённый плейлист" : $"Плейлист {kind}", revision,
            trackCount = 1, tracks = new[] { new { id = kind == "17" ? "track-17-2" : $"track-{kind}-{revision}", albumId = "1" } } };
        f.RespondOverride = (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/tracks" && renamed && allowSparseMetadata)
                return Task.FromResult<HttpResponseMessage?>(Json("{}", System.Net.HttpStatusCode.ServiceUnavailable));
            if (path == prefix + "list")
                return Task.FromResult<HttpResponseMessage?>(Json(JsonSerializer.Serialize(new { result = !deleted || stale
                    ? new[] { Playlist("17", renamed ? 2 : 1), Playlist("18", 1) } : new[] { Playlist("17", 2) } })));
            if (path == prefix + "17/name")
            {
                renamed = true;
                return Task.FromResult<HttpResponseMessage?>(Json(JsonSerializer.Serialize(new { result = Playlist("17", 2) })));
            }
            if (path == prefix + "18/delete")
            {
                deleted = true;
                return Task.FromResult<HttpResponseMessage?>(Json("""{"result":"ok"}"""));
            }
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) return Task.FromResult<HttpResponseMessage?>(null);
            var kind = path[prefix.Length..];
            return Task.FromResult<HttpResponseMessage?>(Json(JsonSerializer.Serialize(new { result = Playlist(kind, kind == "17" && renamed && !stale ? 2 : 1) })));
        };
        Task Open(string kind) => Call(w, "OpenCatalogAsync", new CatalogItem(kind, "playlist", $"Плейлист {kind}", "", "12345", kind), true);
        await Open("17");
        await Call(w, "RenamePlaylistFromCatalogAsync", SaField<PlaylistDetails>(w, "_activePlaylist"), "Обновлённый плейлист");
        Assert(SaField<PlaylistDetails>(w, "_activePlaylist").InheritedMetadataPositions?.SetEquals([0]) == true,
            "the live playlist did not acquire inherited metadata before eviction");
        allowSparseMetadata = false;
        await Open("18");
        await Call(w, "DeletePlaylistFromCatalogAsync", SaField<PlaylistDetails>(w, "_activePlaylist"));
        await Until(() => SaField<CancellationTokenSource?>(w, "_featureCts") is null);
        for (var i = 0; i < 30; i++) await Open($"other-{i}");

        var cache = SaField<System.Collections.IDictionary>(w, "_playlistCatalogState");
        Assert(cache.Count >= 32 && NavHistoryCount(w, "_browseHistory") == 24 && cache.Values.Cast<object>().All(state =>
            state.GetType().GetProperty("Header")?.GetValue(state) is PlaylistDetails { Tracks.Count: 0, InheritedMetadataPositions: null }),
            "the account-wide playlist headers retained rows or their provenance beyond the bounded history");
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

    private static async Task NavPlaylistAddSession(string selectedKind)
    {
        using var f = new SaFixture("playlist-chooser-session-" + selectedKind); await f.Activate(); var w = f.Window;
        var path = "/users/12345/playlists/" + selectedKind;
        var selectedTitle = selectedKind == "17" ? "Плейлист" : "Плейлист · №17";
        var newerSession = FakeSession with { AccessToken = "fixture-new-access" };
        var firstDetail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revision = 1;
        var detailReads = 0;
        object Playlist() => new { kind = selectedKind, owner = new { uid = "12345" }, title = selectedTitle, revision, visibility = "private",
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
                    return Json(JsonSerializer.Serialize(new { result = new object[]
                    {
                        new { kind = "18", owner = new { uid = "12345" }, title = "Плейлист", description = "№17" },
                        selectedKind == "17" ? Playlist() : new { kind = "17", owner = new { uid = "12345" }, title = "Плейлист" },
                        selectedKind == "19" ? Playlist() : new { kind = "19", owner = new { uid = "12345" }, title = "Плейлист · №17" }
                    } }));
                case var detailPath when detailPath == path:
                    if (++detailReads == 1)
                    {
                        firstDetail.TrySetResult();
                        await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    return Json(JsonSerializer.Serialize(new { result = Playlist() }));
                case var changePath when changePath == path + "/change":
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
                list.UpdateLayout();
                var labels = Enumerable.Range(0, list.Items.Count).Select(index =>
                {
                    var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index)!;
                    var label = SaVisuals<TextBlock>(row).First().Text;
                    Assert(System.Windows.Automation.AutomationProperties.GetName(row) == label, "the chooser's visible and accessible target names differ");
                    return label;
                }).ToArray();
                Assert(labels.SequenceEqual(["№18 · Плейлист · №17", "№17 · Плейлист", "№19 · Плейлист · №17"]),
                    "identically named playlist targets were not distinguishable before confirmation");
                UlSet(w, "_session", newerSession);
                list.SelectedIndex = selectedKind == "17" ? 1 : 2;
                AccessKeyManager.ProcessKey(source, "\r", false);
                Assert(!dialog.IsVisible && dialog.Selected?.Id == selectedKind, "native Enter did not confirm the chosen playlist");
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
            var selected = new CatalogItem(selectedKind, "playlist", selectedTitle, "", "12345", selectedKind);
            Assert(SaField<bool>(w, "_catalogWriting"), "playlist detail read was outside the reserved write slot");
            await Call(w, "InsertTrackIntoPlaylistAsync", selected, new LikedId("second", "1"));
            Assert(Control<TextBlock>(w, "StatusText").Text.Contains("другого изменения", StringComparison.Ordinal), "busy write was silently discarded");
            release.TrySetResult();
            await pending;
            Assert(f.Handler.Seen.First(call => call.Uri.AbsolutePath == "/users/12345/playlists/list").Authorization == "OAuth " + FakeSession.AccessToken
                && f.Handler.Seen.Where(call => call.Uri.AbsolutePath == path).All(call => call.Authorization == "OAuth " + newerSession.AccessToken)
                && f.Handler.Seen.Count(call => call.Uri.AbsolutePath.EndsWith("/change", StringComparison.Ordinal)) == 1
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

    private static async Task NavRetryInitialCatalogRead()
    {
        foreach (var section in new[] { "search", "playlists" })
        {
            using var f = new SaFixture("initial-collection-" + section); await f.Activate(); var w = f.Window;
            var path = section == "search" ? "/search" : "/users/12345/playlists/list";
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            f.RespondOverride = async (request, _) =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath != path) return null;
                if (Interlocked.Increment(ref reads) == 1) { entered.TrySetResult(); await release.Task; }
                if (section == "playlists") return Json("""{"result":[{"kind":"77","title":"Подборка","owner":{"uid":"12345"}}]}""");
                Assert(Uri.UnescapeDataString(uri.Query).Contains("text=original", StringComparison.Ordinal), "Back submitted the draft instead of the original query");
                return Json(SaSearchJson("all", Uri.UnescapeDataString(uri.Query).Contains("page=1", StringComparison.Ordinal) ? 1 : 0));
            };
            var query = Control<TextBox>(w, "CatalogQuery");
            try
            {
                Task initial;
                if (section == "search")
                {
                    SaClick(Control<Button>(w, "SearchNav")); query.Text = "original";
                    initial = Call(w, "SearchCatalogViewAsync", false);
                }
                else initial = Call(w, "LoadCollectionAsync", "playlists");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (section == "search") query.Text = "draft";
                SaClick(Control<Button>(w, "FavoritesNav"));
                NavMouse(w, MouseButton.XButton1);
                await Until(() => Volatile.Read(ref reads) == 2 && SaField<CancellationTokenSource?>(w, "_featureCts") is null);
                Assert(SaField<string>(w, "_section") == section && Control<ListBox>(w, "CatalogList").Items.Count > 0
                    && (section != "search" || query.Text == "draft" && SaField<string>(w, "_searchText") == "original"),
                    "Back left a blank catalog or replaced an unsent draft");
                NavMouse(w, MouseButton.XButton2);
                release.TrySetResult();
                await initial;
                Assert(SaField<string>(w, "_section") == "favorites", "late initial read overtook Forward");
                NavMouse(w, MouseButton.XButton1);
                Assert(SaField<string>(w, "_section") == section && Volatile.Read(ref reads) == 2,
                    "a completed catalog was unnecessarily requested again");
                if (section == "search")
                {
                    await Call(w, "SearchCatalogViewAsync", true);
                    Assert(query.Text == "draft" && Volatile.Read(ref reads) == 3 && SaField<int>(w, "_searchPage") == 1
                        && Control<ListBox>(w, "CatalogList").Items.OfType<CatalogItem>().Where(item => item.Kind == "track")
                            .Select(item => item.Id).SequenceEqual(["track-0", "track-1"]),
                        "More after resuming the initial search replaced its results or altered the unsent draft");
                }
            }
            finally { release.TrySetResult(); }
        }
    }

    private static async Task NavExactDoubleClick()
    {
        using var f = new SaFixture("exact-double-click"); await f.Activate(); var w = f.Window;
        SaClick(Control<Button>(w, "SearchNav"));
        var albums = new[] { new CatalogItem("first-album", "album", "Первый", ""), new CatalogItem("second-album", "album", "Второй", "") };
        SaInvoke(w, "ShowCatalogItems", albums, false);
        var catalog = Control<ListBox>(w, "CatalogList");
        catalog.SelectedItem = albums[0];
        catalog.ScrollIntoView(albums[1]); w.UpdateLayout();
        var catalogRow = (ListBoxItem)catalog.ItemContainerGenerator.ContainerFromIndex(1)!;
        var link = SaVisuals<Button>(catalogRow).First();
        var apiCalls = f.Handler.Seen.Count;
        Assert(!Click("Catalog_DoubleClick", catalog, catalogRow, MouseButton.Right)
            && !Click("Catalog_DoubleClick", catalog, link, MouseButton.Left)
            && f.Handler.Seen.Count == apiCalls && SaField<string>(w, "_section") == "search",
            "right double-click or an embedded link activated a catalog row");
        Assert(Click("Catalog_DoubleClick", catalog, catalogRow, MouseButton.Left), "left catalog double-click did not claim the row");
        await Until(() => SaField<AlbumDetails?>(w, "_activeAlbumDetails")?.Id == "second-album"
            && SaField<CancellationTokenSource?>(w, "_featureCts") is null);

        var wave = WriteWave("artist-double-click.wav", 20);
        TrackRow ArtistTrack(string id) => new() { Id = id, RequestId = id, Title = id, LocalPath = wave, DurationMs = 20000, Available = true };
        var tracks = new[] { ArtistTrack("first"), ArtistTrack("second") };
        SaInvoke(w, "SetSection", "artist-detail", "Исполнитель");
        SaInvoke(w, "ShowArtistDetails", new ArtistDetails("artist-double-click", "Исполнитель", null, null, tracks, [], [], ""));
        var artist = Control<ListBox>(w, "ArtistTopTracks");
        artist.SelectedItem = tracks[0];
        artist.ScrollIntoView(tracks[1]); w.UpdateLayout();
        var artistRow = (ListBoxItem)artist.ItemContainerGenerator.ContainerFromIndex(1)!;
        Assert(!Click("ArtistTop_DoubleClick", artist, artistRow, MouseButton.Right)
            && !Click("ArtistTop_DoubleClick", artist, artist, MouseButton.Left)
            && !Click("ArtistTop_DoubleClick", artist, SaVisuals<Button>(artistRow).First(), MouseButton.Left)
            && !f.Player.HasSource && SaField<PlaybackQueue>(w, "_playbackQueue").Items.Count == 0,
            "right, background or embedded-button double-click activated an artist track");
        Assert(Click("ArtistTop_DoubleClick", artist, artistRow, MouseButton.Left), "left artist double-click did not claim the row");
        await Until(() => f.Player.IsPlaying && !SaField<bool>(w, "_loadingStream"));
        Assert(ReferenceEquals(SaField<PlaybackQueue>(w, "_playbackQueue").Current, tracks[1]),
            "artist double-click played the selected track instead of the clicked row");

        bool Click(string handler, ListBox list, DependencyObject source, MouseButton button)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
                { RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent, Source = source };
            SaInvoke(w, handler, list, args);
            return args.Handled;
        }
    }

    private static async Task NavOfflineSavedVersion()
    {
        using var f = new Library017Fixture("offline-exact-history");
        var shared = f.Row("shared", "album-a");
        var source = new Uri("https://s1.vlx.yandex.net/audio.mp3");
        await f.Library.SaveAsync(shared, new AudioDescriptor(source, "mp3", 192), default);
        await f.SaveShared();
        var w = f.Window;
        await Call(w, "ShowOffline");
        var list = Control<ListView>(w, "TracksList");
        var versions = list.Items.Cast<TrackRow>().ToArray();
        Assert(versions.Length == 2 && versions.All(row => row.RequestId == shared.RequestId && row.LocalPath is not null),
            "the fixture did not save two versions of the same track");
        list.SelectedItem = versions[1];
        var exactPath = versions[1].LocalPath!;
        SaInvoke(w, "SetSection", "favorites", "Мне нравится");
        NavMouse(w, MouseButton.XButton1);
        await Until(() => SaField<CancellationTokenSource?>(w, "_featureCts") is null);
        Assert(list.SelectedItem is TrackRow selected && selected.LocalPath == exactPath,
            "Back selected a different saved quality of the same track");

        SaInvoke(w, "SetSection", "favorites", "Мне нравится");
        File.Delete(exactPath);
        NavMouse(w, MouseButton.XButton1);
        await Until(() => SaField<CancellationTokenSource?>(w, "_featureCts") is null);
        Assert(list.SelectedItem is null && list.Items.Cast<TrackRow>().Any(row => row.LocalPath == versions[0].LocalPath)
            && !f.Player.HasSource, "Back substituted a different quality after the exact file disappeared");
    }

    private static async Task NavOfflineDownloadCommit()
    {
        using var f = new Library017Fixture("offline-download-commit");
        var w = f.Window;
        var first = f.Row("batch-first", "1");
        var second = f.Row("batch-second", "1");
        var playlist = new PlaylistDetails("12345", "77", "Скачиваемый плейлист", "", 1, [first]);
        await f.Library.RememberPlaylistAsync(playlist, AudioQuality.Optimal, default);
        await f.Library.RememberPlaylistAsync(playlist with { Owner = "other-owner", Title = "Другой плейлист" }, AudioQuality.Optimal, default);
        await Call(w, "ShowOfflinePlaylistsAsync");
        var catalog = Control<ListBox>(w, "CatalogList");
        CatalogItem Card() => catalog.Items.OfType<CatalogItem>().Single(item => item.Owner == playlist.Owner);
        string VisibleStatus()
        {
            var card = Card();
            catalog.ScrollIntoView(card);
            w.UpdateLayout();
            var item = (ListBoxItem)catalog.ItemContainerGenerator.ContainerFromItem(card)!;
            return SaVisuals<TextBlock>(item).Single(text => text.Name == "CatalogSubtitle").Text;
        }
        catalog.SelectedItem = Card();
        Assert(Card().OwnerName == "Fixture" && VisibleStatus() == "Скачано 0 из 1",
            "the current-owner card did not render its initial incomplete status");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        w.OfflineViewAfterRead = _ => { entered.TrySetResult(); return release.Task; };
        try
        {
            await Call(w, "StartDownloadBatchAsync", (object)new[] { first }, playlist.Title, playlist, false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(f.Library.GetPlaylistStatus(playlist.Owner, playlist.Kind).Complete && VisibleStatus() == "Скачано 0 из 1",
                "the held card read did not follow a completed download");
            release.TrySetResult();
            await Until(() => SaField<CancellationTokenSource?>(w, "_offlineDownloadRefresh") is null);
            Assert(VisibleStatus().StartsWith("✓ Скачан", StringComparison.Ordinal), "completion did not update the rendered card status");
            Assert(ReferenceEquals(catalog.SelectedItem, Card()), "completion lost the selected card for the same owner and kind");

            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var opening = Call(w, "ShowOffline");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var navigation = SaField<CancellationTokenSource?>(w, "_featureCts");
            var updated = playlist with { Revision = 2, Tracks = [first, second] };
            await Call(w, "StartDownloadBatchAsync", updated.Tracks, updated.Title, updated, false);
            Assert(f.Library.Read().Select(row => row.Id).ToHashSet().SetEquals([first.Id, second.Id])
                && f.Library.GetPlaylists().Single(saved => saved.Owner == updated.Owner && saved.Kind == updated.Kind).Revision == 2,
                "the second full-playlist batch did not commit the updated snapshot and both files");
            Assert(navigation is { IsCancellationRequested: false } && ReferenceEquals(navigation, SaField<CancellationTokenSource?>(w, "_featureCts"))
                && !opening.IsCompleted, "a download replaced the user's pending offline navigation");
            release.TrySetResult();
            await opening;
            Assert(SaField<string>(w, "_section") == "offline"
                && Control<ListView>(w, "TracksList").Items.OfType<TrackRow>().Select(row => row.Id).ToHashSet().SetEquals([first.Id, second.Id]),
                "the pending offline view published an old file list");
            Assert(f.MediaRequests == 2 && !f.Player.HasSource, "refresh replayed a download or opened audio");
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
