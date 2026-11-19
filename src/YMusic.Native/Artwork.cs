using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

public enum ArtworkLoadStatus { None, Loading, Ready, Unavailable }

/// <summary>Loads CDN artwork for visible, loaded Images without blocking the UI thread.</summary>
public static class Artwork
{
    public static readonly DependencyProperty SourceUriProperty = DependencyProperty.RegisterAttached(
        "SourceUri", typeof(string), typeof(Artwork), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty PixelSizeProperty = DependencyProperty.RegisterAttached(
        "PixelSize", typeof(int), typeof(Artwork), new FrameworkPropertyMetadata(128, Changed, CoerceSize));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(ImageState), typeof(Artwork));
    private static readonly DependencyPropertyKey LoadStatusPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "LoadStatus", typeof(ArtworkLoadStatus), typeof(Artwork), new PropertyMetadata(ArtworkLoadStatus.None));
    public static readonly DependencyProperty LoadStatusProperty = LoadStatusPropertyKey.DependencyProperty;
    public static ArtworkLoadStatus GetLoadStatus(DependencyObject image) => (ArtworkLoadStatus)image.GetValue(LoadStatusProperty);
    private static ArtworkCache? _cache;
    // Internal injection point keeps deterministic checks away from the user's cache and network.
    internal static ArtworkCache Cache
    {
        get => _cache ??= new(Path.Combine(BrandPaths.Current.DataDirectory, "artwork"));
        set => _cache = value;
    }

    public static string? GetSourceUri(DependencyObject value) => (string?)value.GetValue(SourceUriProperty);
    public static void SetSourceUri(DependencyObject value, string? uri) => value.SetValue(SourceUriProperty, uri);
    internal static void SetTrackSource(Image image, string? uri, bool trackChanged)
    {
        if (GetSourceUri(image) != uri) SetSourceUri(image, uri);
        else if (trackChanged && GetLoadStatus(image) == ArtworkLoadStatus.Unavailable
            && image.GetValue(StateProperty) is ImageState state) Start(image, state);
    }
    public static int GetPixelSize(DependencyObject value) => (int)value.GetValue(PixelSizeProperty);
    public static void SetPixelSize(DependencyObject value, int size) => value.SetValue(PixelSizeProperty, size);
    private static object CoerceSize(DependencyObject value, object size) => ArtworkUri.Size((int)size);

    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not Image image) return;
        var state = (ImageState?)image.GetValue(StateProperty);
        if (state is null)
        {
            image.SetValue(StateProperty, state = new ImageState());
            image.Loaded += Loaded;
            image.Unloaded += Unloaded;
            image.IsVisibleChanged += VisibilityChanged;
        }
        Start(image, state);
    }
    private static void Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is Image image && image.GetValue(StateProperty) is ImageState state) Start(image, state);
    }
    private static void Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is Image image && image.GetValue(StateProperty) is ImageState state) Stop(image, state);
    }
    private static void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is Image image && image.GetValue(StateProperty) is ImageState state) Start(image, state);
    }
    private static void Stop(Image image, ImageState state)
    {
        state.Generation++;
        state.Cancellation?.Cancel();
        state.Cancellation = null;
        image.SetValue(LoadStatusPropertyKey, string.IsNullOrWhiteSpace(GetSourceUri(image)) ? ArtworkLoadStatus.None : ArtworkLoadStatus.Loading);
        image.Source = null;
    }
    private static void Start(Image image, ImageState state)
    {
        Stop(image, state);
        if (!image.IsLoaded || !image.IsVisible || string.IsNullOrWhiteSpace(GetSourceUri(image))) return;
        var cancellation = new CancellationTokenSource();
        state.Cancellation = cancellation;
        _ = ApplyAsync(image, state, state.Generation, cancellation, GetSourceUri(image), GetPixelSize(image));
    }
    private static async Task ApplyAsync(Image image, ImageState state, long generation,
        CancellationTokenSource cancellation, string? source, int size)
    {
        bool IsCurrent() => !cancellation.IsCancellationRequested && state.Generation == generation
            && image.IsLoaded && image.IsVisible && GetSourceUri(image) == source && GetPixelSize(image) == size;
        void Publish(ArtworkCache.ArtworkResult result)
        {
            image.Source = result.Bitmap;
            if (IsCurrent()) image.SetValue(LoadStatusPropertyKey, result.Bitmap is null ? ArtworkLoadStatus.Unavailable : ArtworkLoadStatus.Ready);
        }
        try
        {
            var cache = Cache;
            while (IsCurrent())
            {
                image.SetValue(LoadStatusPropertyKey, ArtworkLoadStatus.Loading);
                var result = await cache.GetResultAsync(source, size, cancellation.Token, retryAfterClear: true);
                if (!IsCurrent()) return;
                if (!cache.TryPublish(result, () => Publish(result))) continue;
                if (!result.TemporaryFailure || !IsCurrent()) return;
                var recovered = await cache.WaitForCachedArtworkAsync(source, size, result.CacheGeneration!, cancellation.Token);
                if (!IsCurrent()) return;
                if (recovered is { } ready && cache.TryPublish(ready, () => Publish(ready))) return;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException or FormatException)
        {
            // A failed current request is distinct from the temporary empty
            // source during a switch. Old/cancelled requests never publish state.
            if (IsCurrent())
                image.SetValue(LoadStatusPropertyKey, ArtworkLoadStatus.Unavailable);
        }
        finally
        {
            if (ReferenceEquals(state.Cancellation, cancellation)) state.Cancellation = null;
            cancellation.Dispose();
        }
    }
    private sealed class ImageState
    {
        public long Generation;
        public CancellationTokenSource? Cancellation;
    }
}

