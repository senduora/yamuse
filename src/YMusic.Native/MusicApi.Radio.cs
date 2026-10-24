using System.Globalization;
using System.Net.Http;
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
    private WaveState? _waveState;
    private long _waveGeneration;

    public async Task<RadioBatch> StartWaveAsync(Session session, CancellationToken cancellationToken,
        WaveMode mode = WaveMode.Personal)
    {
        _ = WaveSeed(mode);
        var generation = WaveGeneration();
        await _waveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfWaveReset(generation, cancellationToken);
            var batch = await NewWaveBatchAsync(session, [], mode, cancellationToken).ConfigureAwait(false);
            CommitWaveBatch(batch, [], session, generation, cancellationToken);
            return batch;
        }
        finally { _waveGate.Release(); }
    }

    /// <summary>
    /// Gets another server batch. Its SessionId can change when the server has
    /// forgotten the session; feedback must use the batch that actually played.
    /// </summary>
    public async Task<RadioBatch> MoreWaveAsync(Session session, string sessionId, CancellationToken cancellationToken)
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
                state = _waveState is { } current && current.SessionId == sessionId && current.Owner == session
                    ? current : throw new MusicException("Сессия Моей волны больше не активна. Запустите волну заново.");
            }
            using var content = WaveJson(new { queue = state.Received });
            using var json = await ApiAsync(HttpMethod.Post,
                $"/rotor/session/{Uri.EscapeDataString(sessionId)}/tracks", session, content, cancellationToken).ConfigureAwait(false);
            ThrowIfWaveReset(generation, cancellationToken);
            var result = Result(json);
            if (result.ValueKind != JsonValueKind.Object) throw WaveFormatError();
            RadioBatch batch;
            if (Bool(result, "unknownSession"))
            {
                batch = await NewWaveBatchAsync(session, state.Received, state.Mode, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!result.TryGetProperty("sequence", out var sequence) || sequence.ValueKind != JsonValueKind.Array)
                {
                    if (!Bool(result, "terminated")) throw WaveFormatError();
                    batch = await NewWaveBatchAsync(session, state.Received, state.Mode, cancellationToken).ConfigureAwait(false);
                }
                else if (sequence.GetArrayLength() == 0)
                {
                    // Exactly one renewal attempt, preserving only the bounded
                    // received queue. No local shuffle or synthesized tracks.
                    batch = await NewWaveBatchAsync(session, state.Received, state.Mode, cancellationToken).ConfigureAwait(false);
                }
                else { batch = ReadWaveBatch(result, sessionId, state.Mode); }
            }
            CommitWaveBatch(batch, state.Received, session, generation, cancellationToken);
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
            if (_waveState is not { } current || current.Owner != session)
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
            $"/rotor/session/{Uri.EscapeDataString(sessionId)}/feedback/", session, content, cancellationToken).ConfigureAwait(false);
        ThrowIfWaveReset(generation, cancellationToken);
        if (Result(json).ValueKind != JsonValueKind.Object)
            throw new MusicException("Сервис не подтвердил событие Моей волны.");
    }

    /// <summary>Call on sign-out and before disposing the shared HTTP client.</summary>
    public void ResetWaveState()
    {
        lock (_waveSync) { _waveState = null; _waveGeneration++; }
    }

    private async Task<RadioBatch> NewWaveBatchAsync(Session session, IReadOnlyList<string> queue, WaveMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var content = WaveJson(new { seeds = new[] { WaveSeed(mode) }, queue, includeTracksInResponse = true });
        using var json = await ApiAsync(HttpMethod.Post, "/rotor/session/new", session, content, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = Result(json);
        if (result.ValueKind != JsonValueKind.Object) throw WaveFormatError();
        var sessionId = Text(result, "radioSessionId");
        if (string.IsNullOrWhiteSpace(sessionId)) throw WaveFormatError();
        return ReadWaveBatch(result, sessionId, mode);
    }

    private static RadioBatch ReadWaveBatch(JsonElement result, string sessionId, WaveMode mode)
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
            if (string.IsNullOrWhiteSpace(id)) throw WaveFormatError();
            var albums = track.TryGetProperty("albums", out var albumArray) && albumArray.ValueKind == JsonValueKind.Array
                ? albumArray.EnumerateArray().ToArray() : [];
            var albumId = albums.Length > 0 ? Text(albums[0], "id") : "";
            var artists = track.TryGetProperty("artists", out var artistArray) && artistArray.ValueKind == JsonValueKind.Array
                ? string.Join(", ", artistArray.EnumerateArray().Select(artist => Text(artist, "name"))) : "";
            var title = Text(track, "title");
            var version = Text(track, "version");
            long.TryParse(Text(track, "durationMs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var durationMs);
            tracks.Add(new TrackRow
            {
                Number = tracks.Count + 1, Id = id,
                RequestId = albumId.Length > 0 && !id.Contains(':') ? $"{id}:{albumId}" : id,
                Title = title.Length == 0 ? "Недоступный трек" : title + (version.Length > 0 ? " · " + version : ""),
                Artist = artists, Artists = ArtistReferences(track), Album = albums.Length > 0 ? Text(albums[0], "title") : "", CoverUri = TrackArtwork(track),
                DurationMs = Math.Max(0, durationMs),
                Available = Bool(track, "available") || Bool(track, "availableForPremiumUsers") || Bool(track, "availableFullWithoutPermission")
            });
        }
        if (tracks.Count == 0) throw new MusicException("Моя волна не вернула поддерживаемых треков. Попробуйте запустить её позже.");
        return new(sessionId, batchId, tracks.ToArray(), mode);
    }

    private long WaveGeneration() { lock (_waveSync) return _waveGeneration; }
    private void ThrowIfWaveReset(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_waveSync)
            if (generation != _waveGeneration) throw new OperationCanceledException("Запрос Моей волны отменён после сброса входа.", cancellationToken);
    }
    private void CommitWaveBatch(RadioBatch batch, IReadOnlyList<string> received, Session owner, long generation,
        CancellationToken cancellationToken)
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
            _waveState = new(batch.SessionId, tail.ToArray(), batch.Mode, owner);
        }
    }
    // Collection seed is supplied by the service's stations/list response, not a
    // local shuffle of likes. The session endpoint and queue format are shared.
    private static string WaveSeed(WaveMode mode) => mode switch
    {
        WaveMode.Personal => "user:onyourwave",
        WaveMode.Collection => "personal:collection",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    private static StringContent WaveJson(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    private static MusicException WaveFormatError() => new("Изменился формат ответа Моей волны. Выдача не была принята.");
    private sealed record WaveState(string SessionId, string[] Received, WaveMode Mode, Session Owner);
}
