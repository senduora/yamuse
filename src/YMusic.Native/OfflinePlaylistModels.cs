using System.Text.Json.Serialization;

namespace YMusic.Native;

internal sealed record OfflinePlaylistTrack(
    [property: JsonRequired] string Id, [property: JsonRequired] string RequestId,
    [property: JsonRequired] string Title, [property: JsonRequired] string Artist,
    [property: JsonRequired] string Album, [property: JsonRequired] long DurationMs,
    [property: JsonRequired] bool Available, string? CoverUri = null, IReadOnlyList<ArtistReference>? Artists = null, string? AlbumId = null);

internal sealed record OfflinePlaylist(
    [property: JsonRequired] string Owner, [property: JsonRequired] string Kind,
    [property: JsonRequired] string Title, [property: JsonRequired] int Revision,
    [property: JsonRequired] AudioQuality RequestedAudioQuality,
    [property: JsonRequired] DateTimeOffset UpdatedUtc,
    [property: JsonRequired] IReadOnlyList<OfflinePlaylistTrack> Tracks);

internal sealed record OfflinePlaylistStatus(int Total, int Downloaded, bool Complete,
    bool ChangedSinceSnapshot, bool HasSnapshot);

internal sealed record OfflinePlaylistIndex(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] IReadOnlyList<OfflinePlaylist> Playlists);
