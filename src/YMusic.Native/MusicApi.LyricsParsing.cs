namespace YMusic.Native;

internal sealed partial class MusicApi
{
    internal static LyricsResult ParseSynchronizedLyrics(string source)
    {
        try { return ParseDiscoveryLrc(source); }
        catch (DiscoveryUnavailable) { throw new MusicException("Некорректный синхронизированный текст."); }
    }
}
