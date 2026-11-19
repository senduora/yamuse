using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

// The factory accepts known-folder anchors only. Production supplies Windows'
// known folders; fixtures supply an isolated tree. A caller cannot select a
// deletion target, install root, relative path, or parent directory.
internal sealed class UninstallPaths
{
    internal string LocalData { get; }
    internal string Documents { get; }
    internal string Data { get; }
    internal string Music { get; }
    internal string LegacyData { get; }
    internal string LegacyMusic { get; }
    internal string Install { get; }
    internal string Updater => Path.Combine(Install, "Update.exe");
    internal string Executable => Path.Combine(Install, "current", "YMusic.Native.exe");
    private readonly Func<string, DriveType>? _classifyDrive;

    private UninstallPaths(string localData, string documents, Func<string, DriveType>? classifyDrive)
    {
        LocalData = UninstallPathGuard.Canonical(localData);
        Documents = UninstallPathGuard.Canonical(documents, allowVolumeRoot: true);
        _classifyDrive = classifyDrive;
        EnsureLocalDocuments();
        if (UninstallPathGuard.Contains(LocalData, Documents) || UninstallPathGuard.Contains(Documents, LocalData))
            throw new IOException("Системные папки пересекаются. Удаление остановлено.");
        var brand = BrandPaths.FromKnownFolders(LocalData, Documents);
        Data = brand.DataDirectory;
        Music = brand.MusicDirectory;
        LegacyData = brand.LegacyDataDirectory;
        LegacyMusic = brand.LegacyMusicDirectory;
        Install = Path.Combine(LocalData, BrandPaths.LegacyPackageId);
    }

    internal static UninstallPaths FromKnownFolders(string localData, string documents, Func<string, DriveType>? classifyDrive = null)
        => new(localData, documents, classifyDrive);
    internal static UninstallPaths ForCurrentUser() => FromKnownFolders(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify));

    internal UninstallPathGuard.AncestorLease HoldDocumentsAncestors()
    {
        EnsureLocalDocuments();
        return UninstallPathGuard.HoldExistingAncestors(Path.GetDirectoryName(Music)!);
    }

    private void EnsureLocalDocuments()
    {
        if (!UninstallPathGuard.IsLocalVolume(Documents, _classifyDrive))
            throw new IOException("Для полного удаления нужна доступная локальная папка «Документы».");
    }
}

internal sealed record UninstallFileIdentity(ulong Volume, ulong Low, ulong High);
internal sealed class EmptyFileIdentityException() : IOException("Не удалось определить идентификатор файла или папки.");
internal sealed class DocumentsIdentityNotSupportedException(Exception inner) : IOException(
    "Файловая система папки «Документы» не предоставляет устойчивый идентификатор каталога. Для полного удаления нужна локальная папка «Документы» на поддерживаемой файловой системе, например NTFS.",
    inner);
internal sealed record UninstallIssue(string Area, string RelativePath, string Reason);
internal sealed record UninstallCleanupResult(int RemovedFiles, int PreservedForeignFiles, IReadOnlyList<UninstallIssue> Issues)
{
    internal bool Complete => Issues.Count == 0;
}

