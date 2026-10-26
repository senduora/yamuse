using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckFullscreenPower0317()
    {
        await Run("FullscreenPower0317: only visible playing fullscreen holds display and system; pause releases", Fp0317Predicate);
        await Run("FullscreenPower0317: unchanged playback makes no repeated OS calls and native flags stay scoped", Fp0317Idempotence);
        await Run("FullscreenPower0317: native failures retain truthful state and owner thread controls release", Fp0317Failures);
        await Run("FullscreenPower0317: pause hide minimize exit and close release without showing a fixture", Fp0317Window);
    }

    private static Task Fp0317Predicate()
    {
        for (var mask = 0; mask < 32; mask++)
        {
            var flags = new List<uint>();
            using var request = new FullscreenDisplayRequest(value => { flags.Add(value); return FullscreenDisplayRequest.Continuous; });
            var fullscreen = (mask & 1) != 0; var visible = (mask & 2) != 0;
            var minimized = (mask & 4) != 0; var playing = (mask & 8) != 0; var closed = (mask & 16) != 0;
            var expected = fullscreen && visible && !minimized && playing && !closed;
            Assert(request.Update(fullscreen, visible, minimized, playing, closed), "native fixture update failed");
            Assert(request.IsHeld == expected && flags.Count == (expected ? 1 : 0), "incorrect display hold predicate " + mask);
            request.Update(fullscreen, visible, minimized, playing: false, closed);
            Assert(!request.IsHeld && (!expected || flags.SequenceEqual(new[] { FullscreenDisplayRequest.WhilePlaying, FullscreenDisplayRequest.Continuous })), "pause retained wake request " + mask);
        }
        return Task.CompletedTask;
    }

    private static Task Fp0317Idempotence()
    {
        var calls = new List<uint>();
        var request = new FullscreenDisplayRequest(value => { calls.Add(value); return FullscreenDisplayRequest.Continuous; });
        for (var count = 0; count < 10000; count++) request.Update(true, true, false, true, false);
        for (var count = 0; count < 10000; count++) request.Update(true, true, false, false, false);
        Assert(calls.SequenceEqual(new[] { 0x80000003u, 0x80000000u }), "unchanged playback repeatedly changed execution state");
        request.SetRequired(true); request.Dispose(); request.Dispose();
        Assert(!request.IsHeld && !request.SetRequired(true) && calls.Count == 4, "dispose was not idempotent or allowed reacquire");
        var native = typeof(FullscreenDisplayRequest).GetMethod("SetThreadExecutionState", BindingFlags.NonPublic | BindingFlags.Static)!;
        var binding = native.GetCustomAttribute<DllImportAttribute>()!;
        Assert(binding.Value == "kernel32.dll" && binding.ExactSpelling && binding.EntryPoint == "SetThreadExecutionState"
            && native.ReturnType == typeof(uint) && native.GetParameters().Single().ParameterType == typeof(uint), "native ABI changed");
        return Task.CompletedTask;
    }

    private static async Task Fp0317Failures()
    {
        var responses = new Queue<uint>(new uint[] { 0, 0x80000000, 0, 0x80000003 });
        var calls = new List<uint>();
        using var request = new FullscreenDisplayRequest(value => { calls.Add(value); return responses.Dequeue(); });
        Assert(!request.SetRequired(true) && !request.IsHeld, "failed acquire was recorded as held");
        Assert(request.SetRequired(true) && request.IsHeld, "failed acquire could not retry");
        Assert(!request.SetRequired(false) && request.IsHeld, "failed release was recorded as released");
        Assert(request.SetRequired(false) && !request.IsHeld && calls.Count == 4, "failed release could not retry");
        var rejected = await Task.Run(() =>
        {
            try { request.SetRequired(true); return false; }
            catch (InvalidOperationException) { return true; }
        });
        Assert(rejected && calls.Count == 4, "another thread mutated UI thread execution state");
    }

    private static Task Fp0317Window()
    {
        using var f = new Fr0317Fixture("fullscreen-power"); var window = f.Window;
        var flags = new List<uint>();
        var request = new FullscreenDisplayRequest(value => { flags.Add(value); return FullscreenDisplayRequest.Continuous; });
        Fr0317Set(window, "_fullscreenDisplayRequest", request);
        f.Open();
        Assert(Fr0317Get<bool>(window, "_fullscreenPowerInitialized") && flags.Count == 0, "offscreen embedded player requested display power");
        var player = Fr0317Get<NativePlayer>(window, "_player");
        typeof(NativePlayer).GetProperty(nameof(NativePlayer.IsPlaying))!.SetValue(player, true);
        Fr0317Call(window, "RefreshExpandedPlayer");
        Assert(!request.IsHeld && flags.Count == 0, "hidden playing fixture prevented display sleep");
        request.SetRequired(true);
        typeof(NativePlayer).GetProperty(nameof(NativePlayer.IsPlaying))!.SetValue(player, false);
        Fr0317Call(window, "RefreshExpandedPlayer");
        Assert(!request.IsHeld, "pause refresh did not release request");
        request.SetRequired(true); window.WindowState = WindowState.Minimized;
        // WPF raises StateChanged from native presentation. This fixture has
        // no HWND, so exercise its handler after setting the state explicitly.
        Fr0317Call(window, "FullscreenPowerStateChanged", window, EventArgs.Empty);
        Assert(!request.IsHeld, "minimize did not release request");
        request.SetRequired(true);
        Fr0317Call(window, "FullscreenPowerVisibilityChanged", window, new DependencyPropertyChangedEventArgs());
        Assert(!request.IsHeld, "visibility event did not release request");
        request.SetRequired(true); Fr0317Call(window, "CloseExpandedPlayer");
        Assert(!request.IsHeld, "leaving expanded player did not release request");
        request.SetRequired(true); window.Close();
        Assert(!request.IsHeld && Fr0317Get<object?>(window, "_fullscreenDisplayRequest") is null
            && !Fr0317Get<bool>(window, "_fullscreenPowerInitialized"), "window close retained request or subscription ownership");
        Assert(PresentationSource.FromVisual(window) is null && new WindowInteropHelper(window).Handle == IntPtr.Zero, "power fixture created native presentation");
        Assert(flags.Count == 10, "unexpected duplicate execution state writes");
        return Task.CompletedTask;
    }
}
