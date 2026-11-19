using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace YMusic.Native;

internal enum AppUpdateStatus
{
    NotConfigured, Portable, Ready, Checking, NoUpdate, Available, Downloading, Downloaded, Error
}

internal sealed record AppUpdateState(AppUpdateStatus Status, bool FeedConfigured, bool IsInstalled,
    string CurrentVersion, string? AvailableVersion, int? ProgressPercent, string Message)
{
    internal bool CheckSucceeded { get; init; }
}

internal sealed record UpdateRateLimitCheckpoint(DateTimeOffset RetryNotBeforeUtc, int SecondaryLimitCount = 0,
    TimeSpan? SecondaryLimitDelay = null)
{
    internal const int MaximumSecondaryLimitCount = 7;
}

// The window owns scheduling and all user interactions. This service has no
// timer, automatic download/application, or account storage access.
internal sealed class UpdateSupport
{
    internal static readonly Uri OfficialRepository = new("https://github.com/senduora/yamuse");
    internal static readonly Uri OfficialLatestRelease = new("https://api.github.com/repos/senduora/yamuse/releases/latest");
    private static readonly Lazy<UpdateSupport> Instance = new(() => new(
        Path.Combine(AppContext.BaseDirectory, "update-feed.json"),
        recoveryBackend: Program.RecoverInstalledUpdateLocator ? uri => new VelopackUpdateBackend(uri, Program.CreateRecoveredUpdateLocator()) : null));
    internal static UpdateSupport Current => Instance.Value;

    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly Func<Uri, IAppUpdateBackend> _backendFactory;
    private readonly Func<bool>? _installedAdmission;
    private readonly Uri? _repository;
    private readonly Func<CancellationToken, Task<string?>> _portableCheck;
    private readonly TimeProvider _clock;
    private sealed record RateLimit(UpdateRateLimitCheckpoint Checkpoint, long Started, TimeSpan Delay);
    private RateLimit? _rateLimit;
    private IAppUpdateBackend? _backend;
    private volatile AppUpdateState _state;
    internal AppUpdateState State => _state;
    internal bool HasPortableCheckOverride { get; }
    internal UpdateRateLimitCheckpoint? RateLimitCheckpoint => Volatile.Read(ref _rateLimit)?.Checkpoint;
    internal TimeSpan RemainingRateLimit => Volatile.Read(ref _rateLimit) is { } limit
        ? limit.Delay - _clock.GetElapsedTime(limit.Started) : TimeSpan.Zero;

    internal void RestoreRateLimit(UpdateRateLimitCheckpoint? checkpoint)
    {
        if (checkpoint is null) return;
        while (true)
        {
            var current = Volatile.Read(ref _rateLimit);
            if (current is not null && (current.Checkpoint == checkpoint
                || current.Checkpoint.RetryNotBeforeUtc > checkpoint.RetryNotBeforeUtc)) return;
            if (current is not null && current.Checkpoint.RetryNotBeforeUtc == checkpoint.RetryNotBeforeUtc)
            {
                if (ReferenceEquals(Interlocked.CompareExchange(ref _rateLimit, current with { Checkpoint = checkpoint }, current), current)) return;
                continue;
            }
            var started = _clock.GetTimestamp();
            var delay = checkpoint.RetryNotBeforeUtc - _clock.GetUtcNow();
            if (current is not null)
            {
                // Adopting a later saved deadline must also preserve the current monotonic wait after a clock change.
                var remaining = current.Delay - _clock.GetElapsedTime(current.Started, started);
                if (remaining > delay) delay = remaining;
            }
            if (ReferenceEquals(Interlocked.CompareExchange(ref _rateLimit, new(checkpoint, started, delay), current), current)) return;
        }
    }

    internal void ObserveRateLimitAtAdmission(UpdateRateLimitCheckpoint? checkpoint)
    {
        while (true)
        {
            var current = Volatile.Read(ref _rateLimit);
            if (current?.Checkpoint == checkpoint) return;
            var started = _clock.GetTimestamp();
            if (current is not null && current.Delay > _clock.GetElapsedTime(current.Started, started))
            {
                RestoreRateLimit(checkpoint);
                return;
            }
            var replacement = checkpoint is null ? null : new RateLimit(checkpoint, started, checkpoint.RetryNotBeforeUtc - _clock.GetUtcNow());
            if (ReferenceEquals(Interlocked.CompareExchange(ref _rateLimit, replacement, current), current)) return;
        }
    }

    // Injection is limited to deterministic fixtures; production uses only the public Yamuse release.
    internal UpdateSupport(string? configurationPath, bool? installed = null,
        Func<Uri, IAppUpdateBackend>? backendFactory = null,
        Func<CancellationToken, Task<string?>>? portableCheck = null,
        Func<Uri, IAppUpdateBackend>? recoveryBackend = null, TimeProvider? clock = null, Func<bool>? installedAdmission = null)
    {
        _backendFactory = backendFactory ?? recoveryBackend ?? (uri => new VelopackUpdateBackend(uri));
        _installedAdmission = installed is null && recoveryBackend is null ? installedAdmission ?? DetectInstalled : null;
        var isInstalled = installed ?? (recoveryBackend is not null || _installedAdmission!());
        var currentVersion = AppBranding.CurrentVersion;
        var (repository, invalid) = ReadRepository(configurationPath);
        _repository = repository;
        _clock = clock ?? TimeProvider.System;
        _portableCheck = portableCheck ?? (token => CheckPortableReleaseAsync(currentVersion, token));
        HasPortableCheckOverride = portableCheck is not null;
        _state = new(invalid ? AppUpdateStatus.Error : repository is null ? AppUpdateStatus.NotConfigured
            : isInstalled ? AppUpdateStatus.Ready : AppUpdateStatus.Portable,
            repository is not null, isInstalled, currentVersion, null, null,
            invalid ? "Файл настройки обновлений некорректен."
            : repository is null ? "Источник обновлений пока не настроен."
            : !isInstalled ? "Переносимая версия: можно проверить обновления и открыть официальный выпуск."
            : "Обновления проверяются при запуске. Можно проверить их вручную.");
    }

    internal Task<AppUpdateState> CheckAsync(CancellationToken cancellationToken = default, Func<Task<bool>>? onCheckStarting = null)
        => _repository is not null ? CheckCoreAsync(cancellationToken, onCheckStarting) : Task.FromResult(State);

