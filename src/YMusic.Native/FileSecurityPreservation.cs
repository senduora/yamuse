using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

internal static class FileSecurityPreservation
{
    private const FileAttributes BasicAttributes = FileAttributes.Normal | FileAttributes.Archive | FileAttributes.Hidden
        | FileAttributes.System | FileAttributes.NotContentIndexed;
    private const FileAttributes ReplacementAttributes = BasicAttributes | FileAttributes.Compressed;
    private const uint SetCompression = 0x0009c040;
    private const ushort CompressionNone = 0, CompressionDefault = 1;
    private const uint FileObject = 1;
    private const int FileIdInfo = 18;
    private const uint PersistentAcls = 0x00000008;
    private const uint SupportsObjectIds = 0x00010000;
    private const uint NamedStreams = 0x00040000;
    private const uint ExtendedAttributes = 0x00800000;
    private const uint OwnerAndGroupInformation = 0x00000003;
    private const uint ResourceAttributeInformation = 0x00000020;
    private const uint DaclInformation = 0x00000004;
    private const uint ProtectedDaclInformation = 0x80000000;
    private const uint UnprotectedDaclInformation = 0x20000000;
    private const ushort DaclProtected = 0x1000;

    internal static void ValidateReplacementAttributes(FileAttributes attributes)
    {
        if ((attributes & ~ReplacementAttributes) != 0) throw Unsupported();
    }

    internal static void CheckReplacementMetadata(SafeFileHandle source)
    {
        ValidateReplacementAttributes(ReadBasicInformation(source).Attributes);
        var fileSystem = GetVolumeFlags(source);
        if ((fileSystem & NamedStreams) != 0) CheckStreams(source);
        if ((fileSystem & SupportsObjectIds) != 0) CheckObjectId(source);
        if ((fileSystem & ExtendedAttributes) != 0) CheckExtendedAttributes(source);
        if ((fileSystem & PersistentAcls) != 0) CheckResourceAttributes(source);
    }

    internal static void PrepareReplacement(SafeFileHandle source, SafeFileHandle destination)
    {
        // A stage can inherit EFS or compression from its parent. Reject EFS
        // and match the original's native compression before writing any bytes.
        var original = ReadBasicInformation(source).Attributes;
        var staged = ReadBasicInformation(destination).Attributes;
        ValidateReplacementAttributes(original);
        ValidateReplacementAttributes(staged);
        PreserveCompression(destination, original, staged);
        if ((GetVolumeFlags(source) & PersistentAcls) == 0) return;
        CheckOwnerAndGroup(source, destination);
        CopyDacl(source, destination);
    }

