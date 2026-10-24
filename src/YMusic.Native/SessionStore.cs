using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace YMusic.Native;

internal sealed class SessionStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "session.dpapi");
    public Session? Load()
    {
        if (!File.Exists(_path)) return null;
        var bytes = Unprotect(File.ReadAllBytes(_path));
        try { return JsonSerializer.Deserialize<Session>(bytes) ?? throw new CryptographicException(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Save(Session session)
    {
        Directory.CreateDirectory(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        byte[] encrypted;
        try { encrypted = Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, encrypted); File.Move(temp, _path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Clear() => File.Delete(_path);

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
                if (!protect) ZeroNative(output.Data, output.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
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
