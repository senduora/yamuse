using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckCrossfadeOnly()
    {
        await Run("Crossfade: old and unknown preferences retain supported choices and serialize the supported schema", CrossfadePreferences);
        await Run("Crossfade: inline Settings retain 0..10 seconds and save without a modal window", CrossfadeSettingsUi);
        await Run("Crossfade: ten-second overlap supports pause seek and zero-duration automatic promotion", CrossfadePlayback);
        await Run("Crossfade: online next loads once and releases only its owned file", CrossfadeOnline);
        await Run("Crossfade: actual overlap remains active beyond eight seconds and completes once at ten", CrossfadeNaturalCompletion);
        await Run("Crossfade: playback uses native Windows media and only the declared application dependency", () =>
        {
            var allowed = new[] { "PresentationCore", "PresentationFramework", "WindowsBase", "Velopack", "WinRT.Runtime", "Microsoft.Windows.SDK.NET", "Microsoft.CSharp", "Microsoft.Win32.Primitives", "Microsoft.Win32.Registry" };
            var references = typeof(NativePlayer).Assembly.GetReferencedAssemblies();
            var undeclared = references.Where(a => !a.Name!.StartsWith("System.", StringComparison.Ordinal) && !allowed.Contains(a.Name)).ToArray();
            Assert(undeclared.Length == 0, "undeclared assembly reference: " + string.Join(", ", undeclared.Select(a => a.Name)));
            Assert(references.Any(a => a.Name == "PresentationCore"), "native media reference missing");
            return Task.CompletedTask;
        });
    }

    private static Task CrossfadePreferences()
    {
        var directory=Path.Combine(_directory,"crossfade-preferences");Directory.CreateDirectory(directory);
        var store=new PreferencesStore(directory);
        File.WriteAllText(store.FilePath,"{\"quality\":\"Optimal\",\"volume\":0.37}");
        var old=store.Load();Assert(old.CrossfadeSeconds==3,"old default changed");
        foreach(double value in new[]{0d,.5,3,9.5,10}) {
            store.Save(old with {CrossfadeSeconds=value}); Assert(store.Load()==old with {CrossfadeSeconds=value},"roundtrip changed ordinary preferences");
        }
        foreach(var pair in new[]{(-1d,0d),(11d,10d),(double.NaN,3d),(double.PositiveInfinity,3d)})
            Assert(PreferencesStore.Normalize(old with {CrossfadeSeconds=pair.Item1}).CrossfadeSeconds==pair.Item2,"invalid value normalization");
        File.WriteAllText(store.FilePath,"{\"quality\":\"Excellent\",\"volume\":0.42,\"playbackSpeed\":1.25,\"concurrentDownloads\":7,\"crossfadeSeconds\":10,\"retiredOption\":true,\"extraPlaybackFlag\":200,\"unknownSettings\":{\"depth\":2}}");
        var imported=store.Load(); Assert(imported.CrossfadeSeconds==10 && imported.Volume==.42 && imported.PlaybackSpeed==1.25 && imported.ConcurrentDownloads==7,"supported preferences lost");
        store.Save(imported);var json=File.ReadAllText(store.FilePath);
        using var document = JsonDocument.Parse(json);
        var fields = document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert(fields.SequenceEqual(new[] { "accentFromCover", "autoDownloadFavorites", "concurrentDownloads", "crossfadeSeconds", "minimizeToTray", "normalizeVolume", "playbackSpeed", "quality", "staticAccentColor", "volume" }), "saved schema differs");
        using var player=new NativePlayer(); Assert(player.CrossfadeSeconds==3,"native default differs");
        player.SetCrossfade(10);
        Assert(!player.TryPlayPrepared(new Uri(Path.Combine(directory,"not-open.wav")),TimeSpan.FromSeconds(10)),"missing next source promoted");
        foreach(double bad in new[]{-1d,10.1,double.NaN,double.PositiveInfinity}) {
            try{player.SetCrossfade(bad);throw new InvalidOperationException("invalid crossfade accepted");}catch(ArgumentOutOfRangeException){}
        }
        return Task.CompletedTask;
    }

    private static async Task CrossfadeSettingsUi()
    {
        using var fixture=new PreloadIntegrationFixture("crossfade-settings",signedIn:false);
        var store=new PreferencesStore(Path.Combine(fixture.DirectoryPath,"settings"));
        typeof(MainWindow).GetField("_preferenceStore",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(fixture.Window,store);
        await CrossfadePage(fixture.Window,async dialog=> {
            var slider=SaVisuals<Slider>(dialog).Single(s=>s.Name=="CrossfadeSetting");
            Assert(slider.Minimum==0 && slider.Maximum==10 && slider.Value==3,"range/default incorrect");
            Assert(fixture.Window.OwnedWindows.Count == 0 && Control<Grid>(fixture.Window, "SettingsPanel").IsVisible, "Settings created a separate window");
            slider.Value=10;dialog.UpdateLayout();await Task.Delay(120);
            ClCaptureElement(dialog,Path.Combine(_directory,"crossfade-settings.png"));
            SaClick(SaVisuals<Button>(dialog).Single(b=>b.Name=="SettingsApplyButton"));
        });
        Assert(fixture.Player.CrossfadeSeconds==10 && store.Load().CrossfadeSeconds==10,"setting not applied and saved");
        await CrossfadePage(fixture.Window,dialog=>{
            var slider=SaVisuals<Slider>(dialog).Single(s=>s.Name=="CrossfadeSetting");Assert(slider.Value==10,"reopen lost value");
            slider.Value=0;SaClick(SaVisuals<Button>(dialog).Single(b=>b.Name=="SettingsApplyButton"));return Task.CompletedTask;
        });
        Assert(fixture.Player.CrossfadeSeconds==0 && store.Load().CrossfadeSeconds==0,"off value not retained");
        fixture.Window.Width=1050;fixture.Window.Height=680;fixture.Window.UpdateLayout();
        var playerBar = Control<Border>(fixture.Window, "PlayerBar");
        Assert(!Control<Grid>(fixture.Window, "SettingsPanel").IsVisible && Math.Abs(playerBar.ActualHeight - 118) <= 1,$"settings did not return to the player: height {playerBar.ActualHeight}");
        App.Capture(fixture.Window,Path.Combine(_directory,"crossfade-player-1050.png"));
    }

    private static async Task CrossfadePlayback()
    {
        var path=WriteWave("crossfade-40s.wav",40);
        using var fixture=new PreloadIntegrationFixture("crossfade-playback",signedIn:false,media:File.ReadAllBytes(path));
        var a=fixture.Local("a");var b=fixture.Local("b");
        fixture.Player.SetCrossfade(10);await fixture.Play(a,b);await PreloadAwaitPrepared(fixture.Window);
        var next=fixture.Queue.PeekNextOccurrenceToken(true);PreloadInvoke(fixture.Window,"CommitSeek",30.05);
        await Until(()=>fixture.Queue.CurrentOccurrenceToken==next);
        var duration=PreloadGet<TimeSpan>(fixture.Player,"_fadeDuration");
        Assert(duration.TotalSeconds>9 && duration.TotalSeconds<=10 && fixture.Player.FadingLocalPath==a.LocalPath,"ten-second value not used");
        fixture.Player.Toggle();var elapsed=PreloadGet<TimeSpan>(fixture.Player,"_fadeElapsed");await Task.Delay(180);
        Assert(!fixture.Player.IsPlaying && PreloadGet<TimeSpan>(fixture.Player,"_fadeElapsed")==elapsed,"pause kept fade running");
        fixture.Player.Toggle();fixture.Player.SetCrossfade(1);
        Assert(PreloadGet<TimeSpan>(fixture.Player,"_fadeDuration")==duration,"future setting changed active fade");
        fixture.Player.Seek(TimeSpan.FromSeconds(3));Assert(fixture.Player.FadingLocalPath is null && fixture.Player.IsPlaying,"seek left previous track playing");
        fixture.Player.Stop();fixture.Player.SetCrossfade(0);
        await fixture.Play(a,b);await PreloadAwaitPrepared(fixture.Window);next=fixture.Queue.PeekNextOccurrenceToken(true);
        PreloadInvoke(fixture.Window,"CommitSeek",39.75);await Until(()=>fixture.Queue.CurrentOccurrenceToken==next);
        Assert(fixture.Player.FadingLocalPath is null && fixture.Player.SourceLocalPath==b.LocalPath,"zero still overlaps");
        fixture.Player.Stop();Assert(File.Exists(a.LocalPath) && File.Exists(b.LocalPath) && fixture.ResolveCalls==0 && fixture.MediaCalls==0,"local source ownership violated");
    }

    private static async Task CrossfadeOnline()
    {
        var path=WriteWave("crossfade-online-40s.wav",40);
        using var fixture=new PreloadIntegrationFixture("crossfade-online",media:File.ReadAllBytes(path));
        var a=fixture.Local("a");var b=fixture.Row("online");fixture.Player.SetCrossfade(10);
        await fixture.Play(a,b);await PreloadAwaitPrepared(fixture.Window);
        string prepared=fixture.Player.PreparedLocalPath!;var next=fixture.Queue.PeekNextOccurrenceToken(true);
        PreloadInvoke(fixture.Window,"CommitSeek",30.05);await Until(()=>fixture.Queue.CurrentOccurrenceToken==next);
        Assert(fixture.Player.SourceLocalPath==prepared && fixture.ResolveCalls==1 && fixture.MediaCalls==1,"online promotion loaded again");
        fixture.Player.Stop();await Until(()=>fixture.BufferEmpty);
        Assert(File.Exists(a.LocalPath) && !File.Exists(prepared),"stop deleted borrowed source or retained owned download");
    }

    private static async Task CrossfadeNaturalCompletion()
    {
        string first=WriteWave("crossfade-natural-a.wav",40),second=WriteWave("crossfade-natural-b.wav",40);
        using var player=new NativePlayer{Volume=0};int completed=0;bool wasNatural=false;
        player.TransitionFinished+=natural=>{completed++;wasNatural=natural;};
        await player.OpenAsync(new Uri(first),default);await player.PrepareNextAsync(new Uri(second),default);
        var clock=System.Diagnostics.Stopwatch.StartNew();
        Assert(player.TryPlayPrepared(new Uri(second),TimeSpan.FromSeconds(10)),"prepared decoder not promoted");
        await Task.Delay(8200);
        Assert(player.FadingLocalPath==first && completed==0,"ten-second fade was cut at eight seconds");
        await Until(()=>player.FadingLocalPath is null);
        Assert(completed==1 && wasNatural && clock.Elapsed.TotalSeconds is >=9.9 and <12,"overlap completion timing/count differs");
        Assert(player.IsPlaying && player.SourceLocalPath==second && File.Exists(first) && File.Exists(second),"natural completion lost current or borrowed source");
        player.Stop();Assert(completed==1,"stop emitted duplicate completion");
    }

    private static async Task CrossfadePage(MainWindow owner, Func<FrameworkElement, Task> action)
    {
        PreloadInvoke(owner, "Settings_Click", owner, new RoutedEventArgs());
        var panel = Control<Grid>(owner, "SettingsPanel");
        try
        {
            owner.UpdateLayout();
            Assert(panel.IsVisible && owner.OwnedWindows.Count == 0, "Settings did not open inside main window");
            await action(panel);
        }
        finally
        {
            if (panel.IsVisible) SaClick(Control<Button>(owner, "SettingsCancelButton"));
        }
    }
}