internal static class ArtworkUri
{
    private const string Placeholder = "yamuse-artwork-size-placeholder";
    internal static int Size(int size) => size <= 64 ? 64 : size <= 128 ? 128 : size <= 256 ? 256 : 1024;
    // Keep small list covers cheap; the expanded player requests its own large rendition.
    private static int CdnSize(int size) => Size(size) switch { 64 => 100, 128 => 200, 256 => 400, _ => 1000 };
    internal static string? Metadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)) return null;
        value = value.Trim();
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        else if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (value.Contains(Placeholder, StringComparison.Ordinal)) return null;
        return Uri.TryCreate(value.Replace("%%", Placeholder, StringComparison.Ordinal), UriKind.Absolute, out var uri)
            && Valid(uri) ? uri.AbsoluteUri.Replace(Placeholder, "%%", StringComparison.Ordinal) : null;
    }
    internal static bool TryCreate(string? value, int size, out Uri uri)
    {
        var metadata = Metadata(value);
        var rendition = CdnSize(size);
        return Uri.TryCreate(metadata?.Replace("%%", $"{rendition}x{rendition}", StringComparison.Ordinal),
            UriKind.Absolute, out uri!) && Valid(uri);
    }
    internal static bool Valid(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && ValidVersion(uri.Query)
        && uri.AbsoluteUri.Length <= 2048 && uri.AbsolutePath.Length > 1
        && uri.DnsSafeHost.ToLowerInvariant() is "avatars.yandex.net" or "avatars.mds.yandex.net" or "avatars.mdst.yandex.net";

    // Personal-playlist covers carry a bare numeric cache version, e.g. ?1730453322475.
    // Keep it in the request and cache key; arbitrary query parameters remain unsupported.
    private static bool ValidVersion(string query) => query.Length == 0
        || query.Length is > 1 and <= 21 && query.AsSpan(1).IndexOfAnyExcept("0123456789") < 0;
}

