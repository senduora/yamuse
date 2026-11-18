using System.ComponentModel;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
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

// The window owns scheduling and all user interactions. This service has no
// timer, automatic download/application, or account storage access.
internal sealed class UpdateSupport
{
    internal static readonly Uri OfficialRepository = new("https://github.com/senduora/yamuse");
    internal static readonly Uri OfficialLatestRelease = new("https://api.github.com/repos/senduora/yamuse/releases/latest");
    private static readonly Lazy<UpdateSupport> Instance = new(() => new(
        Path.Combine(AppContext.BaseDirectory, "update-feed.json")));
    internal static UpdateSupport Current => Instance.Value;

    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly Func<Uri, IAppUpdateBackend> _backendFactory;
    private readonly Uri? _repository;
    private readonly Func<CancellationToken, Task<string?>> _portableCheck;
    private IAppUpdateBackend? _backend;
    private volatile AppUpdateState _state;
    internal AppUpdateState State => _state;
    internal bool HasPortableCheckOverride { get; }

    // Injection is limited to deterministic fixtures; production uses only the public Yamuse release.
    internal UpdateSupport(string? configurationPath, bool? installed = null,
        Func<Uri, IAppUpdateBackend>? backendFactory = null,
        Func<CancellationToken, Task<string?>>? portableCheck = null)
    {
        _backendFactory = backendFactory ?? (uri => new VelopackUpdateBackend(uri));
        var isInstalled = installed ?? DetectInstalled();
        var currentVersion = AppBranding.CurrentVersion;
        var (repository, invalid) = ReadRepository(configurationPath);
        _repository = repository;
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

    internal Task<AppUpdateState> CheckAsync(CancellationToken cancellationToken = default)
        => _repository is not null ? CheckCoreAsync(cancellationToken) : Task.FromResult(State);

    internal Task<AppUpdateState> DownloadAsync(IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
        => CanUseUpdates ? DownloadCoreAsync(progress, cancellationToken) : Task.FromResult(State);

    private bool CanUseUpdates => _repository is not null && State.IsInstalled;

    private async Task<AppUpdateState> CheckCoreAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        var before = State;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!State.IsInstalled)
            {
                // Portable previews never create the installer backend. The
                // only metadata authority is this project's public endpoint.
                Set(AppUpdateStatus.Checking, "Проверяем обновления…");
                var release = await _portableCheck(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return release is null ? Set(AppUpdateStatus.NoUpdate, "Используется последняя доступная версия.", checkSucceeded: true)
                    : Set(AppUpdateStatus.Available, "Доступна новая версия. Откройте официальный выпуск для скачивания.", release, checkSucceeded: true);
            }
            _backend ??= _backendFactory(_repository!);
            Set(AppUpdateStatus.Checking, "Проверяем обновления…");
            // The backend commits the selected candidate under the same operation lock.
            var version = await _backend.CheckAsync(cancellationToken).ConfigureAwait(false);
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

    private static bool IsTemporaryCheckFailure(Exception error)
        => error is OperationCanceledException
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
            if (!CanUseUpdates || State.Status != AppUpdateStatus.Downloaded || _backend is null)
                throw new InvalidOperationException("Обновление ещё не загружено.");
            try { _backend.ApplyAndRestart(); }
            catch (Exception)
            {
                Set(AppUpdateStatus.Error, "Не удалось применить обновление. Повторите позже.", State.AvailableVersion);
                throw new InvalidOperationException("Не удалось применить обновление. Повторите позже.");
            }
        }
        finally { _operation.Release(); }
    }

    private AppUpdateState Set(AppUpdateStatus status, string message, string? version = null, int? percent = null, bool checkSucceeded = false)
        => _state = State with { Status = status, Message = message, AvailableVersion = version, ProgressPercent = percent, CheckSucceeded = checkSucceeded };

    private static bool DetectInstalled()
    {
        try
        {
            var locator = VelopackLocator.Current;
            return locator.CurrentlyInstalledVersion is not null && !locator.IsPortable;
        }
        catch (InvalidOperationException)
        {
            // A host/test which bypasses Program has no locator. Fail closed:
            // such a process must never create an installer backend.
            return false;
        }
    }

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
}

