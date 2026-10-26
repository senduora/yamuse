using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace YMusic.Native;

internal sealed record Session(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAtUtc, string DeviceId);
internal sealed record DeviceCode(string Code, string UserCode, Uri VerificationUri, int IntervalSeconds, int ExpiresInSeconds);
internal sealed record Account(string Uid, string DisplayName, bool Plus, string? AvatarUri = null);
internal enum AudioQuality { Economical, Optimal, Excellent }
internal sealed record AudioDescriptor(Uri Uri, string Codec, int Bitrate, string? Key = null)
{
    public bool IsLossless => Codec is "flac" or "flac-mp4";
    public string Extension => Codec switch { "flac" => ".flac", "flac-mp4" or "aac-mp4" or "he-aac-mp4" or "aac" or "he-aac" => ".m4a", _ => ".mp3" };
    public string QualityLabel => IsLossless ? "Lossless · FLAC" : Codec.ToUpperInvariant() + (Bitrate > 0 ? $" · {Bitrate} кбит/с" : "");
    public override string ToString() => QualityLabel; // Never format signed URLs or keys.
}
internal sealed record LikedId(string Id, string? AlbumId)
{
    public string RequestId => string.IsNullOrEmpty(AlbumId) ? Id : $"{Id}:{AlbumId}";
}

internal sealed record ArtistReference(string Id, string Name);

internal sealed class TrackRow : INotifyPropertyChanged
{
    public int Number { get; init; }
    public string Id { get; init; } = "";
    public string RequestId { get; init; } = "";
    public string Title { get; init; } = "Недоступный трек";
    public string Artist { get; init; } = "";
    public IReadOnlyList<ArtistReference> Artists { get; init; } = [];
    public string Album { get; init; } = "";
    public string? AlbumId { get; init; }
    public string? CoverUri { get; init; }
    public long DurationMs { get; init; }
    public bool Available { get; init; }
    public string? LocalPath { get; init; }
    public string? LocalQuality { get; init; }
    private bool _isLiked, _likeStateKnown, _canToggleLike;
    public bool IsLiked { get => _isLiked; set { if (_isLiked == value) return; _isLiked = value; Changed(); } }
    public bool LikeStateKnown { get => _likeStateKnown; set { if (_likeStateKnown == value) return; _likeStateKnown = value; Changed(); } }
    public bool CanToggleLike { get => _canToggleLike; set { if (_canToggleLike == value) return; _canToggleLike = value; Changed(); } }
    private string? _downloadedPath;
    private string? _downloadedQuality;
    public string? DownloadedPath { get => _downloadedPath; set { if (_downloadedPath == value) return; _downloadedPath = value; Changed(); Changed(nameof(IsDownloaded)); Changed(nameof(DownloadBadge)); Changed(nameof(CanPlay)); Changed(nameof(RowOpacity)); } }
    public string? DownloadedQuality { get => _downloadedQuality; set { if (_downloadedQuality == value) return; _downloadedQuality = value; Changed(); } }
    public bool IsDownloaded => DownloadedPath is not null;
    public bool CanPlay => Available || IsDownloaded;
    public string DownloadBadge => IsDownloaded ? "✓" : "";
    public string Duration => DurationMs > 0 ? TimeSpan.FromMilliseconds(DurationMs).ToString(@"m\:ss") : "—";
    public double RowOpacity => CanPlay ? 1 : 0.45;
    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; set { _isPlaying = value; Changed(); } }
    public bool Matches(string query) => Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Artist.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Album.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class MusicException(string message, bool authentication = false) : Exception(message)
{
    public bool Authentication { get; } = authentication;
}