/// <summary>Anonymous, bounded, shared cache. Frozen results can cross dispatcher threads.</summary>
internal sealed class ArtworkCache : IDisposable
{
    internal const int MaximumResponseBytes = 2 * 1024 * 1024;
    internal const int MaximumSourceDimension = 4096;
    internal const long MaximumSourcePixels = 16 * 1024 * 1024;
    private const int MemoryEntryLimit = 128;
    private const long MemoryByteLimit = 16 * 1024 * 1024;
    private const long DiskByteLimit = 128 * 1024 * 1024;
    internal const int DiskEntryLimit = 2048;
    private const int PruneAttemptsPerWrite = 4;
    private static readonly TimeSpan DownloadRetryDelay = TimeSpan.FromMilliseconds(500);
    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly TimeSpan _downloadTimeout;
    private readonly SemaphoreSlim _workers = new(4, 4);
    private readonly SemaphoreSlim _diskGate = new(1, 1);
    private readonly Lock _sync = new();
    private readonly object _publication = new();
    private readonly Dictionary<string, LinkedListNode<MemoryEntry>> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<MemoryEntry> _lru = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    // An abandoned subscription can leave its file open until the reader returns.
    private readonly HashSet<Task> _diskReads = [];
    private readonly Dictionary<string, Recovery> _recoveryWaiters = new(StringComparer.Ordinal);
    private long _memoryBytes;
    private int _pruneOffset;
    private volatile Task _clearCompletion = Task.CompletedTask;
    private bool _disposed;

    internal Func<Task>? DiskReadOpenedForChecks { private get; init; }

