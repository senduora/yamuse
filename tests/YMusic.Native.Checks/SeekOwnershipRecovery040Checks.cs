using System.Windows.Controls;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckSeekOwnershipRecovery040()
    {
        foreach (var surface in new[] { "main", "full", "mini" })
            await Run("SeekOwnershipRecovery040: near-end automatic transition respects held " + surface + " seek", () => So040Held(surface));
    }

    private static async Task So040Held(string surface)
    {
        using var f = new Pc016Fixture("seek-owner040-" + surface); await f.Play();
        var path = f.Player.SourceLocalPath!;
        TrackRow Row(string id) => new() { Id = id, RequestId = id, Title = "Fixture " + id, Artist = "Fixture", DurationMs = 30000, Available = true, LocalPath = path };
        var a = Row("seek-a-" + surface); var b = Row("seek-b-" + surface);
        var queue = MediaField<PlaybackQueue>(f.Window, "_playbackQueue"); queue.SetItems([a, b], a);
        f.Player.SetCrossfade(3); await f.Window.PlayTrackAsync(a); f.Views();
        if (MediaField<Task?>(f.Window, "_preloadTask") is { } prepare) await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(f.Player.HasPrepared, "next track was not prepared for the crossfade boundary");
        MediaField<DispatcherTimer>(f.Window, "_progressTimer").Stop();
        f.Player.Seek(TimeSpan.FromSeconds(28)); await Until(() => f.Player.Position.TotalSeconds > 27.5);
        var slider = surface == "main" ? Control<Slider>(f.Window, "SeekSlider")
            : surface == "full" ? EpElement<Slider>(f.Window, "ExpandedPlayerSeek") : f.Mini!.SeekSlider;
        Pc016Drag(slider, true); slider.Value = 4;
        Assert(surface == "main" ? MediaField<bool>(f.Window, "_seekDragging")
            : surface == "full" ? MediaField<bool>(f.Window, "_expandedSeekDragging") : f.Mini!.IsSeeking, "pointer seek did not begin");
        SaInvoke(f.Window, "TryAutomaticTransition"); await Task.Delay(30);
        Assert(queue.CurrentIndex == 0 && ReferenceEquals(queue.Current, a) && ReferenceEquals(MediaField<TrackRow>(f.Window, "_current"), a),
            "automatic transition advanced while holding the " + surface + " seek");
        Pc016Drag(slider, false); await Until(() => Math.Abs(f.Player.Position.TotalSeconds - 4) < .5);
        Assert(queue.CurrentIndex == 0 && f.Player.IsPlaying && f.Mini?.IsSeeking == false,
            "seek release did not retain playback at the requested position");
    }
}
