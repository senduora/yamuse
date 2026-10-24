using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

// Fixed branded locations and the explicit compatibility boundary for releases
// that used the prototype's names. Package identity and mutex must stay stable.
internal sealed class BrandPaths
{
    internal const string LegacyInstanceMutex = @"Local\YMusic.Native.Prototype";
    internal const string LegacyPackageId = "YMusic.Native";
    private static readonly Lazy<BrandPaths> CurrentPaths = new(() => FromKnownFolders(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
    internal static BrandPaths Current => CurrentPaths.Value;

    internal string LocalData { get; }
    internal string Documents { get; }
    internal string DataDirectory { get; }
    internal string MusicDirectory { get; }
    internal string LegacyDataDirectory { get; }
    internal string LegacyMusicDirectory { get; }

    private BrandPaths(string localData, string documents)
    {
        LocalData = UninstallPathGuard.Canonical(localData);
        Documents = UninstallPathGuard.Canonical(documents);
        if (UninstallPathGuard.Contains(LocalData, Documents) || UninstallPathGuard.Contains(Documents, LocalData))
            throw new IOException("Системные папки пересекаются. Перенос данных остановлен.");
        DataDirectory = FixedChild(LocalData, "Yamuse");
        MusicDirectory = FixedChild(Documents, "Yamuse", "Music");
        LegacyDataDirectory = FixedChild(LocalData, "YMusic.Native.Prototype");
        LegacyMusicDirectory = FixedChild(Documents, "YMusic Native", "Music");
    }

    internal static BrandPaths FromKnownFolders(string localData, string documents) => new(localData, documents);

    private static string FixedChild(string root, params string[] parts)
    {
        var path = UninstallPathGuard.Canonical(Path.Combine([root, .. parts]));
        if (!UninstallPathGuard.Contains(root, path) || path.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Папка данных находится за пределами системной папки.");
        return path;
    }

    // Call only after acquiring LegacyInstanceMutex, before constructing stores
    // or MainWindow. Each tree is renamed on its original volume: no audio or
    // encrypted account file is read, copied, decrypted, rewritten or hashed.
    // All plans are preflighted before the first rename. A later I/O failure can
    // leave one completed rename; the next launch resumes the missing one.
    internal void MigrateLegacyData(Action<string>? beforeMove = null)
    {
        using (var data = MigrationPlan.Prepare(LegacyDataDirectory, DataDirectory))
        using (var music = MigrationPlan.Prepare(LegacyMusicDirectory, MusicDirectory))
        {
            data.Move(beforeMove);
            music.Move(beforeMove);
        }
        // The old Documents parent may also contain files unrelated to Music.
        // Remove it only when empty; failure here never invalidates a completed
        // data rename and is safe to retry on the next launch.
        try
        {
            var parent = Path.GetDirectoryName(LegacyMusicDirectory)!;
            using var ancestors = UninstallPathGuard.HoldAncestors(parent);
            if (ancestors.Complete && UninstallPathGuard.EntryExists(parent))
            {
                using var directory = OpenMigrationDirectory(parent);
                if (!Directory.EnumerateFileSystemEntries(parent).Any()) UninstallPathGuard.DeleteByHandle(directory);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { }
    }

    private sealed class MigrationPlan : IDisposable
    {
        private readonly string _source;
        private readonly string _target;
        private readonly List<IDisposable> _leases = [];
        private SafeFileHandle? _sourceHandle;
        private SafeFileHandle? _emptyTarget;

        private MigrationPlan(string source, string target) { _source = source; _target = target; }

        internal static MigrationPlan Prepare(string source, string target)
        {
            var plan = new MigrationPlan(source, target);
            try
            {
                // Leases pin all existing parents, rejecting redirected known
                // folders and nested junctions even on a fresh/no-op launch.
                plan._leases.Add(UninstallPathGuard.HoldAncestors(source));
                plan._leases.Add(UninstallPathGuard.HoldAncestors(target));
                var sourceExists = UninstallPathGuard.EntryExists(source);
                var targetExists = UninstallPathGuard.EntryExists(target);
                if (!sourceExists)
                {
                    if (targetExists) plan._leases.Add(UninstallPathGuard.HoldDirectory(target));
                    return plan; // No enumeration of a migrated 16 GB library.
                }
                plan._sourceHandle = OpenMigrationDirectory(source);
                if (targetExists)
                {
                    var destination = OpenMigrationDirectory(target);
                    plan._leases.Add(destination);
                    if (Directory.EnumerateFileSystemEntries(target).Any())
                    {
                        if (Directory.EnumerateFileSystemEntries(source).Any())
                            throw new IOException("Нельзя автоматически перенести данные: обе папки содержат файлы. Файлы сохранены.\n\n" + source + "\n" + target);
                        plan._sourceHandle.Dispose();
                        plan._sourceHandle = null;
                        return plan; // An empty legacy folder contains no data.
                    }
                    plan._emptyTarget = destination;
                }
                ValidateTree(source);
                return plan;
            }
            catch { plan.Dispose(); throw; }
        }

        private static void ValidateTree(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                // Never recurse through a link. Pin checked directories while
                // walking children; regular files require metadata checks only.
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Папка переноса содержит ссылку. Файлы и внешние данные сохранены.\n\n" + path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    using var child = UninstallPathGuard.HoldDirectory(path);
                    ValidateTree(path);
                }
            }
        }

        internal void Move(Action<string>? beforeMove)
        {
            if (_sourceHandle is null) return;
            beforeMove?.Invoke(_source);
            var parent = Path.GetDirectoryName(_target)!;
            Directory.CreateDirectory(parent);
            using var targetParents = UninstallPathGuard.HoldExistingAncestors(_target);
            if (_emptyTarget is not null)
            {
                // Delete only the checked, empty directory by handle. A file
                // created in it since preflight makes deletion fail safely.
                UninstallPathGuard.DeleteByHandle(_emptyTarget);
                _emptyTarget.Dispose();
                _emptyTarget = null;
            }
            RenameByHandle(_sourceHandle, _target); // ReplaceIfExists is false.
            _sourceHandle.Dispose();
            _sourceHandle = null;
        }

        public void Dispose()
        {
            _sourceHandle?.Dispose();
            foreach (var lease in _leases.AsEnumerable().Reverse()) lease.Dispose();
        }
    }

    // Rename the checked root through its own DELETE-capable handle while its
    // parents remain pinned. An external rename/link substitution is denied;
    // an unexpected existing destination is never replaced or merged.
    private static SafeFileHandle OpenMigrationDirectory(string path)
    {
        var handle = CreateFile(path, 0x10000 | 0x80 | 1, 1 | 2, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        try
        {
            if (!GetFileInformationByHandleEx(handle, 9, out var attributes, (uint)Marshal.SizeOf<AttributeTag>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((attributes.Attributes & 0x400) != 0 || (attributes.Attributes & 0x10) == 0)
                throw new IOException("Папка переноса содержит ссылку или изменилась. Данные сохранены.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void RenameByHandle(SafeFileHandle source, string target)
    {
        // Win32 normalizes an absolute FileName as a NUL-terminated path even
        // though FileNameLength excludes the terminator. Reserve it explicitly.
        var name = Encoding.Unicode.GetBytes(target + '\0');
        var rootOffset = IntPtr.Size;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var size = nameOffset + name.Length;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length - sizeof(char));
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            if (!SetFileInformationByHandle(source, 3, buffer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { internal uint Attributes; internal uint ReparseTag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr information, uint size);
}
