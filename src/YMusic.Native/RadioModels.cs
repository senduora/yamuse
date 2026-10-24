namespace YMusic.Native;

internal enum WaveMode { Personal, Collection }

internal sealed record RadioBatch(string SessionId, string BatchId, IReadOnlyList<TrackRow> Tracks,
    WaveMode Mode = WaveMode.Personal);
