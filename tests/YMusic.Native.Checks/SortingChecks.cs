using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckListSorting()
    {
        await Run("Sorting: numeric duration, culture text, missing-last, stable ties and source ranks across pages", () =>
        {
            var a = SortTrack("a", "same", "Z", "B", 90000);
            var b = SortTrack("b", "SAME", "A", "A", 600000);
            var c = SortTrack("c", "Тихо", "Б", "", 0);
            var d = SortTrack("d", "Альфа", "А", "C", 120000);
            var source = new ObservableCollection<TrackRow>([a,b,c,d]); using var order = new ListSourceOrder(source);
            TrackRow[] Sorted(ListSortField field, bool desc = false) => source.Cast<object>().OrderBy(x=>x, Comparer<object>.Create((x,y)=>new StableListComparer(new(field,desc),order).Compare(x,y))).Cast<TrackRow>().ToArray();
            Assert(Sorted(ListSortField.Duration).SequenceEqual([a,d,b,c]), "duration compared formatted minutes or misplaced unknown duration");
            Assert(Sorted(ListSortField.Duration,true).SequenceEqual([b,d,a,c]), "descending duration misplaced unknown duration");
            foreach (var descending in new[]{false,true})
            {
                var rows=Sorted(ListSortField.Title,descending);
                Assert(Array.IndexOf(rows,a)<Array.IndexOf(rows,b), "equal text changed source order");
                Assert(Sorted(ListSortField.Album,descending)[^1]==c, "empty album did not stay last");
            }
            a.DownloadedPath="fixture-owned";
            Assert(Sorted(ListSortField.Downloaded,true)[0]==a, "downloaded ordering ignored file state");
            var page=SortTrack("page","same","","",90000); source.Add(page);
            Assert(Sorted(ListSortField.Title).Where(t=>t.Title.Equals("same",StringComparison.OrdinalIgnoreCase)).SequenceEqual([a,b,page]), "next page changed stable ties");
            source.Move(4,0); Assert(order.Rank(page)==0 && order.Rank(a)==1,"source move left stale ranks");
            source.Remove(page); source.Clear(); source.Add(b); Assert(order.Rank(b)==0,"source reset retained ranks");
            return Task.CompletedTask;
        });
        await Run("Sorting: clickable headers, directions, filter, multi-selection and source order restoration", async () =>
        {
            using var fixture=new SaFixture("sorting-tracks"); await fixture.Activate(); var window=fixture.Window;
            var rows=new[]{SortTrack("z","Ясно","Б","Д",600000),SortTrack("b","Бета","А","Б",90000),SortTrack("a","Альфа","В","А",120000)};
            SaInvoke(window,"ShowRows",(object)rows); var list=Control<ListView>(window,"TracksList"); list.SelectedItems.Add(rows[0]);list.SelectedItems.Add(rows[2]);
            SortHeader(window,ListSortField.Title);
            Assert(list.Items.Cast<TrackRow>().SequenceEqual([rows[2],rows[1],rows[0]]),"title header did not sort ascending");
            Assert(list.SelectedItems.Contains(rows[0]) && list.SelectedItems.Contains(rows[2]),"sort lost multi-selection");
            SortHeader(window,ListSortField.Title); Assert(list.Items[0]==rows[0],"repeated header did not reverse");
            SortHeader(window,ListSortField.Title); Assert(list.Items.Cast<TrackRow>().SequenceEqual(rows),"third header did not restore source order");
            SortChoose(window,ListSortField.Duration); Assert(list.Items.Cast<TrackRow>().SequenceEqual([rows[1],rows[2],rows[0]]),"duration menu did not sort numerically");
            Control<TextBox>(window,"FilterBox").Text="Альфа"; await Task.Delay(300); Assert(list.Items.Count==1 && list.Items[0]==rows[2],"sort broke filter");
            Control<TextBox>(window,"FilterBox").Clear(); await Task.Delay(300); Assert(list.Items[0]==rows[1],"clearing filter lost sort");
            rows[0].DownloadedPath="fixture-mark";
            foreach(var (field,first) in new[]{(ListSortField.Artist,rows[1]),(ListSortField.Album,rows[2]),(ListSortField.Downloaded,rows[0])})
            { SortHeader(window,field); Assert((ListSortField)Control<ComboBox>(window,"ListSortCombo").SelectedValue==field && ReferenceEquals(list.Items[0],first),"header and menu order disagree"); }
        });
        await Run("Sorting: collections and grouped search retain section ranks, selection and appended page order", async () =>
        {
            using var fixture=new SaFixture("sorting-catalog"); await fixture.Activate(); var w=fixture.Window;
            SaInvoke(w,"SetSection","albums","Альбомы");
            var one=new CatalogItem("1","album","Январь","Борис");var two=new CatalogItem("2","album","Август","Анна");
            SaInvoke(w,"ShowCatalogItems",new[]{one,two},false); var list=Control<ListBox>(w,"CatalogList");list.SelectedItem=one;
            SortChoose(w,ListSortField.Artist);Assert(ReferenceEquals(list.Items[0],two) && ReferenceEquals(list.SelectedItem,one),"album artist sort lost selection");
            SaInvoke(w,"SetSection","playlists","Плейлисты");SaInvoke(w,"ShowCatalogItems",new[]{one,two},false);
            Assert(Control<ComboBox>(w,"ListSortCombo").Items.Count==2,"playlist offers metadata it does not have");
            SortChoose(w,ListSortField.Title);Assert(ReferenceEquals(list.Items[0],two),"playlist title sort failed");
            SaInvoke(w,"SetSection","search","Поиск");
            var p=new CatalogItem("p","playlist","ААА","");var t=new CatalogItem("t","track","Ясно","",Track:SortTrack("t","Ясно","А","Б",120000));
            var u=new CatalogItem("u","track","Альфа","",Track:SortTrack("u","Альфа","Б","А",90000));
            SaInvoke(w,"ShowCatalogItems",new[]{t,p,u},false);SortChoose(w,ListSortField.Title);
            var view=CollectionViewSource.GetDefaultView(list.ItemsSource);
            Assert(view.Groups!.Cast<CollectionViewGroup>().Select(g=>g.Name).SequenceEqual(new[]{"track","playlist"}),"sort changed server category order");
            Assert(list.Items.Cast<CatalogItem>().SequenceEqual([u,t,p]),"sort did not act within search groups");
            var next=new CatalogItem("n","track","Бета","",Track:SortTrack("n","Бета","В","А",100000));
            SaInvoke(w,"ShowCatalogItems",new[]{next},true);Assert(list.Items.Cast<CatalogItem>().SequenceEqual([u,next,t,p]),"append bypassed active grouped sort");
            SortChoose(w,ListSortField.Original);Assert(list.Items.Cast<CatalogItem>().SequenceEqual([t,u,next,p]),"group source order did not restore");
            Assert(fixture.Searches.Count==0,"sorting sent catalog requests");
        });
        await Run("Sorting: queue occurrences, moves and removals remain exact without changing playback on sort", async () =>
        {
            using var fixture=new SaFixture("sorting-queue");await fixture.Activate();var w=fixture.Window;
            var a=SortTrack("a","Ясно","","",100000);var b=SortTrack("b","Альфа","","",200000);
            var queue=SaField<PlaybackQueue>(w,"_playbackQueue");queue.SetItems([a,b,a]);queue.SelectAt(2);var version=queue.Version;var token=queue.CurrentOccurrenceToken;
            SaInvoke(w,"ShowQueue");var list=Control<ListView>(w,"TracksList");Assert(list.SelectedIndex==2,"fixture did not select second duplicate");
            SortChoose(w,ListSortField.Title);
            Assert(queue.Version==version && queue.CurrentOccurrenceToken==token && queue.Items.SequenceEqual([a,b,a]),"view sort changed playback queue");
            Assert(list.SelectedIndex==2,"sort changed selected duplicate occurrence");
            SaInvoke(w,"MoveQueueUp_Click",null!,null!);
            Assert(queue.Items.SequenceEqual([a,a,b]) && queue.CurrentOccurrenceToken==token && queue.CurrentIndex==1,"move used sorted index instead of source occurrence");
            Assert((int)SaInvokeResult(w,"SelectedQueueIndex")! ==1,"move selected the wrong sorted row");
            SaInvoke(w,"SelectVisibleQueueOccurrence",0);
            SaInvoke(w,"RemoveQueue_Click",null!,null!); await Dispatcher.Yield(DispatcherPriority.Background);
            Assert(queue.Items.SequenceEqual([a,b]) && queue.CurrentOccurrenceToken==token,"remove deleted the current duplicate instead of selected occurrence");
            Assert(!fixture.Player.HasSource,"sorting or removing noncurrent queue item started audio");
        });
        await Run("Sorting: bounded preference store roundtrip, malformed recovery and atomic failure preserve prior data", () =>
        {
            var directory=Path.Combine(_directory,"sorting-store");var store=new ListSortStore(directory);
            store.Save(new Dictionary<string,ListSortRule>{{"tracks:favorites",new(ListSortField.Title,true)},{"catalog:albums",new(ListSortField.Artist)},{"downloads",new(ListSortField.Size,true)},{"unknown/account/path",new(ListSortField.Album)}});
            var saved=new ListSortStore(directory).Load();Assert(saved.Count==3 && saved["tracks:favorites"]==new ListSortRule(ListSortField.Title,true),"store changed known rules or retained arbitrary keys");
            var path=Path.Combine(directory,"list-sorting.json");var bytes=File.ReadAllBytes(path);
            using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                try { store.Save(new Dictionary<string,ListSortRule>()); throw new InvalidOperationException("locked replace succeeded"); } catch(IOException) { }
            }
            Assert(File.ReadAllBytes(path).SequenceEqual(bytes),"failed replacement destroyed previous sorting");
            File.WriteAllText(path,"{broken");Assert(store.Load().Count==0 && File.ReadAllText(path)=="{broken","corrupt load modified data");
            File.WriteAllText(path,new string(' ',8193));Assert(store.Load().Count==0,"oversized sort data accepted");
            Assert(!File.ReadAllText(path).Contains("http"),"unexpected location persisted");
            return Task.CompletedTask;
        });
        await Run("Sorting: explicit Play uses visible offline order and changing a playing list preserves the active queue", async () =>
        {
            var directory=Path.Combine(_directory,"sorting-playback");Directory.CreateDirectory(directory);
            var music=Path.Combine(directory,"music");Directory.CreateDirectory(music);
            var wave=WriteWave("sorting-offline.wav",12);var sourceBytes=File.ReadAllBytes(wave);
            var rows=new[]{SortTrack("501","Бета","Исполнитель","Альбом",12000),SortTrack("502","Альфа","Исполнитель","Альбом",12000)};
            var indexed=rows.Select(row=>
            {
                var name=row.Id+".mp3";File.WriteAllBytes(Path.Combine(music,name),sourceBytes);
                return new OfflineItem(row.Id,row.RequestId,row.Title,row.Artist,row.Album,row.DurationMs,name,"mp3",320,sourceBytes.Length,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)),DateTimeOffset.UtcNow);
            }).ToArray();
            File.WriteAllText(Path.Combine(music,"library.index.json"),JsonSerializer.Serialize(indexed));
            var handler=new FakeHandler((_,_)=>throw new InvalidOperationException("sorted offline playback opened network"));
            using var api=new MusicApi(handler);using var player=new NativePlayer(new AudioBuffer(Path.Combine(directory,"audio")));
            using var offline=new OfflineLibrary(music,new AudioBuffer(Path.Combine(directory,"downloads")));
            var w=new MainWindow(api:api,store:new SessionStore(directory),restore:false,diagnosticsDirectory:directory,offline:offline,player:player);
            try
            {
                Control<Slider>(w,"VolumeSlider").Value=0;SaInvoke(w,"ShowRows",(object)rows);SortChoose(w,ListSortField.Title);
                await Call(w,"PlaySelectedAsync");
                var queue=SaField<PlaybackQueue>(w,"_playbackQueue");
                Assert(queue.Items.SequenceEqual([rows[1],rows[0]]) && player.SourceLocalPath==Path.Combine(music,"502.mp3") && player.IsPlaying,"Play did not use sorted visible order");
                Assert(Control<TextBlock>(w,"QualityText").Text.StartsWith("Офлайн · ",StringComparison.Ordinal) && Control<TextBlock>(w,"QualityText").Text.Contains("320")
                    && Control<TextBlock>(w,"StatusText").Text=="", "offline source must be shown with audio quality in the player, not the collection status");
                var version=queue.Version;var current=queue.CurrentOccurrenceToken;SaClick(Control<Button>(w,"SortDirectionButton"));
                Assert(queue.Version==version && queue.CurrentOccurrenceToken==current && player.SourceLocalPath==Path.Combine(music,"502.mp3"),"sorting interrupted playback or rewrote its queue");
                Assert(handler.Seen.Count==0,"sorting resolved offline audio online");
            }
            finally { w.Close(); }
            Assert(indexed.All(item=>File.Exists(Path.Combine(music,item.FileName))),"sorting or closing removed indexed music");
        });
        await Run("Sorting: download numeric progress and size reorder only their view and preserve worker source order", async () =>
        {
            using var fixture=new SaFixture("sorting-downloads");await fixture.Activate();var w=fixture.Window;
            var queue=SaField<DownloadQueueState>(w,"_downloadQueue");queue.Replace([SortTrack("1","Ясно","A","",1000),SortTrack("2","Альфа","Б","",2000)],"Проверка сортировки");
            var entries=queue.Entries.ToArray();entries[0].Begin();entries[0].Progress(new AudioProgress(900,1000));entries[1].Begin();entries[1].Progress(new AudioProgress(1000,10000));
            SaInvoke(w,"DownloadCenter_Click",null!,null!);SortChoose(w,ListSortField.Size);
            var list=((Grid)Control<ContentControl>(w,"DownloadCenterPanel").Content).Children.OfType<ListView>().Single();
            Assert(list.Items[0]==entries[0],"size sorted formatted kilobytes instead of numeric totals");
            SortChoose(w,ListSortField.Progress);Assert(list.Items[0]==entries[1],"progress sorted bytes instead of percent");
            entries[0].Complete(1000);entries[1].Complete(10000);SaInvoke(w,"RefreshDownloadCenter");
            Assert(list.Items[0]==entries[0],"final refresh retained a throttled intermediate progress order");
            Assert(queue.Entries.SequenceEqual(entries),"view sorting changed worker scheduling order");
            await SortCapture(w,"downloads");
        });
        await Run("Sorting: section choices survive navigation and private profile restart without reducing list viewport", async () =>
        {
            using var fixture=new SaFixture("sorting-navigation");await fixture.Activate();var w=fixture.Window;
            var directory=Path.Combine(_directory,"sorting-restart");var store=new ListSortStore(directory);
            typeof(MainWindow).GetField("_listSortStore",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(w,store);
            var rows=Enumerable.Range(1,70).Select(i=>SortTrack(i.ToString(),"Композиция "+(71-i),"Исполнитель "+(i%5),"Альбом "+(i%3),i*1000)).ToArray();
            SaInvoke(w,"ShowRows",(object)rows);SortChoose(w,ListSortField.Title);SaClick(Control<Button>(w,"SortDirectionButton"));
            SaInvoke(w,"SetSection","offline","Скачанное");SaInvoke(w,"ShowRows",(object)rows);SortChoose(w,ListSortField.Duration);
            SaInvoke(w,"SetSection","favorites","Мне нравится");SaInvoke(w,"ShowRows",(object)rows);
            Assert((ListSortField)Control<ComboBox>(w,"ListSortCombo").SelectedValue==ListSortField.Title && Control<Button>(w,"SortDirectionButton").Content!.ToString()=="↓","navigation forgot per-section sort");
            await SortCapture(w,"tracks");
            var api=new MusicApi(new FakeHandler((_,_)=>throw new InvalidOperationException("restart opened network")));
            var player=new NativePlayer(new AudioBuffer(Path.Combine(directory,"audio")));
            var restart=new MainWindow(api:api,store:new SessionStore(Path.Combine(directory,"session")),restore:true,diagnosticsDirectory:directory,
                offline:new OfflineLibrary(Path.Combine(directory,"music"),new AudioBuffer(Path.Combine(directory,"downloads"))),player:player,preferences:new PreferencesStore(Path.Combine(directory,"prefs")));
            try { restart.Show();await Task.Delay(80);SaInvoke(restart,"ShowRows",(object)rows);
                Assert((ListSortField)Control<ComboBox>(restart,"ListSortCombo").SelectedValue==ListSortField.Title && Control<Button>(restart,"SortDirectionButton").Content!.ToString()=="↓","restart did not load private sorting preferences"); }
            finally { restart.Close(); }
        });
    }
    private static TrackRow SortTrack(string id,string title,string artist,string album,long duration) => new(){Id=id,RequestId=id,Title=title,Artist=artist,Album=album,DurationMs=duration,Available=true};
    private static void SortChoose(MainWindow w,ListSortField field) => Control<ComboBox>(w,"ListSortCombo").SelectedValue=field;
    private static void SortHeader(MainWindow w,ListSortField field)
    { var grid=(GridView)Control<ListView>(w,"TracksList").View;var header=grid.Columns.Select(c=>c.Header).OfType<GridViewColumnHeader>().Single(h=>(string)h.Tag==field.ToString());header.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,header)); }
    private static object? SaInvokeResult(MainWindow w,string method) => typeof(MainWindow).GetMethod(method,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(w,null);
    private static async Task SortCapture(MainWindow w,string name)
    {
        foreach(var (width,height) in new[]{(1280,860),(1050,680)})
        { w.Width=width;w.Height=height;w.UpdateLayout();await Task.Delay(180);
          var combo=Control<ComboBox>(w,"ListSortCombo");Assert(combo.IsVisible && combo.ActualWidth>=180,"sort choice clipped");
          Assert(!SaVisuals<TextBlock>(Control<StackPanel>(w,"ListSortControls")).Any(t=>t.Text.Contains("ListSortOption",StringComparison.Ordinal)),"sort control exposes a technical record label");
          if(name=="tracks")Assert(Control<ListView>(w,"TracksList").ActualHeight>=250,"sorting reduced track viewport below supported minimum");
          App.Capture(w,Path.Combine(_directory,$"sorting-{name}-{width}.png")); }
    }
}