    internal Task<AppUpdateState> DownloadAsync(IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
        => CanUseUpdates ? DownloadCoreAsync(progress, cancellationToken) : Task.FromResult(State);

    private bool CanUseUpdates => _repository is not null && State.IsInstalled;

    private async Task<AppUpdateState> CheckCoreAsync(CancellationToken cancellationToken, Func<Task<bool>>? onCheckStarting)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        var before = State;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RemainingRateLimit > TimeSpan.Zero) return SetRateLimited(before);
            var admitted = onCheckStarting is null || await onCheckStarting().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (RemainingRateLimit > TimeSpan.Zero) return SetRateLimited(before);
            if (!admitted) return before with { CheckSucceeded = false };
            var installation = _backend is null ? _installedAdmission?.Invoke() : null;
            if (installation is bool isInstalled && isInstalled != before.IsInstalled)
            {
                before = _state = before with
                {
                    IsInstalled = isInstalled, Status = isInstalled ? AppUpdateStatus.Ready : AppUpdateStatus.Portable,
                    AvailableVersion = null, ProgressPercent = null, CheckSucceeded = false
                };
            }
            if (!State.IsInstalled)
            {
                // Portable previews never create the installer backend. The
                // only metadata authority is this project's public endpoint.
                Set(AppUpdateStatus.Checking, "Проверяем обновления…");
                var release = await _portableCheck(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref _rateLimit, null);
                return release is null ? Set(AppUpdateStatus.NoUpdate, "Используется последняя доступная версия.", checkSucceeded: true)
                    : Set(AppUpdateStatus.Available, "Доступна новая версия. Откройте официальный выпуск для скачивания.", release, checkSucceeded: true);
            }
            _backend ??= _backendFactory(_repository!);
            Set(AppUpdateStatus.Checking, "Проверяем обновления…");
            // The backend commits the selected candidate under the same operation lock.
            var version = await _backend.CheckAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _rateLimit, null);
            if (version is not null && _backend.PendingVersion == version)
                return Set(AppUpdateStatus.Downloaded, "Обновление загружено. Установите его, когда закончите слушать.", version, 100, checkSucceeded: true);
            return version is null
                ? Set(AppUpdateStatus.NoUpdate, "Установлена последняя доступная версия.", checkSucceeded: true)
                : Set(AppUpdateStatus.Available, "Доступна новая версия. Можно скачать её без остановки музыки.", version, checkSucceeded: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _state = before with { Message = "Проверка обновлений отменена.", CheckSucceeded = false };
            return State;
        }
        catch (LatestReleaseRateLimitException limit)
        {
            var started = _clock.GetTimestamp();
            var now = _clock.GetUtcNow();
            var delay = limit.GetRetryDelay(now);
            var prior = RateLimitCheckpoint;
            var count = limit.IsSecondary ? Math.Min((prior?.SecondaryLimitCount ?? 0) + 1,
                UpdateRateLimitCheckpoint.MaximumSecondaryLimitCount) : 0;
            if (count > 1)
            {
                var minimum = TimeSpan.FromMinutes(1 << (count - 1));
                if (prior?.SecondaryLimitDelay is { } previous)
                {
                    var maximumDefault = TimeSpan.FromMinutes(1 << (UpdateRateLimitCheckpoint.MaximumSecondaryLimitCount - 1));
                    var doubled = previous >= maximumDefault ? previous
                        : TimeSpan.FromTicks(Math.Min(previous.Ticks * 2, maximumDefault.Ticks));
                    if (doubled > minimum) minimum = doubled;
                }
                if (minimum > delay) delay = minimum;
            }
            var until = delay <= DateTimeOffset.MaxValue - now ? now + delay : DateTimeOffset.MaxValue;
            Volatile.Write(ref _rateLimit, new(new(until, count, count > 0 ? delay : null), started, delay));
            return SetRateLimited(before);
        }
        catch (Exception error) when (before.Status == AppUpdateStatus.Downloaded && IsTemporaryCheckFailure(error)
            && _backend?.PendingVersion == before.AvailableVersion)
        {
            _state = before with { Message = "Не удалось проверить обновления. Ранее загруженная версия доступна.", CheckSucceeded = false };
            return State;
        }
        catch (Exception)
        {
            return Set(AppUpdateStatus.Error, "Не удалось проверить обновления. Повторите позже.");
        }
        finally { _operation.Release(); }
    }

    private AppUpdateState SetRateLimited(AppUpdateState before)
    {
        const string message = "GitHub временно ограничил проверку обновлений.";
        if (before.Status == AppUpdateStatus.Downloaded && _backend?.PendingVersion == before.AvailableVersion)
            return _state = before with { Message = message + " Ранее загруженная версия доступна.", CheckSucceeded = false };
        return Set(AppUpdateStatus.Error, message + " Повторите позже.");
    }

    private static bool IsTemporaryCheckFailure(Exception error)
        => error is OperationCanceledException or IncompleteReleaseFeedException
            || error is HttpRequestException request && (request.StatusCode is null or HttpStatusCode.TooManyRequests
                or HttpStatusCode.RequestTimeout || (int)request.StatusCode.GetValueOrDefault() >= 500);

    private async Task<AppUpdateState> DownloadCoreAsync(IProgress<int>? progress, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (State.Status == AppUpdateStatus.Downloaded) return State;
            if (_backend is null || State.AvailableVersion is null)
                return Set(AppUpdateStatus.Error, "Сначала проверьте наличие обновления.");
            var version = State.AvailableVersion;
            Set(AppUpdateStatus.Downloading, "Скачиваем обновление. Музыка продолжает играть.", version, 0);
            var progressGate = new object();
            var acceptingProgress = true;
            try
            {
                await _backend.DownloadAsync(percent =>
                {
                    lock (progressGate)
                    {
                        if (!acceptingProgress || cancellationToken.IsCancellationRequested) return;
                        var value = Math.Clamp(percent, 0, 100);
                        Set(AppUpdateStatus.Downloading, "Скачиваем обновление. Музыка продолжает играть.", version, value);
                        progress?.Report(value);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            finally { lock (progressGate) acceptingProgress = false; }
            // A successful backend return means the verified file already committed.
            return Set(AppUpdateStatus.Downloaded, "Обновление загружено. Установите его, когда закончите слушать.", version, 100);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Set(State.AvailableVersion is null ? AppUpdateStatus.Ready : AppUpdateStatus.Available,
                "Скачивание обновления отменено.", State.AvailableVersion);
        }
        catch (Exception)
        {
            return Set(AppUpdateStatus.Error, "Не удалось скачать обновление. Проверьте соединение и повторите.", State.AvailableVersion);
        }
        finally { _operation.Release(); }
    }

    // Only a user action may call this. The caller must finish saving playback
    // state first: the official SDK exits the process and restarts the new build.
    internal void ApplyAndRestart()
    {
        if (!_operation.Wait(0)) throw new InvalidOperationException("Дождитесь завершения проверки или скачивания.");
        try
        {
            var before = State;
            if (!CanUseUpdates || before.Status != AppUpdateStatus.Downloaded || _backend is null)
                throw new InvalidOperationException("Обновление ещё не загружено.");
            try { _backend.ApplyAndRestart(); }
            catch (Exception)
            {
                const string message = "Не удалось применить обновление. Повторите позже.";
                // The backend restores PendingVersion only after verifying the package again on a held file.
                if (before.AvailableVersion is { } version && _backend.PendingVersion == version)
                    _state = before with { Message = message, CheckSucceeded = false };
                else Set(AppUpdateStatus.Error, message, before.AvailableVersion);
                throw new InvalidOperationException(message);
            }
        }
        finally { _operation.Release(); }
    }

    private AppUpdateState Set(AppUpdateStatus status, string message, string? version = null, int? percent = null, bool checkSucceeded = false)
        => _state = State with { Status = status, Message = message, AvailableVersion = version, ProgressPercent = percent, CheckSucceeded = checkSucceeded };

    private static bool DetectInstalled()
        => VelopackLocator.IsCurrentSet && Program.CanUseInstalledUpdateLocator(VelopackLocator.Current);

    private static (Uri? Repository, bool Invalid) ReadRepository(string? path)
    {
        try
        {
            if (path is null) return (null, false);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > 16 * 1024) return (null, true);
            var configuration = JsonSerializer.Deserialize<UpdateFeedConfiguration>(file, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            });
            var text = configuration?.GithubRepository;
            if (string.IsNullOrWhiteSpace(text)) return (null, false);
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || !uri.AbsoluteUri.TrimEnd('/').Equals(OfficialRepository.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                return (null, true);
            return (OfficialRepository, false);
        }
        catch (FileNotFoundException) { return (null, false); }
        catch (DirectoryNotFoundException) { return (null, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, true);
        }
    }

    private sealed record UpdateFeedConfiguration(string? GithubRepository);

    // This metadata-only path has no credentials, assets, arbitrary URLs or
    // redirects. Keep the response bounded even without Content-Length.
    internal static async Task<string?> CheckPortableReleaseAsync(string currentVersion, CancellationToken token,
        HttpMessageHandler? handler = null)
    {
        using var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, OfficialLatestRelease);
        request.Headers.UserAgent.ParseAdd("Yamuse-Update-Check/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        await CheckLatestRateLimitAsync(response, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 65536) throw new InvalidDataException("Release metadata exceeds its bound.");
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > 65536) throw new InvalidDataException("Release metadata exceeds its bound.");
            buffer.Write(bytes, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        var (_, version) = ParseStableRelease(document.RootElement);
        if (!SemanticVersion.TryParse(currentVersion, out var current)) throw new InvalidDataException("Release version is invalid.");
        return version > current ? version.ToString() : null;
    }

    internal static (string Tag, SemanticVersion Version) ParseStableRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
            || !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Release metadata is invalid.");
        var text = tag.GetString();
        if (text is null || text.Length is 0 or > 64 || !SemanticVersion.TryParse(text.TrimStart('v', 'V'), out var version)
            || version.IsPrerelease)
            throw new InvalidDataException("Release version is invalid.");
        return (text, version);
    }

    internal static async Task CheckLatestRateLimitAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) return;
        var primary = response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
            && remaining.Any(value => value.Trim() == "0");
        var retryAfter = response.Headers.RetryAfter;
        if (!primary && retryAfter is null && response.StatusCode == HttpStatusCode.Forbidden
            && !await HasSecondaryRateLimitMessageAsync(response, token).ConfigureAwait(false)) return;

        DateTimeOffset? reset = null;
        if (primary && response.Headers.TryGetValues("x-ratelimit-reset", out var timestamps))
        {
            var lastUnixSecond = DateTimeOffset.MaxValue.ToUnixTimeSeconds();
            foreach (var timestamp in timestamps)
                if (long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                    && seconds >= 0 && seconds <= lastUnixSecond)
                {
                    var date = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    if (!reset.HasValue || date > reset.Value) reset = date;
                }
        }
        throw new LatestReleaseRateLimitException(retryAfter, reset, secondary: !primary);
    }

    private static async Task<bool> HasSecondaryRateLimitMessageAsync(HttpResponseMessage response, CancellationToken token)
    {
        // Secondary limits can have a positive primary quota and no Retry-After.
        try
        {
            var body = await ReadMetadataBodyAsync(response, 16 * 1024, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                && message.GetString()!.StartsWith("You have exceeded a secondary rate limit", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException) { return false; }
    }

    internal static async Task<byte[]> ReadMetadataBodyAsync(HttpResponseMessage response, int maximumBytes, CancellationToken token)
    {
        var length = response.Content.Headers.ContentLength;
        if (length > maximumBytes) throw new InvalidDataException("Метаданные обновления слишком велики.");
        Stream body;
        try { body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false); }
        catch (Exception error) when (IsMetadataBodyInterruption(error)) { throw MetadataBodyInterrupted(error, token); }
        await using (body)
        {
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                int read;
                try { read = await body.ReadAsync(buffer, token).ConfigureAwait(false); }
                catch (Exception error) when (IsMetadataBodyInterruption(error)) { throw MetadataBodyInterrupted(error, token); }
                if (read == 0) break;
                if (read > maximumBytes - data.Length) throw new InvalidDataException("Метаданные обновления слишком велики.");
                data.Write(buffer, 0, read);
            }
            if (length is { } expected && data.Length != expected)
                throw new HttpRequestException("Соединение оборвалось при чтении метаданных обновления.");
            return data.ToArray();
        }
    }

    private static bool IsMetadataBodyInterruption(Exception error) => error switch
    {
        HttpIOException wire => wire.HttpRequestError is HttpRequestError.ResponseEnded or HttpRequestError.ConnectionError,
        InvalidDataException => false,
        _ => error is IOException or SocketException
    };

    private static HttpRequestException MetadataBodyInterrupted(Exception error, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return new HttpRequestException("Соединение оборвалось при чтении метаданных обновления.", error);
    }
}

