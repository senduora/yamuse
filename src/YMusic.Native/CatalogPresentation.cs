using System.Globalization;
using System.Windows.Data;

namespace YMusic.Native;

public sealed class CatalogGroupLabel : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "track" => "Треки", "artist" => "Исполнители", "album" => "Альбомы и сборники",
        "playlist" => "Плейлисты", "offline-playlist" => "Офлайн-плейлисты", _ => "Подборки"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

// Keep the existing action caption (including its accessible text) while the
// download symbol has a stable semantic color independent of the cover accent.
public sealed class DownloadCaption : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var caption = value as string ?? "";
        return parameter as string == "glyph" ? caption.TrimStart().StartsWith('✓') ? "\uE73E" : "\uE896"
            : caption.TrimStart('↓', '✓', ' ');
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
