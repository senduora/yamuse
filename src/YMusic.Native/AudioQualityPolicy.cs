using System.Net.Http;
using System.Text.Json;

namespace YMusic.Native;

// Compare service tiers, rather than treating AAC and MP3 bitrates as equivalent.
internal static class AudioQualityPolicy
{
    internal const int OfflineUpgradeTimeoutMilliseconds = 1500;

    internal static bool IsSufficient(string codec, int bitrate, AudioQuality quality)
        => Rank(codec, bitrate) >= (quality switch
        { AudioQuality.Excellent => 2, AudioQuality.Optimal => 1, _ => 0 });

    internal static bool IsBetter(string candidateCodec, int candidateBitrate, string existingCodec, int existingBitrate)
    {
        var candidate = Rank(candidateCodec, candidateBitrate);
        var existing = Rank(existingCodec, existingBitrate);
        return candidate > existing || candidate == existing && candidate < 3
            && Family(candidateCodec) == Family(existingCodec) && candidateBitrate > existingBitrate;
    }

    private static int Rank(string codec, int bitrate)
    {
        var family = Family(codec);
        if (family == "flac") return 3;
        if (family == "aac" && bitrate >= 256 || family == "mp3" && bitrate >= 320) return 2;
        return bitrate >= 192 ? 1 : 0;
    }

    private static string Family(string codec) => codec.ToLowerInvariant() switch
    {
        "flac" or "flac-mp4" => "flac",
        "aac" or "aac-mp4" or "he-aac" or "he-aac-mp4" => "aac",
        var other => other
    };
}

public partial class MainWindow
{
    private async Task<AudioDescriptor?> TryResolveOfflineUpgradeAsync(TrackRow track, string localPath,
        AudioQuality quality, CancellationToken token)
    {
        // An optional quality probe must never refresh credentials or require login
        // before opening a valid offline file. Unindexed user files stay local.
        var session = _session;
        if (session is null || session.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1)) return null;
        var accountGeneration = _accountGeneration;
        try
        {
            var existing = _offline.Find(track);
            if (existing is null || !string.Equals(localPath, _offline.ResolvePath(existing.FileName), StringComparison.OrdinalIgnoreCase)
                || AudioQualityPolicy.IsSufficient(existing.Codec, existing.Bitrate, quality)) return null;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(AudioQualityPolicy.OfflineUpgradeTimeoutMilliseconds);
            var audio = await _api.ResolveAudioAsync(session, track.RequestId, quality, budget.Token);
            token.ThrowIfCancellationRequested();
            budget.Token.ThrowIfCancellationRequested();
            if (accountGeneration != _accountGeneration || !ReferenceEquals(session, _session)) return null;
            return AudioQualityPolicy.IsBetter(audio.Codec, audio.Bitrate, existing.Codec, existing.Bitrate) ? audio : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is MusicException or HttpRequestException or IOException or JsonException or FormatException or TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            _diagnostics.LastErrorType = "QualityUpgrade:" + error.GetType().Name;
            return null;
        }
    }
}