internal sealed class LatestReleaseRateLimitException(RetryConditionHeaderValue? retryAfter, DateTimeOffset? reset, bool secondary)
    : HttpRequestException("Достигнут лимит проверок обновлений.", null, HttpStatusCode.TooManyRequests)
{
    internal bool IsSecondary => secondary;
    internal TimeSpan GetRetryDelay(DateTimeOffset now)
    {
        var delay = retryAfter?.Delta;
        if (retryAfter?.Date is { } retryAt && retryAt >= now) delay = retryAt - now;
        if (reset is { } next && next >= now && (delay is null || next - now > delay.Value)) delay = next - now;
        return delay ?? TimeSpan.FromMinutes(1);
    }
}

internal sealed class IncompleteReleaseFeedException()
    : IOException("В последнем официальном выпуске отсутствует полный пакет обновления.");

internal interface IAppUpdateBackend
{
    // A failed apply may retain the pending version only when local revalidation succeeded and the updater did not start.
    string? PendingVersion { get; }
    // Cancellation before committing the selection must leave the previous selection intact.
    Task<string?> CheckAsync(CancellationToken cancellationToken);
    // Returning successfully commits the verified file; later cancellation cannot revoke it.
    Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken);
    void ApplyAndRestart();
}

internal sealed class VelopackUpdateBackend : IAppUpdateBackend
{
    private const string PackageId = "YMusic.Native";
    private const string ReceiptFileName = "yamuse-package-receipts.dat";
    private const int MaximumReceipts = 64;
    private const int MaximumReceiptBytes = 24 * 1024;
    private const int MaximumProtectedReceiptBytes = 32 * 1024;
    private static readonly JsonSerializerOptions ReceiptJson = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly IVelopackLocator _locator;
    private readonly UpdateManager _manager;
    private readonly Func<HttpMessageHandler> _metadataHandler;
    private readonly Func<HttpMessageHandler> _packageHandler;
    private readonly TimeSpan _metadataDeadline;
    private readonly Func<SafeFileHandle, FileSecurityPreservation.FileRevision?> _packageRevision;
    private readonly Func<string> _localAppDataFolder;
    private readonly Func<Stream, CancellationToken, byte[]> _retiredPackageHash;
    private readonly Action<SafeFileHandle> _deleteRetiredPackage;
    private readonly object _maintenanceGate = new();
    private PackageMaintenanceRequest? _activeMaintenance;
    private PackageMaintenanceRequest? _queuedMaintenance;
    private Task? _maintenanceWorker;
    private OfficialPackage? _selected;
    private bool _prepared;

