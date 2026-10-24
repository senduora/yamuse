namespace YMusic.Native;

internal sealed record HomeSection(string Title, IReadOnlyList<CatalogItem> Items, string? Note = null);
internal sealed record LyricLine(double TimeSeconds, string Text);
internal sealed record LyricsResult(string PlainText, IReadOnlyList<LyricLine> Lines, bool Synchronized, string? Note);
