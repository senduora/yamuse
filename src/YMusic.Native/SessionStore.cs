using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

internal sealed class SessionStore(string directory)
{
    private const int MaximumEncryptedBytes = 1024 * 1024;
    private readonly string _path = Path.GetFullPath(Path.Combine(directory, "session.dpapi"));
    public Session? Load()
    {
        byte[] encrypted;
        try
        {
            using var parents = UninstallPathGuard.HoldAncestors(_path);
            if (!parents.Complete) return null;
            using var handle = GuardedStoreFile.OpenForRead(_path);
            if (handle is null) return null;
            using var file = new FileStream(handle, FileAccess.Read);
            var length = file.Length;
            if (length is <= 0 or > MaximumEncryptedBytes) throw new CryptographicException();
            encrypted = new byte[(int)length];
            file.ReadExactly(encrypted);
        }
        catch (Win32Exception error) { throw new IOException("Не удалось прочитать сохранённый вход.", error); }
        return Decode(encrypted);
    }

    private static Session Decode(byte[] encrypted)
    {
        var bytes = Unprotect(encrypted);
        try
        {
            var session = JsonSerializer.Deserialize<Session>(bytes);
            return session is not null && MusicApi.IsOAuthValue(session.AccessToken) ? session : throw new CryptographicException();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static bool IsOwnedFile(SafeFileHandle file)
    {
        if (GuardedStoreFile.ReadFromHandle(file, MaximumEncryptedBytes) is not { Length: > 0 } bytes) return false;
        try { _ = Decode(bytes); return true; }
        catch (Exception error) when (error is CryptographicException or JsonException) { return false; }
    }
    public void Save(Session session) => GuardedStoreFile.Write(_path, Encode(session));

    internal bool SaveIfMatches(Session session, Session? expected)
    {
        using var storage = GuardedStoreFile.BeginWrite(_path);
        if (expected is null ? storage.Exists : !Matches(storage, expected)) return false;
        storage.Write(Encode(session));
        return true;
    }

    private static byte[] Encode(Session session)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        byte[] encrypted;
        try { encrypted = Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length > MaximumEncryptedBytes) throw new CryptographicException();
        return encrypted;
    }

    public void Clear()
    {
        try
        {
            using var storage = GuardedStoreFile.BeginWrite(_path, createParents: false, deleteAccess: true);
            if (storage.Exists) storage.Delete();
        }
        catch (Win32Exception error) { throw new IOException("Не удалось удалить сохранённый вход.", error); }
    }

    internal bool ClearIfMatches(Session expected)
    {
        using var storage = GuardedStoreFile.BeginWrite(_path, createParents: false, deleteAccess: true);
        if (!storage.Exists) return true;
        if (!Matches(storage, expected)) return false;
        storage.Delete();
        return true;
    }

    private static bool Matches(GuardedStoreFile.WriteTransaction storage, Session expected)
    {
        try
        {
            return storage.ReadExisting(MaximumEncryptedBytes) is { } bytes && Decode(bytes) == expected;
        }
        catch (Exception error) when (error is CryptographicException or JsonException) { return false; }
    }

    // CRYPTPROTECT_UI_FORBIDDEN, CurrentUser scope. No plaintext token fallback.
    private static byte[] Protect(byte[] input) => Transform(input, true);
    private static byte[] Unprotect(byte[] input) => Transform(input, false);
    private static byte[] Transform(byte[] input, bool protect)
    {
        var blob = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        try
        {
            Marshal.Copy(input, 0, blob.Data, input.Length);
            DataBlob output;
            var success = protect ? CryptProtectData(ref blob, "Yamuse", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                if (!protect) ZeroNative(output.Data, output.Length);
                LocalFree(output.Data);
            }
        }
        finally { ZeroNative(blob.Data, blob.Length); Marshal.FreeHGlobal(blob.Data); }
    }
    private static void ZeroNative(IntPtr data, int length) { for (var i = 0; i < length; i++) Marshal.WriteByte(data, i, 0); }
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
