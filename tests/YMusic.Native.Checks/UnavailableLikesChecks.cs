using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckUnavailableLikes()
    {
        await Run("Unavailable likes: confirmed removal excludes only favorites-batch retries and re-like restores eligibility", () =>
        {
            foreach(var favorites in new[]{false,true})
            {
                var q=new DownloadQueueState();q.Replace([UlRow("dead",false),UlRow("failed",true)],"fixture",favorites);
                var entries=q.BeginRun(false);entries[0].SetState(DownloadEntryState.Unavailable);entries[1].SetState(DownloadEntryState.Failed,"fixture error");q.EndRun(false);
                q.SetCollectionLike("failed",false);Assert(entries[1].RemovedFromCollection && entries[1].CollectionText.Contains("Убран"),"confirmed unlike lacks row acknowledgement");
                Assert(q.CanRetry==!favorites,"unlike changed retry policy for the wrong batch type");
                var retry=q.BeginRun(true);Assert(retry.Length==(favorites?0:1),"removed favorite was re-admitted or normal playlist was skipped");q.EndRun(false);
                q.SetCollectionLike("failed",true);entries[1].SetState(DownloadEntryState.Failed);
                Assert(q.CanRetry && !entries[1].SkipFavoritesDownload && entries[1].CollectionText=="","re-like retained stale exclusion");
            }
            return Task.CompletedTask;
        });
        await Run("Unavailable likes: sorted right-click targets one unavailable ID without album or audio, waits for confirmation and preserves files", async () =>
        {
            using var f=new UlFixture("success");await f.Start();var w=f.Window;var list=f.List;
            SortChoose(w,ListSortField.Title);list.SelectedItem=f.Queue.Entries.Single(e=>e.Track.Id=="ul-error");
            var dead=f.Queue.Entries.Single(e=>e.Track.Id=="ul-dead");list.ScrollIntoView(dead);w.UpdateLayout();await Task.Delay(80);
            var container=(ListViewItem)list.ItemContainerGenerator.ContainerFromItem(dead);
            Assert(container is not null,"unavailable row is not realized");SaInvoke(w,"SelectDownloadContextRow",container!);
            Assert(ReferenceEquals(list.SelectedItem,dead)&&f.Unlike.IsEnabled&&f.Menu.IsEnabled,"right click retained the previously selected row");
            var before=f.FileHashes();f.HoldWrite=true;
            f.Menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,f.Menu));await f.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(f.IsLiked("ul-dead") && !dead.RemovedFromCollection && !f.Unlike.IsEnabled && !f.Retry.IsEnabled,"UI optimistically removed an unconfirmed like or allowed duplicate operations");
            await Call(w,"UnlikeSelectedDownloadAsync");Assert(f.Writes.Count==1,"repeated click sent another mutation");
            f.WriteRelease.TrySetResult();await Until(()=>dead.RemovedFromCollection&&!SaField<bool>(w,"_catalogWriting"));
            Assert(f.Writes.Single()=="ul-dead"&&!f.IsLiked("ul-dead")&&f.IsLiked("ul-error"),"unlike sent an album guess or wrong selected ID");
            Assert(!SaField<HashSet<string>>(w,"_favoriteTrackIds").Contains("ul-dead")&&!SaField<List<TrackRow>>(w,"_favoritesCache").Any(t=>t.Id=="ul-dead"),"confirmed ID remains in favorites cache");
            Assert(!f.Unlike.IsEnabled&&!f.Menu.IsEnabled&&f.MetadataRequests==0&&f.FileHashes()==before,"unlike redownloaded library metadata, changed files or remained actionable");
            Assert(f.AudioRequests==0&&!f.Player.HasSource,"unavailable unlike attempted audio playback or download");
            await UlCapture(f,"confirmed");
            var failed=f.Queue.Entries.Single(e=>e.Track.Id=="ul-error");list.SelectedItem=failed;
            f.Unlike.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,f.Unlike));await Until(()=>failed.RemovedFromCollection&&!SaField<bool>(w,"_catalogWriting"));
            var resolves=f.ResolveRequests;await Call(w,"RetryFailedDownloads");
            Assert(f.Writes.SequenceEqual(["ul-dead","ul-error"])&&!f.Queue.CanRetry&&f.ResolveRequests==resolves&&f.MetadataRequests==0&&f.FileHashes()==before,"unliked failed favorite was retried or modified local files");
        });
        await Run("Unavailable likes: server errors, missing revision, unchanged readback and wrong owner never show false success", async () =>
        {
            foreach(var failure in new[]{"http","revision","readback","owner"})
            {
                using var f=new UlFixture("failure-"+failure);await f.Start();f.Failure=failure;
                var entry=f.Queue.Entries.Single(e=>e.Track.Id=="ul-dead");f.List.SelectedItem=entry;var files=f.FileHashes();
                await Call(f.Window,"UnlikeSelectedDownloadAsync");
                Assert(!entry.RemovedFromCollection&&!entry.SkipFavoritesDownload&&SaField<HashSet<string>>(f.Window,"_favoriteTrackIds").Contains("ul-dead"),"failed/unconfirmed mutation changed collection state");
                Assert(f.FileHashes()==files&&f.Writes.Count==(failure=="owner"?0:1),"failure altered local data, guessed access or retried automatically");
                Assert(f.Unlike.IsEnabled&&f.Retry.IsEnabled,"failure did not restore explicit actions");
            }
        });
        await Run("Unavailable likes: empty row, unknown likes, signed-out and another-account queues cannot send a request", async () =>
        {
            using var f=new UlFixture("gates");await f.Start();var w=f.Window;var entry=f.Queue.Entries[0];
            f.List.SelectedItem=entry;SaInvoke(w,"SelectDownloadContextRow",(object)null!);await Call(w,"UnlikeSelectedDownloadAsync");Assert(!f.Unlike.IsEnabled&&f.Writes.Count==0,"blank-area menu targeted an old selection");
            f.List.SelectedItem=entry;UlSet(w,"_favoriteIdsKnown",false);SaInvoke(w,"RefreshDownloadUnlikeAction");await Call(w,"UnlikeSelectedDownloadAsync");Assert(!f.Unlike.IsEnabled&&f.Writes.Count==0,"unknown favorite state allowed unlike");
            UlSet(w,"_favoriteIdsKnown",true);UlSet(w,"_account",new Account("67890","Other fixture",true));SaInvoke(w,"RefreshDownloadUnlikeAction");await Call(w,"UnlikeSelectedDownloadAsync");Assert(!f.Menu.IsEnabled&&f.Writes.Count==0,"old batch targeted another account");
            UlSet(w,"_account",new Account("12345","Fixture",true));UlSet(w,"_session",null);SaInvoke(w,"RefreshDownloadUnlikeAction");await Call(w,"UnlikeSelectedDownloadAsync");Assert(!f.Unlike.IsEnabled&&f.Writes.Count==0,"signed out state allowed a write");
        });
        await Run("Unavailable likes: a late favorites ID or metadata response cannot restore a confirmed unlike or close Settings", async () =>
        {
            foreach(var phase in new[]{"ids","metadata"})
            {
                using var f=new UlFixture("race-"+phase);await f.Start();f.HoldRead=phase;
                var refresh=f.Window.RefreshLibraryAsync();await f.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                f.List.SelectedItem=f.Queue.Entries.Single(e=>e.Track.Id=="ul-dead");f.HoldWrite=true;
                var unlike=Call(f.Window,"UnlikeSelectedDownloadAsync");await f.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                SaInvoke(f.Window,"Settings_Click",null!,null!);Control<Slider>(f.Window,"CrossfadeSetting").Value=9.5;
                f.WriteRelease.TrySetResult();await unlike;f.ReadRelease.TrySetResult();await refresh;
                Assert(!SaField<List<TrackRow>>(f.Window,"_favoritesCache").Any(t=>t.Id=="ul-dead")&&!SaField<HashSet<string>>(f.Window,"_favoriteTrackIds").Contains("ul-dead"),"late refresh revived removed ID");
                Assert(SaField<bool>(f.Window,"_settingsOpen")&&Control<Slider>(f.Window,"CrossfadeSetting").Value==9.5&&f.Player.CrossfadeSeconds==3,"unlike/refresh closed Settings or reset/committed the draft");
                SaClick(Control<Button>(f.Window,"SettingsCancelButton"));SaInvoke(f.Window,"Favorites_Click",null!,null!);
                Assert(!Control<ListView>(f.Window,"TracksList").Items.Cast<TrackRow>().Any(t=>t.Id=="ul-dead"),"removed track returned to the visible collection");
            }
        });
        await Run("Unavailable likes: late reply after signout cannot update another account or mark its queue", async () =>
        {
            using var f=new UlFixture("signout");await f.Start();var entry=f.Queue.Entries[0];f.List.SelectedItem=entry;f.HoldWrite=true;
            var task=Call(f.Window,"UnlikeSelectedDownloadAsync");await f.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Window.SignOut();UlSet(f.Window,"_account",new Account("67890","Other fixture",true));UlSet(f.Window,"_session",FakeSession);
            var favorites=SaField<HashSet<string>>(f.Window,"_favoriteTrackIds");favorites.Add("new-account-id");UlSet(f.Window,"_favoriteIdsKnown",true);
            f.WriteRelease.TrySetResult();await task;
            Assert(favorites.SetEquals(["new-account-id"])&&!entry.RemovedFromCollection&&f.Writes.Count==1&&!f.Unlike.IsEnabled,"stale reply affected the new account or download row");
        });
    }
    private static TrackRow UlRow(string id,bool available)=>new(){Id=id,RequestId=id,Title=id=="ul-dead"?"Альфа · недоступный трек":id=="ul-error"?"Ясно · ошибка загрузки":"Другой трек",Artist="Тестовый исполнитель",DurationMs=180000,Available=available};
    private static void UlSet(MainWindow w,string field,object? value)=>typeof(MainWindow).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(w,value);
    private static async Task UlCapture(UlFixture f,string name)
    {
        foreach(var(width,height)in new[]{(1280,860),(1050,680)})
        {f.Window.Width=width;f.Window.Height=height;f.Window.UpdateLayout();await Task.Delay(300);Assert(f.Unlike.IsVisible&&f.Unlike.ActualWidth>60,"unlike action is clipped");App.Capture(f.Window,Path.Combine(_directory,$"unavailable-likes-{name}-{width}.png"));}
    }
    private sealed class UlFixture:IDisposable
    {
        private readonly object _gate=new();private readonly HashSet<string> _likes=["ul-dead","ul-error","ul-other"];
        internal readonly TrackRow[] Rows=[UlRow("ul-dead",false),UlRow("ul-error",true),UlRow("ul-other",false)];
        internal MainWindow Window{get;}internal NativePlayer Player{get;}internal OfflineLibrary Library{get;}
        internal string DirectoryPath{get;}internal ConcurrentQueue<string> Writes{get;}=[];
        internal bool HoldWrite;internal string? HoldRead,Failure;internal int MetadataRequests,AudioRequests,ResolveRequests,FailureAtWrite;
        internal TaskCompletionSource WriteEntered=new(TaskCreationOptions.RunContinuationsAsynchronously),WriteRelease=new(TaskCreationOptions.RunContinuationsAsynchronously),ReadEntered=new(TaskCreationOptions.RunContinuationsAsynchronously),ReadRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DownloadQueueState Queue=>SaField<DownloadQueueState>(Window,"_downloadQueue");
        internal ListView List=>((Grid)Control<ContentControl>(Window,"DownloadCenterPanel").Content).Children.OfType<ListView>().Single();
        internal Button Unlike=>SaVisuals<Button>(Window.DownloadCenterView).Single(b=>b.Name=="DownloadUnlikeButton");
        internal Button Retry=>SaVisuals<Button>(Window.DownloadCenterView).Single(b=>b.Name=="DownloadRetryButton");
        internal MenuItem Menu=>List.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString()?.StartsWith("Убрать из «Мне нравится»", StringComparison.Ordinal) == true);
        internal UlFixture(string name)
        {
            DirectoryPath=Path.Combine(_directory,"unavailable-likes-"+name);Directory.CreateDirectory(DirectoryPath);
            var noAudio=new FakeHandler((_,_)=>{AudioRequests++;throw new InvalidOperationException("unlike requested audio");});
            Library=new OfflineLibrary(Path.Combine(DirectoryPath,"music"),new AudioBuffer(Path.Combine(DirectoryPath,"downloads"),noAudio));
            Directory.CreateDirectory(Library.DirectoryPath);File.WriteAllText(Path.Combine(Library.DirectoryPath,"kept-file.txt"),"own fixture must remain unchanged");
            Player=new NativePlayer(new AudioBuffer(Path.Combine(DirectoryPath,"audio"),noAudio));
            Window=new MainWindow(api:new MusicApi(new FakeHandler(Respond)),store:new SessionStore(Path.Combine(DirectoryPath,"session")),restore:false,diagnosticsDirectory:DirectoryPath,offline:Library,player:Player)
            {ShowActivated=false,ShowInTaskbar=false,Left=-16000,Top=0,WindowStartupLocation=WindowStartupLocation.Manual};
            Window.Show();Control<Slider>(Window,"VolumeSlider").Value=0;UlSet(Window,"_session",FakeSession);UlSet(Window,"_account",new Account("12345","Fixture",true));
            Control<TextBlock>(Window,"AccountText").Text="Fixture Account";Control<TextBlock>(Window,"ModeLabel").Text="Тестовый аккаунт";Control<Button>(Window,"LoginButton").Visibility=Visibility.Collapsed;
            SaField<HashSet<string>>(Window,"_favoriteTrackIds").UnionWith(_likes);UlSet(Window,"_favoriteIdsKnown",true);SaField<List<TrackRow>>(Window,"_favoritesCache").AddRange(Rows);SaField<Diagnostics>(Window,"_diagnostics").TotalTracks=Rows.Length;
            SaInvoke(Window,"ShowRows",(object)Rows);
        }
        internal async Task Start()
        {await Call(Window,"StartDownloadBatchAsync",Rows,"Мне нравится · тест",new PlaylistDetails("local:favorites","liked","Мне нравится","",0,Rows),false);SaInvoke(Window,"DownloadCenter_Click",null!,null!);Window.UpdateLayout();}
        internal bool IsLiked(string id){lock(_gate)return _likes.Contains(id);}
        internal string FileHashes()=>string.Join(";",Directory.EnumerateFiles(Library.DirectoryPath,"*",SearchOption.AllDirectories).Order().Select(p=>Path.GetRelativePath(Library.DirectoryPath,p)+":"+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request,CancellationToken token)
        {
            Assert(request.RequestUri!.Host=="api.music.yandex.net","unlike left API host");var path=request.RequestUri.AbsolutePath;
            if(path=="/account/status")return Failure=="owner"?Json("""{"result":{"account":{"uid":67890,"displayName":"Other"},"plus":{"hasPlus":true}}}"""):Json(Profile);
            if(path=="/get-file-info"){ResolveRequests++;return new(HttpStatusCode.ServiceUnavailable){Content=new StringContent("fixture resolve failure")};}
            if(path=="/users/12345/likes/tracks/remove")
            {
                Assert(request.Method==HttpMethod.Post,"unlike used the wrong method");var body=Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(token));
                var id=body.Split('&').Single(p=>p.StartsWith("track-ids=",StringComparison.Ordinal))[10..];Writes.Enqueue(id);
                WriteEntered.TrySetResult();if(HoldWrite)await WriteRelease.Task;
                var failure=FailureAtWrite==0||FailureAtWrite==Writes.Count?Failure:null;
                if(failure=="http")return new(HttpStatusCode.ServiceUnavailable){Content=new StringContent("fixture write failure")};
                if(failure!="readback")lock(_gate)_likes.Remove(id);
                return Json(failure=="revision"?"{\"result\":{}}":"{\"result\":{\"revision\":7}}");
            }
            if(path=="/users/12345/likes/tracks")
            {
                string json;lock(_gate)json=JsonSerializer.Serialize(new{result=new{library=new{tracks=_likes.Order().Select(id=>new{id}).ToArray()}}});
                if(HoldRead=="ids"){HoldRead=null;ReadEntered.TrySetResult();await ReadRelease.Task;}
                return Json(json);
            }
            if(path=="/tracks")
            {
                MetadataRequests++;var body=Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(token));var ids=body.Split('&').Single(p=>p.StartsWith("track-ids=",StringComparison.Ordinal))[10..].Split(',');
                var json=JsonSerializer.Serialize(new{result=ids.Select(id=>Rows.Single(t=>t.Id==id)).Select(t=>new{id=t.Id,title=t.Title,artists=new[]{new{name=t.Artist}},durationMs=t.DurationMs,available=t.Available}).ToArray()});
                if(HoldRead=="metadata"){HoldRead=null;ReadEntered.TrySetResult();await ReadRelease.Task;}
                return Json(json);
            }
            throw new InvalidOperationException("Unexpected fixture route: "+path);
        }
        public void Dispose(){WriteRelease.TrySetResult();ReadRelease.TrySetResult();Window.Close();}
    }
}
