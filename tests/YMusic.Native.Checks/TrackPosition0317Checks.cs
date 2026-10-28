using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using YMusic.Native;

internal static partial class Program
{
    private static string Tp0317DirectoryPath => Path.Combine(_directory, "track-position-0317");
    private static async Task Tp0317Run(string name, Func<Task<object>> body)
    {
        await Run("TrackPosition0317: " + name, async () =>
        {
            var evidence = await body();
            File.AppendAllText(Path.Combine(Tp0317DirectoryPath, "evidence.jsonl"), JsonSerializer.Serialize(new
            {
                name, evidence, productSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location))),
                runtime = Environment.Version.ToString(), fixture = "private generated 60-second per-position frequency WAV", externalRequests = 0,
                realAccountUsed = false, userProfileRead = false, userMusicRead = false, applicationWindowsShown = false
            }) + Environment.NewLine);
        });
    }
    private static async Task CheckTrackPosition0317()
    {
        Directory.CreateDirectory(Tp0317DirectoryPath);
        foreach(var prepared in new[]{false,true})
        foreach(var crossfade in new[]{0d,3d})
        await Tp0317Run($"A seek 30 then Next B; prepared={prepared}; crossfade={crossfade}", async () =>
        {
            using var f=new Tp0317Fixture("next-"+prepared+"-"+crossfade); f.Player.SetCrossfade(crossfade);
            f.Queue.SetItems([f.A,f.B],f.A); await f.Window.PlayTrackAsync(f.A);
            Tp0317Invoke(f.Window,"CommitSeek",30d); await Tp0317Until(()=>f.Player.Position.TotalSeconds>=29.8);
            if(prepared) await f.Prepared(); else Tp0317Invoke(f.Window,"CancelNextPreparation",true);
            var before=f.State(); await Tp0317Call(f.Window,"AdvanceAsync",1,false);
            await Task.Delay(110); Tp0317Invoke(f.Window,"UpdateProgress"); var after=f.State();
            Tp0317Need(f.Player.SourceLocalPath==f.B.LocalPath && f.Player.Position.TotalSeconds<1,"B started at old native position");
            Tp0317Need(f.Main.Value<1,"B has old main timeline position");
            return new { before, after };
        });
        foreach(var direction in new[]{1,-1})
        await Tp0317Run($"Restored single repeat-all new navigation direction={direction} starts zero",async()=>
        {
            using var f=new Tp0317Fixture("restore-nav-"+direction, restored:true);
            var before=f.State(); await Tp0317Call(f.Window,"AdvanceAsync",direction,false);
            await Task.Delay(120); Tp0317Invoke(f.Window,"UpdateProgress"); var after=f.State();
            File.WriteAllText(Path.Combine(Tp0317DirectoryPath,"restore-nav-"+direction+".json"),JsonSerializer.Serialize(new{before,after},new JsonSerializerOptions{WriteIndented=true}));
            Tp0317Need(f.Player.Position.TotalSeconds<1,"new navigation resumed saved position: "+f.Player.Position.TotalSeconds);
            return new{before,after};
        });
        await Tp0317Run("Restored initial Play deliberately resumes 30 seconds",async()=>
        {
            using var f=new Tp0317Fixture("restore-play", restored:true);
            await f.Window.PlayTrackAsync(f.A); await Tp0317Until(()=>f.Player.Position.TotalSeconds>29.8);
            Tp0317Need(f.Player.Position.TotalSeconds<31,"initial resume position exceeded expected bound"); return f.State();
        });
        foreach(var dropped in new[]{"no-target", "wave-guard"})
        await Tp0317Run($"Unaccepted {dropped} Next preserves first restored resume",async()=>
        {
            using var f=new Tp0317Fixture("restore-retained-"+dropped, restored:true);
            if(dropped=="no-target") f.Queue.Repeat=RepeatMode.Off;
            else { Tp0317Set(f.Window,"_advancingPlayback",true); Tp0317Set(f.Window,"_advancingWave",true); }
            await Tp0317Call(f.Window,"AdvanceAsync",1,false);
            Tp0317Need(!f.Player.HasSource && Tp0317Get<double?>(f.Window,"_restoredPlaybackSeconds")==30,"unaccepted navigation lost the saved resume");
            Tp0317Set(f.Window,"_advancingPlayback",false); Tp0317Set(f.Window,"_advancingWave",false);
            await f.Window.PlayTrackAsync(f.A); await Tp0317Until(()=>f.Player.Position.TotalSeconds>=29.8);
            Tp0317Need(f.Player.Position.TotalSeconds<31,"retained resume exceeded its saved bound"); return f.State();
        });
        foreach(var view in new[]{"main","expanded","mini"})
        foreach(var gesture in new[]{"pointer","keyboard","keyboard-lost-focus"})
        await Tp0317Run($"Old {view} {gesture} completion after prepared Next cannot seek B",async()=>
        {
            using var f=new Tp0317Fixture("gesture-"+view+"-"+gesture); f.Queue.SetItems([f.A,f.B],f.A); await f.Window.PlayTrackAsync(f.A);
            f.Views(); await f.Prepared(); Tp0317Invoke(f.Window,"CommitSeek",30d); await Tp0317Until(()=>f.Player.Position.TotalSeconds>=29.8);
            var slider=view=="main"?f.Main:view=="expanded"?f.Expanded:f.Mini!.SeekSlider;
            if(gesture=="pointer") slider.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
            else slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,new Tp0317SyntheticSource(),Environment.TickCount,Key.Right){RoutedEvent=Keyboard.PreviewKeyDownEvent});
            slider.Value=37;
            await Tp0317Call(f.Window,"AdvanceAsync",1,false);
            // The hidden fixture has no DesktopShell. Mirror its synchronous
            // UpdateShellState notification before the old mini release arrives.
            f.UpdateMini();
            if(gesture=="pointer")slider.RaiseEvent(new DragCompletedEventArgs(0,0,false){RoutedEvent=Thumb.DragCompletedEvent});
            else if(gesture=="keyboard") slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,new Tp0317SyntheticSource(),Environment.TickCount,Key.Right){RoutedEvent=Keyboard.PreviewKeyUpEvent});
            else slider.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,Environment.TickCount,slider,null){RoutedEvent=Keyboard.LostKeyboardFocusEvent});
            await Task.Delay(90); Tp0317Invoke(f.Window,"UpdateProgress"); f.UpdateMini();
            Tp0317Need(f.Player.Position.TotalSeconds<1 && f.Main.Value<1 && f.Expanded.Value<1 && f.Mini!.SeekSlider.Value<1,"late gesture sought B or retained stale timeline");
            return f.State();
        });
        foreach(var repeat in new[]{RepeatMode.One,RepeatMode.All})
        await Tp0317Run($"Same TrackRow automatic repeat {repeat} resets native and all timelines",async()=>
        {
            using var f=new Tp0317Fixture("repeat-"+repeat);f.Queue.SetItems([f.A],f.A);f.Queue.Repeat=repeat;
            await f.Window.PlayTrackAsync(f.A); f.Views(); await f.Prepared();
            Tp0317Invoke(f.Window,"CommitSeek",30d);await Tp0317Until(()=>f.Player.Position.TotalSeconds>=29.8);
            await Tp0317Call(f.Window,"AdvanceAsync",1,true);await Task.Delay(90);Tp0317Invoke(f.Window,"UpdateProgress");f.UpdateMini();
            Tp0317Need(f.Player.Position.TotalSeconds<1 && f.Main.Value<1 && f.Expanded.Value<1 && f.Mini!.SeekSlider.Value<1,"same TrackRow repeat retained old position");return f.State();
        });
        await Tp0317Run("Rapid mixed navigation after seek preserves newest zero-position occurrence",async()=>
        {
            using var f=new Tp0317Fixture("rapid");f.Queue.SetItems([f.A,f.B,f.A,f.B],f.A);await f.Window.PlayTrackAsync(f.A);
            Tp0317Invoke(f.Window,"CommitSeek",30d);await Tp0317Until(()=>f.Player.Position.TotalSeconds>=29.8);
            await Task.WhenAll(Tp0317Call(f.Window,"AdvanceAsync",1,false),Tp0317Call(f.Window,"AdvanceAsync",1,false),Tp0317Call(f.Window,"AdvanceAsync",-1,false));
            await Task.Delay(90);Tp0317Invoke(f.Window,"UpdateProgress");
            Tp0317Need(f.Queue.CurrentIndex==1 && f.Player.SourceLocalPath==f.B.LocalPath && f.Player.Position.TotalSeconds<1,"rapid navigation retained old source/position");return f.State();
        });
        await Tp0317Run("Delayed obsolete quality completion after seek cannot move the newest occurrence",async()=>
        {
            using var delayed=new Tp0317DelayedResolve(); using var f=new Tp0317Fixture("delayed-quality", delayed:delayed);
            f.Queue.SetItems([f.A],f.A);await f.Window.PlayTrackAsync(f.A);
            f.Queue.SetItems([f.A,f.B,f.A],f.A);
            Tp0317Invoke(f.Window,"CommitSeek",30d);await Tp0317Until(()=>f.Player.Position.TotalSeconds>=29.8);
            var obsolete=Tp0317Call(f.Window,"AdvanceAsync",1,false);await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var current=Tp0317Call(f.Window,"AdvanceAsync",1,false); delayed.Release.TrySetResult();
            await Task.WhenAll(obsolete,current).WaitAsync(TimeSpan.FromSeconds(5));await Task.Delay(90);Tp0317Invoke(f.Window,"UpdateProgress");
            Tp0317Need(f.Queue.CurrentIndex==2 && f.Player.SourceLocalPath==f.A.LocalPath && f.Player.Position.TotalSeconds<1 && delayed.Requests==1,"obsolete quality completion transferred a seek/source or retried");
            return new { state=f.State(), fakeRequests=delayed.Requests };
        });
    }
    static void Tp0317Need(bool ok,string message) { if(!ok)throw new InvalidOperationException(message); }
    static object? Tp0317Invoke(object o,string method,params object?[] args)=>o.GetType().GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(o,args);
    static Task Tp0317Call(object o,string method,params object?[] args)=>(Task)Tp0317Invoke(o,method,args)!;
    static T Tp0317Get<T>(object o,string field)=>(T)o.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(o)!;
    static void Tp0317Set(object o,string field,object? value)=>o.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(o,value);
    static async Task Tp0317Until(Func<bool> condition) { var clock=System.Diagnostics.Stopwatch.StartNew(); while(!condition()){ if(clock.Elapsed.TotalSeconds>6)throw new TimeoutException(); await Task.Delay(20); } }
    sealed class Tp0317RejectNetwork:HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>throw new InvalidOperationException("External network forbidden: "+request.RequestUri!.Host); }
    sealed class Tp0317DelayedResolve:HttpMessageHandler
    {
        internal int Requests;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Tp0317Need(request.RequestUri!.AbsolutePath=="/get-file-info","unexpected fake quality route");
            Requests++; Entered.TrySetResult(); await Release.Task;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("private delayed response")};
        }
    }
    sealed class Tp0317Fixture:IDisposable
    {
        public MainWindow Window; public NativePlayer Player; public TrackRow A,B;
        public PlaybackQueue Queue=>Tp0317Get<PlaybackQueue>(Window,"_playbackQueue");
        public Slider Main=>(Slider)Window.FindName("SeekSlider");
        public Slider Expanded=>Tp0317Get<Slider>(Window,"_expandedPlayerSeek");
        public MiniPlayerWindow? Mini;
        readonly HttpMessageHandler Network;
        public Tp0317Fixture(string name, bool restored=false, Tp0317DelayedResolve? delayed=null)
        {
            var directory=Path.Combine(Tp0317DirectoryPath,name); Directory.CreateDirectory(directory);
            var music=Path.Combine(directory,"offline");Directory.CreateDirectory(music);
            A=Row("A",music,440);B=Row("B",music,880);Network=delayed is null?new Tp0317RejectNetwork():delayed;
            var diagnostics=Path.Combine(directory,"diagnostics");
            if(restored || delayed is not null)
            {
                var items=new[]{A,B}.Select(row=>new OfflineItem(row.Id,row.RequestId,row.Title,row.Artist,"private fixture",60000,
                    Path.GetFileName(row.LocalPath!),"mp3",delayed is not null && row.Id=="B"?128:320,new FileInfo(row.LocalPath!).Length,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(row.LocalPath!))),DateTimeOffset.UtcNow)).ToArray();
                File.WriteAllText(Path.Combine(music,"library.index.json"),JsonSerializer.Serialize(items));
            }
            if(restored)
            {
                var saved=new PlaybackQueue();saved.SetItems([A],A);saved.Repeat=RepeatMode.All;
                new PlaybackStateStore(diagnostics).Save(PlaybackStateStore.Capture("position-fixture",saved,30));
            }
            var prefs=new PreferencesStore(Path.Combine(directory,"preferences")); prefs.Save(new PlayerPreferences{Volume=0,CrossfadeSeconds=0});
            Player=new NativePlayer(new AudioBuffer(Path.Combine(directory,"buffer"),Network));
            Window=new MainWindow(api:new MusicApi(Network),store:new SessionStore(Path.Combine(directory,"session")),restore:restored,
                diagnosticsDirectory:diagnostics,offline:new OfflineLibrary(music,new AudioBuffer(Path.Combine(directory,"download"),Network)),player:Player,preferences:prefs);
            Player.SetVolume(0);
            if(restored)
            {
                Tp0317Set(Window,"_account",new Account("position-fixture","private fixture",true));
                Tp0317Invoke(Window,"RestorePlaybackState","position-fixture");A=Queue.Current!;
                Tp0317Need(!Player.HasSource && Tp0317Get<double?>(Window,"_restoredPlaybackSeconds")==30,"disk restoration did not retain paused source-free position");
            }
            if(delayed is not null) Tp0317Set(Window,"_session",new Session("private-fake-token",null,DateTimeOffset.UtcNow.AddHours(1),"fixture",MusicApi.RequiredScopes));
        }
        static TrackRow Row(string id,string directory,int hz)
        {
            var path=Path.Combine(directory,id+".wav"); Tp0317WriteWave(path,hz);
            return new TrackRow{Id=id,RequestId=id,Title=id,Artist="private generated fixture",DurationMs=60000,Available=true,LocalPath=path,LocalQuality="private WAV"};
        }
        public async Task Prepared() { if(Tp0317Get<Task?>(Window,"_preloadTask") is {} t)await t; await Tp0317Until(()=>Player.HasPrepared); }
        public void Views()
        {
            Tp0317Invoke(Window,"SetExpandedPlayerVisible",true,false); Mini=new MiniPlayerWindow();
            Mini.SeekRequested+=seconds=>Tp0317Invoke(Window,"CommitSeek",seconds);UpdateMini();
        }
        public void UpdateMini()
        {
            if(Mini is null)return;
            var current=Tp0317Get<TrackRow?>(Window,"_current");Mini.Update(current?.Title??"",current?.Artist??"",Player.IsPlaying,true,true);
            Mini.UpdateDetails(null,((TimeSpan)Tp0317Invoke(Window,"DisplayPosition",Player.Position)!).TotalSeconds,Player.Duration.TotalSeconds,Main.IsEnabled,false,false,0,$"{Tp0317Get<int>(Window,"_playbackGeneration")}:{current?.RequestId}");
        }
        public object State()=>new { nativeSeconds=Player.Position.TotalSeconds, mainSeconds=Main.Value, mainBaseSeconds=(double)Main.GetAnimationBaseValue(Slider.ValueProperty), positionText=((TextBlock)Window.FindName("PositionText")).Text,
            expandedSeconds=Tp0317Get<Slider?>(Window,"_expandedPlayerSeek")?.Value,miniSeconds=Mini?.SeekSlider.Value,currentId=Tp0317Get<TrackRow?>(Window,"_current")?.Id, currentPath=Player.SourceLocalPath, pending=Tp0317Get<double?>(Window,"_pendingSeek"), prepared=Player.HasPrepared, fading=Player.FadingLocalPath, occurrence=Queue.CurrentOccurrenceToken, queueVersion=Queue.Version };
        public void Dispose(){if(Network is Tp0317DelayedResolve delayed)delayed.Release.TrySetResult();Mini?.Close();Window.Close();Player.Dispose();Network.Dispose();}
    }
    sealed class Tp0317SyntheticSource:PresentationSource
    {
        public override Visual RootVisual {get;set;}=null!;
        public override bool IsDisposed=>false;
        protected override CompositionTarget GetCompositionTargetCore()=>null!;
    }
    static void Tp0317WriteWave(string path,int hz)
    {
        const int rate=22050, samples=rate*60; using var w=new BinaryWriter(File.Create(path));
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36+samples*2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1);w.Write((short)1);w.Write(rate);w.Write(rate*2);w.Write((short)2);w.Write((short)16);w.Write(Encoding.ASCII.GetBytes("data"));w.Write(samples*2);
        for(var i=0;i<samples;i++) { var second=i/rate; var frequency=hz+second*2; w.Write((short)(Math.Sin(i*2*Math.PI*frequency/rate)*500)); }
    }
}
