using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sonari.Presentation.Interop;

/// <summary>
/// Iconos de ejecutables mediante <c>SHGetFileInfo</c>, sin depender de System.Drawing. Se cachean por ruta y se
/// congelan para poder compartirlos entre vistas. Solo desde el hilo de UI.
/// </summary>
public static class ShellIcons
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? ForExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Cache.TryGetValue(path, out ImageSource? cached))
        {
            return cached;
        }

        ImageSource? icon = Load(path);
        Cache[path] = icon;
        return icon;
    }

    private static ImageSource? Load(string path)
    {
        var info = new ShFileInfo();
        IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            // CreateBitmapSourceFromHIcon copia los píxeles: el HICON se puede destruir enseguida.
            DestroyIcon(info.hIcon);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }
}
