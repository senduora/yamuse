using System.ComponentModel;
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

    private UninstallPaths(string localData, string documents)
    {
        LocalData = UninstallPathGuard.Canonical(localData);
        Documents = UninstallPathGuard.Canonical(documents);
        if (UninstallPathGuard.Contains(LocalData, Documents) || UninstallPathGuard.Contains(Documents, LocalData))
            throw new IOException("Системные папки пересекаются. Удаление остановлено.");
        var brand = BrandPaths.FromKnownFolders(LocalData, Documents);
        Data = brand.DataDirectory;
        Music = brand.MusicDirectory;
        LegacyData = brand.LegacyDataDirectory;
        LegacyMusic = brand.LegacyMusicDirectory;
        Install = Path.Combine(LocalData, BrandPaths.LegacyPackageId);
    }

    internal static UninstallPaths FromKnownFolders(string localData, string documents) => new(localData, documents);
    internal static UninstallPaths ForCurrentUser() => FromKnownFolders(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
}

internal sealed record UninstallIssue(string Area, string RelativePath, string Reason);
internal sealed record UninstallCleanupResult(int RemovedFiles, int PreservedForeignFiles, IReadOnlyList<UninstallIssue> Issues)
{
    internal bool Complete => Issues.Count == 0;
}

internal static class UninstallCleanup
{
    internal static UninstallCleanupResult Execute(UninstallPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var issues = new List<UninstallIssue>();
        var removed = 0;
        var preserved = 0;
        // Independent paths remain independent: a failed music preflight must
        // not become permission to follow a junction in the data tree.
        CleanMusic(paths.Music, "Музыка");
        CleanMusic(paths.LegacyMusic, "Музыка (предыдущая версия)");
        CleanData(paths.Data, "Данные");
        CleanData(paths.LegacyData, "Данные (предыдущая версия)");
        return new(removed, preserved, issues.AsReadOnly());

        void CleanData(string data, string area)
        {
            try
            {
                using var parents = UninstallPathGuard.HoldAncestors(data);
                if (parents.Complete) DeleteOwnedDirectory(data, area, data, issues, ref removed);
                if (UninstallPathGuard.EntryExists(data))
                    issues.Add(new(area, "", "Папка данных удалена не полностью."));
            }
            catch (Exception error) when (Expected(error)) { Add(area, "", error); }
        }

        void CleanMusic(string music, string area)
        {
            try
            {
                using (var parents = UninstallPathGuard.HoldAncestors(music))
                {
                if (!parents.Complete) return;
                if (!UninstallPathGuard.EntryExists(music)) return;
                using (var directory = UninstallPathGuard.HoldDirectory(music))
                {
                    // Execute is already called on the uninstall worker. An
                    // authentic interrupted cleanup must repair its index before
                    // ownership-based removal; a bad journal blocks this tree.
                    using (var library = new OfflineLibrary(music, new AudioBuffer(Path.Combine(music, ".uninstall-unused-buffer"))))
                        library.RecoverPendingAsync().GetAwaiter().GetResult();
                    var index = Path.Combine(music, "library.index.json");
                    var rows = ReadIndex(index);
                    var failed = false;
                    foreach (var item in rows.DistinctBy(row => row.FileName, StringComparer.OrdinalIgnoreCase))
                    {
                        var path = Path.Combine(music, item.FileName);
                        try
                        {
                            if (!UninstallPathGuard.EntryExists(path)) continue;
                            using var file = UninstallPathGuard.OpenOwnedFile(path, read: true);
                            using var stream = new FileStream(file, FileAccess.Read);
                            if (stream.Length != item.Bytes || !Convert.ToHexString(SHA256.HashData(stream)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                            { preserved++; continue; }
                            UninstallPathGuard.DeleteByHandle(file);
                            removed++;
                        }
                        catch (Exception error) when (Expected(error)) { failed = true; Add(area, item.FileName, error); }
                    }
                    // Retain the complete index on a partial failure. Deleted
                    // rows are harmless on retry and provenance is not lost.
                    if (!failed)
                    {
                        DeleteKnownFile(index, area, music, issues, ref removed);
                        DeleteKnownFile(Path.Combine(music, "playlists.index.json"), area, music, issues, ref removed);
                    }
                    var remaining = Directory.EnumerateFileSystemEntries(music).ToArray();
                    preserved += remaining.Count(path => !rows.Any(row => row.FileName.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
                        && Path.GetFileName(path) is not "library.index.json" and not "playlists.index.json");
                }
                RemoveEmptyDirectory(music);
                }
                // Never remove Documents or a nonempty application parent.
                var parentPath = Path.GetDirectoryName(music)!;
                using (var parents = UninstallPathGuard.HoldAncestors(parentPath))
                {
                    if (!parents.Complete) return;
                    if (UninstallPathGuard.EntryExists(parentPath))
                    {
                        using (var parent = UninstallPathGuard.HoldDirectory(parentPath)) { }
                        RemoveEmptyDirectory(parentPath);
                    }
                }
            }
            catch (Exception error) when (Expected(error)) { Add(area, "", error); }
        }

        void Add(string area, string path, Exception error) => issues.Add(new(area, path, Explain(error)));
    }

    private sealed record IndexedAudio(string FileName, long Bytes, string Sha256);
    private static IReadOnlyList<IndexedAudio> ReadIndex(string path)
    {
        if (!UninstallPathGuard.EntryExists(path)) return [];
        using var file = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: false);
        using var stream = new FileStream(file, FileAccess.Read);
        if (stream.Length > 64 * 1024 * 1024) throw new IOException("Индекс музыки слишком большой. Файлы сохранены.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 48 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
        var result = new List<IndexedAudio>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var name = row.GetProperty("FileName").GetString();
            if (!row.GetProperty("Bytes").TryGetInt64(out var bytes)) throw new JsonException();
            var hash = row.GetProperty("Sha256").GetString();
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains(':')
                || name.Any(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c))
                || name.EndsWith('.') || name.EndsWith(' ') || name is "." or ".."
                || name.Equals("library.index.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("playlists.index.json", StringComparison.OrdinalIgnoreCase)
                || bytes <= 0 || hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new JsonException();
            result.Add(new(name, bytes, hash));
        }
        if (result.GroupBy(row => row.FileName, StringComparer.OrdinalIgnoreCase).Any(group => group.Select(row => (row.Bytes, row.Sha256.ToUpperInvariant())).Distinct().Count() != 1))
            throw new JsonException();
        return result;
    }

    private static void DeleteOwnedDirectory(string path, string area, string root, List<UninstallIssue> issues, ref int removed)
    {
        if (!UninstallPathGuard.EntryExists(path)) return;
        try
        {
            using (var directory = UninstallPathGuard.HoldDirectory(path))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.Directory) != 0)
                        DeleteOwnedDirectory(child, area, root, issues, ref removed);
                    else DeleteKnownFile(child, area, root, issues, ref removed);
                }
            }
            RemoveEmptyDirectory(path);
        }
        catch (Exception error) when (Expected(error)) { issues.Add(new(area, Path.GetRelativePath(root, path), Explain(error))); }
    }

    private static void DeleteKnownFile(string path, string area, string root, List<UninstallIssue> issues, ref int removed)
    {
        try
        {
            if (!UninstallPathGuard.EntryExists(path)) return;
            using var file = UninstallPathGuard.OpenOwnedFile(path, read: false);
            UninstallPathGuard.DeleteByHandle(file);
            removed++;
        }
        catch (Exception error) when (Expected(error)) { issues.Add(new(area, Path.GetRelativePath(root, path), Explain(error))); }
    }

    private static void RemoveEmptyDirectory(string path)
    {
        if (!UninstallPathGuard.EntryExists(path)) return;
        // Directory.Delete is deliberately nonrecursive. A last-moment link
        // substitution can only remove the link itself, never its target.
        if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path, recursive: false);
    }

    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception
        or JsonException or KeyNotFoundException or InvalidOperationException or CryptographicException or MusicException;
    private static string Explain(Exception error) => error is CryptographicException
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
    internal static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.IndexOf(':', 2) >= 0 || path.Any(char.IsControl)) throw new IOException("Некорректный путь удаления.");
        var result = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (result.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(result)!), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Корневая папка диска не может быть целью удаления.");
        return result;
    }

    internal static bool Contains(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static AncestorLease HoldAncestors(string path)
    {
        var handles = new List<IDisposable>();
        try
        {
            var ancestors = new Stack<string>();
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(Canonical(path))!); directory is not null; directory = directory.Parent)
                ancestors.Push(directory.FullName);
            while (ancestors.TryPop(out var directory))
            {
                if (!EntryExists(directory)) return new AncestorLease(handles, complete: false);
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

    internal static SafeFileHandle HoldDirectory(string path)
    {
        var handle = Open(path, 0x80000000, 1 | 2, 0x02000000 | 0x00200000);
        try { Validate(handle, directory: true); return handle; }
        catch { handle.Dispose(); throw; }
    }

    internal static SafeFileHandle OpenOwnedFile(string path, bool read, bool deleteAccess = true)
    {
        var handle = Open(path, (read ? 0x80000000u : 0x80u) | (deleteAccess ? 0x10000u : 0), 1, 0x00200000);
        try { Validate(handle, directory: false); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint flags)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, 3, flags, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return handle;
    }

    private static void Validate(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out var information, (uint)Marshal.SizeOf<AttributeTag>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((information.Attributes & 0x400) != 0) throw new IOException("Папка или файл содержит ссылку. Ссылка и внешние данные сохранены.");
        if (((information.Attributes & 0x10) != 0) != directory) throw new IOException("Тип объекта изменился. Удаление остановлено.");
    }

    internal static void DeleteByHandle(SafeFileHandle handle)
    {
        var disposition = new Disposition { Delete = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<Disposition>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal sealed class AncestorLease(List<IDisposable> handles, bool complete) : IDisposable
    {
        internal bool Complete => complete;
        public void Dispose() { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { internal uint Attributes; internal uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] private struct Disposition { internal byte Delete; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref Disposition information, uint size);
}
