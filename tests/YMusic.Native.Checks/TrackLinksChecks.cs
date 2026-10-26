using System.IO;
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
        await Run("Track links037: album column opens exact album and Back preserves playlist selection and offset", TlAlbumNavigation);
        await Run("Track links037: legacy album resolves by exact track and portable updater stays inactive", TlLegacyAlbum);
        await Run("Track links037: update controls separate download and explicit restart and finish pending offline writes", TlUpdateControls);
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

    private static async Task TlUpdateControls()
    {
        using var fixture=new SaFixture("update-controls");
        var window=fixture.Window;await fixture.Activate();
        var directory=Path.Combine(_directory,"update-controls");Directory.CreateDirectory(directory);
        var config=Path.Combine(directory,"feed.json");File.WriteAllText(config,"{\"githubRepository\":\"https://github.com/example/fixture\"}");
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
        SaClick(button);await Task.Delay(30);
        Assert(backend.Applies==0 && !button.IsEnabled,"restart did not wait for the pending offline write");
        pending.TrySetResult();await Until(()=>backend.Applies==1 && button.IsEnabled);
        Assert(SaField<bool>(window,"_autoFavoritesEnabled"),"restart changed the user's auto-download preference");
        Assert(!fixture.Player.HasSource,"update controls started or replaced media");
    }

    private sealed class TlUpdateBackend:IAppUpdateBackend
    {
        internal int Checks,Downloads,Applies;
        public string? PendingVersion{get;private set;}
        public Task<string?> CheckAsync(){Checks++;return Task.FromResult<string?>("0.3.8");}
        public Task DownloadAsync(Action<int> progress,CancellationToken token)
        {token.ThrowIfCancellationRequested();Downloads++;progress(50);PendingVersion="0.3.8";progress(100);return Task.CompletedTask;}
        public void ApplyAndRestart(){Applies++;}
    }
}
