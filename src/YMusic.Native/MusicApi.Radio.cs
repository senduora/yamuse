using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace YMusic.Native;

// Protocol source: upstream-dotnet, MIT, Copyright (c) 2026 FrigaT, commit
// 8c929094386137669766632f9cc2c97d3b05f670: RadioSessionsClient.cs,
// RotorSession.cs, SessionFeedback.cs and sample MusicCatalog.Radio.cs.
// Attribution and the upstream license belong in THIRD-PARTY-NOTICES.txt.
internal sealed partial class MusicApi
{
    private const int WaveQueueLimit = 20;
    private readonly SemaphoreSlim _waveGate = new(1, 1);
    private readonly object _waveSync = new();
    // Confirmed refreshes share the same in-memory owner across token rotations.
    // A newly authenticated or loaded session starts a separate chain.
    private readonly ConditionalWeakTable<Session, object> _waveOwners = new();
    private WaveState? _waveState;
    private long _waveGeneration;

    public async Task<RadioBatch> StartWaveAsync(Session session, CancellationToken cancellationToken,
        WaveMode mode = WaveMode.Personal, string? trackSeedId = null, string? albumSeedId = null)
    {
        var candidate = await RequestWaveAsync(session, cancellationToken, mode, trackSeedId, albumSeedId).ConfigureAwait(false);
        return AcceptWave(candidate, cancellationToken);
    }

    // Fetching a new station must not displace the playing station. The window
    // accepts the candidate only after it has checked the current playback intent.
    internal async Task<WaveCandidate> RequestWaveAsync(Session session, CancellationToken cancellationToken,
        WaveMode mode, string? trackSeedId, string? albumSeedId)
    {
        _ = WaveSeed(mode, trackSeedId, albumSeedId);
        var generation = WaveGeneration();
        ThrowIfWaveReset(generation, cancellationToken);
        var batch = await NewWaveBatchAsync(session, [], mode, trackSeedId, albumSeedId, cancellationToken).ConfigureAwait(false);
        ThrowIfWaveReset(generation, cancellationToken);
        return new(batch with { Station = new object() }, WaveOwner(session), generation);
    }

    internal RadioBatch AcceptWave(WaveCandidate candidate, CancellationToken cancellationToken)
    {
        CommitWaveBatch(candidate.Batch, [], candidate.Account, candidate.Generation, cancellationToken);
        return candidate.Batch;
    }

    /// <summary>
    /// Gets another server batch. Its SessionId can change when the server has
    /// forgotten the session; feedback must use the batch that actually played.
    /// </summary>
    public Task<RadioBatch> MoreWaveAsync(Session session, string sessionId, CancellationToken cancellationToken)
        => MoreWaveCoreAsync(session, sessionId, null, cancellationToken);

    // An ignored UI result can still have renewed the server ID. Only the batch's
    // station identity grants access to a newer ID in that same account and station.
    internal Task<RadioBatch> MoreWaveAsync(Session session, RadioBatch batch, CancellationToken cancellationToken)
        => MoreWaveCoreAsync(session, batch.SessionId, batch.Station, cancellationToken);

