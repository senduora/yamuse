using System.IO;
using System.Text.Json;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckTrackLinks037()
    {
        await Run("Track links037: text links and search rows use flat templates at both window sizes", TlAppearance);
        await Run("Track links037: player exposes exact individual artists and complete artist menu without changing queue", TlPlayerArtists);
        await Run("Track links037: a reused artist presenter closes the previous track menu", TlReusedArtistPopup);
        await Run("Track links037: album column opens exact album and Back preserves playlist selection and offset", TlAlbumNavigation);
        await Run("Track links037: legacy album resolves by exact track and portable updater stays inactive", TlLegacyAlbum);
        await Run("Track links037: newer unresolved album link owns navigation before either response", TlLegacyAlbumLatestLookup);
        await Run("Track links037: replacement and section navigation cancel held album and artist HTTP reads", TlNavigationCancelsAlbumAndArtistRead);
        await Run("Track links037: a new query in the same section supersedes an earlier unresolved album link", TlNewSearchWinsOverTrackLink);
        await Run("Track links037: latest offline or legacy album navigation wins while both reads are held", TlOfflineAndLegacyAlbum);
        await Run("Track links037: update controls separate download and explicit restart and finish pending offline writes", TlUpdateControls);
    }

    private static async Task TlReusedArtistPopup()
    {
        using var fixture = new SaFixture("artist-popup-reuse");
        var window = fixture.Window;
        await fixture.Activate();
        var template = (DataTemplate)window.FindResource("TrackArtistLinks");
        var host = (Grid)template.LoadContent();
        host.DataContext = new TrackRow
        {
            Id = "401", RequestId = "401", Title = "Earlier", Artist = "Original artist",
            Artists = [new("301", "Original artist")]
        };
        Ta0317RaiseOpening(host, 10, 10);
        var stale = host.ContextMenu;
        Assert(stale is not null && stale.Items.Count == 1, "fixture did not attach an artist menu to the shared template");
        host.DataContext = new TrackRow { Id = "402", RequestId = "402", Title = "Later", Artist = "Artist without ID" };
        Ta0317RaiseOpening(host, 10, 10);
        var requestCount = fixture.Handler.Seen.Count;
        ((MenuItem)stale!.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert(host.ContextMenu is null && !stale.IsOpen && fixture.Handler.Seen.Count == requestCount,
            "a reused presenter kept or executed the previous track's artist menu");
    }

    private static async Task TlAppearance()
    {
        using var fixture = new SaFixture("links-appearance");
        var window = fixture.Window; await fixture.Activate();
        SaClick(Control<Button>(window,"SearchNav"));
        Control<TextBox>(window,"CatalogQuery").Text="fixture";
        await Call(window,"SearchCatalogViewAsync",false);
        var results=Control<ListBox>(window,"CatalogList");
        var track=results.Items.Cast<CatalogItem>().First(item=>item.Kind=="track");
        results.SelectedItem=track; results.ScrollIntoView(track); window.UpdateLayout();
        await Task.Delay(200);
        var item=(ListBoxItem)results.ItemContainerGenerator.ContainerFromItem(track)!;
        Assert(item.Template.FindName("ItemSurface",item) is Border { BorderThickness.Left:0 },"search still uses the system blue selection frame");
        var link=SaVisuals<Button>(item).First(button=>button.Tag is ArtistReference);
        Assert(link.BorderThickness==new Thickness(0) && link.Template.FindName("LinkSurface",link) is Grid
            && link.Template.FindName("ButtonBorder",link) is null,"artist is still a rounded command button");
        var before=link.RenderSize;
        link.Focus(); window.UpdateLayout(); await Task.Delay(190);
        Assert(link.RenderSize==before && link.BorderThickness==new Thickness(0),"focus changed the text link's size or created a pill border");
        Assert(link.FocusVisualStyle is null && link.Template.FindName("LinkUnderline",link) is null,"text links must use accent color rather than a focus frame or underline");
        foreach(var (width,height) in new[]{(1280,860),(1050,680)})
        {
            window.Width=width;window.Height=height;window.UpdateLayout();await Task.Delay(290);
            Assert(Control<Grid>(window,"NowPlayingPanel").ActualWidth>150,"player controls crowded the now-playing area");
            App.Capture(window,Path.Combine(_directory,$"flat-search-links-{width}x{height}.png"));
        }
    }

    private static async Task TlPlayerArtists()
    {
        using var fixture=new SaFixture("player-artists");
        var window=fixture.Window;await fixture.Activate();
        var track=new TrackRow {Id="playing",RequestId="playing:1",Title="Трек с несколькими исполнителями",Artist="Исполнитель 42, Второй участник с длинным именем",
            Artists=[new("artist-42","Исполнитель 42"),new("artist-99","Второй участник с длинным именем")],Album="Альбом",AlbumId="1",Available=true};
        typeof(MainWindow).GetField("_current",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,track);
        Control<TextBlock>(window,"TrackTitle").Text=track.Title;
        var queue=SaField<PlaybackQueue>(window,"_playbackQueue");queue.SetItems([track],track);var version=queue.Version;
        SaInvoke(window,"UpdateControls");window.UpdateLayout();await Task.Delay(60);
        var panel=Control<DockPanel>(window,"PlayerArtistPanel");
        var buttons=SaVisuals<Button>(panel).Where(button=>button.Tag is ArtistReference).ToArray();
        Assert(panel.IsVisible && buttons.Length==2 && Control<TextBlock>(window,"TrackArtist").Visibility==Visibility.Collapsed,"player artists are still plain text");
        Assert(buttons.Select(button=>((ArtistReference)button.Tag).Id).SequenceEqual(new[]{"artist-42","artist-99"}),"player artist identity/order changed");
        Assert(window.FindName("PlayerArtistsMenuButton") is null && !SaVisuals<Button>(panel).Any(button=>button.Content?.ToString()=="⋯"),"player retained the extra artist menu button");
        Assert(SaVisuals<TextBlock>(panel).Count(text=>text.Text==", "&&text.IsVisible)==1,"artist names need one visible comma between two participants");
        var menu=panel.ContextMenu!;
        Assert(menu.Items.Cast<MenuItem>().Select(item=>item.Header.ToString()).SequenceEqual(track.Artists.Select(artist=>artist.Name)),"player artist menu omits a participant");
        // Invoke the actual item handler without opening an offscreen native popup.
        ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(()=>SaField<ArtistDetails?>(window,"_artistDetails")?.Id=="artist-99" && SaField<CancellationTokenSource?>(window,"_featureCts") is null);
        Assert(queue.Version==version && ReferenceEquals(queue.Current,track) && ReferenceEquals(SaField<TrackRow?>(window,"_current"),track)
            && !fixture.Player.HasSource,"artist navigation replaced the playing queue or opened audio");
        SaClick(buttons[0]);
        await Until(()=>SaField<ArtistDetails?>(window,"_artistDetails")?.Id=="artist-42" && SaField<CancellationTokenSource?>(window,"_featureCts") is null);
        await SaCapture(window,"player-artist-links");
        window.SignOut();window.UpdateLayout();
        Assert(!panel.IsVisible && Control<ContentControl>(window,"PlayerArtistLinks").Content is null && panel.ContextMenu is null,"signout retained the previous player's artist links");
    }

    private static async Task TlAlbumNavigation()
    {
        using var fixture=new SaFixture("album-column");
        var window=fixture.Window;await fixture.Activate();
        var rows=Enumerable.Range(0,60).Select(i=>new TrackRow {Number=i+1,Id="album-track-"+i,RequestId="album-track-"+i+":same-title-b",Title="Альбомный трек "+i,
            Artist="Исполнитель",Artists=[new("artist-42","Исполнитель")],Album="Одинаковое название",AlbumId="same-title-b",Available=true}).ToArray();
        SaInvoke(window,"ShowPlaylist",new PlaylistDetails("owner","album-source","Исходный плейлист","",1,rows));
        Control<TextBox>(window,"FilterBox").Text="Альбомный";await Task.Delay(300);
        var list=Control<ListView>(window,"TracksList");list.SelectedIndex=8;list.ScrollIntoView(rows[8]);window.UpdateLayout();await Task.Delay(60);
        var viewer=SaVisuals<ScrollViewer>(list).First();var offset=viewer.VerticalOffset;
        var queue=SaField<PlaybackQueue>(window,"_playbackQueue");queue.SetItems([rows[3]],rows[3]);var version=queue.Version;
        var container=(ListViewItem)list.ItemContainerGenerator.ContainerFromItem(rows[8])!;
        var button=SaVisuals<Button>(container).First(button=>ReferenceEquals(button.Tag,rows[8]) && Equals(button.ToolTip,"Открыть альбом") && SaVisuals<TextBlock>(button).Any(text=>text.Text==rows[8].Album));
        Assert(SaVisuals<TextBlock>(button).Any(text=>text.Text==rows[8].Album),"album column lost its title");
        SaClick(button);
        await Until(()=>SaField<string>(window,"_section")=="album-detail" && SaField<CancellationTokenSource?>(window,"_featureCts") is null);
        Assert(fixture.Handler.Seen.Any(call=>call.Uri.AbsolutePath=="/albums/same-title-b/with-tracks")
            && !fixture.Handler.Seen.Any(call=>call.Uri.AbsolutePath=="/search"),"album navigation guessed by title");
        Assert(queue.Version==version && ReferenceEquals(queue.Current,rows[3]) && !fixture.Player.HasSource,"opening an album changed playback");
        SaClick(Control<Button>(window,"BrowseBackButton"));await Task.Delay(100);
        Assert(SaField<string>(window,"_section")=="playlist-detail" && list.SelectedIndex==8
            && Control<TextBox>(window,"FilterBox").Text=="Альбомный" && Math.Abs(viewer.VerticalOffset-offset)<1,"album Back lost the source playlist state");
        await SaCapture(window,"clickable-album-column");
    }

    private static async Task TlLegacyAlbum()
    {
        using var fixture=new SaFixture("legacy-album-link");
        var window=fixture.Window;await fixture.Activate();
        var legacy=new TrackRow {Id="old-album",RequestId="old-album:1",Title="Старый сохранённый трек",Album="Старое имя",Available=true};
        SaInvoke(window,"TrackAlbum_Click",new Button{Tag=legacy},new RoutedEventArgs(Button.ClickEvent));
        await Until(()=>SaField<string>(window,"_section")=="album-detail" && SaField<CancellationTokenSource?>(window,"_featureCts") is null);
        Assert(fixture.Handler.Seen.Any(call=>call.Uri.AbsolutePath=="/tracks" && Uri.UnescapeDataString(call.Body).Contains("old-album:1",StringComparison.Ordinal))
            && fixture.Handler.Seen.Any(call=>call.Uri.AbsolutePath=="/albums/1/with-tracks")
            && !fixture.Handler.Seen.Any(call=>call.Uri.AbsolutePath=="/search"),"legacy album lookup is not exact-track metadata");
        var state=UpdateSupport.Current.State;
        Assert(!state.IsInstalled,"test host was treated as an installed client");
        await Call(window,"CheckForAppUpdatesAsync");
        Assert(UpdateSupport.Current.State==state,"portable startup attempted an update operation");
        var panel=(FrameworkElement)typeof(MainWindow).GetMethod("CreateUpdateSettingsPanel",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null)!;
        Assert(SaVisuals<Button>(panel).Single(button=>button.Name=="AppUpdateButton").IsEnabled==state.FeedConfigured,"portable manual check does not follow its configured feed");
    }

    private static async Task TlLegacyAlbumLatestLookup()
    {
        using var f = new SaFixture("legacy-album-latest"); await f.Activate(); var window = f.Window;
        var old = new TrackRow { Id = "older-link", RequestId = "older-link:earlier" };
        var latest = new TrackRow { Id = "latest-link", RequestId = "latest-link:later" };
        var enteredOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredLatest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLatest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldRead = default, latestRead = default;
        f.RespondOverride = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath != "/tracks") return null;
            var body = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(token));
            var isOld = body.Contains(old.RequestId, StringComparison.Ordinal);
            var row = isOld ? old : latest;
            if (isOld) oldRead = token; else latestRead = token;
            (isOld ? enteredOld : enteredLatest).TrySetResult();
            await (isOld ? releaseOld : releaseLatest).Task;
            return Json(JsonSerializer.Serialize(new
            {
                result = new[] { new { id = row.Id, title = row.Id,
                    albums = new[] { new { id = isOld ? "earlier" : "later", title = "Альбом " + row.Id } } } }
            }));
        };
        try
        {
            var pendingOld = Call(window, "OpenTrackAlbumAsync", old);
            await enteredOld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pendingLatest = Call(window, "OpenTrackAlbumAsync", latest);
            await enteredLatest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(oldRead.IsCancellationRequested && !latestRead.IsCancellationRequested,
                "a replacement link left the former HTTP request alive or canceled its own read");
            releaseOld.TrySetResult();
            await pendingOld;
            Assert(SaField<string>(window, "_section") == "favorites" && f.Handler.Seen.All(call => !call.Uri.AbsolutePath.StartsWith("/albums/", StringComparison.Ordinal)),
                "the first response won after a newer link was clicked");
            releaseLatest.TrySetResult();
            await pendingLatest;
            Assert(SaField<AlbumDetails?>(window, "_activeAlbumDetails")?.Id == "later" && !f.Player.HasSource,
                "the newer exact album was never opened or changed playback");
        }
        finally { releaseOld.TrySetResult(); releaseLatest.TrySetResult(); }
    }

    private static async Task TlNavigationCancelsAlbumAndArtistRead()
    {
        using var f = new SaFixture("legacy-album-artist-cancel"); await f.Activate(); var w = f.Window;
        var album = new TrackRow { Id = "old-album", RequestId = "old-album:1" };
        var artist = new TrackRow { Id = "old-artist", RequestId = "old-artist:1" };
        var albumEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var artistEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.RespondOverride = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath != "/tracks") return null;
            var body = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(token));
            var isAlbum = body.Contains(album.RequestId, StringComparison.Ordinal);
            (isAlbum ? albumEntered : artistEntered).TrySetResult(token);
            await release.Task;
            return Json(JsonSerializer.Serialize(new { result = new[] { SaTrack(isAlbum ? album.Id : artist.Id) } }));
        };
        try
        {
            var old = Call(w, "OpenTrackAlbumAsync", album);
            var albumToken = await albumEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SaInvoke(w, "ArtistName_Click", new Button { Tag = artist }, new RoutedEventArgs(Button.ClickEvent));
            var artistToken = await artistEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(albumToken.IsCancellationRequested && !artistToken.IsCancellationRequested,
                "resolving the newer artist link did not cancel only the former album read");
            SaClick(Control<Button>(w, "SearchNav"));
            Assert(artistToken.IsCancellationRequested, "leaving the section did not cancel the artist metadata HTTP read");
            release.TrySetResult();
            await old;
            await w.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert(SaField<string>(w, "_section") == "search" && !f.Player.HasSource
                && !f.Handler.Seen.Any(call => call.Uri.AbsolutePath.StartsWith("/albums/", StringComparison.Ordinal)
                    || call.Uri.AbsolutePath.EndsWith("/brief-info", StringComparison.Ordinal)),
                "a late lookup navigated or opened playback after both tokens were canceled");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task TlNewSearchWinsOverTrackLink()
    {
        using var fixture = new SaFixture("same-section-search-over-link");
        await fixture.Activate();
        var window = fixture.Window;
        SaClick(Control<Button>(window, "SearchNav"));
        var track = new TrackRow { Id = "legacy-track", RequestId = "legacy-track:1" };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RespondOverride = async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/tracks") return null;
            entered.TrySetResult();
            await release.Task;
            return Json(JsonSerializer.Serialize(new { result = new[] { SaTrack(track.Id) } }));
        };
        try
        {
            var oldLookup = Call(window, "OpenTrackAlbumAsync", track);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Control<TextBox>(window, "CatalogQuery").Text = "Fixture newer query";
            await Call(window, "SearchCatalogViewAsync", false);
            release.TrySetResult();
            await oldLookup;
            Assert(SaField<string>(window, "_section") == "search" && fixture.Searches.Any(search => search.Text == "Fixture newer query")
                && fixture.Handler.Seen.All(request => !request.Uri.AbsolutePath.StartsWith("/albums/", StringComparison.Ordinal)),
                "the earlier metadata lookup replaced the newer search in the same section");
            Assert(!fixture.Player.HasSource, "catalog navigation started playback");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task TlOfflineAndLegacyAlbum()
    {
        foreach (var offlineIsLatest in new[] { false, true })
        {
            using var f = new SaFixture("offline-legacy-order-" + offlineIsLatest);
            await f.Activate();
            var window = f.Window;
            var legacy = new TrackRow { Id = "legacy-held-track", RequestId = "legacy-held-track:1" };
            var enteredLegacy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLegacy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var enteredOffline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOffline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.RespondOverride = async (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath != "/tracks") return null;
                enteredLegacy.TrySetResult();
                await releaseLegacy.Task;
                return Json(JsonSerializer.Serialize(new { result = new[] { SaTrack(legacy.Id) } }));
            };
            window.OfflineViewAfterRead = _ => { enteredOffline.TrySetResult(); return releaseOffline.Task; };
            Task OpenLegacy() => Call(window, "OpenTrackAlbumAsync", legacy);
            Task OpenOffline() => Call(window, "ShowOffline");
            try
            {
                if (offlineIsLatest)
                {
                    var older = OpenLegacy();
                    await enteredLegacy.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var latest = OpenOffline();
                    await enteredOffline.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    releaseLegacy.TrySetResult();
                    await older;
                    Assert(SaField<string>(window, "_section") == "favorites", "the obsolete link changed sections while offline was loading");
                    releaseOffline.TrySetResult();
                    await latest;
                    Assert(SaField<string>(window, "_section") == "offline"
                        && f.Handler.Seen.All(call => !call.Uri.AbsolutePath.StartsWith("/albums/", StringComparison.Ordinal)),
                        "the newer offline view was canceled by the obsolete metadata");
                }
                else
                {
                    var older = OpenOffline();
                    await enteredOffline.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var latest = OpenLegacy();
                    await enteredLegacy.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    releaseOffline.TrySetResult();
                    await older;
                    Assert(SaField<string>(window, "_section") == "favorites", "the obsolete offline read changed sections while the album link was resolving");
                    releaseLegacy.TrySetResult();
                    await latest;
                    Assert(SaField<AlbumDetails?>(window, "_activeAlbumDetails")?.Id == "1", "the newer exact album was never opened");
                }
                Assert(!f.Player.HasSource, "navigation started playback");
            }
            finally { releaseLegacy.TrySetResult(); releaseOffline.TrySetResult(); }
        }
    }

    private static async Task TlUpdateControls()
    {
        using var fixture=new SaFixture("update-controls");
        var window=fixture.Window;await fixture.Activate();
        var directory=Path.Combine(_directory,"update-controls");Directory.CreateDirectory(directory);
        var config=Path.Combine(directory,"feed.json");File.WriteAllText(config,"{\"githubRepository\":\"https://github.com/senduora/yamuse\"}");
        var backend=new TlUpdateBackend();
        var service=new UpdateSupport(config,true,_=>backend);
        var panel=(FrameworkElement)typeof(MainWindow).GetMethod("CreateUpdateSettingsPanelCore",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,[service])!;
        Grid.SetColumn(panel,1);panel.VerticalAlignment=VerticalAlignment.Top;
        Control<Grid>(window,"LayoutRoot").Children.Add(panel);window.UpdateLayout();await Task.Delay(30);
        var button=SaVisuals<Button>(panel).Single(control=>control.Name=="AppUpdateButton");
        SaClick(button);await Until(()=>service.State.Status==AppUpdateStatus.Available && button.IsEnabled);
        Assert(backend.Checks==1 && backend.Downloads==0 && backend.Applies==0 && Equals(button.Content,"Скачать обновление"),"check applied or downloaded an update implicitly");
        SaClick(button);await Until(()=>service.State.Status==AppUpdateStatus.Downloaded && button.IsEnabled);
        Assert(backend.Downloads==1 && backend.Applies==0 && Equals(button.Content,"Обновить и перезапустить"),"download restarted the application implicitly");
        var pending=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(MainWindow).GetField("_downloadTask",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,pending.Task);
        typeof(MainWindow).GetField("_autoFavoritesEnabled",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,true);
        SaClick(button);await Until(()=>SaField<bool>(window,"_appUpdatePreparingRestart"));
        Assert(backend.Applies==0 && !button.IsEnabled,"restart did not wait for the pending offline write");
        pending.TrySetResult();await Until(()=>backend.Applies==1 && !SaField<bool>(window,"_appUpdateActionInProgress"));
        SaClick(button);
        Assert(backend.Checks==1 && backend.Downloads==1 && backend.Applies==1 && !button.IsEnabled
            && SaField<bool>(window,"_startupUpdateRestartRequested"),"settings allowed a second action after explicit restart");
        Assert(SaField<bool>(window,"_autoFavoritesEnabled"),"restart changed the user's auto-download preference");
        Assert(!fixture.Player.HasSource,"update controls started or replaced media");
    }

    private sealed class TlUpdateBackend:IAppUpdateBackend
    {
        internal int Checks,Downloads,Applies;
        public string? PendingVersion{get;private set;}
        public Task<string?> CheckAsync(CancellationToken token){token.ThrowIfCancellationRequested();Checks++;return Task.FromResult<string?>("0.3.8");}
        public Task DownloadAsync(Action<int> progress,CancellationToken token)
        {token.ThrowIfCancellationRequested();Downloads++;progress(50);PendingVersion="0.3.8";progress(100);return Task.CompletedTask;}
        public void ApplyAndRestart(){Applies++;}
    }
}
