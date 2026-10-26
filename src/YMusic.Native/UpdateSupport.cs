using System.Reflection;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace YMusic.Native;

internal enum AppUpdateStatus
{
    NotConfigured, Portable, Ready, Checking, NoUpdate, Available, Downloading, Downloaded, Error
}

internal sealed record AppUpdateState(AppUpdateStatus Status, bool FeedConfigured, bool IsInstalled,
    string CurrentVersion, string? AvailableVersion, int? ProgressPercent, string Message);

// The window owns scheduling and all user interactions. This service has no
// timer, automatic download/application, or account storage access.
internal sealed class UpdateSupport
{
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

    // Injection is limited to deterministic fixtures; production only uses a
    // canonical, public github.com repository and Velopack's built-in source.
    internal UpdateSupport(string configurationPath, bool? installed = null,
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
                if (!string.Equals(_repository!.AbsoluteUri.TrimEnd('/'), "https://github.com/senduora/yamuse", StringComparison.Ordinal))
                    return Set(AppUpdateStatus.Error, "Проверка переносимой версии доступна только для официального проекта Yamuse.");
                Set(AppUpdateStatus.Checking, "Проверяем обновления…");
                var release = await _portableCheck(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return release is null ? Set(AppUpdateStatus.NoUpdate, "Используется последняя доступная версия.")
                    : Set(AppUpdateStatus.Available, "Доступна новая версия. Откройте официальный выпуск для скачивания.", release);
            }
            if (State.Status == AppUpdateStatus.Downloaded) return State;
            _backend ??= _backendFactory(_repository!);
            if (_backend.PendingVersion is { } pending)
                return Set(AppUpdateStatus.Downloaded, "Обновление загружено. Установите его, когда закончите слушать.", pending, 100);
            Set(AppUpdateStatus.Checking, "Проверяем обновления…");
            // The SDK check API has no CancellationToken. Keep the operation
            // serialized until it finishes; do not abandon a request in flight.
            var version = await _backend.CheckAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return version is null
                ? Set(AppUpdateStatus.NoUpdate, "Установлена последняя доступная версия.")
                : Set(AppUpdateStatus.Available, "Доступна новая версия. Можно скачать её без остановки музыки.", version);
        }
        catch (OperationCanceledException)
        {
            _state = before with { Message = "Проверка обновлений отменена." };
            return State;
        }
        catch (Exception)
        {
            return Set(AppUpdateStatus.Error, "Не удалось проверить обновления. Повторите позже.");
        }
        finally { _operation.Release(); }
    }

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
            cancellationToken.ThrowIfCancellationRequested();
            return Set(AppUpdateStatus.Downloaded, "Обновление загружено. Установите его, когда закончите слушать.", version, 100);
        }
        catch (OperationCanceledException)
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

    private AppUpdateState Set(AppUpdateStatus status, string message, string? version = null, int? percent = null)
        => _state = State with { Status = status, Message = message, AvailableVersion = version, ProgressPercent = percent };

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

    private static (Uri? Repository, bool Invalid) ReadRepository(string path)
    {
        try
        {
            if (!File.Exists(path)) return (null, false);
            var file = new FileInfo(path);
            if (file.Length is <= 0 or > 16 * 1024) return (null, true);
            var configuration = JsonSerializer.Deserialize<UpdateFeedConfiguration>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            });
            var text = configuration?.GithubRepository;
            if (string.IsNullOrWhiteSpace(text)) return (null, false);
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return (null, true);
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part is "." or ".."
                || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')))
                return (null, true);
            return (new Uri($"https://github.com/{parts[0]}/{parts[1]}"), false);
        }
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
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/senduora/yamuse/releases/latest");
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
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
            || !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Release metadata is invalid.");
        var text = tag.GetString();
        if (text is null || text.Length is 0 or > 64 || !SemanticVersion.TryParse(text.TrimStart('v', 'V'), out var version)
            || version.IsPrerelease || !SemanticVersion.TryParse(currentVersion, out var current))
            throw new InvalidDataException("Release version is invalid.");
        return version > current ? version.ToString() : null;
    }
}

internal interface IAppUpdateBackend
{
    string? PendingVersion { get; }
    Task<string?> CheckAsync();
    Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken);
    void ApplyAndRestart();
}

internal sealed class VelopackUpdateBackend : IAppUpdateBackend
{
    private readonly UpdateManager _manager;
    private UpdateInfo? _update;
    internal VelopackUpdateBackend(Uri repository)
        => _manager = new UpdateManager(new GithubSource(repository.AbsoluteUri, null, false));
    public string? PendingVersion => _manager.UpdatePendingRestart?.Version.ToString();
    public async Task<string?> CheckAsync()
    {
        _update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return _update?.TargetFullRelease.Version.ToString();
    }
    public Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
        => _manager.DownloadUpdatesAsync(_update ?? throw new InvalidOperationException("Нет выбранного обновления."), progress, cancellationToken);
    public void ApplyAndRestart()
    {
        var package = _manager.UpdatePendingRestart ?? throw new InvalidOperationException("Обновление ещё не загружено.");
        _manager.ApplyUpdatesAndRestart(package);
    }
}
