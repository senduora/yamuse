using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckBrandingAppearance038()
    {
        await Run("Branding038: main exposes Yamuse branding and a dark caption; Settings stay inside that window", BrandingNativeAppearance038);
        await Run("Branding038: actual root mini binding forwards cover volume like seek pause and next through one player and queue", BrandingRootMini038);
    }

    private static async Task BrandingNativeAppearance038()
    {
        using var fixture=new SaFixture("branding038-caption-"+Guid.NewGuid().ToString("N"));
        var owner=fixture.Window; await fixture.Activate();owner.UpdateLayout();
        Assert(owner.Title=="Yamuse", "main native caption retained the previous application title");
        Assert(owner.Icon is {IsFrozen:true}, "main Window.Icon is missing or mutable");
        var logo=SaVisuals<Image>(owner).Single(image=>AutomationProperties.GetName(image)=="Логотип Yamuse");
        Assert(logo.Source is BitmapSource {PixelWidth:>0,PixelHeight:>0}&&logo.IsVisible, "sidebar Yamuse bitmap resource did not render");
        var mainNative=BrandingNativeFacts(owner,"main");
        SaInvoke(owner, "Settings_Click", Control<Button>(owner, "SettingsButton"), new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(180); owner.UpdateLayout();
        Assert(Control<Grid>(owner, "SettingsPanel").IsVisible && owner.OwnedWindows.Count == 0 && owner.IsEnabled,
            "Settings did not stay in the enabled main window");
        Assert(ReferenceEquals(PresentationSource.FromVisual(Control<Grid>(owner, "SettingsPanel")), PresentationSource.FromVisual(owner)),
            "Settings created another presentation source");
        App.Capture(owner, Path.Combine(_directory, "settings-inline.png"));
        SaClick(Control<Button>(owner, "SettingsCancelButton"));
        Assert(!Control<Grid>(owner, "SettingsPanel").IsVisible, "Settings did not close");
        File.WriteAllText(Path.Combine(_directory, "branding038-native-appearance.json"), JsonSerializer.Serialize(new
        {
            main = mainNative, settingsInsideMain = true, ownedWindows = owner.OwnedWindows.Count,
            syntheticAccountOnly = true, systemThemeChanged = false, externalWindowsCaptured = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object BrandingNativeFacts(Window window,string fixtureKind)
    {
        var handle=new WindowInteropHelper(window).Handle;
        Assert(handle!=0, "fixture has no native HWND");
        _=BrandingGetWindowThreadProcessId(handle,out var processId);
        Assert(processId==Environment.ProcessId, "native inspection left the fixture process");
        var small=BrandingSendMessage(handle,0x7F,0,0);var large=BrandingSendMessage(handle,0x7F,1,0);
        Assert(small!=0||large!=0, "WM_GETICON did not expose the branded native window icon");
        var highContrast=SystemParameters.HighContrast;
        var darkResult=BrandingDwmGetWindowAttribute(handle,20,out uint dark,sizeof(uint));
        var captionResult=BrandingDwmGetWindowAttribute(handle,35,out uint caption,sizeof(uint));
        var textResult=BrandingDwmGetWindowAttribute(handle,36,out uint text,sizeof(uint));
        var application=WindowAppearance.GetLastApplication(window);
        var facts=new{title=window.Title,fixtureKind,windowIconFrozen=window.Icon is {IsFrozen:true},wmGetIconPresent=small!=0||large!=0,
            sameProcess=true,highContrast,osVersion=Environment.OSVersion.Version.ToString(),
            dark=new{supported=darkResult==0,hresult=darkResult,hresultHex=$"0x{unchecked((uint)darkResult):X8}",rawOutput=dark,value=darkResult==0?(uint?)dark:null},
            caption=new{supported=captionResult==0,unsupportedGetter=captionResult==unchecked((int)0x80070057),hresult=captionResult,hresultHex=$"0x{unchecked((uint)captionResult):X8}",rawOutput=caption,value=captionResult==0?(uint?)caption:null},
            text=new{supported=textResult==0,unsupportedGetter=textResult==unchecked((int)0x80070057),hresult=textResult,hresultHex=$"0x{unchecked((uint)textResult):X8}",rawOutput=text,value=textResult==0?(uint?)text:null},
            actualHelperApplication=application,
            failedGetterOutputsAreNotAttributeValues=true,
            windows11ColorAttributesRequired=OperatingSystem.IsWindowsVersionAtLeast(10,0,22000)};
        File.WriteAllText(Path.Combine(_directory,$"branding038-native-{fixtureKind}-getter.json"),JsonSerializer.Serialize(facts,new JsonSerializerOptions{WriteIndented=true}));
        Assert(application is not null&&application.HighContrast==highContrast, "actual WindowAppearance.Apply did not record this fixture's high-contrast-aware native calls");
        if(OperatingSystem.IsWindowsVersionAtLeast(10,0,22000))
        {
            Assert(application!.DarkResult==0&&application.CaptionResult==0&&application.TextResult==0, "actual WindowAppearance.Apply native DWM setters failed");
            Assert(darkResult==0&&dark==(highContrast?0u:1u), "actual native immersive dark readback failed or differs from high-contrast-aware expectation");
            var background=window.Background is SolidColorBrush brush?brush.Color:Color.FromRgb(16,18,22);
            var expectedCaption=highContrast?uint.MaxValue:(uint)(background.R|(background.G<<8)|(background.B<<16));
            var expectedText=highContrast?uint.MaxValue:0x00F7F5F4u;
            Assert(application.CaptionColor==expectedCaption&&application.TextColor==expectedText, "actual helper setter inputs differ from the requested dark surface or high-contrast default");
            Assert(captionResult==0||captionResult==unchecked((int)0x80070057), "native caption color getter failed with an unexpected HRESULT");
            Assert(textResult==0||textResult==unchecked((int)0x80070057), "native text color getter failed with an unexpected HRESULT");
            if(captionResult==0)Assert(caption==expectedCaption, "supported native caption color getter differs from the requested value");
            if(textResult==0)Assert(text==expectedText, "supported native text color getter differs from the requested value");
        }
        if(darkResult==0)Assert(dark==(highContrast?0u:1u), "actual native immersive dark flag differs from high-contrast-aware expectation");
        return facts;
    }

    private static async Task BrandingRootMini038()
    {
        var directory=Path.Combine(_directory,"branding038-root-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var liked=false;var writes=new List<string>();var unexpected=0;
        var handler=new FakeHandler(async(request,token)=>
        {
            var path=request.RequestUri!.AbsolutePath;
            Assert(request.RequestUri.Host=="api.music.yandex.net", "root mini fake API left its exact host");
            if(path=="/account/status")return Json(Profile);
            if(path=="/account/about")return Json("""{"result":{"uid":"12345"}}""");
            if(path=="/users/12345/likes/tracks")return Json(liked?"""{"result":{"library":{"tracks":[{"id":"branding-mini-a","albumId":"1"}]}}}""":"""{"result":{"library":{"tracks":[]}}}""");
            if(path is "/users/12345/likes/tracks/add-multiple" or "/users/12345/likes/tracks/remove")
            {
                Assert(request.Method==HttpMethod.Post, "mini like did not invoke the existing write API");
                var body=Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(token));
                Assert(body.Contains("track-ids=branding-mini-a:1",StringComparison.Ordinal), "mini liked another playback reference");
                liked=path.EndsWith("add-multiple",StringComparison.Ordinal);writes.Add(liked?"add":"remove");return Json("""{"result":{"revision":17}}""");
            }
            if(path=="/tracks")return Json("""{"result":[{"id":"branding-mini-a","title":"Первый fixture трек","available":true,"durationMs":30000,"artists":[{"id":"fixture-artist","name":"Fixture artist"}],"albums":[{"id":"1","title":"Fixture album"}]}]}""");
            unexpected++;throw new InvalidOperationException("Unexpected root mini fixture endpoint");
        });
        var player=new NativePlayer(new AudioBuffer(Path.Combine(directory,"audio"),new FakeHandler((_,_)=>throw new InvalidOperationException("local mini fixture contacted media network"))));
        var library=new OfflineLibrary(Path.Combine(directory,"offline"),new AudioBuffer(Path.Combine(directory,"downloads"),new FakeHandler((_,_)=>throw new InvalidOperationException("mini fixture downloaded audio"))));
        var window=new MainWindow(api:new MusicApi(handler),store:new SessionStore(Path.Combine(directory,"session")),restore:false,diagnosticsDirectory:directory,offline:library,player:player)
        {Left=-16000,Top=0,Width=1280,Height=860,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};
        MiniPlayerWindow? mini=null; nint miniHandle=0;bool closed=false;
        try
        {
            window.Show();Control<Slider>(window,"VolumeSlider").Value=0;await Call(window,"ActivateAsync",FakeSession,CancellationToken.None);
            var a=BrandingLocalTrack("branding-mini-a","Первый fixture трек","branding038-a.wav");
            var b=BrandingLocalTrack("branding-mini-b","Второй fixture трек","branding038-b.wav");
            SaInvoke(window,"ShowPlaylist",new PlaylistDetails("12345","branding-fixture","Synthetic playlist","",1,[a,b]));
            Control<ListView>(window,"TracksList").SelectedIndex=0;await Call(window,"PlaySelectedAsync");
            await Until(()=>player.HasSource&&player.Duration.TotalSeconds>20&&!SaField<bool>(window,"_loadingStream"));
            var queue=SaField<PlaybackQueue>(window,"_playbackQueue");var shell=SaField<DesktopShell>(window,"_shell");
            Assert(ReferenceEquals(SaField<NativePlayer>(window,"_player"),player), "root replaced its injected native player");
            shell.ShowMiniPlayer();mini=BrandingMini(shell);Assert(mini is not null, "actual root shell did not create its mini window");
            mini!.ShowActivated=false;mini.WindowStartupLocation=WindowStartupLocation.Manual;mini.Left=-16000;mini.Top=0;miniHandle=new WindowInteropHelper(mini).Handle;
            await Until(()=>mini.TitleText.Text==a.Title&&mini.SeekSlider.IsEnabled&&mini.LikeButton.IsEnabled);
            Assert(mini.ArtistText.Text==a.Artist&&Math.Abs(mini.VolumeSlider.Value)<.01, "initial root mini metadata or muted volume differs");
            var coverA=BrandingCover(Color.FromRgb(75,65,140));
            await window.Dispatcher.InvokeAsync(()=>Control<Image>(window,"PlayerCover").Source=coverA,DispatcherPriority.Background);
            await Until(()=>ReferenceEquals(mini.CoverImage.Source,coverA));
            Control<Slider>(window,"VolumeSlider").Value=23;
            await Until(()=>Math.Abs(mini.VolumeSlider.Value-23)<.01&&Math.Abs(player.Volume-.23)<.001);
            mini.VolumeSlider.Value=61;
            await Until(()=>Math.Abs(Control<Slider>(window,"VolumeSlider").Value-61)<.01&&Math.Abs(player.Volume-.61)<.001);
            Assert(mini.LikeButton.ToolTip?.ToString()=="Добавить в Мне нравится", "mini initial current-like state is stale");
            mini.LikeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>liked&&writes.Count==1&&mini.LikeButton.ToolTip?.ToString()=="Убрать из Мне нравится"&&!SaField<bool>(window,"_catalogWriting"));
            Control<Button>(window,"PlayerLikeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>!liked&&writes.Count==2&&mini.LikeButton.ToolTip?.ToString()=="Добавить в Мне нравится"&&!SaField<bool>(window,"_catalogWriting"));
            mini.PlayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>!player.IsPlaying&&AutomationProperties.GetName(mini.PlayButton)=="Играть");
            var source=PresentationSource.FromVisual(mini.SeekSlider);Assert(source is not null, "mini seek slider lacks a real presentation source");
            mini.SeekSlider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source!,Environment.TickCount,Key.Right){RoutedEvent=Keyboard.PreviewKeyDownEvent});
            mini.SeekSlider.Value=8.25;
            mini.SeekSlider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source!,Environment.TickCount,Key.Right){RoutedEvent=Keyboard.PreviewKeyUpEvent});
            var display=(TimeSpan)typeof(MainWindow).GetMethod("DisplayPosition",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[player.Position])!;
            Assert(Math.Abs(display.TotalSeconds-8.25)<.9&&Math.Abs(Control<Slider>(window,"SeekSlider").Value-8.25)<.9, "mini seconds were not committed through the root display-position seek path");
            await Until(()=>Math.Abs(player.Position.TotalSeconds-8.25)<1.1);
            mini.PlayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>player.IsPlaying&&AutomationProperties.GetName(mini.PlayButton)=="Пауза");
            SaVisuals<Button>(mini).Single(button=>AutomationProperties.GetName(button)=="Следующий трек").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>ReferenceEquals(SaField<TrackRow?>(window,"_current"),b)&&player.SourceLocalPath==b.LocalPath&&mini.TitleText.Text==b.Title&&!SaField<bool>(window,"_loadingStream"));
            var coverB=BrandingCover(Color.FromRgb(145,63,72));
            await window.Dispatcher.InvokeAsync(()=>Control<Image>(window,"PlayerCover").Source=coverB,DispatcherPriority.Background);
            await Until(()=>ReferenceEquals(mini.CoverImage.Source,coverB));
            Assert(ReferenceEquals(SaField<NativePlayer>(window,"_player"),player)&&ReferenceEquals(SaField<PlaybackQueue>(window,"_playbackQueue"),queue)
                &&queue.Items.Select(row=>row.RequestId).SequenceEqual(new[]{a.RequestId,b.RequestId}), "mini transport replaced the root player/queue or rewrote queue content");
            Assert(writes.SequenceEqual(new[]{"add","remove"})&&unexpected==0, "mini like callback sequence or fake API routing differs");
            File.WriteAllText(Path.Combine(_directory,"branding038-root-mini.json"),JsonSerializer.Serialize(new
            {metadataForwarded=true,asyncCoverForwarded=true,volumeBothDirections=true,likeWrites=writes,seekTargetSeconds=8.25,nativeSeekAcknowledged=true,pauseResumeForwarded=true,nextForwarded=true,sameRootPlayerAndQueue=true,liveNetworkRequests=0,mutedPcmWave=true,realAccountLoaded=false},new JsonSerializerOptions{WriteIndented=true}));
            window.Close();closed=true;await Until(()=>!mini.IsVisible&&!BrandingIsWindow(miniHandle));
            Assert(!player.HasSource&&!player.IsPlaying, "owner teardown retained its audio source");
        }
        finally{if(!closed)window.Close();player.Dispose();handler.Dispose();}
    }

    private static TrackRow BrandingLocalTrack(string id,string title,string waveName)
    {
        var path=WriteWave(waveName,30);var bytes=File.ReadAllBytes(path);Array.Clear(bytes,44,bytes.Length-44);File.WriteAllBytes(path,bytes);
        return new TrackRow{Id=id,RequestId=id+":1",Title=title,Artist="Fixture artist",Album="Fixture album",AlbumId="1",Available=true,DurationMs=30000,LocalPath=path,LocalQuality="Muted PCM WAV"};
    }
    private static MiniPlayerWindow? BrandingMini(DesktopShell shell)=>(MiniPlayerWindow?)typeof(DesktopShell).GetField("_mini",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(shell);
    private static ImageSource BrandingCover(Color color)
    {var drawing=new DrawingImage(new GeometryDrawing(new SolidColorBrush(color),null,new RectangleGeometry(new Rect(0,0,64,64))));drawing.Freeze();return drawing;}

    private sealed record BrandingCaptureFacts(bool Captured,string? Reason=null,int Width=0,int Height=0,
        bool NonClientGeometryIncluded=false,int CaptionBandX=0,int CaptionBandY=0,int CaptionBandWidth=0,int CaptionBandHeight=0,
        int CaptionBandSamples=0,double CaptionColorNearFraction=0,double CaptionDarkFraction=0,
        bool CaptureHasNonBlackSamples=false,bool CaptionPixelsVerified=false,bool HighContrast=false);

    private static BrandingCaptureFacts BrandingPrintWindow(Window window,string path)
    {
        var handle=new WindowInteropHelper(window).Handle;_=BrandingGetWindowThreadProcessId(handle,out var processId);
        if(processId!=Environment.ProcessId)return new(false,"not_fixture_process");
        var clientOrigin=new BrandingPoint();
        if(!BrandingGetWindowRect(handle,out var rect)||!BrandingGetClientRect(handle,out var client)||!BrandingClientToScreen(handle,ref clientOrigin))return new(false,"geometry_unavailable");
        var width=rect.Right-rect.Left;var height=rect.Bottom-rect.Top;
        if(width<=0||height<=0||width>4096||height>4096)return new(false,"geometry_rejected");
        var dc=BrandingGetWindowDC(handle);if(dc==0)return new(false,"window_dc_unavailable");
        nint memory=0,bitmap=0,old=0;
        try
        {
            memory=BrandingCreateCompatibleDC(dc);bitmap=BrandingCreateCompatibleBitmap(dc,width,height);
            if(memory==0||bitmap==0)return new(false,"gdi_allocation_failed");
            old=BrandingSelectObject(memory,bitmap);
            var printed=BrandingPrintWindowNative(handle,memory,2)||BrandingPrintWindowNative(handle,memory,0);
            if(!printed)return new(false,"print_window_failed");
            var image=Imaging.CreateBitmapSourceFromHBitmap(bitmap,0,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());image.Freeze();
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var stream=File.Create(path))encoder.Save(stream);
            var stride=image.PixelWidth*4;var pixels=new byte[stride*image.PixelHeight];var bgra=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);bgra.CopyPixels(pixels,stride,0);
            var dpi=BrandingGetDpiForWindow(handle);if(dpi==0)dpi=96;
            var captionHeight=BrandingGetSystemMetricsForDpi(4,dpi);
            var clientTop=clientOrigin.Y-rect.Top;
            var x0=width/2;var x1=width*65/100;
            var y0=Math.Max(1,clientTop-captionHeight+Math.Max(3,captionHeight/4));
            var y1=Math.Min(height,clientTop-Math.Max(3,captionHeight/5));
            var highContrast=SystemParameters.HighContrast;
            var application=WindowAppearance.GetLastApplication(window);
            if(application?.CaptionColor is not uint expected||captionHeight<=0||y0>=y1||x0>=x1)
                return new(true,"caption_band_geometry_unavailable",width,height,HighContrast:highContrast);
            var expectedColors=highContrast?new[]{BrandingGetSysColor(2),BrandingGetSysColor(3)}:new[]{expected};
            var samples=0;var near=0;var dark=0;var nonBlack=0;
            for(var y=y0;y<y1;y++)for(var x=x0;x<x1;x++)
            {
                var at=y*stride+x*4;var b=pixels[at];var g=pixels[at+1];var r=pixels[at+2];samples++;
                if(r!=0||g!=0||b!=0)nonBlack++;
                if(r<80&&g<80&&b<80)dark++;
                if(expectedColors.Any(color=>Math.Abs(r-(int)(color&255))<=12&&Math.Abs(g-(int)((color>>8)&255))<=12&&Math.Abs(b-(int)((color>>16)&255))<=12))near++;
            }
            var nearFraction=samples==0?0:(double)near/samples;var darkFraction=samples==0?0:(double)dark/samples;
            var nonClient=clientTop>0&&height>(client.Bottom-client.Top);
            var verified=nonClient&&samples>=20&&nonBlack>0&&nearFraction>=.9&&(highContrast||darkFraction>=.95);
            return new(true,verified?null:"caption_pixels_unverified",width,height,nonClient,x0,y0,x1-x0,y1-y0,samples,nearFraction,darkFraction,nonBlack>0,verified,highContrast);
        }
        catch(Exception error)when(error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {return new(false,"bitmap_capture_rejected_"+error.GetType().Name);}
        finally{if(old!=0&&memory!=0)BrandingSelectObject(memory,old);if(bitmap!=0)BrandingDeleteObject(bitmap);if(memory!=0)BrandingDeleteDC(memory);BrandingReleaseDC(handle,dc);}
    }
    [StructLayout(LayoutKind.Sequential)] private struct BrandingRect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] private struct BrandingPoint{public int X,Y;}
    [DllImport("user32.dll",EntryPoint="SendMessageW")]private static extern nint BrandingSendMessage(nint window,uint message,nint wParam,nint lParam);
    [DllImport("user32.dll",EntryPoint="GetWindowThreadProcessId")]private static extern uint BrandingGetWindowThreadProcessId(nint window,out int processId);
    [DllImport("dwmapi.dll",EntryPoint="DwmGetWindowAttribute")]private static extern int BrandingDwmGetWindowAttribute(nint window,int attribute,out uint value,int size);
    [DllImport("user32.dll",EntryPoint="IsWindow")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool BrandingIsWindow(nint window);
    [DllImport("user32.dll",EntryPoint="IsWindowEnabled")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool BrandingIsWindowEnabled(nint window);
    [DllImport("user32.dll",EntryPoint="GetWindowRect")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool BrandingGetWindowRect(nint window,out BrandingRect rect);
    [DllImport("user32.dll",EntryPoint="GetClientRect")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool BrandingGetClientRect(nint window,out BrandingRect rect);
    [DllImport("user32.dll",EntryPoint="ClientToScreen")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool BrandingClientToScreen(nint window,ref BrandingPoint point);
    [DllImport("user32.dll",EntryPoint="GetDpiForWindow")]private static extern uint BrandingGetDpiForWindow(nint window);
    [DllImport("user32.dll",EntryPoint="GetSystemMetricsForDpi")]private static extern int BrandingGetSystemMetricsForDpi(int index,uint dpi);
    [DllImport("user32.dll",EntryPoint="GetSysColor")]private static extern uint BrandingGetSysColor(int index);
    [DllImport("user32.dll",EntryPoint="GetWindowDC")]private static extern nint BrandingGetWindowDC(nint window);
    [DllImport("user32.dll",EntryPoint="ReleaseDC")]private static extern int BrandingReleaseDC(nint window,nint dc);
    [DllImport("gdi32.dll",EntryPoint="CreateCompatibleDC")]private static extern nint BrandingCreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll",EntryPoint="CreateCompatibleBitmap")]private static extern nint BrandingCreateCompatibleBitmap(nint dc,int width,int height);
    [DllImport("gdi32.dll",EntryPoint="SelectObject")]private static extern nint BrandingSelectObject(nint dc,nint value);
    [DllImport("gdi32.dll",EntryPoint="DeleteObject")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool BrandingDeleteObject(nint value);
    [DllImport("gdi32.dll",EntryPoint="DeleteDC")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool BrandingDeleteDC(nint dc);
    [DllImport("user32.dll",EntryPoint="PrintWindow")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool BrandingPrintWindowNative(nint window,nint dc,uint flags);
}
