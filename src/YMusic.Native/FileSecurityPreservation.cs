using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    private const uint LabelAndScopeInformation = 0x00000010 | 0x00000040;
    private const uint ResourceAttributeInformation = 0x00000020;
    private const uint DaclInformation = 0x00000004;
    private const uint ProtectedDaclInformation = 0x80000000;
    private const uint UnprotectedDaclInformation = 0x20000000;
    private const ushort DaclProtected = 0x1000;

    internal enum ReplacementMetadata { Attributes, NamedStreams, ObjectId, ExtendedAttributes, ResourceAttributes, Compression, Security }

    internal sealed class UnsupportedMetadataException(ReplacementMetadata metadata)
        : IOException("Не удалось записать файл с его специальными свойствами. Существующие данные не изменены.")
    {
        internal ReplacementMetadata Metadata { get; } = metadata;
    }

    internal static void ValidateReplacementAttributes(FileAttributes attributes)
    {
        if ((attributes & ~ReplacementAttributes) != 0) throw new UnsupportedMetadataException(ReplacementMetadata.Attributes);
    }

    internal static void CheckReplacementMetadata(SafeFileHandle source)
        => CheckMetadata(source, allowUnknownVolume: false);

    internal static void CheckNewReportMetadata(SafeFileHandle source)
        => CheckMetadata(source, allowUnknownVolume: true);

    private static void CheckMetadata(SafeFileHandle source, bool allowUnknownVolume)
    {
        ValidateReplacementAttributes(ReadBasicInformation(source).Attributes);
        uint fileSystem;
        try { fileSystem = GetVolumeFlags(source); }
        catch (IOException error) when (allowUnknownVolume && IsUnsupportedCapability(error))
        { return; } // A new report has no preceding file or metadata to displace.
        if ((fileSystem & NamedStreams) != 0 && ProbeStreams(source)) throw new UnsupportedMetadataException(ReplacementMetadata.NamedStreams);
        if ((fileSystem & SupportsObjectIds) != 0) CheckObjectId(source);
        if ((fileSystem & ExtendedAttributes) != 0 && ProbeExtendedAttributes(source))
            throw new UnsupportedMetadataException(ReplacementMetadata.ExtendedAttributes);
        if ((fileSystem & PersistentAcls) != 0) CheckResourceAttributes(source);
    }

    internal static bool IsUnsupportedCapability(Exception? error)
    {
        while (error is not null)
        {
            if (error is Win32Exception { NativeErrorCode: 1 or 50 or 87 or 120 or 124 }) return true;
            error = error.InnerException;
        }
        return false;
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
        if ((GetVolumeFlags(source) & PersistentAcls) != 0)
        {
            CheckOwnerAndGroup(source, destination);
            CopyDacl(source, destination);
            // These components use READ_CONTROL. Check the prepared descriptor
            // after the DACL is applied, and refuse a policy we cannot preserve.
            if (!ReadLabelAndScope(source).AsSpan().SequenceEqual(ReadLabelAndScope(destination)))
                throw new UnsupportedMetadataException(ReplacementMetadata.Security);
        }
        CheckReplacementMetadata(destination);
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

    internal static (uint Volume, ulong FileReference)? TryGetNtfsFileReference(SafeFileHandle file)
    {
        // The legacy 64-bit file reference survives a rename on NTFS. FAT
        // directory positions and truncated ReFS identifiers cannot establish it.
        const int capacity = 261;
        var fileSystem = Marshal.AllocHGlobal(capacity * sizeof(char));
        try
        {
            if (!GetVolumeInformationByHandle(file, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, out _, fileSystem, capacity))
                throw LastIoError();
            if (!string.Equals(Marshal.PtrToStringUni(fileSystem), "NTFS", StringComparison.OrdinalIgnoreCase)) return null;
        }
        finally { Marshal.FreeHGlobal(fileSystem); }
        if (!GetFileInformationByHandle(file, out var legacy)) throw LastIoError();
        var reference = ((ulong)legacy.IndexHigh << 32) | legacy.IndexLow;
        return reference == 0 ? null : (legacy.Volume, reference);
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
            throw new UnsupportedMetadataException(ReplacementMetadata.Compression);
        var attributes = original.Attributes & BasicAttributes;
        var value = new BasicInformation { Created = original.Created, Attributes = attributes == 0 ? FileAttributes.Normal : attributes };
        if (!SetFileInformationByHandle(destination, 0, ref value, (uint)Marshal.SizeOf<BasicInformation>()))
            throw LastIoError();
    }

    internal static void RestoreArchiveAttribute(SafeFileHandle file, FileAttributes expected)
    {
        var current = ReadBasicInformation(file).Attributes;
        var archive = expected & FileAttributes.Archive;
        if ((current & FileAttributes.Archive) == archive) return;
        ValidateReplacementAttributes(current);
        var attributes = (current & BasicAttributes & ~(FileAttributes.Archive | FileAttributes.Normal)) | archive;
        var value = new BasicInformation { Attributes = attributes == 0 ? FileAttributes.Normal : attributes };
        if (!SetFileInformationByHandle(file, 0, ref value, (uint)Marshal.SizeOf<BasicInformation>()))
            throw LastIoError();
    }

    internal static void RestoreArchiveAfterRename(SafeFileHandle renamed, FileAttributes expected, bool preserveRevisionTimes = false,
        bool closeRenameHandle = true)
    {
        if ((expected & FileAttributes.Archive) != 0) return;
        const uint readWriteAttributes = 0x80 | 0x100, shareReadAndDelete = 1 | 4;
        // Closing the rename handle can itself set Archive. Reopen the same
        // object before that close, with no data-write or delete access.
        using var metadata = ReOpenFile(renamed, readWriteAttributes, shareReadAndDelete, 0);
        if (metadata.IsInvalid) throw LastIoError();
        if (preserveRevisionTimes) PinWriteAndChangeTimes(metadata);
        if (closeRenameHandle) renamed.Dispose();
        RestoreArchiveAttribute(metadata, expected);
    }

    internal static void PinWriteAndChangeTimes(SafeFileHandle stage)
    {
        // -1 disables implicit updates for operations through this handle only.
        // A later independent handle will still advance the change timestamp.
        var value = new BasicInformation { Written = -1, Changed = -1 };
        if (!SetFileInformationByHandle(stage, 0, ref value, (uint)Marshal.SizeOf<BasicInformation>()))
            throw LastIoError();
    }

    private static void PreserveCompression(SafeFileHandle destination, FileAttributes original, FileAttributes staged)
    {
        var expected = original & FileAttributes.Compressed;
        if ((staged & FileAttributes.Compressed) == expected) return;
        // NTFS uses LZNT1 for COMPRESSION_FORMAT_DEFAULT.
        var mode = Marshal.AllocHGlobal(sizeof(ushort));
        try
        {
            Marshal.WriteInt16(mode, (short)(expected == 0 ? CompressionNone : CompressionDefault));
            var error = RunFileControl(destination, SetCompression, mode, sizeof(ushort), IntPtr.Zero, 0);
            if (error != 0) throw new IOException("Не удалось сохранить режим сжатия файла.", new Win32Exception(error));
        }
        finally { Marshal.FreeHGlobal(mode); }
        if ((ReadBasicInformation(destination).Attributes & FileAttributes.Compressed) != expected)
            throw new UnsupportedMetadataException(ReplacementMetadata.Compression);
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

    internal static bool HasNamedDataStreams(SafeFileHandle source)
        => (GetVolumeFlags(source) & NamedStreams) != 0 && ProbeStreams(source);

    internal static bool HasNamedStreamsOrExtendedAttributes(SafeFileHandle source)
    {
        var fileSystem = GetVolumeFlags(source);
        return ((fileSystem & NamedStreams) != 0 && ProbeStreams(source))
            || ((fileSystem & ExtendedAttributes) != 0 && ProbeExtendedAttributes(source));
    }

    internal static bool HasSingleLink(SafeFileHandle file)
        => GetFileInformationByHandle(file, out var information) && information.LinkCount == 1;

    private static bool ProbeStreams(SafeFileHandle source)
    {
        const int streamNameOffset = 24;
        const string defaultStream = "::$DATA";
        var info = new byte[1024];
        if (!GetFileInformationByHandleEx(source, 7, info, (uint)info.Length))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 38) return false; // ERROR_HANDLE_EOF: no data streams.
            // The unnamed stream alone always fits this buffer.
            if (error is 122 or 234) return true;
            throw DaclError(error);
        }
        return BitConverter.ToUInt32(info) != 0 || BitConverter.ToUInt32(info, 4) != defaultStream.Length * sizeof(char)
            || System.Text.Encoding.Unicode.GetString(info, streamNameOffset, defaultStream.Length * sizeof(char)) != defaultStream;
    }

    private static void CheckObjectId(SafeFileHandle source)
    {
        const uint getObjectId = 0x0009009c;
        const int objectIdSize = 64;
        var info = Marshal.AllocHGlobal(objectIdSize);
        try
        {
            var error = RunFileControl(source, getObjectId, IntPtr.Zero, 0, info, objectIdSize);
            if (error == 0) throw new UnsupportedMetadataException(ReplacementMetadata.ObjectId);
            if (error is not (2 or 1168 or 4312)) throw DaclError(error); // No object ID assigned to this file.
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    private static int RunFileControl(SafeFileHandle file, uint control, IntPtr input, uint inputSize, IntPtr output, uint outputSize)
    {
        var retained = false;
        file.DangerousAddRef(ref retained);
        try
        {
            var handle = file.DangerousGetHandle();
            if (!file.IsAsync)
                return DeviceIoControl(handle, control, input, inputSize, output, outputSize, out _, IntPtr.Zero)
                    ? 0 : Marshal.GetLastWin32Error();

            using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
            var state = Marshal.AllocHGlobal(Marshal.SizeOf<OverlappedControl>());
            try
            {
                // Use a dedicated event and suppress IO completion port delivery.
                // FileStream can still use its own asynchronous I/O on this handle.
                var eventHandle = completed.SafeWaitHandle.DangerousGetHandle();
                Marshal.StructureToPtr(new OverlappedControl { Event = new IntPtr(eventHandle.ToInt64() | 1) }, state, false);
                if (DeviceIoControl(handle, control, input, inputSize, output, outputSize, out _, state)) return 0;
                var error = Marshal.GetLastWin32Error();
                if (error != 997) return error; // ERROR_IO_PENDING
                return GetOverlappedResult(handle, state, out _, wait: true) ? 0 : Marshal.GetLastWin32Error();
            }
            finally { Marshal.FreeHGlobal(state); }
        }
        finally { if (retained) file.DangerousRelease(); }
    }

    private static bool ProbeExtendedAttributes(SafeFileHandle source)
    {
        const int fileEaInformation = 7;
        var status = NtQueryInformationFile(source, out _, out var size, sizeof(uint), fileEaInformation);
        if (status < 0) throw DaclError((int)RtlNtStatusToDosError(status));
        return size != 0;
    }

    private static void CheckResourceAttributes(SafeFileHandle source)
    {
        var result = GetSecurityInfo(source, FileObject, ResourceAttributeInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            IntPtr.Zero, out var descriptor);
        if (result != 0) throw DaclError((int)result);
        try
        {
            if (ReadPolicyEntries(descriptor).Length != 0) throw new UnsupportedMetadataException(ReplacementMetadata.ResourceAttributes);
        }
        finally { _ = LocalFree(descriptor); }
    }

    internal static byte[] ReadLabelAndScope(SafeFileHandle file)
    {
        var result = GetSecurityInfo(file, FileObject, LabelAndScopeInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            IntPtr.Zero, out var descriptor);
        if (result != 0) throw DaclError((int)result);
        try { return ReadPolicyEntries(descriptor); }
        finally { _ = LocalFree(descriptor); }
    }

    internal static string CaptureReplacementSecurity(SafeFileHandle file)
    {
        if ((GetVolumeFlags(file) & PersistentAcls) == 0) return "no-persistent-acl";
        var result = GetSecurityInfo(file, FileObject, OwnerAndGroupInformation | DaclInformation,
            out var owner, out var group, out var dacl, IntPtr.Zero, out var descriptor);
        if (result != 0) throw DaclError((int)result);
        try
        {
            if (!GetSecurityDescriptorControl(descriptor, out var control, out _)) throw LastIoError();
            return FingerprintReplacementSecurity(control, ReadSid(owner), ReadSid(group), ReadDacl(dacl), ReadLabelAndScope(file));
        }
        finally { _ = LocalFree(descriptor); }
    }

    internal static string FingerprintReplacementSecurity(ushort control, byte[] owner, byte[] group, byte[] dacl, byte[] labelAndScope)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(control));
        AppendSecuritySection(hash, owner);
        AppendSecuritySection(hash, group);
        AppendSecuritySection(hash, dacl);
        AppendSecuritySection(hash, labelAndScope);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendSecuritySection(IncrementalHash hash, byte[] bytes)
    {
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static byte[] ReadSid(IntPtr sid)
    {
        if (sid == IntPtr.Zero) return [];
        var length = GetLengthSid(sid);
        if (length is < 8 or > 68) throw Unsupported();
        var bytes = new byte[length];
        Marshal.Copy(sid, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] ReadDacl(IntPtr dacl)
    {
        if (dacl == IntPtr.Zero) return [];
        const int aclSizeInformation = 2;
        if (!GetAclInformation(dacl, out var size, (uint)Marshal.SizeOf<AclSizeInformation>(), aclSizeInformation)) throw LastIoError();
        if (size.BytesInUse is < 8 or > ushort.MaxValue) throw Unsupported();
        var bytes = new byte[size.BytesInUse];
        Marshal.Copy(dacl, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static byte[] ReadPolicyEntries(IntPtr descriptor)
    {
        const int aclSizeInformation = 2, aclHeaderSize = 8;
        if (!GetSecurityDescriptorSacl(descriptor, out var present, out var acl, out _)) throw LastIoError();
        if (!present || acl == IntPtr.Zero) return [];
        if (!GetAclInformation(acl, out var size, (uint)Marshal.SizeOf<AclSizeInformation>(), aclSizeInformation)) throw LastIoError();
        if (size.AceCount == 0) return [];
        if (size.BytesInUse is <= aclHeaderSize or > ushort.MaxValue) throw Unsupported();
        // Compare the requested ACEs, without ACL allocation padding.
        var entries = new byte[size.BytesInUse - aclHeaderSize];
        Marshal.Copy(acl + aclHeaderSize, entries, 0, entries.Length);
        return entries;
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
                if (!SameSid(owner, stagedOwner) || !SameSid(group, stagedGroup))
                    throw new UnsupportedMetadataException(ReplacementMetadata.Security);
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
    private static IOException Unsupported() => new("Не удалось записать файл с его специальными свойствами. Существующие данные не изменены.");

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

    [StructLayout(LayoutKind.Sequential)]
    private struct AclSizeInformation { internal uint AceCount, BytesInUse, BytesFree; }

    [StructLayout(LayoutKind.Sequential)]
    private struct OverlappedControl
    {
        internal IntPtr Internal, InternalHigh;
        internal uint Offset, OffsetHigh;
        internal IntPtr Event;
    }

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInfo,
        IntPtr owner, IntPtr group, out IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInfo,
        out IntPtr owner, out IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInfo,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualSid(IntPtr first, IntPtr second);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorControl(IntPtr descriptor, out ushort control, out uint revision);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetLengthSid(IntPtr sid);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present,
        out IntPtr acl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAclInformation(IntPtr acl, out AclSizeInformation information, uint length, int kind);

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
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle file, uint access, uint share, uint flags);

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
    private static extern bool DeviceIoControl(IntPtr file, uint code, IntPtr input, uint inputLength,
        IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(IntPtr file, IntPtr overlapped, out uint transferred,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock status, out uint information,
        uint length, int informationClass);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
}
