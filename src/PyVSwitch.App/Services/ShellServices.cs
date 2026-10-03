using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using PyVSwitch.Models;
using PyVSwitch.Services;

namespace PyVSwitch.App.Services;

public sealed class ShortcutService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Kept from the 0.x releases so the existing startup entry is updated instead of duplicated.
    private const string RunValueName = "PythonVersionSwitch";
    private const string ShortcutName = "pyvswitch.lnk";
    private const string LegacyShortcutName = "Python Version Switch.lnk";

    private static string StartupCommand => $"\"{Environment.ProcessPath}\" --tray";

    public bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string;
    }

    public void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            key.SetValue(RunValueName, StartupCommand);
        }
        else
        {
            key.DeleteValue(RunValueName, false);
        }
    }

    /// <summary>
    /// Older releases registered pyvswitch.exe (then the GUI, now the CLI) to start with Windows.
    /// Point that entry at this executable so sign-in keeps opening the tray app.
    /// </summary>
    public void MigrateStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is string current && !current.Equals(StartupCommand, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(RunValueName, StartupCommand);
                AppLog.Info($"Updated the startup entry from {current} to {StartupCommand}");
            }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var legacy = Path.Combine(desktop, LegacyShortcutName);
            if (File.Exists(legacy))
            {
                File.Delete(legacy);
                CreateDesktopShortcut();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Could not migrate the startup entry.");
        }
    }

    public string CreateDesktopShortcut()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var shortcutPath = Path.Combine(desktop, ShortcutName);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        shortcut.WorkingDirectory = AppContext.BaseDirectory;
        shortcut.IconLocation = shortcut.TargetPath;
        shortcut.Description = "Switch, install and manage Python versions.";
        shortcut.Save();
        return shortcutPath;
    }
}

public static partial class ThemeService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmRoundCorners = 2;

    public static bool IsDark { get; private set; } = true;

    public static event Action? Changed;

    public static void Apply(AppTheme theme)
    {
        var dark = theme switch
        {
            AppTheme.Light => false,
            AppTheme.System => SystemUsesDarkApps(),
            _ => true
        };

        IsDark = dark;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"/Themes/Palette.{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative)
        };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        // Index 0 is always the palette; Styles.xaml follows it.
        dictionaries[0] = palette;

        foreach (Window window in Application.Current.Windows)
        {
            ApplyWindowChrome(window);
        }

        Changed?.Invoke();
    }

    public static void ApplyWindowChrome(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var dark = IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            var corners = DwmRoundCorners;
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));
            // COLORREF is 0x00BBGGRR.
            var border = IsDark ? 0x00382922 : 0x00EAE1DC;
            _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Failed to apply window chrome attributes.");
        }
    }

    private static bool SystemUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not 1;
        }
        catch
        {
            return true;
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}