    internal VelopackUpdateBackend(Uri repository, IVelopackLocator? locator = null, Func<HttpMessageHandler>? metadataHandler = null,
        Func<HttpMessageHandler>? packageHandler = null, TimeSpan? metadataDeadline = null,
        Func<SafeFileHandle, FileSecurityPreservation.FileRevision?>? packageRevision = null, Func<string>? localAppDataFolder = null,
        Func<Stream, CancellationToken, byte[]>? retiredPackageHash = null, Action<SafeFileHandle>? deleteRetiredPackage = null)
    {
        if (repository != UpdateSupport.OfficialRepository)
            throw new ArgumentException("Источник обновлений не поддерживается.", nameof(repository));
        _locator = locator ?? VelopackLocator.Current;
        _manager = new UpdateManager(new GithubSource(UpdateSupport.OfficialRepository.AbsoluteUri, null, false), locator: _locator);
        _metadataHandler = metadataHandler ?? (() => new HttpClientHandler { AllowAutoRedirect = false });
        _packageHandler = packageHandler ?? (metadataHandler is null
            ? () => new HttpClientHandler { AllowAutoRedirect = false }
            : () => throw new InvalidOperationException("Для тестового источника пакета требуется тестовый HTTP-обработчик."));
        _metadataDeadline = metadataDeadline ?? TimeSpan.FromSeconds(30);
        _packageRevision = packageRevision ?? FileSecurityPreservation.GetPersistentRevision;
        _localAppDataFolder = localAppDataFolder ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _retiredPackageHash = retiredPackageHash ?? HashRetiredPackage;
        _deleteRetiredPackage = deleteRetiredPackage ?? UninstallPathGuard.DeleteByHandle;
    }