internal interface IAppUpdateBackend
{
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
    private OfficialPackage? _selected;
    private bool _prepared;

    internal VelopackUpdateBackend(Uri repository, IVelopackLocator? locator = null, Func<HttpMessageHandler>? metadataHandler = null,
        Func<HttpMessageHandler>? packageHandler = null, TimeSpan? metadataDeadline = null,
        Func<SafeFileHandle, FileSecurityPreservation.FileRevision?>? packageRevision = null)
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
        PackageReceipt? receipt = null;
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
                    receipt = ObserveVerifiedPackage(file!, asset);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _selected = candidate;
        _prepared = prepared;
        // A completed check can retire a few known old archives without scanning the package directory.
        await Task.Run(() => MaintainPackageReceipts(receipt, current, cancellationToken)).ConfigureAwait(false);
        return candidate?.Asset.Version.ToString();
    }

    public async Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
    {
        var selected = _selected ?? throw new InvalidOperationException("Нет выбранного обновления.");
        var package = selected.Asset;
        var path = GetPackagePath(package);
        using var parents = UninstallPathGuard.HoldAncestors(path, create: true);
        using (var cached = TryOpenPackage(path))
        {
            if (cached is not null && await MatchesHashAsync(cached, package, cancellationToken).ConfigureAwait(false))
            {
                ValidateManifest(cached, package);
                cancellationToken.ThrowIfCancellationRequested();
                CommitPreparedPackage(cached, package);
                return;
            }
        }

        _prepared = false;
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".yamuse-update-{Guid.NewGuid():N}.tmp");
        using var handle = UninstallPathGuard.CreateStagedFile(temporary, asynchronous: true);
        var published = false;
        FileStream? staged = null;
        try
        {
            UninstallPathGuard.SetDeleteDisposition(handle, delete: true);
            staged = new FileStream(handle, FileAccess.ReadWrite, 65536, isAsync: true);
            await DownloadOfficialPackageAsync(selected, staged, progress, cancellationToken).ConfigureAwait(false);
            await staged.FlushAsync(cancellationToken).ConfigureAwait(false);
            staged.Position = 0;
            if (!await MatchesHashAsync(staged, package, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Контрольная сумма обновления не совпадает.");
            ValidateManifest(staged, package);
            cancellationToken.ThrowIfCancellationRequested();
            using (var replaced = TryOpenPackage(path, deleteAccess: true))
            {
                if (replaced is not null)
                {
                    if (await MatchesHashAsync(replaced, package, cancellationToken).ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        CommitPreparedPackage(replaced, package);
                        return;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    UninstallPathGuard.DeleteByHandle(replaced.SafeFileHandle);
                }
            }
            ManagedCacheCleanup.RenameByHandle(handle, path, releaseDeleteDisposition: true);
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
        _prepared = true;
        if (ObserveVerifiedPackage(file, package) is { } receipt) MaintainPackageReceipts(receipt);
    }

    private PackageReceipt? ObserveVerifiedPackage(FileStream file, VelopackAsset package)
    {
        try
        {
            if (package.SHA256 is not { } hash || GetPackageIdentity(file) is not { } identity) return null;
            var receipt = new PackageReceipt(package.Version.ToString(), package.FileName, package.Size, hash.ToUpperInvariant(),
                identity);
            return IsValidReceipt(receipt) ? receipt : null;
        }
        catch (Exception error) when (IsReceiptFailure(error)) { return null; }
    }

    private void MaintainPackageReceipts(PackageReceipt? verified, SemanticVersion? installed = null, CancellationToken token = default)
    {
        try
        {
            var directory = GetPackagesDirectory();
            var path = Path.Combine(directory, ReceiptFileName);
            var entropy = SHA256.HashData(Encoding.UTF8.GetBytes("Yamuse.VerifiedUpdatePackages.1\0" + path.ToUpperInvariant()));
            using var transaction = GuardedStoreFile.BeginWrite(path, createParents: false);
            List<PackageReceipt> receipts = [];
            if (transaction.Exists)
            {
                var stored = transaction.ReadExisting(MaximumProtectedReceiptBytes);
                if (stored is null) return;
                var raw = ProtectedData.Unprotect(stored, entropy, DataProtectionScope.CurrentUser);
                if (raw.Length > MaximumReceiptBytes
                    || JsonSerializer.Deserialize<PackageReceiptLedger>(raw, ReceiptJson) is not { Schema: 1, Packages: { } packages }
                    || packages.Length > MaximumReceipts || packages.Any(receipt => !IsValidReceipt(receipt))
                    || packages.Select(receipt => receipt.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != packages.Length)
                    return;
                receipts.AddRange(packages);
            }

            var changed = false;
            if (installed is not null)
            {
                foreach (var receipt in receipts.Where(receipt => SemanticVersion.Parse(receipt.Version) < installed).Take(2).ToArray())
                {
                    if (token.IsCancellationRequested) break;
                    var removed = TryRetirePackage(directory, receipt, token);
                    // Rotate blocked or changed files so one inaccessible entry cannot starve the next check.
                    if (removed || receipts[^1] != receipt)
                    {
                        receipts.Remove(receipt);
                        if (!removed) receipts.Add(receipt);
                        changed = true;
                    }
                }
            }

            if (verified is { } observed)
            {
                var previous = receipts.FindIndex(item => item.FileName.Equals(observed.FileName, StringComparison.OrdinalIgnoreCase));
                if (previous < 0)
                {
                    if (receipts.Count == MaximumReceipts) ForgetOldestReceipt(receipts);
                    receipts.Add(observed);
                    changed = true;
                }
                else if (previous >= 0 && receipts[previous] != observed)
                {
                    receipts[previous] = observed;
                    changed = true;
                }
            }

            if (changed)
            {
                var data = JsonSerializer.SerializeToUtf8Bytes(new PackageReceiptLedger(1, [.. receipts]), ReceiptJson);
                while (data.Length > MaximumReceiptBytes && verified is not null && ForgetOldestReceipt(receipts, verified))
                    data = JsonSerializer.SerializeToUtf8Bytes(new PackageReceiptLedger(1, [.. receipts]), ReceiptJson);
                if (data.Length > MaximumReceiptBytes) return;
                var stored = ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);
                if (stored.Length <= MaximumProtectedReceiptBytes) transaction.Write(stored);
            }
        }
        catch (Exception error) when (IsReceiptFailure(error) || error is JsonException)
        {
            // Receipt or cleanup failure leaves the verified package and update outcome usable.
        }
    }

    private static bool ForgetOldestReceipt(List<PackageReceipt> receipts, PackageReceipt? keep = null)
    {
        // Reclaim ledger space only. A forgotten archive must pass a new official verification before it can be retired.
        var oldest = receipts.Where(receipt => receipt != keep).MinBy(receipt => SemanticVersion.Parse(receipt.Version));
        return oldest is not null && receipts.Remove(oldest);
    }

    private bool TryRetirePackage(string directory, PackageReceipt receipt, CancellationToken token)
    {
        try
        {
            // The receipt transaction pins this directory and all ancestors for the entire operation.
            using var file = TryOpenPackage(Path.Combine(directory, receipt.FileName), deleteAccess: true);
            if (file is null) return true;
            if (file.Length != receipt.Size || GetPackageIdentity(file) != receipt.Identity) return false;
            FileSecurityPreservation.CheckReplacementMetadata(file.SafeFileHandle);
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase)
                || token.IsCancellationRequested) return false;
            UninstallPathGuard.DeleteByHandle(file.SafeFileHandle);
            return true;
        }
        catch (Exception error) when (IsReceiptFailure(error)) { return false; }
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
        _prepared = true;
        PrepareUpdaterAndStart(file, package);
        _locator.Process.Exit(0);
    }

    private void PrepareUpdaterAndStart(FileStream packageFile, VelopackAsset package)
    {
        var updater = UninstallPathGuard.Canonical(_locator.UpdateExePath
            ?? throw new IOException("Программа установки обновлений недоступна."));
        var root = UninstallPathGuard.Canonical(_locator.RootAppDir
            ?? throw new IOException("Папка установки обновлений недоступна."));
        if (!updater.Equals(Path.Combine(root, "Update.exe"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Расположение программы установки обновлений некорректно.");
        using var parents = UninstallPathGuard.HoldExistingAncestors(updater);

        packageFile.Position = 0;
        using var archive = new ZipArchive(packageFile, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.Entries.SingleOrDefault(item => item.FullName == "lib/app/Squirrel.exe");
        if (entry is null || entry.Length is <= 0 or > 64 * 1024 * 1024)
            throw new InvalidDataException("В пакете отсутствует корректная программа установки обновлений.");

        using var current = TryOpenPackage(updater) ?? throw new IOException("Программа установки обновлений отсутствует.");
        if (current.Length is <= 0 or > 64 * 1024 * 1024)
            throw new InvalidDataException("Некорректный размер установленной программы обновлений.");

        byte[] checksum;
        using (var contents = entry.Open()) checksum = CopyUpdater(contents, null, entry.Length);
        if (MatchesUpdater(current, entry.Length, checksum))
        {
            _manager.WaitExitThenApplyUpdates(package);
            return;
        }

        FileSecurityPreservation.CheckReplacementMetadata(current.SafeFileHandle);
        using var stagedHandle = UninstallPathGuard.CreateStagedFile(UpdaterTemporaryPath(root), writeThrough: true, writeDacl: true);
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

            using var backupHandle = UninstallPathGuard.CreateStagedFile(UpdaterTemporaryPath(root), writeThrough: true, writeDacl: true);
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

    private async Task<OfficialPackage?> CheckLatestReleaseAsync(HttpClient client, SemanticVersion current, CancellationToken token)
    {
        using var latest = await OpenResponseAsync(client, UpdateSupport.OfficialLatestRelease, _ => false, token).ConfigureAwait(false);
        if (latest.StatusCode == HttpStatusCode.NotFound) return null;
        if (latest.StatusCode == HttpStatusCode.Forbidden && (latest.Headers.RetryAfter is not null
            || latest.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.Any(value => value.Trim() == "0")))
            throw new HttpRequestException("Достигнут лимит проверок обновлений.", null, HttpStatusCode.TooManyRequests);
        var metadataBytes = await ReadMetadataAsync(latest, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(metadataBytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var (tag, version) = UpdateSupport.ParseStableRelease(root);
        if (version <= current) return null;
        var feedUri = GetReleaseAsset(root, tag, $"releases.{_locator.Channel ?? "win"}.json");
        using var response = await OpenResponseAsync(client, feedUri, target => IsAssetRedirect(feedUri, target), token).ConfigureAwait(false);
        var feedBytes = await ReadMetadataAsync(response, token).ConfigureAwait(false);
        var feed = VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(feedBytes).TrimStart('\uFEFF'));
        var fullAssets = feed.Assets.Where(asset => asset.Type == VelopackAssetType.Full).ToArray();
        if (fullAssets.Length == 0) return null;
        var candidate = fullAssets.SingleOrDefault(asset => string.Equals(asset.Version?.ToString(), version.ToString(), StringComparison.Ordinal))
            ?? throw new InvalidDataException("Версия пакета не совпадает с последним официальным выпуском.");
        ValidateRelease(candidate);
        return new(candidate, GetReleaseAsset(root, tag, candidate.FileName));
    }

    private static Uri GetReleaseAsset(JsonElement release, string tag, string fileName)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Список файлов официального выпуска отсутствует.");
        var expectedUri = new Uri($"{UpdateSupport.OfficialRepository.AbsoluteUri}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(fileName)}");
        Uri? selected = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !string.Equals(name.GetString(), fileName, StringComparison.Ordinal)) continue;
            if (selected is not null || !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || !IsSecure(uri)
                || !uri.AbsoluteUri.Equals(expectedUri.AbsoluteUri, StringComparison.Ordinal))
                throw new InvalidDataException("Адрес файла не совпадает с последним официальным выпуском.");
            selected = expectedUri;
        }
        return selected ?? throw new InvalidDataException("Файл отсутствует в последнем официальном выпуске.");
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

    private static async Task<byte[]> ReadMetadataAsync(HttpResponseMessage response, CancellationToken token)
    {
        const int maximumBytes = 2 * 1024 * 1024;
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length is > maximumBytes) throw new InvalidDataException("Метаданные обновления слишком велики.");
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
