using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

internal static class AppBranding
{
    internal const string Name = "Yamuse";
    public static string VersionLabel { get; } = "v" + (typeof(AppBranding).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
    private static readonly Lazy<ImageSource> Icon = new(LoadWindowIcon);
    internal static ImageSource WindowIcon => Icon.Value;

    private static byte[]? ReadIcon()
    {
        // Read into memory and close the stream: neither WPF nor Explorer holds
        // the installed icon file open during an application update.
        try
        {
            var resource = Application.GetResourceStream(new Uri("/YMusic.Native;component/Assets/Yamuse.ico", UriKind.Relative));
            if (resource is not null)
            {
                using var source = resource.Stream;
                using var copy = new MemoryStream();
                source.CopyTo(copy);
                return copy.Length is > 0 and <= 2 * 1024 * 1024 ? copy.ToArray() : null;
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException) { }
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Yamuse.ico");
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (source.Length is <= 0 or > 2 * 1024 * 1024) return null;
            using var copy = new MemoryStream();
            source.CopyTo(copy);
            return copy.ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    private static ImageSource LoadWindowIcon()
    {
        if (ReadIcon() is { } bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes, false);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderByDescending(item => item.PixelWidth).First().Clone();
                frame.Freeze();
                return frame;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException or ArgumentException) { }
        }
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(234, 87, 28)), null,
            new RectangleGeometry(new Rect(0, 0, 64, 64), 17, 17)));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(30, 25, 30)), null,
            Geometry.Parse("M19,18 L26,18 32,29 38,18 45,18 35,36 35,47 29,47 29,36 Z")));
        var fallback = new DrawingImage(drawing); fallback.Freeze(); return fallback;
    }

    internal static OwnedIcon CreateTrayIcon(nint window)
    {
        var dpi = GetDpiForWindow(window);
        if (dpi == 0) dpi = 96;
        var width = Math.Max(16, GetSystemMetricsForDpi(49, dpi)); // SM_CXSMICON
        var height = Math.Max(16, GetSystemMetricsForDpi(50, dpi));
        if (ReadIcon() is { Length: >= 6 } bytes && BitConverter.ToUInt16(bytes, 2) == 1)
        {
            var count = BitConverter.ToUInt16(bytes, 4);
            if (count > 0 && 6 + count * 16 <= bytes.Length)
            {
                var entries = Enumerable.Range(0, count).Select(index => 6 + index * 16)
                    .OrderBy(index => Math.Abs((bytes[index] == 0 ? 256 : bytes[index]) - width))
                    .ThenByDescending(index => bytes[index + 6]);
                foreach (var entry in entries)
                {
                    var length = BitConverter.ToUInt32(bytes, entry + 8);
                    var offset = BitConverter.ToUInt32(bytes, entry + 12);
                    if (length == 0 || (ulong)offset + length > (ulong)bytes.Length) continue;
                    var data = bytes.AsSpan((int)offset, (int)length).ToArray();
                    var icon = CreateIconFromResourceEx(data, (uint)data.Length, true, 0x00030000, width, height, 0);
                    if (icon != 0) return new OwnedIcon(icon, true);
                }
            }
        }
        // The emergency fallback is copied so every returned HICON is owned.
        // The normal packaged path above always uses the Yamuse multi-size ICO.
        return new OwnedIcon(CopyIcon(LoadIcon(0, (nint)32512)), false);
    }

    internal sealed class OwnedIcon : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal bool IsBranded { get; }
        internal OwnedIcon(nint handle, bool branded) : base(true) { SetHandle(handle); IsBranded = branded; }
        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern nint CreateIconFromResourceEx(byte[] bytes, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")] private static extern nint CopyIcon(nint icon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
}