    public string? PendingVersion => _prepared ? _selected?.Asset.Version.ToString() : null;

    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_manager.AppId, PackageId, StringComparison.OrdinalIgnoreCase) || _manager.CurrentVersion is not { } current)
            throw new InvalidOperationException("Установленный пакет обновления не распознан.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_metadataDeadline);
        using var metadataClient = OfficialClient(_metadataHandler());
        var candidate = await CheckLatestReleaseAsync(metadataClient, current, deadline.Token).ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested();
        var prepared = false;
        if (candidate is not null)
        {
            var asset = candidate.Asset;
            var path = GetPackagePath(asset);
            using var parents = UninstallPathGuard.HoldAncestors(path);
            if (parents.Complete)
            {
                using var file = TryOpenPackage(path);
                prepared = file is not null && await MatchesHashAsync(file, asset, cancellationToken).ConfigureAwait(false);
                if (prepared)
                {
                    ValidateManifest(file!, asset);
                    cancellationToken.ThrowIfCancellationRequested();
                    RecordVerifiedPackage(file!, asset);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _selected = candidate;
        _prepared = prepared;
        QueuePackageMaintenance(current, cancellationToken);
        return candidate?.Asset.Version.ToString();
    }

    public async Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
    {
        var selected = _selected ?? throw new InvalidOperationException("Нет выбранного обновления.");
        var package = selected.Asset;
        var path = GetPackagePath(package);
        using var parents = UninstallPathGuard.HoldAncestors(path, create: true);
        using var cached = TryOpenPackage(path);
        if (cached is not null && await MatchesHashAsync(cached, package, cancellationToken).ConfigureAwait(false))
        {
            ValidateManifest(cached, package);
            cancellationToken.ThrowIfCancellationRequested();
            CommitPreparedPackage(cached, package);
            return;
        }

        _prepared = false;
        FileSecurityPreservation.FileRevision? revision = null;
        if (cached is not null)
        {
            FileSecurityPreservation.CheckReplacementMetadata(cached.SafeFileHandle);
            revision = FileSecurityPreservation.GetRevision(cached.SafeFileHandle);
        }
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".yamuse-update-{Guid.NewGuid():N}.tmp");
        using var handle = UninstallPathGuard.CreateStagedFile(temporary, asynchronous: true, writeDacl: cached is not null);
        var published = false;
        FileStream? staged = null;
        try
        {
            UninstallPathGuard.SetDeleteDisposition(handle, delete: true);
            staged = new FileStream(handle, FileAccess.ReadWrite, 65536, isAsync: true);
            FileSecurityPreservation.CheckReplacementMetadata(handle);
            if (cached is not null) FileSecurityPreservation.PrepareReplacement(cached.SafeFileHandle, handle);
            await DownloadOfficialPackageAsync(selected, staged, progress, cancellationToken).ConfigureAwait(false);
            await staged.FlushAsync(cancellationToken).ConfigureAwait(false);
            staged.Position = 0;
            if (!await MatchesHashAsync(staged, package, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Контрольная сумма обновления не совпадает.");
            ValidateManifest(staged, package);
            if (revision is { } expected)
            {
                using var current = TryOpenPackage(path);
                if (current is null || FileSecurityPreservation.GetRevision(current.SafeFileHandle) != expected)
                    throw new IOException("Кэш обновления изменился во время скачивания.");
                FileSecurityPreservation.CheckReplacementMetadata(current.SafeFileHandle);
                FileSecurityPreservation.PreserveBasicInformation(current.SafeFileHandle, handle);
                staged.Flush(flushToDisk: true);
                if (FileSecurityPreservation.GetRevision(current.SafeFileHandle) != expected)
                    throw new IOException("Кэш обновления изменился во время скачивания.");
            }
            else staged.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            // Windows requires closing readers of the checked old directory entry just before the atomic replacement.
            cached?.Dispose();
            ManagedCacheCleanup.RenameByHandle(handle, path, replace: revision is not null, releaseDeleteDisposition: true);
            published = true;
            CommitPreparedPackage(staged, package);
        }
        finally
        {
            if (!published) UninstallPathGuard.DiscardStagedFile(handle);
            staged?.Dispose();
        }
    }

    private void CommitPreparedPackage(FileStream file, VelopackAsset package)
    {
        RecordVerifiedPackage(file, package);
        _prepared = true;
    }

    private void QueuePackageMaintenance(SemanticVersion installed, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        lock (_maintenanceGate)
        {
            var request = new PackageMaintenanceRequest(installed, token);
            if (_queuedMaintenance is null && _activeMaintenance == request) return;
            _queuedMaintenance = request;
            _maintenanceWorker ??= Task.Run(RunPackageMaintenance);
        }
    }

    private void RunPackageMaintenance()
    {
        try
        {
            while (true)
            {
                PackageMaintenanceRequest request;
                lock (_maintenanceGate)
                {
                    if (_queuedMaintenance is not { } next) return;
                    request = next;
                    _activeMaintenance = request;
                    _queuedMaintenance = null;
                }

                if (!request.Token.IsCancellationRequested) RetirePreviousPackages(request.Installed, request.Token);
            }
        }
        finally
        {
            lock (_maintenanceGate)
            {
                _activeMaintenance = null;
                _maintenanceWorker = _queuedMaintenance is null ? null : Task.Run(RunPackageMaintenance);
            }
        }
    }

    internal async Task WaitForPackageMaintenanceAsync()
    {
        while (true)
        {
            Task? worker;
            lock (_maintenanceGate) worker = _maintenanceWorker;
            if (worker is null) return;
            await worker.ConfigureAwait(false);
        }
    }

    private void RecordVerifiedPackage(FileStream file, VelopackAsset package)
    {
        try
        {
            if (package.SHA256 is not { } hash || GetPackageIdentity(file) is not { } identity) return;
            var verified = new PackageReceipt(package.Version.ToString(), package.FileName, package.Size, hash.ToUpperInvariant(), identity);
            if (!IsValidReceipt(verified)) return;
            var path = Path.Combine(GetPackagesDirectory(), ReceiptFileName);
            var entropy = ReceiptEntropy(path);
            using var transaction = GuardedStoreFile.BeginWrite(path, createParents: false);
            var storedReceipts = ReadStoredReceipts(transaction, entropy, out var damaged);
            if (storedReceipts is null && damaged is null) return;
            List<PackageReceipt> receipts = [.. (storedReceipts ?? [])];
            var index = receipts.FindIndex(item => item.FileName.Equals(verified.FileName, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                if (receipts.Count == MaximumReceipts) ForgetOldestReceipt(receipts);
                receipts.Add(verified);
            }
            else
            {
                if (receipts[index] == verified) return;
                receipts[index] = verified;
            }
            if (ProtectReceipts(receipts, entropy, verified) is not { } stored) return;
            if (damaged is null) transaction.Write(stored);
            else ArchiveDamagedReceipts(transaction, path, damaged, stored);
        }
        catch (Exception error) when (IsReceiptFailure(error) || error is JsonException)
        {
            // Saving a receipt is best-effort. An unrecorded package remains usable and cannot be retired on that basis.
        }
    }

    private void RetirePreviousPackages(SemanticVersion installed, CancellationToken token)
    {
        try
        {
            var directory = GetPackagesDirectory();
            var ledger = Path.Combine(directory, ReceiptFileName);
            var entropy = ReceiptEntropy(ledger);
            PackageReceipt[]? snapshot;
            using (var transaction = GuardedStoreFile.BeginWrite(ledger, createParents: false))
                snapshot = ReadStoredReceipts(transaction, entropy, out _);
            if (snapshot is null) return;
            foreach (var receipt in snapshot.Where(item => SemanticVersion.Parse(item.Version) < installed).Take(2))
            {
                token.ThrowIfCancellationRequested();
                try { RetirePackage(directory, receipt, ledger, entropy, token); }
                catch (Exception error) when (IsReceiptFailure(error) || error is JsonException) { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsReceiptFailure(error) || error is JsonException) { }
    }

    private static byte[] ReceiptEntropy(string path)
        => SHA256.HashData(Encoding.UTF8.GetBytes("Yamuse.VerifiedUpdatePackages.1\0" + path.ToUpperInvariant()));

    private static PackageReceipt[]? ReadStoredReceipts(GuardedStoreFile.WriteTransaction transaction, byte[] entropy, out byte[]? damaged)
    {
        damaged = null;
        if (!transaction.Exists) return [];
        if (transaction.ReadExisting(MaximumProtectedReceiptBytes) is not { } stored) return null;
        var raw = ProtectedData.Unprotect(stored, entropy, DataProtectionScope.CurrentUser);
        var receipts = ReadAuthenticatedReceipts(raw, out var unknownSchema);
        if (receipts is null && !unknownSchema) damaged = stored;
        return receipts;
    }

    private static byte[]? ProtectReceipts(List<PackageReceipt> receipts, byte[] entropy, PackageReceipt? keep = null)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(new PackageReceiptLedger(1, [.. receipts]), ReceiptJson);
        while (data.Length > MaximumReceiptBytes && keep is not null && ForgetOldestReceipt(receipts, keep))
            data = JsonSerializer.SerializeToUtf8Bytes(new PackageReceiptLedger(1, [.. receipts]), ReceiptJson);
        if (data.Length > MaximumReceiptBytes) return null;
        var stored = ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);
        return stored.Length <= MaximumProtectedReceiptBytes ? stored : null;
    }

    private static PackageReceipt[]? ReadAuthenticatedReceipts(byte[] raw, out bool unknownSchema)
    {
        unknownSchema = false;
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = ReceiptJson.MaxDepth });
            var root = document.RootElement;
            unknownSchema = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Schema", out var schema)
                && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var number) && number > 1;
            if (unknownSchema || raw.Length > MaximumReceiptBytes
                || root.Deserialize<PackageReceiptLedger>(ReceiptJson) is not { Schema: 1, Packages: { } packages }
                || packages.Length > MaximumReceipts || packages.Any(receipt => !IsValidReceipt(receipt))
                || packages.Select(receipt => receipt.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != packages.Length)
                return null;
            return packages;
        }
        catch (JsonException) { return null; }
    }

    private static void ArchiveDamagedReceipts(GuardedStoreFile.WriteTransaction transaction, string path, byte[] damaged, byte[] fresh)
    {
        // DPAPI must authenticate the bytes before this path is reachable. Failed Unprotect leaves the opaque ledger in place.
        using var archive = transaction.CreateStage($"{path}.corrupt-{Guid.NewGuid():N}.bak");
        var committed = false;
        try
        {
            using (var original = transaction.OpenExisting() ?? throw new IOException("Журнал пакетов изменился."))
            {
                RandomAccess.Write(archive, damaged, 0);
                FileSecurityPreservation.PreserveBasicInformation(original, archive);
                RandomAccess.FlushToDisk(archive);
            }
            UninstallPathGuard.KeepFileOnClose(archive);
            transaction.Write(fresh);
            committed = true;
        }
        finally { if (!committed) UninstallPathGuard.DiscardStagedFile(archive); }
    }

    private static bool ForgetOldestReceipt(List<PackageReceipt> receipts, PackageReceipt? keep = null)
    {
        // Reclaim ledger space only. A forgotten archive must pass a new official verification before it can be retired.
        var oldest = receipts.Where(receipt => receipt != keep).MinBy(receipt => SemanticVersion.Parse(receipt.Version));
        return oldest is not null && receipts.Remove(oldest);
    }

    private void RetirePackage(string directory, PackageReceipt receipt, string ledger, byte[] entropy, CancellationToken token)
    {
        var path = Path.Combine(directory, receipt.FileName);
        using var parents = UninstallPathGuard.HoldAncestors(path);
        if (!parents.Complete) return;
        FileStream? file = null;
        try
        {
            var mayRetire = false;
            try
            {
                file = TryOpenPackage(path, deleteAccess: true);
                if (file is null) mayRetire = true;
                else if (file.Length == receipt.Size && GetPackageIdentity(file) == receipt.Identity)
                {
                    FileSecurityPreservation.CheckReplacementMetadata(file.SafeFileHandle);
                    mayRetire = Convert.ToHexString(_retiredPackageHash(file, token)).Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception error) when (IsReceiptFailure(error)) { }

            // Large archive reads run outside the ledger mutex. Keep the archive pinned until the exact receipt is rechecked.
            token.ThrowIfCancellationRequested();
            using var transaction = GuardedStoreFile.BeginWrite(ledger, createParents: false);
            if (ReadStoredReceipts(transaction, entropy, out _) is not { } stored) return;
            var index = Array.IndexOf(stored, receipt);
            if (index < 0) return;
            if (mayRetire)
            {
                try
                {
                    if (file is null) mayRetire = !UninstallPathGuard.EntryExists(path);
                    else FileSecurityPreservation.CheckReplacementMetadata(file.SafeFileHandle);
                }
                catch (Exception error) when (IsReceiptFailure(error)) { mayRetire = false; }
            }
            var receipts = stored.ToList();
            receipts.RemoveAt(index);
            if (mayRetire && PublishRetirementReceipts(transaction, receipts, entropy, token, file?.SafeFileHandle)) return;
            if (index == stored.Length - 1) return;
            // Keep the exact receipt and rotate it after any file-specific failure, including a late refusal to delete.
            receipts.Add(receipt);
            PublishRetirementReceipts(transaction, receipts, entropy, token);
        }
        finally { file?.Dispose(); }
    }

    private bool PublishRetirementReceipts(GuardedStoreFile.WriteTransaction transaction, List<PackageReceipt> receipts,
        byte[] entropy, CancellationToken token, SafeFileHandle? delete = null)
    {
        if (ProtectReceipts(receipts, entropy) is not { } protectedReceipts) return false;
        using var staged = transaction.Stage(protectedReceipts);
        var committed = false;
        try
        {
            committed = transaction.Commit(staged, publish =>
            {
                token.ThrowIfCancellationRequested();
                if (delete is not null)
                {
                    try { _deleteRetiredPackage(delete); }
                    catch (Exception error) when (IsReceiptFailure(error)) { return false; }
                }
                publish();
                return true;
            });
            return committed;
        }
        finally { if (!committed) UninstallPathGuard.DiscardStagedFile(staged); }
    }

    internal static byte[] HashRetiredPackage(Stream source, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            token.ThrowIfCancellationRequested();
            if (read == 0) return hash.GetHashAndReset();
            hash.AppendData(buffer, 0, read);
        }
    }

    private static bool IsReceiptFailure(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception
        or System.Security.SecurityException or CryptographicException;

    private static bool IsValidReceipt(PackageReceipt? receipt)
        => receipt is { Version: { Length: > 0 and <= 64 } text, Size: > 0, Sha256: { Length: 64 } hash }
            && receipt.Identity is { Created: > 0 } identity && (identity.Low != 0 || identity.High != 0)
            && hash.All(Uri.IsHexDigit) && SemanticVersion.TryParse(text, out var version) && !version.IsPrerelease
            && version.ToString().Equals(text, StringComparison.Ordinal) && IsPackageName(receipt.FileName, version);

    private PackageFileIdentity? GetPackageIdentity(FileStream file)
        => _packageRevision(file.SafeFileHandle) is { } revision
            ? new(revision.Volume, revision.Low, revision.High, revision.Created) : null;

    private sealed record PackageFileIdentity(ulong Volume, ulong Low, ulong High, long Created);
    private sealed record PackageReceipt(string Version, string FileName, long Size, string Sha256, PackageFileIdentity Identity);
    private sealed record PackageReceiptLedger(int Schema, PackageReceipt[] Packages);
    private sealed record PackageMaintenanceRequest(SemanticVersion Installed, CancellationToken Token);

    public void ApplyAndRestart()
    {
        if (!_prepared || _selected is not { Asset: var package })
            throw new InvalidOperationException("Обновление ещё не загружено.");

        _prepared = false;
        var path = GetPackagePath(package);
        using var parents = UninstallPathGuard.HoldExistingAncestors(path);
        using var file = TryOpenPackage(path) ?? throw new IOException("Загруженный пакет обновления отсутствует.");
        if (file.Length != package.Size || !HashEquals(SHA256.HashData(file), package))
            throw new InvalidDataException("Контрольная сумма обновления не совпадает.");
        ValidateManifest(file, package);
        RecordVerifiedPackage(file, package);
        _prepared = true;
        PrepareUpdaterAndStart(file, package);
        _prepared = false;
        _locator.Process.Exit(0);
    }

    private void PrepareUpdaterAndStart(FileStream packageFile, VelopackAsset package)
    {
        var root = UninstallPathGuard.Canonical(_locator.RootAppDir
            ?? throw new IOException("Папка установки обновлений недоступна."));
        var updater = Program.CheckedUpdateExecutable(_locator, root, _localAppDataFolder);
        var primary = Path.Combine(root, "Update.exe");
        var isFallback = !updater.Equals(primary, StringComparison.OrdinalIgnoreCase);
        var updaterDirectory = Path.GetDirectoryName(updater)!;
        using var installationParents = isFallback ? UninstallPathGuard.HoldExistingAncestors(primary) : null;
        using var parents = UninstallPathGuard.HoldExistingAncestors(updater);

        packageFile.Position = 0;
        using var archive = new ZipArchive(packageFile, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.Entries.SingleOrDefault(item => item.FullName == "lib/app/Squirrel.exe");
        if (entry is null || entry.Length is <= 0 or > 64 * 1024 * 1024)
            throw new InvalidDataException("В пакете отсутствует корректная программа установки обновлений.");

        byte[] checksum;
        using (var contents = entry.Open()) checksum = CopyUpdater(contents, null, entry.Length);
        using var existing = TryOpenPackage(updater);
        if (existing is null && !isFallback) throw new IOException("Программа установки обновлений отсутствует.");
        var created = existing is null ? PublishMissingFallback(updater, entry, checksum) : null;
        using var reopened = existing is null ? TryOpenPackage(updater) : null;
        var current = existing ?? reopened ?? throw new IOException("Программа установки обновлений отсутствует.");
        if (created is not null)
        {
            if (GetPackageIdentity(current) != created)
                throw new InvalidDataException("Программа установки обновлений изменилась.");
            FileSecurityPreservation.CheckReplacementMetadata(current.SafeFileHandle);
        }
        if (current.Length is <= 0 or > 64 * 1024 * 1024)
            throw new InvalidDataException("Некорректный размер установленной программы обновлений.");

        if (MatchesUpdater(current, entry.Length, checksum))
        {
            _manager.WaitExitThenApplyUpdates(package);
            return;
        }
        if (created is not null) throw new InvalidDataException("Программа установки обновлений изменилась.");

        FileSecurityPreservation.CheckReplacementMetadata(current.SafeFileHandle);
        using var stagedHandle = UninstallPathGuard.CreateStagedFile(UpdaterTemporaryPath(updaterDirectory), writeThrough: true, writeDacl: true);
        var published = false;
        FileStream? staged = null;
        try
        {
            UninstallPathGuard.SetDeleteDisposition(stagedHandle, delete: true);
            staged = new FileStream(stagedHandle, FileAccess.ReadWrite);
            FileSecurityPreservation.PrepareReplacement(current.SafeFileHandle, stagedHandle);
            using (var contents = entry.Open())
                if (!CopyUpdater(contents, staged, entry.Length).AsSpan().SequenceEqual(checksum))
                    throw new InvalidDataException("Программа установки обновлений изменилась.");
            FileSecurityPreservation.PreserveBasicInformation(current.SafeFileHandle, stagedHandle);
            staged.Flush(flushToDisk: true);

            using var backupHandle = UninstallPathGuard.CreateStagedFile(UpdaterTemporaryPath(updaterDirectory), writeThrough: true, writeDacl: true);
            FileStream? backup = null;
            var preserveBackup = false;
            try
            {
                UninstallPathGuard.SetDeleteDisposition(backupHandle, delete: true);
                backup = new FileStream(backupHandle, FileAccess.ReadWrite);
                FileSecurityPreservation.PrepareReplacement(current.SafeFileHandle, backupHandle);
                current.Position = 0;
                CopyUpdater(current, backup, current.Length);
                FileSecurityPreservation.PreserveBasicInformation(current.SafeFileHandle, backupHandle);
                backup.Flush(flushToDisk: true);
                current.Dispose();

                UninstallPathGuard.SetDeleteDisposition(backupHandle, delete: false);
                ManagedCacheCleanup.RenameByHandle(stagedHandle, updater, replace: true, releaseDeleteDisposition: true);
                published = true;
                staged.Dispose();
                try
                {
                    using var locked = TryOpenPackage(updater);
                    if (locked is null || !MatchesUpdater(locked, entry.Length, checksum))
                        throw new InvalidDataException("Программа установки обновлений изменилась.");
                    // The SDK receives the selected full package and starts the canonical updater. It waits for this process to exit.
                    _manager.WaitExitThenApplyUpdates(package);
                }
                catch
                {
                    // Keep the old bytes for recovery if Windows also rejects the rollback rename.
                    preserveBackup = true;
                    ManagedCacheCleanup.RenameByHandle(backupHandle, updater, replace: true);
                    throw;
                }
            }
            finally
            {
                if (!preserveBackup) UninstallPathGuard.DiscardStagedFile(backupHandle);
                backup?.Dispose();
            }
        }
        finally
        {
            if (!published) UninstallPathGuard.DiscardStagedFile(stagedHandle);
            staged?.Dispose();
        }
    }

    private PackageFileIdentity? PublishMissingFallback(string updater, ZipArchiveEntry entry, byte[] checksum)
    {
        using var handle = UninstallPathGuard.CreateStagedFile(UpdaterTemporaryPath(Path.GetDirectoryName(updater)!),
            writeThrough: true, deleteOnClose: true);
        using var staged = new FileStream(handle, FileAccess.ReadWrite);
        FileSecurityPreservation.CheckReplacementMetadata(handle);
        using (var contents = entry.Open())
            if (!CopyUpdater(contents, staged, entry.Length).AsSpan().SequenceEqual(checksum))
                throw new InvalidDataException("Программа установки обновлений изменилась.");
        staged.Flush(flushToDisk: true);
        var identity = GetPackageIdentity(staged) ?? throw new IOException("Не удалось подтвердить идентичность программы установки обновлений.");
        // An entry created in the meantime is handled by the existing-file path and its metadata checks.
        try { ManagedCacheCleanup.RenameByHandle(handle, updater); }
        catch (Win32Exception) when (UninstallPathGuard.EntryExists(updater)) { return null; }
        UninstallPathGuard.KeepFileOnClose(handle);
        return identity;
    }

    private static string UpdaterTemporaryPath(string root) => Path.Combine(root, $".yamuse-updater-{Guid.NewGuid():N}.tmp");

    private static bool MatchesUpdater(FileStream file, long size, byte[] checksum)
        => file.Length == size && SHA256.HashData(file).AsSpan().SequenceEqual(checksum);

    private static byte[] CopyUpdater(Stream source, FileStream? destination, long size)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long written = 0;
        int count;
        while ((count = source.Read(buffer)) > 0)
        {
            if (count > size - written) throw new InvalidDataException("Размер программы установки обновлений не совпадает.");
            destination?.Write(buffer, 0, count);
            hash.AppendData(buffer, 0, count);
            written += count;
        }
        if (written != size) throw new InvalidDataException("Размер программы установки обновлений не совпадает.");
        return hash.GetHashAndReset();
    }

    private void ValidateRelease(VelopackAsset package)
    {
        if (!string.Equals(package.PackageId, PackageId, StringComparison.OrdinalIgnoreCase)
            || package.Type != VelopackAssetType.Full || package.Version is not { IsPrerelease: false } version
            || _manager.CurrentVersion is not { } current || version <= current
            || package.Size <= 0 || package.SHA256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)
            || !IsPackageName(package.FileName, version))
            throw new InvalidDataException("Официальные метаданные пакета обновления некорректны.");
    }

    private static bool IsPackageName(string? fileName, SemanticVersion version)
        => string.Equals(fileName, $"Yamuse-{version}-full.nupkg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, $"{PackageId}-{version}-full.nupkg", StringComparison.OrdinalIgnoreCase);

    // ValidateRelease restricts FileName to the supported full-package basenames.
    private string GetPackagePath(VelopackAsset package)
        => Path.Combine(GetPackagesDirectory(), package.FileName);

    private string GetPackagesDirectory() => UninstallPathGuard.Canonical(_locator.PackagesDir
        ?? throw new IOException("Папка обновлений отсутствует."));

    private static FileStream? TryOpenPackage(string path, bool deleteAccess = false)
    {
        try
        {
            var handle = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: deleteAccess);
            try { return new FileStream(handle, FileAccess.Read); }
            catch { handle.Dispose(); throw; }
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
    }

    private static async Task<bool> MatchesHashAsync(FileStream file, VelopackAsset package, CancellationToken token)
        => file.Length == package.Size && HashEquals(await SHA256.HashDataAsync(file, token).ConfigureAwait(false), package);

    private static bool HashEquals(byte[] hash, VelopackAsset package)
        => Convert.ToHexString(hash).Equals(package.SHA256, StringComparison.OrdinalIgnoreCase);

    private static void ValidateManifest(FileStream file, VelopackAsset expected)
    {
        file.Position = 0;
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = archive.GetEntry(PackageId + ".nuspec") ?? throw new InvalidDataException("Манифест обновления отсутствует.");
        using var entry = manifest.Open();
        using var reader = XmlReader.Create(entry, new XmlReaderSettings { MaxCharactersInDocument = 1024 * 1024, DtdProcessing = DtdProcessing.Prohibit });
        var root = XDocument.Load(reader).Root;
        var ns = root?.Name.Namespace ?? XNamespace.None;
        var metadata = root?.Element(ns + "metadata");
        if (root?.Name.LocalName != "package"
            || !string.Equals((string?)metadata?.Element(ns + "id"), expected.PackageId, StringComparison.OrdinalIgnoreCase)
            || !SemanticVersion.TryParse((string?)metadata?.Element(ns + "version"), out var version)
            || !string.Equals(version.ToString(), expected.Version.ToString(), StringComparison.Ordinal))
            throw new InvalidDataException("Состав пакета обновления не совпадает с официальными метаданными.");
    }

    private sealed record OfficialPackage(VelopackAsset Asset, Uri DownloadUri);
    private sealed record ReleaseAsset(Uri DownloadUri, long? Size, string? Sha256, bool IsStarter)
    {
        internal void Verify(long size, string? sha256)
        {
            if ((Size is { } expectedSize && expectedSize != size)
                || (Sha256 is { } expectedHash && !expectedHash.Equals(sha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Размер или контрольная сумма файла не совпадают с официальным выпуском.");
        }
    }

    private async Task<OfficialPackage?> CheckLatestReleaseAsync(HttpClient client, SemanticVersion current, CancellationToken token)
    {
        using var latest = await OpenResponseAsync(client, UpdateSupport.OfficialLatestRelease, _ => false, token).ConfigureAwait(false);
        if (latest.StatusCode == HttpStatusCode.NotFound) return null;
        await UpdateSupport.CheckLatestRateLimitAsync(latest, token).ConfigureAwait(false);
        var metadataBytes = await ReadMetadataAsync(latest, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(metadataBytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var (tag, version) = UpdateSupport.ParseStableRelease(root);
        if (version <= current) return null;
        var feedAsset = GetReleaseAsset(root, tag, $"releases.{_locator.Channel ?? "win"}.json");
        if (feedAsset is null || feedAsset.IsStarter) throw IncompleteRelease(version);
        var feedUri = feedAsset.DownloadUri;
        using var response = await OpenResponseAsync(client, feedUri, target => IsAssetRedirect(feedUri, target), token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) throw IncompleteRelease(version);
        var feedBytes = await ReadMetadataAsync(response, token).ConfigureAwait(false);
        feedAsset.Verify(feedBytes.LongLength, feedAsset.Sha256 is null ? null : Convert.ToHexString(SHA256.HashData(feedBytes)));
        var feedText = Encoding.UTF8.GetString(feedBytes).TrimStart('\uFEFF');
        using (var feedDocument = JsonDocument.Parse(feedText, new JsonDocumentOptions { MaxDepth = 32 }))
        {
            var index = feedDocument.RootElement;
            if (index.ValueKind != JsonValueKind.Object || !index.TryGetProperty("Assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Список пакетов официального выпуска некорректен.");
        }
        var feed = VelopackAssetFeed.FromJson(feedText);
        var fullAssets = feed.Assets.Where(asset => asset.Type == VelopackAssetType.Full).ToArray();
        if (fullAssets.Length == 0) throw IncompleteRelease(version);
        var candidate = fullAssets.SingleOrDefault(asset => string.Equals(asset.Version?.ToString(), version.ToString(), StringComparison.Ordinal))
            ?? throw new InvalidDataException("Версия пакета не совпадает с последним официальным выпуском.");
        ValidateRelease(candidate);
        var packageAsset = GetReleaseAsset(root, tag, candidate.FileName);
        if (packageAsset is null) throw IncompleteRelease(version);
        packageAsset.Verify(candidate.Size, candidate.SHA256);
        if (packageAsset.IsStarter) throw IncompleteRelease(version);
        return new(candidate, packageAsset.DownloadUri);
    }

    private Exception IncompleteRelease(SemanticVersion latest) => _prepared && _selected is { Asset.Version: { } pending } && latest <= pending
        ? new InvalidDataException("Ранее выбранный полный пакет больше не опубликован в официальном выпуске.")
        : new IncompleteReleaseFeedException();

    private static ReleaseAsset? GetReleaseAsset(JsonElement release, string tag, string fileName)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Список файлов официального выпуска отсутствует.");
        var expectedUri = new Uri($"{UpdateSupport.OfficialRepository.AbsoluteUri}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(fileName)}");
        ReleaseAsset? selected = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !string.Equals(name.GetString(), fileName, StringComparison.Ordinal)) continue;
            if (selected is not null) throw new InvalidDataException("Файл повторяется в последнем официальном выпуске.");
            var starter = false;
            if (asset.TryGetProperty("state", out var state) && state.ValueKind != JsonValueKind.Null)
            {
                if (state.ValueKind != JsonValueKind.String || state.GetString() is not ("uploaded" or "starter"))
                    throw new InvalidDataException("Состояние официального файла некорректно.");
                starter = state.GetString() == "starter";
            }
            if (asset.TryGetProperty("browser_download_url", out var url) && url.ValueKind != JsonValueKind.Null)
            {
                if (url.ValueKind != JsonValueKind.String || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)
                    || !IsSecure(uri) || !uri.AbsoluteUri.Equals(expectedUri.AbsoluteUri, StringComparison.Ordinal))
                    throw new InvalidDataException("Адрес файла не совпадает с последним официальным выпуском.");
            }
            else if (!starter) throw new InvalidDataException("Адрес файла отсутствует в последнем официальном выпуске.");
            long? size = null;
            if (asset.TryGetProperty("size", out var length) && length.ValueKind != JsonValueKind.Null)
            {
                if (length.ValueKind != JsonValueKind.Number || !length.TryGetInt64(out var value) || value < 0 || value == 0 && !starter)
                    throw new InvalidDataException("Размер официального файла некорректен.");
                if (value > 0) size = value;
            }
            string? sha256 = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind != JsonValueKind.Null)
            {
                if (digest.ValueKind != JsonValueKind.String || digest.GetString() is not { Length: 71 } value
                    || !value.StartsWith("sha256:", StringComparison.Ordinal) || !value[7..].All(Uri.IsHexDigit))
                    throw new InvalidDataException("Контрольная сумма официального файла некорректна.");
                sha256 = value[7..];
            }
            selected = new(expectedUri, size, sha256, starter);
        }
        return selected;
    }

    private static HttpClient OfficialClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Yamuse-Update-Check/1.0");
        return client;
    }

    private static bool IsGithubAssetUri(Uri uri) => IsSecure(uri)
        && uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    private static bool IsAssetRedirect(Uri original, Uri target)
        => IsSecure(target) && (target.AbsoluteUri.Equals(original.AbsoluteUri, StringComparison.Ordinal) || IsGithubAssetUri(target));

    private static bool IsSecure(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static async Task<HttpResponseMessage> OpenResponseAsync(HttpClient client, Uri uri, Func<Uri, bool> allowRedirect,
        CancellationToken token)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or > 399) return response;
            using (response)
            {
                if (response.Headers.Location is not { } location || !Uri.TryCreate(uri, location, out var next) || !allowRedirect(next))
                    throw new InvalidDataException("Перенаправление источника обновлений недопустимо.");
                uri = next;
            }
        }
        throw new InvalidDataException("Цепочка адресов источника обновлений слишком длинная.");
    }

    private static Task<byte[]> ReadMetadataAsync(HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        return UpdateSupport.ReadMetadataBodyAsync(response, 2 * 1024 * 1024, token);
    }

    private async Task DownloadOfficialPackageAsync(OfficialPackage selected, FileStream destination,
        Action<int> progress, CancellationToken cancellationToken)
    {
        using var client = OfficialClient(_packageHandler());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        var token = deadline.Token;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        var idleTime = TimeSpan.FromSeconds(30);
        idle.CancelAfter(idleTime);
        var uri = selected.DownloadUri;
        var package = selected.Asset;
        using var response = await OpenResponseAsync(client, uri, target => IsAssetRedirect(uri, target), idle.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != package.Size)
            throw new InvalidDataException("Размер файла обновления не совпадает.");
        await using var body = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
        var buffer = new byte[65536];
        long written = 0;
        var lastProgress = 0;
        while (true)
        {
            idle.CancelAfter(idleTime);
            var read = await body.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            idle.CancelAfter(Timeout.InfiniteTimeSpan);
            if (read == 0) break;
            if (read > package.Size - written) throw new InvalidDataException("Размер файла обновления не совпадает.");
            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            written += read;
            var percent = (int)(written * 100d / package.Size);
            if (percent > lastProgress) { lastProgress = percent; progress(percent); }
        }
        if (written != package.Size) throw new InvalidDataException("Размер файла обновления не совпадает.");
    }
}
