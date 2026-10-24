using System.Buffers.Binary;
using System.Security.Cryptography;

namespace YMusic.Native;

// AES-CTR byte stream as supplied by the authenticated encraw endpoint. MIT attribution in notices.
internal sealed class AesCounter : IDisposable
{
    private readonly Aes _aes = Aes.Create();
    private readonly byte[] _counters = new byte[4096];
    private readonly byte[] _keystream = new byte[4096];
    private UInt128 _counter;
    private int _offset = 4096;
    public AesCounter(string hexKey)
    {
        var key = Convert.FromHexString(hexKey);
        try { if (key.Length is not (16 or 24 or 32)) throw new MusicException("Некорректный ключ аудиопотока."); _aes.Key = key; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public void Transform(Span<byte> bytes)
    {
        var position = 0;
        while (position < bytes.Length)
        {
            if (_offset == _keystream.Length)
            {
                for (var block = 0; block < 256; block++) BinaryPrimitives.WriteUInt128BigEndian(_counters.AsSpan(block * 16, 16), _counter++);
                _aes.EncryptEcb(_counters, _keystream, PaddingMode.None); _offset = 0;
            }
            var count = Math.Min(bytes.Length - position, _keystream.Length - _offset);
            for (var i = 0; i < count; i++) bytes[position + i] ^= _keystream[_offset + i];
            position += count; _offset += count;
        }
    }
    public void Dispose() { _aes.Dispose(); CryptographicOperations.ZeroMemory(_keystream); }
}