internal static class UninstallCleanup
{
    // Exact reserved primary slots used by the client in either profile.
    private static readonly Dictionary<string, Func<SafeFileHandle, bool>> ProfileFiles = new(StringComparer.Ordinal)
    {
        ["session.dpapi"] = SessionStore.IsOwnedFile,
        ["settings.json"] = PreferencesStore.IsOwnedFile,
        [PlaybackStateStore.FileName] = PlaybackStateStore.IsOwnedFile,
        [DownloadQueueStore.FileName] = DownloadQueueStore.IsOwnedFile,
        ["list-sorting.json"] = ListSortStore.IsOwnedFile,
        ["update-schedule.json"] = UpdateScheduleStore.IsOwnedFile
    };
    internal static UninstallCleanupResult Execute(UninstallPaths paths, UninstallFileIdentity? admittedDocuments = null,
        Func<SafeFileHandle, UninstallFileIdentity>? identifyDocuments = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var issues = new List<UninstallIssue>();
        var removed = 0;
        var preserved = 0;
        // Independent paths remain independent: a failed music preflight must
        // not become permission to follow a junction in the data tree.
        try
        {
            // A missing library below a checked Documents folder is empty. A missing
            // Documents folder can hide the entire library until it is available again.
            using var parents = paths.HoldDocumentsAncestors();
            using var documents = UninstallPathGuard.HoldDirectory(paths.Documents);
            if (admittedDocuments is not null && UninstallPathGuard.RequireDocumentsIdentity(documents, identifyDocuments) != admittedDocuments)
            {
                issues.Add(new("Музыка", "", "Папка «Документы» изменилась. Верните подтверждённую папку или создайте новый запрос полного удаления."));
                return new(removed, preserved, issues.AsReadOnly());
            }
            CleanMusic(paths.Music, "Музыка");
            CleanMusic(paths.LegacyMusic, "Музыка (предыдущая версия)");
        }
        catch (DocumentsIdentityNotSupportedException error)
        {
            issues.Add(new("Музыка", "", error.Message));
            return new(removed, preserved, issues.AsReadOnly());
        }
        catch (Exception error) when (Expected(error))
        {
            issues.Add(new("Музыка", "", "Не удалось проверить папку «Документы». " + Explain(error)));
        }
        CleanData(paths.Data, "Данные");
        CleanData(paths.LegacyData, "Данные (предыдущая версия)");
        return new(removed, preserved, issues.AsReadOnly());

        void CleanData(string data, string area)
        {
            try
            {
                using var parents = UninstallPathGuard.HoldAncestors(data);
                if (!parents.Complete || !UninstallPathGuard.EntryExists(data)) return;
                VisitProfileDirectory(data, data, area, () =>
                {
                    foreach (var (name, prove) in ProfileFiles)
                    {
                        try
                        {
                            var path = Path.Combine(data, name);
                            var recovery = new GuardedStoreRecovery(path);
                            if (!recovery.Pending && !UninstallPathGuard.EntryExists(path)) continue;
                            using var writer = GuardedStoreFile.HoldWriteGate(path);
                            removed += recovery.RemoveOwnedForUninstall(prove);
                            DeleteProfileFile(path, data, area, prove);
                        }
                        catch (Exception error) when (Expected(error)) { Add(area, name, error); }
                    }
                    foreach (var entry in Directory.EnumerateFileSystemEntries(data))
                    {
                        var name = Path.GetFileName(entry);
                        if (ProfileFiles.ContainsKey(name)) continue;
                        switch (name)
                        {
                            case "artwork":
                                VisitProfileDirectory(entry, data, area, () =>
                                {
                                    using var cache = new ArtworkCache(entry);
                                    var result = cache.ClearAsync(CancellationToken.None).GetAwaiter().GetResult();
                                    RecordCacheCleanup(entry, data, area, result);
                                });
                                break;
                            case "diagnostics":
                                VisitProfileDirectory(entry, data, area, () => CleanProfileFiles(entry, data, area,
                                    IsAutomaticReportName, IsAutomaticReport));
                                break;
                            case "favorites-cache":
                                VisitProfileDirectory(entry, data, area, () => CleanProfileFiles(entry, data, area,
                                    IsFavoritesFileName, FavoritesCacheStore.IsOwnedFile));
                                break;
                            case "buffer":
                                VisitProfileDirectory(entry, data, area, () => CleanBuffer(entry, data, area));
                                break;
                            default:
                                preserved++;
                                break;
                        }
                    }
                });
            }
            catch (Exception error) when (Expected(error)) { Add(area, "", error); }
        }

        void VisitProfileDirectory(string path, string data, string area, Action clean)
        {
            try
            {
                const FileAttributes kind = FileAttributes.Directory | FileAttributes.ReparsePoint;
                if ((File.GetAttributes(path) & kind) != FileAttributes.Directory) { preserved++; return; }
                UninstallFileIdentity? identity;
                using (var directory = UninstallPathGuard.HoldDirectory(path))
                {
                    clean();
                    identity = IdentifyEmptyDirectory(path, directory, ref preserved);
                }
                RemoveEmptyCheckedDirectory(path, identity, ref preserved);
            }
            catch (Exception error) when (Expected(error)) { Add(area, Path.GetRelativePath(data, path), error); }
        }

        void CleanProfileFiles(string directory, string data, string area, Func<string, bool> knownName,
            Func<string, SafeFileHandle, bool> prove)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(path);
                if (knownName(name)) DeleteProfileFile(path, data, area, file => prove(name, file));
                else preserved++;
            }
        }

        void DeleteProfileFile(string path, string data, string area, Func<SafeFileHandle, bool> prove)
        {
            try
            {
                if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) { preserved++; return; }
                using var file = UninstallPathGuard.OpenOwnedFile(path, read: true);
                if (!prove(file) || FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(file))
                { preserved++; return; }
                UninstallPathGuard.DeleteByHandle(file);
                removed++;
            }
            catch (FileNotFoundException) { }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { }
            catch (Exception error) when (Expected(error)) { Add(area, Path.GetRelativePath(data, path), error); }
        }

        void CleanBuffer(string buffer, string data, string area)
        {
            // The recorder authenticates its receipt and compares the exact audio
            // object before removing anything. Unproved remnants are left alone.
            var leases = Path.Combine(buffer, AudioBufferRecovery.LeaseDirectory);
            if (UninstallPathGuard.EntryExists(leases))
                VisitProfileDirectory(leases, data, area, () =>
                {
                    var result = AudioBufferRecovery.RecoverForUninstall(buffer);
                    removed += result.Removed;
                    if (result.Failed > 0) Add(area, Path.GetRelativePath(data, leases), new IOException());
                    preserved += Directory.EnumerateFileSystemEntries(leases).Count();
                });
            foreach (var entry in Directory.EnumerateFileSystemEntries(buffer))
            {
                var name = Path.GetFileName(entry);
                if (name == AudioBufferRecovery.LeaseDirectory) continue;
                if (name == "loudness")
                    VisitProfileDirectory(entry, data, area, () => RecordCacheCleanup(entry, data, area,
                        LoudnessCache.ClearDirectoryAsync(entry, CancellationToken.None).GetAwaiter().GetResult()));
                else preserved++;
            }
        }

        void RecordCacheCleanup(string directory, string data, string area, CacheCleanupResult result)
        {
            removed += result.Removed;
            // Read back the remainder, including items with no producer proof.
            preserved += Directory.EnumerateFileSystemEntries(directory).Count();
            if (result.Failed > 0) Add(area, Path.GetRelativePath(data, directory), new IOException());
        }

        void CleanMusic(string music, string area)
        {
            try
            {
                var parentPath = Path.GetDirectoryName(music)!;
                if (!UninstallPathGuard.EntryExists(parentPath)) return;
                UninstallFileIdentity? parentIdentity;
                using (var parent = UninstallPathGuard.HoldDirectory(parentPath))
                {
                    if (UninstallPathGuard.EntryExists(music))
                    {
                        UninstallFileIdentity? musicIdentity;
                        using (var directory = UninstallPathGuard.HoldDirectory(music))
                        {
                            CleanMusicEntries(music, area);
                            musicIdentity = IdentifyEmptyDirectory(music, directory, ref preserved);
                        }
                        RemoveEmptyCheckedDirectory(music, musicIdentity, ref preserved);
                    }
                    parentIdentity = IdentifyEmptyDirectory(parentPath, parent, ref preserved);
                }
                // Never remove Documents or a nonempty application parent.
                RemoveEmptyCheckedDirectory(parentPath, parentIdentity, ref preserved);
            }
            catch (Exception error) when (Expected(error)) { Add(area, "", error); }
        }

        void CleanMusicEntries(string music, string area)
        {
            // Execute is already called on the uninstall worker. An authentic interrupted
            // cleanup repairs its index before ownership-based removal.
            using var library = new OfflineLibrary(music, new AudioBuffer(Path.Combine(music, ".uninstall-unused-buffer")));
            library.RecoverPendingAsync().GetAwaiter().GetResult();
            var index = Path.Combine(music, "library.index.json");
            using var indexFile = UninstallPathGuard.EntryExists(index) ? UninstallPathGuard.OpenOwnedFile(index, read: true) : null;
            var rows = indexFile is null ? [] : library.ReadAudioIndex(indexFile);
            if (rows.GroupBy(row => row.FileName, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Select(row => (row.Bytes, row.Sha256.ToUpperInvariant())).Distinct().Count() != 1))
                throw new JsonException();
            var failed = false;
            foreach (var item in rows.DistinctBy(row => row.FileName, StringComparer.OrdinalIgnoreCase))
            {
                var path = Path.Combine(music, item.FileName);
                try
                {
                    if (!UninstallPathGuard.EntryExists(path)) continue;
                    using var file = UninstallPathGuard.OpenOwnedFile(path, read: true);
                    using var stream = new FileStream(file, FileAccess.Read);
                    if (stream.Length != item.Bytes || !Convert.ToHexString(SHA256.HashData(stream)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)
                        || FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(file))
                    { preserved++; continue; }
                    UninstallPathGuard.DeleteByHandle(file);
                    removed++;
                }
                catch (Exception error) when (Expected(error)) { failed = true; Add(area, item.FileName, error); }
            }
            // Keep the ownership index on a partial failure. Retrying is safe
            // because missing rows have no file to delete.
            if (!failed)
            {
                if (indexFile is not null)
                {
                    if (FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(indexFile)) preserved++;
                    else { UninstallPathGuard.DeleteByHandle(indexFile); removed++; }
                }
                DeletePlaylistIndex(Path.Combine(music, "playlists.index.json"), area);
            }
            preserved += Directory.EnumerateFileSystemEntries(music).Count(path =>
            {
                var name = Path.GetFileName(path);
                return !name.Equals("library.index.json", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("playlists.index.json", StringComparison.OrdinalIgnoreCase)
                    && !rows.Any(row => row.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
            });
        }

        void DeletePlaylistIndex(string path, string area)
        {
            try
            {
                if (!UninstallPathGuard.EntryExists(path)) return;
                using var file = UninstallPathGuard.OpenOwnedFile(path, read: true);
                if (FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(file) || !OfflineLibrary.IsSupportedPlaylistIndex(file))
                { preserved++; return; }
                UninstallPathGuard.DeleteByHandle(file);
                removed++;
            }
            catch (Exception error) when (Expected(error)) { Add(area, Path.GetFileName(path), error); }
        }

        void Add(string area, string path, Exception error) => issues.Add(new(area, path, Explain(error)));
    }

    private static bool IsFavoritesFileName(string name)
        => name.Length == 69 && name.EndsWith(".json", StringComparison.Ordinal) && name.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool IsAutomaticReportName(string name) => TryAutomaticReportProcessId(name, out _);

    private static bool TryAutomaticReportProcessId(string name, out int processId)
    {
        processId = 0;
        if (!name.EndsWith(".json", StringComparison.Ordinal)) return false;
        var parts = name[..^5].Split('-');
        return parts.Length is 4 or 5 && parts[0] == "session"
            && DateTime.TryParseExact(parts[1] + "-" + parts[2], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out processId) && processId > 0
            && (parts.Length == 4 || Guid.TryParseExact(parts[4], "N", out _));
    }

    private static bool IsAutomaticReport(string name, SafeFileHandle file)
    {
        if (!TryAutomaticReportProcessId(name, out var pid) || GuardedStoreFile.ReadFromHandle(file, 16 * 1024 * 1024) is not { } bytes)
            return false;
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 12 });
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) && version == 1
                && root.TryGetProperty("processId", out var process) && process.ValueKind == JsonValueKind.Number && process.TryGetInt32(out var id) && id == pid
                && root.TryGetProperty("appVersion", out var app) && app.ValueKind == JsonValueKind.String
                && root.TryGetProperty("executableSha256", out var hash) && hash.ValueKind == JsonValueKind.String
                && hash.GetString() is { Length: 64 } sha && sha.All(Uri.IsHexDigit)
                && root.TryGetProperty("samples", out var samples) && samples.ValueKind == JsonValueKind.Array;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }

    private static UninstallFileIdentity? IdentifyEmptyDirectory(string path, SafeFileHandle directory, ref int preserved)
    {
        if (FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(directory)) { preserved++; return null; }
        if (Directory.EnumerateFileSystemEntries(path).Any()) return null;
        try { return UninstallPathGuard.GetStableFileIdentity(directory); }
        catch (Exception error) when (error is EmptyFileIdentityException or Win32Exception { NativeErrorCode: 1 or 50 or 87 or 124 })
        { preserved++; return null; }
    }

    private static void RemoveEmptyCheckedDirectory(string path, UninstallFileIdentity? expected, ref int preserved)
    {
        if (expected is null || !UninstallPathGuard.EntryExists(path)) return;
        using var empty = UninstallPathGuard.HoldDirectory(path, deleteAccess: true);
        if (UninstallPathGuard.GetStableFileIdentity(empty) != expected
            || FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(empty)) { preserved++; return; }
        // The disposition is nonrecursive and applies to the checked handle.
        UninstallPathGuard.DeleteByHandle(empty);
    }

    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception
        or JsonException or KeyNotFoundException or InvalidOperationException or CryptographicException or MusicException;
    private static string Explain(Exception error) => error is MusicException { Message: OfflineLibrary.RecoveryAuthenticationFailure }
        ? OfflineLibrary.RecoveryAuthenticationFailure
        : error is CryptographicException
        ? "Журнал восстановления очистки повреждён или не принадлежит этой папке. Музыкальные файлы сохранены."
        : error is JsonException or KeyNotFoundException or InvalidOperationException
        ? "Индекс музыки повреждён. Файлы сохранены."
        : error is UnauthorizedAccessException ? "Нет доступа к файлу или папке."
        : error is IOException io && io.Message.Contains("ссыл", StringComparison.OrdinalIgnoreCase) ? io.Message
        : "Файл занят, недоступен или папка изменилась. Закройте использующее его приложение и повторите.";
}

