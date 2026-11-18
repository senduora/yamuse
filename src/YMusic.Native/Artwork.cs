using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.ComponentModel;

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
        try
        {
            var bitmap = await Cache.GetAsync(source, size, cancellation.Token, retryAfterClear: true);
            if (!cancellation.IsCancellationRequested && state.Generation == generation
                && image.IsLoaded && image.IsVisible && GetSourceUri(image) == source && GetPixelSize(image) == size)
            {
                image.Source = bitmap;
                image.SetValue(LoadStatusPropertyKey, bitmap is null ? ArtworkLoadStatus.Unavailable : ArtworkLoadStatus.Ready);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException or FormatException)
        {
            // A failed current request is distinct from the temporary empty
            // source during a switch. Old/cancelled requests never publish state.
            if (!cancellation.IsCancellationRequested && state.Generation == generation
                && image.IsLoaded && image.IsVisible && GetSourceUri(image) == source && GetPixelSize(image) == size)
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
    private const int DiskEntryLimit = 2048;
    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly TimeSpan _downloadTimeout;
    private readonly SemaphoreSlim _workers = new(4, 4);
    private readonly SemaphoreSlim _diskGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<MemoryEntry>> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<MemoryEntry> _lru = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private long _memoryBytes;
    private volatile Task _clearCompletion = Task.CompletedTask;
    private bool _disposed;

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
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ArtworkUri.TryCreate(source, size, out var uri)) return null;
        // Small views still share one bounded decode. The expanded player keeps
        // a distinct large bitmap even when metadata specifies a fixed CDN URL.
        var decodeSize = ArtworkUri.Size(size) > 256 ? 1024 : 256;
        var key = uri.AbsoluteUri + (decodeSize > 256 ? "|decode1024" : "");
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
                        return node.Value.Bitmap;
                    }
                    pending = GetPending(uri, key, decodeSize);
                    pending.Waiters++;
                }
            }
            if (pending is not null)
            {
                BitmapSource? bitmap;
                try { bitmap = await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (retryAfterClear && !cancellationToken.IsCancellationRequested
                    && !ReferenceEquals(pending.AdmittedAfterClear, _clearCompletion))
                { bitmap = null; }
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
                if (ReferenceEquals(pending.AdmittedAfterClear, clearCompletion)) return bitmap;
                if (!retryAfterClear) throw new OperationCanceledException();
            }
            // The image stays in Loading while the cache is being cleared.
            // Hiding or recycling it cancels this wait through its own token.
            if (!retryAfterClear) return null;
            await clearCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private Pending GetPending(Uri uri, string key, int decodeSize)
    {
        if (_pending.TryGetValue(key, out var existing) && ReferenceEquals(existing.AdmittedAfterClear, _clearCompletion))
            return existing;
        var pending = new Pending(_clearCompletion);
        _pending[key] = pending;
        pending.Task = Task.Run(() => LoadAsync(uri, key, pending, decodeSize));
        _ = pending.Task.ContinueWith(_ =>
        {
            lock (_sync) if (pending.Waiters == 0) pending.Cancellation.Dispose();
        }, TaskScheduler.Default);
        return pending;
    }

    private void ReleaseWaiter(string key, Pending pending)
    {
        if (--pending.Waiters != 0) return;
        if (pending.Task.IsCompleted)
        {
            pending.Cancellation.Dispose();
            return;
        }
        pending.Cancellation.Cancel();
        if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, pending)) _pending.Remove(key);
    }

    private async Task<BitmapSource?> LoadAsync(Uri uri, string key, Pending pending, int decodeSize)
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
            var (bitmap, encodedBytes) = await ReadDiskAsync(path, decodeSize, cancellationToken).ConfigureAwait(false);
            if (bitmap is null)
            {
                var bytes = await DownloadAsync(uri, cancellationToken).ConfigureAwait(false);
                EnsureCurrent();
                bitmap = Decode(bytes, decodeSize);
                encodedBytes = bytes.Length;
                EnsureCurrent();
                await WriteDiskAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            }
            lock (_sync)
            {
                EnsureCurrent();
                Remember(key, bitmap, encodedBytes);
            }
            return bitmap;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or ArgumentException or FormatException)
        { return null; }
        finally
        {
            if (acquired) _workers.Release();
            lock (_sync)
                if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, pending)) _pending.Remove(key);
        }
    }

    private async Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_downloadTimeout);
        try
        {
            return await DownloadCoreAsync(uri, timeout.Token).ConfigureAwait(false);
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
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Artwork unavailable.");
            if (response.StatusCode == HttpStatusCode.PartialContent || response.Content.Headers.NonValidated.Contains("Content-Range"))
                throw new InvalidDataException("Artwork is incomplete.");
            var length = response.Content.Headers.ContentLength;
            if (length > MaximumResponseBytes) throw new InvalidDataException("Artwork is too large.");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && mediaType != "application/octet-stream") throw new InvalidDataException("Unsupported artwork format.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
            if (length is { } expected && bytes.Length != expected) throw new InvalidDataException("Artwork is incomplete.");
            return bytes;
        }
    }

    private async Task<(BitmapSource? Bitmap, int EncodedBytes)> ReadDiskAsync(string path, int decodeSize,
        CancellationToken cancellationToken)
    {
        try
        {
            using var ancestors = UninstallPathGuard.HoldAncestors(path);
            if (!ancestors.Complete || !UninstallPathGuard.EntryExists(path)) return default;
            using var handle = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: false);
            using var file = new FileStream(handle, FileAccess.Read, 16 * 1024, isAsync: false);
            if (file.Length is <= 0 or > MaximumResponseBytes) return default;
            var bytes = await ReadBoundedAsync(file, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = Decode(bytes, decodeSize);
            cancellationToken.ThrowIfCancellationRequested();
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return (bitmap, bytes.Length);
        }
        // A miss can be replaced by a valid download. Deleting by name here
        // could remove a newer generation's cache entry after cancellation.
        catch (Exception error) when (DecodeFailure(error) || error is UnauthorizedAccessException or Win32Exception)
        { return default; }
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
            using var ancestors = UninstallPathGuard.HoldAncestors(path, create: true);
            var replace = UninstallPathGuard.EntryExists(path);
            if (replace)
            {
                using var existing = UninstallPathGuard.OpenOwnedFile(path, read: false, deleteAccess: false);
            }
            using (var handle = UninstallPathGuard.CreateStagedFile(temporary, asynchronous: true))
            {
                var published = false;
                try
                {
                    await RandomAccess.WriteAsync(handle, bytes, 0, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    ManagedCacheCleanup.RenameByHandle(handle, path, replace);
                    published = true;
                }
                finally { if (!published) UninstallPathGuard.DiscardStagedFile(handle); }
            }
            PruneDisk();
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
        var clearing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_clearCompletion.IsCompleted) throw new InvalidOperationException("Очистка кэша уже выполняется.");
            _clearCompletion = clearing.Task;
            requests = _pending.Values.ToArray();
            _memory.Clear(); _lru.Clear(); _memoryBytes = 0;
        }
        var acquired = false;
        try
        {
            // Cancellation can complete an HTTP task inline and re-enter the
            // pending map. Iterate a snapshot outside the lock and always leave
            // the clearing state, including a concurrently completed request.
            foreach (var request in requests)
            {
                try { request.Cancellation.Cancel(); }
                catch (ObjectDisposedException) when (request.Task.IsCompleted) { }
            }
            // Canceled loads must leave the disk/memory publish phase before
            // enumeration; GetAsync cannot admit a new load during this clear.
            try { await Task.WhenAll(requests.Select(p => p.Task)).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false); acquired = true;
            _ = UninstallPathGuard.Canonical(_directory);
            using var ancestors = UninstallPathGuard.HoldAncestors(Path.Combine(_directory, ".cache-clear"));
            if (!ancestors.Complete) return new(0, 0, 0, 0);
            var removed = 0; var preserved = 0; var failed = 0; long bytes = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(_directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SafeCacheName(Path.GetFileName(path), ".img")) { preserved++; continue; }
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) { preserved++; continue; }
                    if ((attributes & FileAttributes.ReadOnly) != 0) { failed++; continue; }
                    using var handle = UninstallPathGuard.OpenOwnedFile(path, read: true);
                    using var file = new FileStream(handle, FileAccess.Read, 16 * 1024, isAsync: false);
                    if (file.Length is <= 0 or > MaximumResponseBytes) { preserved++; continue; }
                    var image = await ReadBoundedAsync(file, cancellationToken).ConfigureAwait(false);
                    try { _ = Decode(image); }
                    catch (Exception error) when (DecodeFailure(error)) { preserved++; continue; }
                    cancellationToken.ThrowIfCancellationRequested();
                    UninstallPathGuard.DeleteByHandle(file.SafeFileHandle);
                    removed++; bytes += image.Length;
                }
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
    private void PruneDisk()
    {
        try
        {
            var files = new DirectoryInfo(_directory).EnumerateFiles("*.img")
                .Where(f => SafeCacheName(f.Name, ".img") && (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long total = 0;
            var count = 0;
            foreach (var file in files)
            {
                total += file.Length;
                if (++count > DiskEntryLimit || total > DiskByteLimit) DeleteCacheEntry(file.FullName);
            }
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.tmp"))
                if (file.Name.Length == 101 && file.Name[64] == '.' && SafeCacheName(file.Name[..64] + ".img", ".img")
                    && Guid.TryParseExact(file.Name.Substring(65, 32), "N", out _) && file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                    DeleteCacheEntry(file.FullName);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static bool SafeCacheName(string name, string extension) => name.Length == 64 + extension.Length
        && name.EndsWith(extension, StringComparison.Ordinal) && name.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdef") < 0;
    private static void DeleteCacheEntry(string path)
    {
        try
        {
            using var handle = UninstallPathGuard.OpenOwnedFile(path, read: false);
            UninstallPathGuard.DeleteByHandle(handle);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { }
    }
    private void Remember(string key, BitmapSource bitmap, int encodedBytes)
    {
        if (_memory.TryGetValue(key, out var existing)) { _lru.Remove(existing); _memoryBytes -= existing.Value.Bytes; }
        // BitmapImage may retain its original OnLoad stream; budget both encoded and decoded data.
        var entry = new MemoryEntry(key, bitmap, encodedBytes + (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(4, (bitmap.Format.BitsPerPixel + 7) / 8));
        _memory[key] = _lru.AddFirst(entry);
        _memoryBytes += entry.Bytes;
        while (_memory.Count > MemoryEntryLimit || _memoryBytes > MemoryByteLimit)
        {
            var last = _lru.Last!;
            _memoryBytes -= last.Value.Bytes; _memory.Remove(last.Value.Key); _lru.RemoveLast();
        }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            // Inline cancellation can complete a load and remove it from the map.
            foreach (var pending in _pending.Values.ToArray()) pending.Cancellation.Cancel();
        }
        _http.Dispose();
    }
    private sealed record MemoryEntry(string Key, BitmapSource Bitmap, long Bytes);
    private sealed class Pending(Task admittedAfterClear)
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly Task AdmittedAfterClear = admittedAfterClear;
        internal Task<BitmapSource?> Task = null!;
        internal int Waiters;
    }
}