    internal static FileRevision GetRevision(SafeFileHandle file)
    {
        if (!GetFileInformationByHandleEx(file, FileIdInfo, out FileIdentity identity, (uint)Marshal.SizeOf<FileIdentity>()))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is not (1 or 50 or 87 or 124)) throw DaclError(error);
            // Older local file systems may only expose a volume serial plus
            // the 64-bit native index. The original remains held meanwhile.
            if (!GetFileInformationByHandle(file, out var legacy)) throw LastIoError();
            identity = new() { Volume = legacy.Volume, Low = ((ulong)legacy.IndexHigh << 32) | legacy.IndexLow };
        }
        return ReadRevision(file, identity);
    }

    internal static FileRevision? GetPersistentRevision(SafeFileHandle file)
    {
        if (GetFileInformationByHandleEx(file, FileIdInfo, out FileIdentity identity, (uint)Marshal.SizeOf<FileIdentity>()))
            return (identity.Low | identity.High) == 0 ? null : ReadRevision(file, identity);

        // The legacy file index is sufficient for a held object, but cannot authorize deleting a file in a later process.
        var error = Marshal.GetLastWin32Error();
        return error is 1 or 50 or 87 or 124 ? null : throw DaclError(error);
    }

    private static FileRevision ReadRevision(SafeFileHandle file, FileIdentity identity)
    {
        var basic = ReadBasicInformation(file);
        return new(identity.Volume, identity.Low, identity.High, RandomAccess.GetLength(file), basic.Created, basic.Written,
            basic.Changed, basic.Attributes);
    }

    internal static void PreserveBasicInformation(SafeFileHandle source, SafeFileHandle destination)
    {
        var original = ReadBasicInformation(source);
        // Compression is managed separately by FSCTL_SET_COMPRESSION.
        if ((original.Attributes & FileAttributes.Compressed) != (ReadBasicInformation(destination).Attributes & FileAttributes.Compressed))
            throw Unsupported();
        var attributes = original.Attributes & BasicAttributes;
        var value = new BasicInformation { Created = original.Created, Attributes = attributes == 0 ? FileAttributes.Normal : attributes };
        if (!SetFileInformationByHandle(destination, 0, ref value, (uint)Marshal.SizeOf<BasicInformation>()))
            throw LastIoError();
    }

    private static void PreserveCompression(SafeFileHandle destination, FileAttributes original, FileAttributes staged)
    {
        var expected = original & FileAttributes.Compressed;
        if ((staged & FileAttributes.Compressed) == expected) return;
        // NTFS uses LZNT1 for COMPRESSION_FORMAT_DEFAULT.
        var mode = expected == 0 ? CompressionNone : CompressionDefault;
        if (!DeviceIoControl(destination, SetCompression, ref mode, sizeof(ushort), IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("Не удалось сохранить режим сжатия файла.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((ReadBasicInformation(destination).Attributes & FileAttributes.Compressed) != expected) throw Unsupported();
    }

    private static BasicInformation ReadBasicInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 0, out BasicInformation result, (uint)Marshal.SizeOf<BasicInformation>()))
            throw LastIoError();
        return result;
    }

    private static uint GetVolumeFlags(SafeFileHandle source)
    {
        if (!GetVolumeInformationByHandle(source, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, out var flags, IntPtr.Zero, 0))
            throw LastIoError();
        return flags;
    }

    private static void CheckStreams(SafeFileHandle source)
    {
        const int streamNameOffset = 24;
        const string defaultStream = "::$DATA";
        var info = new byte[1024];
        if (!GetFileInformationByHandleEx(source, 7, info, (uint)info.Length))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 38) return; // ERROR_HANDLE_EOF: no data streams.
            if (error is 122 or 234) throw Unsupported();
            throw DaclError(error);
        }
        if (BitConverter.ToUInt32(info) != 0 || BitConverter.ToUInt32(info, 4) != defaultStream.Length * sizeof(char)
            || System.Text.Encoding.Unicode.GetString(info, streamNameOffset, defaultStream.Length * sizeof(char)) != defaultStream)
            throw Unsupported();
    }

    private static void CheckObjectId(SafeFileHandle source)
    {
        const uint getObjectId = 0x0009009c;
        var info = new byte[64];
        if (DeviceIoControl(source, getObjectId, IntPtr.Zero, 0, info, (uint)info.Length, out _, IntPtr.Zero)) throw Unsupported();
        var error = Marshal.GetLastWin32Error();
        if (error is not (2 or 1168 or 4312)) throw DaclError(error); // No object ID assigned to this file.
    }

    private static void CheckExtendedAttributes(SafeFileHandle source)
    {
        const int fileEaInformation = 7;
        var status = NtQueryInformationFile(source, out _, out var size, sizeof(uint), fileEaInformation);
        if (status < 0) throw DaclError((int)RtlNtStatusToDosError(status));
        if (size != 0) throw Unsupported();
    }

    private static void CheckResourceAttributes(SafeFileHandle source)
    {
        var result = GetSecurityInfo(source, FileObject, ResourceAttributeInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            out var attributes, out var descriptor);
        if (result != 0) throw DaclError((int)result);
        try
        {
            // ACL header AceCount is a WORD at byte offset 4. A null or empty
            // ACL has no resource properties requiring preservation.
            if (attributes != IntPtr.Zero && Marshal.ReadInt16(attributes, 4) != 0) throw Unsupported();
        }
        finally { _ = LocalFree(descriptor); }
    }

    private static void CheckOwnerAndGroup(SafeFileHandle source, SafeFileHandle destination)
    {
        var result = GetSecurityInfo(source, FileObject, OwnerAndGroupInformation, out var owner, out var group,
            IntPtr.Zero, IntPtr.Zero, out var descriptor);
        if (result != 0) throw DaclError((int)result);
        try
        {
            result = GetSecurityInfo(destination, FileObject, OwnerAndGroupInformation, out var stagedOwner, out var stagedGroup,
                IntPtr.Zero, IntPtr.Zero, out var stagedDescriptor);
            if (result != 0) throw DaclError((int)result);
            try
            {
                if (!SameSid(owner, stagedOwner) || !SameSid(group, stagedGroup)) throw Unsupported();
            }
            finally { _ = LocalFree(stagedDescriptor); }
        }
        finally { _ = LocalFree(descriptor); }
    }

    private static bool SameSid(IntPtr first, IntPtr second) => first == second
        || first != IntPtr.Zero && second != IntPtr.Zero && EqualSid(first, second);

    internal static void CopyDacl(SafeFileHandle source, SafeFileHandle destination)
    {
        var result = GetSecurityInfo(source, FileObject, DaclInformation, IntPtr.Zero, IntPtr.Zero, out var dacl,
            IntPtr.Zero, out var descriptor);
        if (result != 0) throw DaclError((int)result);
        try
        {
            if (!GetSecurityDescriptorControl(descriptor, out var control, out _))
                throw DaclError(Marshal.GetLastWin32Error());

            var protection = (control & DaclProtected) != 0 ? ProtectedDaclInformation : UnprotectedDaclInformation;
            result = SetSecurityInfo(destination, FileObject, DaclInformation | protection, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
            if (result != 0) throw DaclError((int)result);
        }
        finally { _ = LocalFree(descriptor); }
    }

    private static IOException DaclError(int error) => new("Не удалось сохранить права доступа к файлу.", new Win32Exception(error));
    private static IOException LastIoError() => DaclError(Marshal.GetLastWin32Error());
    private static IOException Unsupported() => new("Не удалось перенести специальные свойства файла. Исходный файл сохранён.");

    internal readonly record struct FileRevision(ulong Volume, ulong Low, ulong High, long Length, long Created, long Written,
        long Changed, FileAttributes Attributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity { internal ulong Volume, Low, High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LegacyFileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        internal uint Volume, SizeHigh, SizeLow, LinkCount, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        internal long Created, Accessed, Written, Changed;
        internal FileAttributes Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { internal IntPtr Status, Information; }

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInfo,
        IntPtr owner, IntPtr group, out IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInfo,
        out IntPtr owner, out IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualSid(IntPtr first, IntPtr second);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorControl(IntPtr descriptor, out ushort control, out uint revision);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out FileIdentity information, uint length);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out LegacyFileInformation information);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out BasicInformation information, uint length);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, [Out] byte[] information, uint length);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref BasicInformation information, uint length);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandle(SafeFileHandle file, IntPtr volume, uint volumeSize, IntPtr serial,
        IntPtr maxName, out uint flags, IntPtr fileSystem, uint fileSystemSize);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inputLength,
        [Out] byte[] output, uint outputLength, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, ref ushort input, uint inputLength,
        IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock status, out uint information,
        uint length, int informationClass);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
}
