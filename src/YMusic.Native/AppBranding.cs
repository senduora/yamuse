using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

internal static class AppBranding
{
    internal const string Name = "Yamuse";
    internal static string CurrentVersion { get; } = typeof(AppBranding).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppBranding).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static string VersionLabel { get; } = "v" + CurrentVersion;
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
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(231, 187, 89)), null,
            new RectangleGeometry(new Rect(0, 0, 256, 256), 52, 52)));
        drawing.Children.Add(new GeometryDrawing(Brushes.Black, null,
            Geometry.Parse("F0 M19,101.856 L19.081,100.911 19.275,100.202 19.567,99.494 20.024,98.785 20.814,97.987 22.704,96.737 24.121,95.674 26.011,94.397 26.484,94 29.083,92.231 33.808,88.898 34.516,88.344 35.461,87.727 39.95,84.523 48.928,78.261 51.054,76.704 54.598,74.252 72.68,61.457 74.679,60.116 74.915,60.085 74.989,60.276 74.992,66.891 74.973,74.451 74.91,74.687 74.679,74.93 73.261,75.927 56.015,87.826 35.569,102.092 35.591,102.329 35.698,102.428 37.588,103.913 39.478,105.496 57.905,120.08 60.504,122.008 62.866,123.596 64.52,124.582 66.883,125.772 68.536,126.49 69.245,126.743 70.438,127.135 72.316,127.654 75.151,128.143 78.695,128.294 82.003,128.119 84.838,127.634 85.074,127.541 85.783,127.426 87.436,126.958 89.09,126.431 90.98,125.737 94.051,124.332 96.178,123.195 99.013,121.483 102.084,119.425 106.1,116.363 109.644,113.459 120.275,104.173 123.346,101.555 127.126,98.494 130.198,96.158 133.269,93.978 135.395,92.6 138.939,90.461 142.955,88.337 146.735,86.645 150.515,85.256 150.987,85.154 152.169,84.738 153.586,84.325 157.839,83.358 160.91,82.877 163.036,82.631 166.107,82.456 168.942,82.447 172.25,82.63 174.376,82.865 176.266,83.147 178.392,83.576 181.464,84.319 182.881,84.755 185.716,85.728 188.551,86.88 191.622,88.344 193.749,89.489 195.402,90.444 198.71,92.553 201.781,94.739 204.852,97.075 209.814,101.117 220.681,110.288 229.59,117.921 229.693,118.157 229.69,133.277 229.659,133.433 229.422,133.487 220.445,126.077 218.555,124.58 216.192,122.558 214.302,121.039 209.341,116.873 199.891,109.113 196.347,106.289 193.276,103.936 190.205,101.784 188.315,100.6 186.189,99.399 183.354,97.987 181.7,97.286 180.519,96.852 180.046,96.734 179.81,96.607 177.447,95.894 175.794,95.54 175.557,95.444 172.722,94.949 169.651,94.655 166.58,94.614 163.981,94.711 161.619,94.946 159.965,95.184 156.657,95.877 153.35,96.827 150.042,98.045 149.334,98.402 147.916,98.958 146.971,99.395 144.136,100.86 140.12,103.242 138.703,104.18 134.694,107.054 131.143,109.844 128.544,111.998 125,115.009 118.621,120.57 113.188,125.093 110.116,127.438 107.518,129.315 103.501,131.926 100.194,133.811 97.831,134.989 95.233,136.155 94.051,136.628 92.87,136.996 92.634,137.13 91.925,137.369 88.854,138.264 86.491,138.79 83.656,139.236 80.585,139.486 76.569,139.48 73.734,139.247 70.899,138.792 70.426,138.642 69.718,138.546 69.245,138.391 68.773,138.314 64.993,137.134 62.63,136.163 59.323,134.549 56.015,132.632 53.18,130.744 47.038,126.194 23.176,107.515 20.578,105.552 19.993,104.927 19.551,104.219 19.264,103.51 19.066,102.801 Z M35.718,141.546 L35.729,152.886 35.747,153.122 35.884,153.358 39.241,156.501 44.675,161.261 49.873,165.51 53.653,168.312 57.433,170.878 60.976,173.017 62.63,173.936 64.993,175.111 67.119,176.071 69.481,177.025 71.608,177.765 73.734,178.424 75.388,178.819 75.624,178.924 76.333,179.051 76.569,179.152 77.75,179.395 78.695,179.522 78.931,179.606 80.113,179.752 80.585,179.862 82.711,180.127 85.783,180.322 89.799,180.335 92.87,180.111 96.414,179.656 99.013,179.141 101.848,178.443 105.391,177.262 107.281,176.554 108.935,175.852 112.006,174.407 115.065,172.731 118.149,170.886 120.984,169.001 123.11,167.536 126.654,164.948 130.198,162.23 132.088,160.671 133.269,159.782 144.845,150.222 148.389,147.427 151.224,145.297 154.295,143.166 157.366,141.241 159.965,139.803 162.327,138.631 165.162,137.42 166.107,137.117 166.344,136.994 166.816,136.884 167.052,136.764 169.415,136.052 172.722,135.315 174.612,135.055 177.447,134.844 180.519,134.868 182.645,135.075 184.062,135.303 186.189,135.774 187.842,136.281 190.205,137.2 193.04,138.631 194.221,139.34 197.056,141.266 201.309,144.538 213.134,154.067 219.401,159.028 219.598,159.265 219.5,159.403 202.017,171.696 200.6,172.75 186.311,182.89 186.137,183.126 186.133,194.702 186.189,195.915 186.425,195.901 190.914,192.919 220.917,172.742 229.422,166.965 235.801,162.737 236.274,162.263 236.555,161.863 236.861,161.155 236.983,160.446 237,159.973 236.87,159.028 236.559,158.32 236.274,157.945 235.921,157.611 229.186,152.249 222.335,146.676 220.681,145.405 207.924,135.103 204.852,132.715 202.254,130.83 198.946,128.715 196.347,127.315 194.221,126.338 191.859,125.391 189.496,124.65 186.661,123.943 183.826,123.486 181.7,123.278 179.101,123.19 176.739,123.215 175.321,123.293 171.541,123.751 168.234,124.418 164.926,125.361 161.619,126.538 158.311,127.965 155.476,129.382 154.531,129.886 150.515,132.274 147.68,134.171 143.427,137.223 139.175,140.546 128.071,149.687 123.11,153.675 117.44,157.987 113.188,160.96 112.006,161.729 109.408,163.326 106.336,165.008 103.501,166.372 100.666,167.506 98.304,168.26 95.705,168.974 93.106,169.451 91.216,169.696 88.145,169.865 85.546,169.847 83.184,169.688 80.113,169.232 77.278,168.58 75.624,168.08 73.498,167.324 71.135,166.375 70.19,165.926 67.591,164.557 65.465,163.299 61.685,160.747 59.086,158.82 53.18,154.111 46.565,148.69 40.186,143.366 38.296,141.864 37.351,141.029 35.934,139.944 35.743,140.128 Z")));
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