    internal ArtworkCache(string directory, HttpMessageHandler? handler = null, TimeSpan? downloadTimeout = null)
    {
        _downloadTimeout = downloadTimeout ?? TimeSpan.FromSeconds(20);
        if (_downloadTimeout <= TimeSpan.Zero || _downloadTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
        _directory = Path.GetFullPath(directory);
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(8)
        }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaximumResponseBytes };
        // Deliberately independent of MusicApi: no OAuth, cookies, referrer or signed audio URL.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Yamuse-Artwork/0.3");
        _http.DefaultRequestHeaders.Accept.ParseAdd("image/*");
    }

    internal async Task<BitmapSource?> GetAsync(string? source, int size, CancellationToken cancellationToken,
        bool retryAfterClear = false)
        => (await GetResultAsync(source, size, cancellationToken, retryAfterClear).ConfigureAwait(false)).Bitmap;

    internal async Task<ArtworkResult> GetResultAsync(string? source, int size, CancellationToken cancellationToken,
        bool retryAfterClear)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ArtworkUri.TryCreate(source, size, out var uri)) return default;
        // Small views still share one bounded decode. The expanded player keeps
        // a distinct large bitmap even when metadata specifies a fixed CDN URL.
        var decodeSize = ArtworkUri.Size(size) > 256 ? 1024 : 256;
        var key = CacheKey(uri, size);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task clearCompletion;
            Pending? pending = null;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                clearCompletion = _clearCompletion;
                if (clearCompletion.IsCompleted)
                {
                    if (_memory.TryGetValue(key, out var node))
                    {
                        _lru.Remove(node); _lru.AddFirst(node);
                        return new(node.Value.Bitmap, CacheGeneration: clearCompletion);
                    }
                    pending = GetPending(uri, key, decodeSize);
                    pending.Waiters++;
                }
            }
            if (pending is not null)
            {
                ArtworkResult result;
                try { result = await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (retryAfterClear && !cancellationToken.IsCancellationRequested
                    && !ReferenceEquals(pending.AdmittedAfterClear, _clearCompletion))
                { result = default; }
                finally
                {
                    lock (_sync)
                    {
                        ReleaseWaiter(key, pending);
                        clearCompletion = _clearCompletion;
                    }
                }
                // Clear may start after the worker returned but before this waiter
                // resumed. The old result belongs to that earlier cache generation.
                if (ReferenceEquals(pending.AdmittedAfterClear, clearCompletion)) return result with { CacheGeneration = clearCompletion };
                if (!retryAfterClear) throw new OperationCanceledException();
            }
            // The image stays in Loading while the cache is being cleared.
            // Hiding or recycling it cancels this wait through its own token.
            if (!retryAfterClear) return default;
            await clearCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string CacheKey(Uri uri, int size) => uri.AbsoluteUri + (ArtworkUri.Size(size) > 256 ? "|decode1024" : "");

    // Order WPF publication with cache clearing even if the bitmap spent time
    // waiting for the dispatcher.
    internal bool TryPublish(ArtworkResult result, Action publish)
    {
        lock (_publication)
        {
            if (_disposed || result.CacheGeneration is { } generation && !ReferenceEquals(generation, _clearCompletion)) return false;
            publish();
            return true;
        }
    }

    // A failed visible binding can reuse a proven result from another consumer.
    // Clear advances the generation, letting the binding join a fresh request.
    internal async Task<ArtworkResult?> WaitForCachedArtworkAsync(string? source, int size, Task generation, CancellationToken cancellationToken)
    {
        if (!ArtworkUri.TryCreate(source, size, out var uri)) return null;
        var key = CacheKey(uri, size);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Recovery recovery;
            lock (_sync)
            {
                if (_disposed || !ReferenceEquals(generation, _clearCompletion)) return null;
                if (_memory.TryGetValue(key, out var node)) return new(node.Value.Bitmap, CacheGeneration: generation);
                if (!_recoveryWaiters.TryGetValue(key, out recovery!)) _recoveryWaiters.Add(key, recovery = new());
                recovery.Waiters++;
            }
            try { await recovery.Signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            finally
            {
                lock (_sync)
                    if (--recovery.Waiters == 0 && _recoveryWaiters.TryGetValue(key, out var current) && ReferenceEquals(current, recovery))
                        _recoveryWaiters.Remove(key);
            }
        }
    }

    private Pending GetPending(Uri uri, string key, int decodeSize)
    {
        if (_pending.TryGetValue(key, out var existing) && ReferenceEquals(existing.AdmittedAfterClear, _clearCompletion))
            return existing;
        var pending = new Pending(_clearCompletion);
        _pending[key] = pending;
        pending.Task = Task.Run(() => LoadAsync(uri, key, pending, decodeSize));
        return pending;
    }

    private void ReleaseWaiter(string key, Pending pending)
    {
        if (--pending.Waiters != 0 || pending.Finished) return;
        if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, pending)) _pending.Remove(key);
        CancelPending(pending);
    }

    // Only schedules callbacks. Cancellation is marked before releasing _sync
    // so the detached worker cannot publish over a new subscriber's result.
    private static void CancelPending(Pending pending)
    {
        if (!pending.Finished) pending.CancellationCallbacks ??= pending.Cancellation.CancelAsync();
    }

    private static async Task DisposePendingCancellationAsync(CancellationTokenSource source, Task? callbacks)
    {
        try { if (callbacks is not null) await callbacks.ConfigureAwait(false); }
        catch (AggregateException) { /* The canceled worker no longer serves any consumer. */ }
        finally { source.Dispose(); }
    }

    private async Task<ArtworkResult> LoadAsync(Uri uri, string key, Pending pending, int decodeSize)
    {
        var cancellationToken = pending.Cancellation.Token;
        void EnsureCurrent()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(pending.AdmittedAfterClear, _clearCompletion))
                throw new OperationCanceledException();
        }
        var acquired = false;
        try
        {
            await _workers.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            var path = Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".img");
            var (bitmap, encodedBytes) = await ReadDiskAsync(path, decodeSize, pending.AdmittedAfterClear, cancellationToken).ConfigureAwait(false);
            if (bitmap is null)
            {
                byte[] bytes;
                try { bytes = await DownloadAsync(uri, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (IsTemporaryDownloadFailure(error)) { return new(null, TemporaryFailure: true); }
                EnsureCurrent();
                bitmap = Decode(bytes, decodeSize);
                encodedBytes = bytes.Length;
                EnsureCurrent();
                await WriteDiskAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            }
            // BitmapImage may retain its original OnLoad stream. Budget both
            // encoded and decoded bytes before taking the cache state lock.
            var entry = new MemoryEntry(key, bitmap,
                encodedBytes + (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(4, (bitmap.Format.BitsPerPixel + 7) / 8));
            Recovery? recovery;
            lock (_sync)
            {
                EnsureCurrent();
                recovery = Remember(entry);
            }
            recovery?.Signal.SetResult();
            return new(bitmap);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or ArgumentException or FormatException)
        { return default; }
        finally
        {
            if (acquired) _workers.Release();
            Task? callbacks;
            lock (_sync)
            {
                if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, pending)) _pending.Remove(key);
                pending.Finished = true;
                callbacks = pending.CancellationCallbacks;
            }
            _ = DisposePendingCancellationAsync(pending.Cancellation, callbacks);
        }
    }

    private async Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_downloadTimeout);
        try
        {
            try { return await DownloadCoreAsync(uri, timeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (IsTemporaryDownloadFailure(error) && !timeout.IsCancellationRequested)
            {
                var delay = error is ArtworkHttpFailure { RetryAfter: { } retryAfter } && retryAfter > DownloadRetryDelay
                    ? retryAfter : DownloadRetryDelay;
                if (delay >= _downloadTimeout) throw;
                await Task.Delay(delay, timeout.Token).ConfigureAwait(false);
                return await DownloadCoreAsync(uri, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("Artwork download timed out.", error); }
    }

    private async Task<byte[]> DownloadCoreAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; ; redirects++)
        {
            if (!ArtworkUri.Valid(uri)) throw new InvalidDataException("Unsupported artwork address.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                if (redirects >= 3 || response.Headers.Location is null
                    || !Uri.TryCreate(uri, response.Headers.Location, out var next) || !ArtworkUri.Valid(next))
                    throw new InvalidDataException("Unsupported artwork redirect.");
                uri = next;
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new ArtworkHttpFailure(response);
            if (response.StatusCode == HttpStatusCode.PartialContent || response.Content.Headers.NonValidated.Contains("Content-Range"))
                throw new InvalidDataException("Artwork is incomplete.");
            var length = response.Content.Headers.ContentLength;
            if (length > MaximumResponseBytes) throw new InvalidDataException("Artwork is too large.");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported artwork format.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
            if (length is { } expected && bytes.Length != expected) throw new InvalidDataException("Artwork is incomplete.");
            return bytes;
        }
    }

    private static bool IsTemporaryDownloadFailure(Exception error) => error is IOException
        or HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout }
        or HttpRequestException { StatusCode: null, HttpRequestError: HttpRequestError.Unknown or HttpRequestError.ConnectionError
            or HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded };

    private sealed class ArtworkHttpFailure(HttpResponseMessage response)
        : HttpRequestException("Artwork unavailable.", null, response.StatusCode)
    {
        internal TimeSpan? RetryAfter { get; } = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
    }

    private async Task<(BitmapSource? Bitmap, int EncodedBytes)> ReadDiskAsync(string path, int decodeSize,
        Task generation, CancellationToken cancellationToken)
    {
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(generation, _clearCompletion)) throw new OperationCanceledException();
            _diskReads.Add(read.Task);
        }
        try
        {
            using var ancestors = UninstallPathGuard.HoldAncestors(path);
            if (!ancestors.Complete) return default;
            var recovery = new GuardedStoreRecovery(path, GuardedStoreKind.Artwork);
            using var handle = OpenDiskRead(path, recovery, out var canTouch);
            if (handle is null) return default;
            using var file = new FileStream(handle, FileAccess.Read, 16 * 1024, isAsync: false);
            // Verifying a pending replacement can advance the synchronous handle.
            if (file.Position != 0) file.Position = 0;
            if (file.Length is <= 0 or > MaximumResponseBytes) return default;
            if (DiskReadOpenedForChecks is { } opened) await opened().ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(file, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = Decode(bytes, decodeSize);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (canTouch && !recovery.Pending)
                {
                    using var writer = GuardedStoreFile.HoldWriteGate(path);
                    cancellationToken.ThrowIfCancellationRequested();
                    // Written is part of the authenticated replacement revision.
                    if (!recovery.Pending && FileSecurityPreservation.HasSingleLink(handle)) File.SetLastWriteTimeUtc(handle, DateTime.UtcNow);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { }
            return (bitmap, bytes.Length);
        }
        // A miss can be replaced by a valid download. Deleting by name here
        // could remove a newer generation's cache entry after cancellation.
        catch (Exception error) when (DecodeFailure(error) || error is UnauthorizedAccessException or Win32Exception)
        { return default; }
        finally
        {
            lock (_sync) _diskReads.Remove(read.Task);
            read.SetResult();
        }
    }

    private static SafeFileHandle? OpenDiskRead(string path, GuardedStoreRecovery recovery, out bool canTouch)
    {
        canTouch = false;
        try
        {
            var handle = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: false, writeAttributes: true);
            try
            {
                if (!recovery.Pending)
                {
                    canTouch = true;
                    return handle;
                }
            }
            catch { handle.Dispose(); throw; }
            handle.Dispose();
        }
        catch (Win32Exception) { }
        return GuardedStoreFile.OpenForRead(path, kind: GuardedStoreKind.Artwork);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var result = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return result.ToArray();
            if (result.Length + read > MaximumResponseBytes) throw new InvalidDataException("Artwork is too large.");
            result.Write(buffer, 0, read);
        }
    }

    internal static BitmapSource Decode(byte[] bytes, int maximumSize = 256)
    {
        if (bytes.Length is <= 0 or > MaximumResponseBytes) throw new InvalidDataException("Invalid artwork size.");
        using var stream = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("Only static artwork is supported.");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth is <= 0 or > MaximumSourceDimension || frame.PixelHeight is <= 0 or > MaximumSourceDimension
            || (long)frame.PixelWidth * frame.PixelHeight > MaximumSourcePixels) throw new InvalidDataException("Artwork dimensions are too large.");
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        var targetSize = maximumSize > 256 ? 1024 : 256;
        if (frame.PixelWidth >= frame.PixelHeight) bitmap.DecodePixelWidth = Math.Min(targetSize, frame.PixelWidth);
        else bitmap.DecodePixelHeight = Math.Min(targetSize, frame.PixelHeight);
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        if (bitmap.PixelWidth > targetSize || bitmap.PixelHeight > targetSize) throw new InvalidDataException("Artwork decode exceeds the target size.");
        bitmap.Freeze();
        return bitmap;
    }
    private static bool DecodeFailure(Exception exception) => exception is IOException or InvalidDataException or InvalidOperationException
        or NotSupportedException or ArgumentException or FormatException;

    private async Task WriteDiskAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = path[..^4] + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // The disk writer's named gate belongs to this thread. Complete
            // the guarded replacement before awaiting cache maintenance.
            using (var storage = GuardedStoreFile.BeginWrite(path, kind: GuardedStoreKind.Artwork))
            using (var handle = storage.CreateStage(temporary))
            {
                var published = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RandomAccess.Write(handle, bytes, 0);
                    RandomAccess.FlushToDisk(handle);
                    cancellationToken.ThrowIfCancellationRequested();
                    storage.Commit(handle);
                    published = true;
                }
                finally { if (!published) UninstallPathGuard.DiscardStagedFile(handle); }
            }
            using var ancestors = UninstallPathGuard.HoldAncestors(path);
            if (ancestors.Complete) PruneDisk(cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { }
        finally { _diskGate.Release(); }
    }

    // This is an explicit disposable-cache operation. Only top-level entries
    // with our exact hash(URL) name and a valid bounded image are removed.
    // No traversal, audio files, settings or account data participate.
    internal Task<CacheCleanupResult> ClearAsync(CancellationToken cancellationToken)
        => Task.Run(() => ClearCoreAsync(cancellationToken), cancellationToken);

    private async Task<CacheCleanupResult> ClearCoreAsync(CancellationToken cancellationToken)
    {
        Pending[] requests;
        Task[] reads;
        Recovery[] recoveries;
        var clearing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_publication)
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_clearCompletion.IsCompleted) throw new InvalidOperationException("Очистка кэша уже выполняется.");
            _clearCompletion = clearing.Task;
            requests = _pending.Values.ToArray();
            reads = _diskReads.ToArray();
            _memory.Clear(); _lru.Clear(); _memoryBytes = 0;
            recoveries = TakeRecoveryWaiters();
            foreach (var request in requests) CancelPending(request);
        }
        var acquired = false;
        try
        {
            foreach (var recovery in recoveries) recovery.Signal.SetResult();
            // A canceled last waiter leaves _pending before its read handle
            // closes. Drain those reads as well before enumerating the cache.
            try { await Task.WhenAll(reads.Concat(requests.Select(p => (Task)p.Task))).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false); acquired = true;
            _ = UninstallPathGuard.Canonical(_directory);
            using var ancestors = UninstallPathGuard.HoldAncestors(Path.Combine(_directory, ".cache-clear"));
            if (!ancestors.Complete) return new(0, 0, 0, 0);
            var removed = 0; var preserved = 0; var failed = 0; long bytes = 0;
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in Directory.EnumerateFileSystemEntries(_directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GuardedStoreRecovery.ArtworkImageName(Path.GetFileName(entry)) is { } name) candidates.Add(name);
                else preserved++;
            }
            foreach (var name in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(_directory, name);
                try
                {
                    using var writer = GuardedStoreFile.HoldWriteGate(path);
                    cancellationToken.ThrowIfCancellationRequested();
                    using var handle = GuardedStoreFile.OpenCacheForCleanupUnderWriteGate(path, GuardedStoreKind.Artwork, out var recovery);
                    if (recovery.ArchivedPrevious) preserved++;
                    if (recovery.PreserveCurrent) { preserved++; continue; }
                    if (handle is null) continue;
                    var attributes = File.GetAttributes(handle);
                    if ((attributes & FileAttributes.ReadOnly) != 0) { failed++; continue; }
                    var removedBytes = DeleteVerifiedImage(handle, cancellationToken);
                    if (removedBytes == 0) { preserved++; continue; }
                    removed++; bytes += removedBytes;
                }
                catch (UninstallPathGuard.UnsafeStoragePathException) { preserved++; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { failed++; }
            }
            return new(removed, preserved, failed, bytes);
        }
        finally
        {
            if (acquired) _diskGate.Release();
            clearing.SetResult();
        }
    }
    private void PruneDisk(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var files = new DirectoryInfo(_directory).EnumerateFiles("*.img")
                .Where(f => SafeCacheName(f.Name, ".img")
                    && (f.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) == 0
                    && f.Length is > 0 and <= MaximumResponseBytes)
                .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long total = 0;
            var retained = 0;
            while (retained < files.Length && retained < DiskEntryLimit)
            {
                total += files[retained].Length;
                if (total > DiskByteLimit) break;
                retained++;
            }
            var overflow = files.Length - retained;
            var attempts = Math.Min(overflow, PruneAttemptsPerWrite);
            var start = overflow == 0 ? 0 : _pruneOffset % overflow;
            // Start at the oldest overflow entries, rotating past preserved
            // files. A cache write validates only a few bounded images.
            _pruneOffset = overflow == 0 ? 0 : (start + attempts) % overflow;
            for (var index = 0; index < attempts; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[files.Length - 1 - (start + index) % overflow];
                try
                {
                    using var writer = GuardedStoreFile.HoldWriteGate(file.FullName);
                    cancellationToken.ThrowIfCancellationRequested();
                    // A later read may restore the parked image if the primary
                    // disappears. Leave both versions for normal recovery.
                    var recovery = new GuardedStoreRecovery(file.FullName, GuardedStoreKind.Artwork);
                    if (!recovery.Pending)
                    {
                        using var handle = UninstallPathGuard.OpenOwnedFile(file.FullName, read: true);
                        DeleteVerifiedImage(handle, cancellationToken);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { }
            }
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.tmp"))
                if (file.Name.Length == 101 && file.Name[64] == '.' && SafeCacheName(file.Name[..64] + ".img", ".img")
                    && Guid.TryParseExact(file.Name.Substring(65, 32), "N", out _) && file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                    DeleteCacheEntry(file.FullName);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static int DeleteVerifiedImage(SafeFileHandle handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GuardedStoreFile.ReadFromHandle(handle, MaximumResponseBytes) is not { } image) return 0;
        cancellationToken.ThrowIfCancellationRequested();
        try { _ = Decode(image); }
        catch (Exception error) when (DecodeFailure(error)) { return 0; }
        cancellationToken.ThrowIfCancellationRequested();
        if (FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(handle)) return 0;
        cancellationToken.ThrowIfCancellationRequested();
        UninstallPathGuard.DeleteByHandle(handle);
        return image.Length;
    }

    private static bool SafeCacheName(string name, string extension) => name.Length == 64 + extension.Length
        && name.EndsWith(extension, StringComparison.Ordinal) && name.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdef") < 0;
    private static void DeleteCacheEntry(string path)
    {
        try
        {
            using var handle = UninstallPathGuard.OpenOwnedFile(path, read: true);
            if (!FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(handle)) UninstallPathGuard.DeleteByHandle(handle);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { }
    }
    private Recovery? Remember(MemoryEntry entry)
    {
        var key = entry.Key;
        if (_memory.TryGetValue(key, out var existing)) { _lru.Remove(existing); _memoryBytes -= existing.Value.Bytes; }
        _memory[key] = _lru.AddFirst(entry);
        _memoryBytes += entry.Bytes;
        while (_memory.Count > MemoryEntryLimit || _memoryBytes > MemoryByteLimit)
        {
            var last = _lru.Last!;
            _memoryBytes -= last.Value.Bytes; _memory.Remove(last.Value.Key); _lru.RemoveLast();
        }
        return _recoveryWaiters.Remove(key, out var recovery) ? recovery : null;
    }
    public void Dispose()
    {
        Recovery[] recoveries;
        lock (_publication)
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            recoveries = TakeRecoveryWaiters();
            foreach (var pending in _pending.Values) CancelPending(pending);
        }
        foreach (var recovery in recoveries) recovery.Signal.SetResult();
        _http.Dispose();
    }

    private Recovery[] TakeRecoveryWaiters()
    {
        var recoveries = _recoveryWaiters.Values.ToArray();
        _recoveryWaiters.Clear();
        return recoveries;
    }

    private sealed record MemoryEntry(string Key, BitmapSource Bitmap, long Bytes);
    internal readonly record struct ArtworkResult(BitmapSource? Bitmap, bool TemporaryFailure = false, Task? CacheGeneration = null);
    private sealed class Recovery
    {
        internal readonly TaskCompletionSource Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Waiters;
    }
    private sealed class Pending(Task admittedAfterClear)
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly Task AdmittedAfterClear = admittedAfterClear;
        internal Task<ArtworkResult> Task = null!;
        internal Task? CancellationCallbacks;
        internal bool Finished;
        internal int Waiters;
    }
}
