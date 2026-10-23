using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Sonari.Presentation.Interop;

/// <summary>Pide a DWM una barra de título oscura para que la ventana nativa encaje con el tema.</summary>
internal static class DarkTitleBar
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE es 20 desde Windows 10 20H1; las builds anteriores usaban 19 sin documentar.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    public static void Apply(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int enabled = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            // Fallar aquí solo cambia el aspecto, así que se ignora el resultado.
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
