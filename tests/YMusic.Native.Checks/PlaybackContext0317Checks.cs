using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckPlaybackContext0317()
    {
        await Run("PlaybackContext0317: playlist manual automatic previous and shuffled commits select the current row without scrolling", async () =>
        {
            using var f = new Pc0317Fixture("transport"); var rows = f.Rows(8);
            f.Playlist("origin", rows); await f.Play(rows[0]);
            var origin = Pc0317Get<object>(f.Window, "_playbackContext");
            Pc0317Invoke(f.Window, "ChangeListSort", new ListSortRule(ListSortField.Title, true));
            foreach (var step in new[] { (1, false), (1, true), (-1, false) })
            {
                await Pc0317Call(f.Window, "AdvanceAsync", step.Item1, step.Item2);
                Assert(ReferenceEquals(f.List.SelectedItem, f.Queue.Current), "transport left selection on a former playlist track");
            }
            f.Queue.Shuffle = true; await Pc0317Call(f.Window, "AdvanceAsync", 1, false);
            Assert(ReferenceEquals(f.List.SelectedItem, f.Queue.Current) && ReferenceEquals(origin, Pc0317Get<object>(f.Window, "_playbackContext")),
                "shuffle changed source or failed to select its actual current track");
            Pc0317Evidence("transport", new { f.Queue.CurrentIndex, current = f.Queue.Current!.RequestId, selected = ((TrackRow)f.List.SelectedItem).RequestId });
        });
        await Run("PlaybackContext0317: another viewed playlist cannot replace source and explicit return preserves native playback", async () =>
        {
            using var f = new Pc0317Fixture("return"); var rows = f.Rows(5); f.Playlist("origin", rows); await f.Play(rows[0]);
            f.Playlist("other", f.Rows(3, "other")); await Pc0317Call(f.Window, "AdvanceAsync", 1, false);
            Assert(f.List.SelectedItem is null && Pc0317Get<PlaylistDetails>(f.Window, "_activePlaylist").Kind == "other", "playback selected or navigated an unrelated playlist");
            var slot = Pc0317Get<object>(f.Player, "_current"); var version = f.Queue.Version; var position = f.Player.Position;
            f.Return(); await Pc0317Idle();
            Assert(Pc0317Get<PlaylistDetails>(f.Window, "_activePlaylist").Kind == "origin" && ReferenceEquals(f.List.SelectedItem, rows[1]), "return did not restore the source and current row");
            Assert(ReferenceEquals(slot, Pc0317Get<object>(f.Player, "_current")) && f.Queue.Version == version && f.Player.Position >= position
                && (f.Player.Position - position).TotalSeconds < 1, "return reopened, sought or changed playback");
            Assert((string)f.Button.ToolTip == "К списку воспроизведения · Плейлист origin", "tooltip follows the viewed page rather than queue source");
        });
        await Run("PlaybackContext0317: fresh playlist rows select by exact composite RequestId and a fresh start replaces source", async () =>
        {
            using var f = new Pc0317Fixture("fresh"); var old = f.Rows(4); f.Playlist("origin", old); await f.Play(old[0]);
            var fresh = f.Rows(4); f.Playlist("origin", fresh);
            await Pc0317Call(f.Window, "AdvanceAsync", 1, false);
            Assert(ReferenceEquals(f.List.SelectedItem, fresh[1]) && !ReferenceEquals(fresh[1], f.Queue.Current), "fresh metadata rows require old object identity");
            var exact = new[] { f.Row("same", "album-A"), f.Row("same", "album-B") };
            f.Playlist("new-origin", exact); await f.Play(exact[1]);
            f.Playlist("other", f.Rows(1, "unrelated")); f.Return(); await Pc0317Idle();
            Assert(Pc0317Get<PlaylistDetails>(f.Window, "_activePlaylist").Kind == "new-origin" && ReferenceEquals(f.List.SelectedItem, exact[1])
                && f.Queue.Current!.RequestId == "same:album-B", "new launch or equal entity IDs lost the exact album-bearing track identity");
        });
        await Run("PlaybackContext0317: favorites and album snapshots restore the source list without a service request", async () =>
        {
            foreach (var section in new[] { "favorites", "album-detail" })
            {
                using var f = new Pc0317Fixture(section); var rows = f.Rows(4); Pc0317Invoke(f.Window, "SetSection", section, "Источник " + section);
                if (section == "album-detail") Pc0317Set(f.Window, "_openedCatalogItem", new CatalogItem("source-album", "album", "Исходный альбом", ""));
                Pc0317Invoke(f.Window, "ShowRows", (object)rows);
                if (section == "album-detail") Pc0317Invoke(f.Window, "ShowCollectionHero", new AlbumDetails("source-album", "Исходный альбом", "Fixture", rows), null);
                await f.Play(rows[0]); f.Playlist("other", f.Rows(2, "other")); await Pc0317Call(f.Window, "AdvanceAsync", 1, false);
                f.Return(); await Pc0317Idle();
                Assert(Pc0317Get<string>(f.Window, "_section") == section && ReferenceEquals(f.List.SelectedItem, rows[1]), "return restored a different source type");
                if (section == "album-detail") Assert(Pc0317Get<AlbumDetails>(f.Window, "_activeAlbumDetails").Id == "source-album", "album metadata/header lost its exact ID");
                Assert(f.Handler.Requests == 0, "source snapshot return reached the service");
            }
        });
        await Run("PlaybackContext0317: filtered source keeps its filter unless explicit return must reveal the current track", async () =>
        {
            using var f = new Pc0317Fixture("filter"); var rows = f.Rows(5); f.Playlist("origin", rows);
            f.Filter("Трек"); await f.Play(rows[0]); f.Filter("Трек 0");
            await Pc0317Call(f.Window, "AdvanceAsync", 1, false);
            Assert(f.List.SelectedItem is null && f.FilterText.Text == "Трек 0", "ordinary Next forced a filtered row into view");
            f.Playlist("other", f.Rows(2, "other")); f.Return(); await Pc0317Idle();
            Assert(f.FilterText.Text == "Трек" && ReferenceEquals(f.List.SelectedItem, rows[1]), "return discarded a source filter that still includes current");
            f.Filter("Трек 0"); f.Return(); await Pc0317Idle();
            Assert(f.FilterText.Text.Length == 0 && ReferenceEquals(f.List.SelectedItem, rows[1]), "explicit return kept a filter that hides current");
            f.Filter("Трек 1"); f.Return(); await Pc0317Idle();
            Assert(f.FilterText.Text == "Трек 1", "return on the same source discarded a current-inclusive filter");
        });
        await Run("PlaybackContext0317: return closes live Settings through its save guard without rolling back changes", async () =>
        {
            using var f = new Pc0317Fixture("settings"); var rows = f.Rows(3); f.Playlist("origin", rows); await f.Play(rows[0]);
            f.Playlist("other", f.Rows(1, "other")); Pc0317Invoke(f.Window, "Settings_Click", null, null);
            ((Slider)f.Window.FindName("CrossfadeSetting")).Value = 7.5;
            Assert(Pc0317Get<bool>(f.Window, "_settingsSavePending"), "settings fixture failed to create a pending live save");
            f.Return(); await Pc0317Idle();
            Assert(!Pc0317Get<bool>(f.Window, "_settingsOpen") && !Pc0317Get<bool>(f.Window, "_settingsSavePending")
                && f.Player.CrossfadeSeconds == 7.5 && f.Preferences.Load().CrossfadeSeconds == 7.5,
                "return reset a live setting or failed to flush its pending save");
        });
        await Run("PlaybackContext0317: source arrays are fixed at launch and normal Back selects current instead of stale selection", async () =>
        {
            using var f = new Pc0317Fixture("snapshot"); var original = f.Rows(4); var mutable = original.ToList();
            f.Playlist("origin", mutable); await f.Play(original[0]);
            f.List.SelectedItem = original[3]; f.Playlist("other", f.Rows(2, "other"));
            await Pc0317Call(f.Window, "AdvanceAsync", 1, false);
            Pc0317Invoke(f.Window, "BrowseBack_Click", null, null); await Pc0317Idle();
            Assert(ReferenceEquals(f.List.SelectedItem, original[1]), "normal Back restored a stale selection over current playback");
            mutable.Clear();
            f.Playlist("other", f.Rows(1, "other")); f.Return(); await Pc0317Idle();
            Assert(f.List.Items.Count == 4 && ReferenceEquals(f.List.SelectedItem, original[1]), "mutable service/cache list emptied the captured source");
        });
        await Run("PlaybackContext0317: source return uses confirmed favorites removals and the latest shown playlist revision", async () =>
        {
            using var f = new Pc0317Fixture("fresh-source"); var rows = f.Rows(4);
            Pc0317Invoke(f.Window, "SetSection", "favorites", "Мне нравится"); Pc0317Invoke(f.Window, "ShowRows", (object)rows);
            Pc0317Get<List<TrackRow>>(f.Window, "_favoritesCache").AddRange(rows);
            Pc0317Get<HashSet<string>>(f.Window, "_favoriteTrackIds").UnionWith(rows.Select(row => row.Id)); Pc0317Set(f.Window, "_favoriteIdsKnown", true);
            await f.Play(rows[0]); Pc0317Invoke(f.Window, "ApplyConfirmedTrackLike", rows[0], false, false);
            f.Playlist("other", f.Rows(1, "other")); f.Return(); await Pc0317Idle();
            Assert(f.List.Items.Count == 3 && !f.List.Items.Cast<TrackRow>().Any(row => row.Id == rows[0].Id), "source return resurrected a confirmed removed favorite");
            f.Playlist("origin", rows); await f.Play(rows[0]);
            Pc0317Invoke(f.Window, "ShowPlaylist", new PlaylistDetails("owner", "origin", "Свежий плейлист", "", 2, rows[1..]));
            f.Playlist("other", f.Rows(1, "other")); f.Return(); await Pc0317Idle();
            Assert(Pc0317Get<PlaylistDetails>(f.Window, "_activePlaylist").Revision == 2 && f.List.Items.Count == 3
                && !f.List.Items.Cast<TrackRow>().Any(row => row.Id == rows[0].Id), "source return replaced a known new playlist revision with its old launch copy");
        });
        await Run("PlaybackContext0317: ordinary start pause and resume stay quiet while informative errors remain", async () =>
        {
            using var f = new Pc0317Fixture("quiet-playback"); var rows = f.Rows(3);f.Playlist("origin", rows);await f.Play(rows[0]);
            var status = (TextBlock)f.Window.FindName("StatusText");
            Assert(status.Text.Length == 0, "ordinary track start left a routine playback notification");
            Pc0317Invoke(f.Window, "SetStatus", "Синтетическая ошибка воспроизведения", true);
            await Pc0317Call(f.Window, "TogglePlaybackAsync");
            Assert(!f.Player.IsPlaying && status.Text == "Синтетическая ошибка воспроизведения", "pause replaced an informative error with a routine toast");
            await Pc0317Call(f.Window, "TogglePlaybackAsync");
            Assert(f.Player.IsPlaying && status.Text == "Синтетическая ошибка воспроизведения", "resume replaced an informative error with a routine toast");
        });
        await Run("PlaybackContext0317: unknown replaced queues account generations and logout cannot expose a former source", async () =>
        {
            using var f = new Pc0317Fixture("guards"); var rows = f.Rows(3); await f.Window.PlayTrackAsync(rows[0]);
            Assert(f.Button.Visibility == Visibility.Collapsed && !f.Button.IsEnabled, "direct unknown playback guessed its source");
            f.Playlist("origin", rows); await f.Play(rows[0]);
            f.Queue.SetItems([rows[2]], rows[2]); await f.Window.PlayTrackAsync(rows[2]);
            Assert(f.Button.Visibility == Visibility.Collapsed && Pc0317Get<object?>(f.Window, "_playbackContext") is null, "new unbound queue retained an old source");
            f.Playlist("origin", rows); await f.Play(rows[0]);
            Pc0317Set(f.Window, "_account", new Account("different-account", "Other private fixture", true)); Pc0317Invoke(f.Window, "UpdateControls");
            Assert(f.Button.Visibility == Visibility.Collapsed, "different account UID exposed a former source without a generation change");
            Pc0317Set(f.Window, "_account", new Account("fixture", "Private fixture", true));
            Pc0317Set(f.Window, "_accountGeneration", Pc0317Get<int>(f.Window, "_accountGeneration") + 1); Pc0317Invoke(f.Window, "UpdateControls");
            Assert(f.Button.Visibility == Visibility.Collapsed, "account-generation change exposed a former account source");
            f.Playlist("new-origin", rows); await f.Play(rows[0]); f.Return(); f.Window.SignOut(); await Pc0317Idle();
            Assert(Pc0317Get<object?>(f.Window, "_playbackContext") is null && f.Button.Visibility == Visibility.Collapsed
                && Pc0317Get<Dictionary<string,List<TrackRow>>>(f.Window, "_playbackContextRows").Count == 0
                && Pc0317Get<string>(f.Window, "_section") == "favorites", "a pending return callback restored account data after logout");
        });
        await Run("PlaybackContext0317: late page IO cannot overwrite an explicit source return or a signed-out view", async () =>
        {
            foreach (var signOut in new[] { false, true })
            {
                using var f = new Pc0317Fixture("late-" + signOut); var rows = f.Rows(3); f.Playlist("origin", rows); await f.Play(rows[0]);
                f.Handler.Block = true;
                var pending = Pc0317Call(f.Window, "OpenCatalogAsync", new CatalogItem("pending", "playlist", "Late page", "", Owner:"owner", PlaylistKind:"pending"), true);
                await f.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (signOut) f.Window.SignOut(); else f.Return();
                f.Handler.Release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); await Pc0317Idle();
                Assert(signOut ? Pc0317Get<string>(f.Window, "_section") == "favorites" && !f.Player.HasSource
                    : Pc0317Get<PlaylistDetails>(f.Window, "_activePlaylist").Kind == "origin" && ReferenceEquals(f.List.SelectedItem, rows[0]), "stale service completion replaced the protected view");
            }
        });
        await Run("PlaybackContext0317: 3900 rows retain their index and user selection through progress observations", async () =>
        {
            using var f = new Pc0317Fixture("large"); var rows = f.Rows(3900); f.Playlist("origin", rows); await f.Play(rows[0]);
            var index = Pc0317Get<object>(f.Window, "_playbackContextRows"); f.List.SelectedItem = rows[2400];
            var clock = Stopwatch.StartNew(); for (var i = 0; i < 100; i++) Pc0317Invoke(f.Window, "UpdateProgress");
            Assert(ReferenceEquals(index, Pc0317Get<object>(f.Window, "_playbackContextRows")) && ReferenceEquals(f.List.SelectedItem, rows[2400]),
                "progress rebuilt source lookup or forced current selection over user navigation");
            var elapsed = clock.Elapsed.TotalMilliseconds;
            await Pc0317Call(f.Window, "AdvanceAsync", 1, false);
            Assert(ReferenceEquals(f.List.SelectedItem, rows[1]), "large-list commit did not select current");
            Pc0317Evidence("large", new { rows = rows.Length, observations = 100, elapsedMs = elapsed, sameIndex = true, noProgressSelectionWrites = true });
        });
        await Run("PlaybackContext0317: compact accessible vector button uses the existing player chrome and renders privately", async () =>
        {
            using var f = new Pc0317Fixture("button"); var rows = f.Rows(2); f.Playlist("origin", rows); await f.Play(rows[0]);
            var button = f.Button; var parent = (Panel)button.Parent; var position = parent.Children.IndexOf(button); parent.Children.Remove(button);
            try
            {
                Assert(button.Width == 30 && button.Content is System.Windows.Shapes.Path { Data: not null }
                    && AutomationProperties.GetName(button).Contains("Плейлист origin", StringComparison.Ordinal)
                    && button.Style.BasedOn == Application.Current.FindResource("PlayerButton"), "compact source action lacks its vector, context or existing style");
                var frame = new Border { Background = new SolidColorBrush(Color.FromRgb(23,25,29)), Padding = new Thickness(10), Child = button };
                frame.Measure(new Size(50,54)); frame.Arrange(new Rect(0,0,50,54)); frame.UpdateLayout();
                var bitmap = new RenderTargetBitmap(50,54,96,96,PixelFormats.Pbgra32); bitmap.Render(frame);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.Combine(_directory,"playback-context-button.png"))) encoder.Save(output);
                frame.Child = null;
            }
            finally { parent.Children.Insert(position, button); }
        });
        await Run("PlaybackContext0317: main source queue lyrics and expand actions fit at minimum and normal widths", async () =>
        {
            using var f = new Pc0317Fixture("player-layout");var rows = f.Rows(2);f.Playlist("origin",rows);await f.Play(rows[0]);
            var player = (Border)f.Window.FindName("PlayerBar");var parent = (Panel)player.Parent;var originalIndex = parent.Children.IndexOf(player);
            parent.Children.Remove(player);var measurements = new List<object>();var host = new Border { Child = player };
            try
            {
                foreach(var windowWidth in new[]{1050d,1280d})
                {
                    var width=windowWidth-220;host.Measure(new Size(width,118));host.Arrange(new Rect(0,0,width,118));host.UpdateLayout();
                    Rect Bounds(FrameworkElement element) => new(element.TranslatePoint(new Point(),player),element.RenderSize);
                    var buttons = new[]{"PlaybackContextButton","PlayerQueueButton","PlayerLyricsButton","ExpandPlayerButton"}.Select(name=>(Button)f.Window.FindName(name)).ToArray();
                    Pc0317Evidence("player-layout-before-"+(int)windowWidth,new{windowWidth,playerBounds=Bounds(player),actions=buttons.Select(button=>new{button.Name,bounds=Bounds(button),button.Visibility,button.IsVisible}).ToArray()});
                    var previous = double.NegativeInfinity;
                    foreach(var button in buttons)
                    {
                        var bounds=Bounds(button);
                        Assert(bounds.Left>=0 && bounds.Right<=width && bounds.Top>=0 && bounds.Bottom<=118 && bounds.Width>=button.Width-.1 && bounds.Left>=previous-.1,
                            "compact player action clips or overlaps at width "+windowWidth+": "+button.Name);
                        previous=bounds.Right;
                    }
                    var volume=Bounds((Slider)f.Window.FindName("VolumeSlider"));Assert(volume.Right<=width && volume.Left>=previous,"compact actions overlap volume");
                    var bitmap=new RenderTargetBitmap((int)width,118,96,96,PixelFormats.Pbgra32);bitmap.Render(player);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using(var output=File.Create(Path.Combine(_directory,"playback-context-player-"+(int)windowWidth+".png")))encoder.Save(output);
                    measurements.Add(new{windowWidth,playerWidth=width,actions=buttons.Select(button=>new{button.Name,bounds=Bounds(button)}).ToArray(),volume});
                }
            }
            finally{host.Child=null;parent.Children.Insert(originalIndex,player);}
            Pc0317Evidence("player-layout",measurements);
        });
        await Run("PlaybackContext0317: return restores the source sort rather than the subsequently viewed playlist sort", async () =>
        {
            foreach (var changeSourceAfterPlay in new[] { false, true })
            {
                using var f = new Pc0317Fixture("sort-restore-" + changeSourceAfterPlay); var rows = f.Rows(3);
                f.Playlist("origin", rows); Pc0317Invoke(f.Window, "ChangeListSort", new ListSortRule(ListSortField.Title, false)); await f.Play(rows[0]);
                if (changeSourceAfterPlay) Pc0317Invoke(f.Window, "ChangeListSort", new ListSortRule(ListSortField.Title, true));
                f.Playlist("other", f.Rows(2, "other")); Pc0317Invoke(f.Window, "ChangeListSort", new ListSortRule(ListSortField.Title, !changeSourceAfterPlay));
                f.Return(); await Pc0317Idle();
                var expected = changeSourceAfterPlay ? rows.Reverse() : rows;
                Assert(f.List.Items.Cast<TrackRow>().Select(row => row.RequestId).SequenceEqual(expected.Select(row => row.RequestId)),
                    "another playlist's sort replaced the last source-view sort");
                Assert(ReferenceEquals(f.List.SelectedItem, rows[0]), "restoring sorting lost the currently playing row");
            }
        });
        await Run("PlaybackContext0317: explicit return to the already visible source rejects its older pending refresh", async () =>
        {
            using var f = new Pc0317Fixture("same-source-refresh"); var rows = f.Rows(3);
            f.Playlist("pending", rows); await f.Play(rows[0]); f.Handler.Block = true;
            var pending = Pc0317Call(f.Window, "RefreshCatalogViewAsync"); await f.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var before = Pc0317Get<int>(f.Window, "_viewGeneration"); f.Return();
            Assert(Pc0317Get<int>(f.Window, "_viewGeneration") > before, "same-source return did not revoke the older page request");
            f.Handler.Release.TrySetResult(); await pending; await Pc0317Idle();
            Assert(f.List.Items.Count == 3 && f.List.Items.Cast<TrackRow>().SequenceEqual(rows) && ReferenceEquals(f.List.SelectedItem, rows[0]),
                "late same-source refresh replaced the returned source or its current selection");
        });
    }

    private static void Pc0317Evidence(string name, object data) => File.WriteAllText(Path.Combine(_directory, "playback-context-" + name + ".json"),
        JsonSerializer.Serialize(new { data, productSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location))),
            runtime = Environment.Version.ToString(), realAccountUsed = false, userProfileRead = false, externalRequests = 0, applicationWindowsShown = false }, new JsonSerializerOptions { WriteIndented = true }));
    private static object? Pc0317Invoke(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethods(BindingFlags.Instance|BindingFlags.NonPublic).Single(m => m.Name == name && m.GetParameters().Length == args.Length
            && m.GetParameters().Select((parameter,index) => args[index] is null || parameter.ParameterType.IsInstanceOfType(args[index])).All(match => match));
        return method.Invoke(target,args);
    }
    private static Task Pc0317Call(object target,string name,params object?[] args) => (Task)Pc0317Invoke(target,name,args)!;
    private static T Pc0317Get<T>(object target,string name) => (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Pc0317Set(object target,string name,object? value) => target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(target,value);
    private static async Task Pc0317Idle() { await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); }

    private sealed class Pc0317Fixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal PreferencesStore Preferences { get; }
        internal Pc0317Handler Handler { get; } = new();
        internal PlaybackQueue Queue => Pc0317Get<PlaybackQueue>(Window,"_playbackQueue");
        internal ListView List => (ListView)Window.FindName("TracksList");
        internal Button Button => (Button)Window.FindName("PlaybackContextButton");
        internal TextBox FilterText => (TextBox)Window.FindName("FilterBox");
        private readonly string _wave;
        internal Pc0317Fixture(string name)
        {
            var directory = Path.Combine(_directory,"playback-context-"+name);Directory.CreateDirectory(directory);
            _wave = Path.Combine(directory,"private.wav");
            using (var output = new BinaryWriter(File.Create(_wave)))
            {
                const int rate=22050,frames=rate*10; output.Write(Encoding.ASCII.GetBytes("RIFF"));output.Write(36+frames*2);output.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                output.Write(16);output.Write((short)1);output.Write((short)1);output.Write(rate);output.Write(rate*2);output.Write((short)2);output.Write((short)16);output.Write(Encoding.ASCII.GetBytes("data"));output.Write(frames*2);
                for(var i=0;i<frames;i++)output.Write((short)(Math.Sin(i*2*Math.PI*997/rate)*500));
            }
            Preferences = new PreferencesStore(Path.Combine(directory,"preferences"));Preferences.Save(new PlayerPreferences { Volume=0,CrossfadeSeconds=0 });
            Player = new NativePlayer(new AudioBuffer(Path.Combine(directory,"buffer"),Handler));
            Window = new MainWindow(api:new MusicApi(Handler),store:new SessionStore(Path.Combine(directory,"session")),restore:false,diagnosticsDirectory:Path.Combine(directory,"diagnostics"),
                offline:new OfflineLibrary(Path.Combine(directory,"offline"),new AudioBuffer(Path.Combine(directory,"download"),Handler)),player:Player,preferences:Preferences);
            Pc0317Set(Window,"_account",new Account("fixture","Private fixture",true));
            Pc0317Set(Window,"_session",new Session("private-fake-token",null,DateTimeOffset.UtcNow.AddHours(1),"fixture"));
            Pc0317Set(Window,"_playbackQueue",new PlaybackQueue(new Random(97)));
            // Deliberately never Show: no shell, SMTC, browser, updater or profile.
            Player.SetVolume(0);
        }
        internal TrackRow Row(string id,string album = "fixture-album",string? title = null) => new() { Id=id,RequestId=id+":"+album,Title=title??id,Artist="Private fixture",AlbumId=album,Album="Fixture album",Available=true,DurationMs=10000,LocalPath=_wave };
        internal TrackRow[] Rows(int count,string prefix="track") => Enumerable.Range(0,count).Select(i=>Row(prefix+"-"+i,title:"Трек "+i)).ToArray();
        internal void Playlist(string kind,IReadOnlyList<TrackRow> rows)
        {
            Pc0317Invoke(Window,"RememberBrowseView");
            Pc0317Invoke(Window,"ShowPlaylist",new PlaylistDetails("owner",kind,"Плейлист "+kind,"",1,rows));
        }
        internal Task Play(TrackRow row) { List.SelectedItem=row;return Pc0317Call(Window,"PlaySelectedAsync"); }
        internal void Return() => Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        internal void Filter(string query) { FilterText.Text=query;Pc0317Get<DispatcherTimer>(Window,"_filterTimer").Stop();Pc0317Set(Window,"_query",query);Pc0317Get<System.ComponentModel.ICollectionView>(Window,"_view").Refresh(); }
        public void Dispose() { Handler.Release.TrySetResult();Window.Close();Player.Dispose();Handler.Dispose(); }
    }
    private sealed class Pc0317Handler : HttpMessageHandler
    {
        internal bool Block;
        internal int Requests;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Requests++;
            Assert(Block && request.RequestUri!.AbsolutePath == "/users/owner/playlists/pending", "private source fixture attempted an unexpected network route");
            Entered.TrySetResult();await Release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent("{\"result\":{\"uid\":\"owner\",\"kind\":\"pending\",\"title\":\"Late page\",\"description\":\"\",\"revision\":1,\"trackCount\":0,\"tracks\":[]}}") };
        }
    }
}
