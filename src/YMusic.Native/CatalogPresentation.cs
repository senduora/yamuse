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
