using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

internal sealed record OfflineCleanupProgress(int Verified, int Total, int Removed, int Preserved, int Failed,
    long VerifiedBytes, long TotalBytes, string Phase);
internal sealed record CacheCleanupResult(int Removed, int Preserved, int Failed, long RemovedBytes);

// Rename the already checked object rather than reopening its pathname. The
// caller keeps READ|DELETE access and denies writers/deleters until completion.
internal static class ManagedCacheCleanup
{
    internal static void RenameByHandle(SafeFileHandle handle, string destination)
    {
        var name = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        var nameOffset = Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileName)).ToInt32();
        // FILE_RENAME_INFO.FileName is a NUL-terminated WCHAR string even
        // though FileNameLength excludes that terminator.
        var size = checked(nameOffset + name.Length + sizeof(char));
        var information = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < nameOffset; i++) Marshal.WriteByte(information, i, 0);
            Marshal.WriteInt32(information, Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileNameLength)).ToInt32(), name.Length);
            Marshal.Copy(name, 0, information + nameOffset, name.Length);
            Marshal.WriteInt16(information, nameOffset + name.Length, 0);
            // ReplaceIfExists is false: neither staging nor recovery overwrites
            // a file created by somebody else.
            if (!SetFileInformationByHandle(handle, 3, information, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(information); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RenameInformation
    {
        internal uint Flags;
        internal IntPtr RootDirectory;
        internal uint FileNameLength;
        internal char FileName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr information, uint size);
}