    private async Task<RadioBatch> MoreWaveCoreAsync(Session session, string sessionId, object? station, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var generation = WaveGeneration();
        await _waveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfWaveReset(generation, cancellationToken);
            WaveState state;
            lock (_waveSync)
            {
                state = _waveState is { } current && ReferenceEquals(current.Owner, WaveOwner(session))
                    && (station is null ? current.SessionId == sessionId : ReferenceEquals(current.Station, station))
                    ? current : throw new MusicException("Сессия Моей волны больше не активна. Запустите волну заново.");
            }
            using var content = WaveJson(new { queue = state.Received });
            using var json = await ApiAsync(HttpMethod.Post,
                $"/rotor/session/{CatalogId(state.SessionId)}/tracks", session, content, cancellationToken).ConfigureAwait(false);
            ThrowIfWaveReset(generation, cancellationToken);
            var result = Result(json);
            if (result.ValueKind != JsonValueKind.Object) throw WaveFormatError();
            RadioBatch batch;
            if (Bool(result, "unknownSession"))
            {
                batch = await NewWaveBatchAsync(session, state.Received, state.Mode, state.TrackSeedId, state.AlbumSeedId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!result.TryGetProperty("sequence", out var sequence) || sequence.ValueKind != JsonValueKind.Array)
                {
                    if (!Bool(result, "terminated")) throw WaveFormatError();
                    batch = await NewWaveBatchAsync(session, state.Received, state.Mode, state.TrackSeedId, state.AlbumSeedId, cancellationToken).ConfigureAwait(false);
                }
                else if (sequence.GetArrayLength() == 0)
                {
                    // Exactly one renewal attempt, preserving only the bounded
                    // received queue. No local shuffle or synthesized tracks.
                    batch = await NewWaveBatchAsync(session, state.Received, state.Mode, state.TrackSeedId, state.AlbumSeedId, cancellationToken).ConfigureAwait(false);
                }
                else { batch = ReadWaveBatch(result, state.SessionId, state.Mode, state.TrackSeedId, state.AlbumSeedId); }
            }
            batch = batch with { Station = state.Station };
            CommitWaveBatch(batch, state.Received, state.Owner, generation, cancellationToken, expectedStation: state.Station);
            return batch;
        }
        finally { _waveGate.Release(); }
    }

    /// <summary>
    /// The caller supplies actual non-paused playback seconds for finished/skip,
    /// never duration, seek position or time spent buffering. No events are
    /// inferred merely from fetching a batch. Await calls in playback order.
    /// </summary>
    public async Task SendWaveFeedbackAsync(Session session, string sessionId, string batchId,
        string type, string? trackId, double playedSeconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        if (type is not ("radioStarted" or "trackStarted" or "trackFinished" or "skip"))
            throw new ArgumentException("Неподдерживаемый тип события Моей волны.", nameof(type));
        if (!double.IsFinite(playedSeconds) || playedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(playedSeconds), "Время прослушивания должно быть конечным и неотрицательным.");
        if (type != "radioStarted") ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        if ((type is "radioStarted" or "trackStarted") && playedSeconds != 0)
            throw new ArgumentException("Событие начала не должно содержать время завершённого прослушивания.", nameof(playedSeconds));
        if (type == "radioStarted" && !string.IsNullOrEmpty(trackId))
            throw new ArgumentException("Начало радио не относится к отдельному треку.", nameof(trackId));

        var generation = WaveGeneration();
        lock (_waveSync)
        {
            ThrowIfWaveReset(generation, cancellationToken);
            if (_waveState is not { } current || !ReferenceEquals(current.Owner, WaveOwner(session)))
                throw new OperationCanceledException("Событие Моей волны отменено после смены входа.", cancellationToken);
        }

        var radioEvent = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
        };
        if (type != "radioStarted") radioEvent["trackId"] = trackId;
        if (type is "trackFinished" or "skip") radioEvent["totalPlayedSeconds"] = playedSeconds;
        using var content = WaveJson(new Dictionary<string, object?> { ["event"] = radioEvent, ["batchId"] = batchId });
        using var json = await ApiAsync(HttpMethod.Post,
            $"/rotor/session/{CatalogId(sessionId)}/feedback/", session, content, cancellationToken).ConfigureAwait(false);
        ThrowIfWaveReset(generation, cancellationToken);
        if (Result(json).ValueKind != JsonValueKind.Object)
            throw new MusicException("Сервис не подтвердил событие Моей волны.");
    }

    /// <summary>Call on sign-out and before disposing the shared HTTP client.</summary>
    public void ResetWaveState()
    {
        lock (_waveSync) { _waveState = null; _waveGeneration++; }
    }

    private async Task<RadioBatch> NewWaveBatchAsync(Session session, IReadOnlyList<string> queue, WaveMode mode, string? trackSeedId, string? albumSeedId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var content = WaveJson(new { seeds = new[] { WaveSeed(mode, trackSeedId, albumSeedId) }, queue, includeTracksInResponse = true });
        using var json = await ApiAsync(HttpMethod.Post, "/rotor/session/new", session, content, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = Result(json);
        if (result.ValueKind != JsonValueKind.Object) throw WaveFormatError();
        var sessionId = Text(result, "radioSessionId");
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 512 || sessionId is "." or ".." || sessionId.Any(char.IsControl)) throw WaveFormatError();
        return ReadWaveBatch(result, sessionId, mode, trackSeedId, albumSeedId);
    }

    private static RadioBatch ReadWaveBatch(JsonElement result, string sessionId, WaveMode mode, string? trackSeedId, string? albumSeedId)
    {
        var batchId = Text(result, "batchId");
        if (string.IsNullOrWhiteSpace(batchId) || !result.TryGetProperty("sequence", out var sequence)
            || sequence.ValueKind != JsonValueKind.Array) throw WaveFormatError();
        var tracks = new List<TrackRow>();
        foreach (var item in sequence.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw WaveFormatError();
            var itemType = Text(item, "type");
            if (itemType.Length > 0 && itemType != "track") continue;
            if (!item.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object) throw WaveFormatError();
            var id = Text(track, "id");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || id.Any(char.IsControl)) throw WaveFormatError();
            var albums = track.TryGetProperty("albums", out var albumArray) && albumArray.ValueKind == JsonValueKind.Array
                ? albumArray.EnumerateArray().ToArray() : [];
            var albumId = albums.Length > 0 ? Text(albums[0], "id") : "";
            var artists = track.TryGetProperty("artists", out var artistArray) && artistArray.ValueKind == JsonValueKind.Array
                ? string.Join(", ", artistArray.EnumerateArray().Select(artist => Text(artist, "name"))) : "";
            tracks.Add(new TrackRow
            {
                Number = tracks.Count + 1, Id = id,
                RequestId = IsTrackReferencePart(albumId) && !id.Contains(':') ? $"{id}:{albumId}" : id,
                Title = TrackTitle(track), Artist = TrackText(artists), Artists = ArtistReferences(track),
                PrimaryArtistName = PrimaryArtistName(track),
                Album = albums.Length > 0 ? TrackText(JsonString(albums[0], "title")) : "", CoverUri = TrackArtwork(track),
                DurationMs = TrackDuration(track),
                Available = Bool(track, "available") || Bool(track, "availableForPremiumUsers") || Bool(track, "availableFullWithoutPermission")
            });
        }
        if (tracks.Count == 0) throw new MusicException("Моя волна не вернула поддерживаемых треков. Попробуйте запустить её позже.");
        return new(sessionId, batchId, tracks.ToArray(), mode, trackSeedId, albumSeedId);
    }

    private long WaveGeneration() { lock (_waveSync) return _waveGeneration; }
    private object WaveOwner(Session session) => _waveOwners.GetValue(session, static _ => new object());
    private void ThrowIfWaveReset(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_waveSync)
            if (generation != _waveGeneration) throw new OperationCanceledException("Запрос Моей волны отменён после сброса входа.", cancellationToken);
    }
    private void CommitWaveBatch(RadioBatch batch, IReadOnlyList<string> received, object owner, long generation,
        CancellationToken cancellationToken, object? expectedStation = null)
    {
        var tail = new List<string>(received);
        foreach (var track in batch.Tracks)
        {
            tail.RemoveAll(id => id == track.RequestId);
            tail.Add(track.RequestId);
            if (tail.Count > WaveQueueLimit) tail.RemoveAt(0);
        }
        lock (_waveSync)
        {
            ThrowIfWaveReset(generation, cancellationToken);
            if (expectedStation is not null && !ReferenceEquals(expectedStation, _waveState?.Station))
                throw new OperationCanceledException("Активная волна сменилась.", cancellationToken);
            _waveState = new(batch.SessionId, tail.ToArray(), batch.Mode, batch.TrackSeedId, batch.AlbumSeedId, owner, batch.Station!);
        }
    }
    // Collection seed is supplied by the service's stations/list response, not a
    // local shuffle of likes. The session endpoint and queue format are shared.
    // Track stations use the canonical track ID, never an album composite or a
    // title search. The same upstream commit's GetSimilarRadioAsync uses
    // StartRadioAsync(["track:" + trackId]) and preserves seeds on renewal.
    internal static bool IsTrackWaveSeed(string? id) => id is { Length: > 0 and <= 128 }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    // Album seeds are documented by pinned yandex-music-api Wave.seeds
    // (9ca132e5c18a72aab779c1fc68c169380a9357d0/yandex_music/wave/wave.py).
    internal static bool IsAlbumWaveSeed(string? id) => IsTrackWaveSeed(id);
    private static string WaveSeed(WaveMode mode, string? trackSeedId, string? albumSeedId) => mode switch
    {
        WaveMode.Personal => "user:onyourwave",
        WaveMode.Collection => "personal:collection",
        WaveMode.Track when IsTrackWaveSeed(trackSeedId) => "track:" + trackSeedId,
        WaveMode.Track => throw new ArgumentException("Для волны нужен точный ID трека.", nameof(trackSeedId)),
        WaveMode.Album when IsAlbumWaveSeed(albumSeedId) => "album:" + albumSeedId,
        WaveMode.Album => throw new ArgumentException("Для волны нужен точный ID альбома.", nameof(albumSeedId)),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    private static StringContent WaveJson(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    private static MusicException WaveFormatError() => new("Изменился формат ответа Моей волны. Выдача не была принята.");
    internal sealed record WaveCandidate(RadioBatch Batch, object Account, long Generation);
    private sealed record WaveState(string SessionId, string[] Received, WaveMode Mode, string? TrackSeedId, string? AlbumSeedId, object Owner, object Station);
}
