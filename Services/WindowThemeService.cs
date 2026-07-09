using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PythonVersionSwitch.Services;

public static partial class WindowThemeService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void ApplyDarkTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var enabled = 1;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));

            var captionColor = 0x00101010;
            _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, sizeof(int));

            var textColor = 0x00F6FAFF;
            _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColor, sizeof(int));
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Failed to apply dark title bar.");
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