// Directory handles deny FILE_SHARE_DELETE for the entire operation. This
// prevents replacement of an already checked ancestor with a junction while
// enumerating children. File deletion uses the checked handle, not a new path.
internal static class UninstallPathGuard
{
    internal sealed class UnsafeStoragePathException(string message) : IOException(message) { }

    internal static string Canonical(string path, bool allowVolumeRoot = false, bool allowNetworkShare = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.IndexOf(':', 2) >= 0 || path.Any(char.IsControl)) throw new IOException("Некорректный путь удаления.");
        var result = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(result)!;
        if (result.StartsWith(@"\\", StringComparison.Ordinal) && (!allowNetworkShare
            || result.StartsWith(@"\\?\", StringComparison.Ordinal) || result.StartsWith(@"\\.\", StringComparison.Ordinal)
            || root.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length != 2))
            throw new IOException("Некорректный путь к файлу.");
        if (!allowVolumeRoot && result.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Корневая папка диска не может быть целью удаления.");
        return result;
    }

    internal static bool IsLocalVolume(string path, Func<string, DriveType>? classifyDrive = null)
    {
        var driveRoot = Path.GetPathRoot(path)!;
        var type = classifyDrive?.Invoke(driveRoot) ?? new DriveInfo(driveRoot).DriveType;
        return type is not (DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown);
    }

    internal static bool Contains(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    internal static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static AncestorLease HoldAncestors(string path, bool create = false, bool allowNetworkShare = false)
    {
        var handles = new List<IDisposable>();
        try
        {
            var ancestors = new Stack<string>();
            var parentPath = Path.GetDirectoryName(Canonical(path, allowNetworkShare: allowNetworkShare))!;
            for (var directory = new DirectoryInfo(parentPath); directory is not null; directory = directory.Parent)
                ancestors.Push(directory.FullName);
            while (ancestors.TryPop(out var directory))
            {
                if (!EntryExists(directory))
                {
                    if (!create) return new AncestorLease(handles, complete: false);
                    // Its immediate parent is already pinned. Validate the new
                    // entry before creating or opening anything beneath it.
                    Directory.CreateDirectory(directory);
                }
                handles.Add(HoldDirectory(directory));
            }
            return new AncestorLease(handles, complete: true);
        }
        catch { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); throw; }
    }

    internal static AncestorLease HoldExistingAncestors(string path)
    {
        var lease = HoldAncestors(path);
        if (lease.Complete) return lease;
        lease.Dispose();
        throw new DirectoryNotFoundException("Папка изменилась или отсутствует. Удаление остановлено.");
    }

    internal static SafeFileHandle HoldDirectory(string path, bool deleteAccess = false)
    {
        var handle = Open(path, 0x80000000u | (deleteAccess ? 0x10000u : 0), 1 | 2, 0x02000000 | 0x00200000);
        try { Validate(handle, directory: true); return handle; }
        catch { handle.Dispose(); throw; }
    }

    // Attributes alone are sufficient to keep a deferred startup probe.
    // Consumers still need a full directory lease before their operation.
    internal static bool CanInspectOrdinaryDirectory(string path)
    {
        const uint readAttributes = 0x80, shareAll = 7, directoryWithoutLinks = 0x02200000;
        try
        {
            using var directory = Open(path, readAttributes, shareAll, directoryWithoutLinks);
            Validate(directory, directory: true);
            return true;
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException) { return false; }
    }

    internal static SafeFileHandle OpenOwnedFile(string path, bool read, bool deleteAccess = true, bool writeAttributes = false)
    {
        const uint fileWriteAttributes = 0x100;
        var handle = Open(path, (read ? 0x80000000u : 0x80u) | (deleteAccess ? 0x10000u : 0)
            | (writeAttributes ? fileWriteAttributes : 0), 1, 0x00200000);
        try { Validate(handle, directory: false); return handle; }
        catch { handle.Dispose(); throw; }
    }

    internal static UninstallFileIdentity GetStableFileIdentity(SafeFileHandle handle)
    {
        const int fileIdInfo = 18;
        if (!GetFileIdInformation(handle, fileIdInfo, out var identity, (uint)Marshal.SizeOf<FileIdInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((identity.Low | identity.High) == 0) throw new EmptyFileIdentityException();
        return new(identity.Volume, identity.Low, identity.High);
    }

    internal static UninstallFileIdentity RequireDocumentsIdentity(SafeFileHandle handle,
        Func<SafeFileHandle, UninstallFileIdentity>? identify = null)
    {
        try { return (identify ?? GetStableFileIdentity)(handle); }
        catch (Exception error) when (error is EmptyFileIdentityException or Win32Exception { NativeErrorCode: 1 or 50 or 87 })
        { throw new DocumentsIdentityNotSupportedException(error); }
    }

    internal static SafeFileHandle CreateStagedFile(string path, bool asynchronous = false, bool writeThrough = false,
        bool writeDacl = false, bool deleteOnClose = false)
    {
        const uint readWriteDelete = 0xC0010000, openReparsePoint = 0x00200000;
        const uint writeDaclAccess = 0x00040000;
        const uint overlapped = 0x40000000, flushWrites = 0x80000000, discardOnClose = 0x04000000;
        return Open(path, readWriteDelete | (writeDacl ? writeDaclAccess : 0), 0, openReparsePoint | (asynchronous ? overlapped : 0)
            | (writeThrough ? flushWrites : 0) | (deleteOnClose ? discardOnClose : 0), disposition: 1);
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint flags, uint disposition = 3)
    {
        var handle = CreateFile(ToWin32Path(path), access, share, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return handle;
    }

    // The managed file APIs add this prefix themselves. Keep canonical paths
    // for validation and comparisons, and extend only at the native boundary.
    internal static string ToWin32Path(string path)
    {
        if (path.Length < 260) return path;
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
    }

    private static void Validate(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out var information, (uint)Marshal.SizeOf<AttributeTag>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((information.Attributes & 0x400) != 0) throw new UnsafeStoragePathException("Папка или файл содержит ссылку. Ссылка и внешние данные сохранены.");
        if (((information.Attributes & 0x10) != 0) != directory) throw new UnsafeStoragePathException("Тип объекта изменился. Удаление остановлено.");
    }

    internal static void DeleteByHandle(SafeFileHandle handle) => SetDeleteDisposition(handle, delete: true);

    internal static void SetDeleteDisposition(SafeFileHandle handle, bool delete)
    {
        var disposition = new Disposition { Delete = delete ? (byte)1 : (byte)0 };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<Disposition>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void KeepFileOnClose(SafeFileHandle handle)
    {
        const int FileDispositionInfoEx = 21;
        // ON_CLOSE without DELETE clears the disposition set when opening the handle.
        uint flags = 8;
        if (!SetFileInformationByHandle(handle, FileDispositionInfoEx, ref flags, sizeof(uint)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void UnlinkByHandle(SafeFileHandle handle)
    {
        const int FileDispositionInfoEx = 21;
        const uint Delete = 1, PosixSemantics = 2;
        uint flags = Delete | PosixSemantics;
        if (!SetFileInformationByHandle(handle, FileDispositionInfoEx, ref flags, sizeof(uint)))
            DeleteByHandle(handle);
    }

    internal static void DiscardStagedFile(SafeFileHandle handle)
    {
        try { DeleteByHandle(handle); }
        catch (Win32Exception) { }
    }

    internal sealed class AncestorLease(List<IDisposable> handles, bool complete) : IDisposable
    {
        internal bool Complete => complete;
        public void Dispose() { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { internal uint Attributes; internal uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInformation { internal ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct Disposition { internal byte Delete; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdInformation(SafeFileHandle handle, int kind, out FileIdInformation information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref Disposition information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref uint information, uint size);
}
