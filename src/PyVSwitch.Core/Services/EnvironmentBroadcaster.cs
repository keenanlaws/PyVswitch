using System.Runtime.InteropServices;

namespace PyVSwitch.Services;

public static partial class EnvironmentBroadcaster
{
    private const int HwndBroadcast = 0xffff;
    private const int WmSettingChange = 0x001A;
    private const int SmtoAbortIfHung = 0x0002;

    /// <summary>Tells Explorer and other listeners to reload environment variables.</summary>
    public static void BroadcastEnvironmentChanged()
    {
        try
        {
            _ = SendMessageTimeout(
                HwndBroadcast,
                WmSettingChange,
                UIntPtr.Zero,
                "Environment",
                SmtoAbortIfHung,
                1000,
                out _);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Failed to broadcast environment change.");
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeout(
        int hWnd,
        int msg,
        UIntPtr wParam,
        string lParam,
        int flags,
        int timeout,
        out UIntPtr result);
}
