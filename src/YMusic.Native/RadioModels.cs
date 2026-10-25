namespace YMusic.Native;

internal enum WaveMode { Personal, Collection, Track }

internal sealed record RadioBatch(string SessionId, string BatchId, IReadOnlyList<TrackRow> Tracks,
    WaveMode Mode = WaveMode.Personal, string? TrackSeedId = null);
