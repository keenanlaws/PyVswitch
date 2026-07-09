using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace PythonVersionSwitch.Services;

public sealed class ShortcutService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "PythonVersionSwitch";

    public bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(AppName) is string;
    }

    public void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }

    public string CreateDesktopShortcut()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var shortcutPath = Path.Combine(desktop, "Python Version Switch.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        shortcut.WorkingDirectory = AppContext.BaseDirectory;
        shortcut.IconLocation = shortcut.TargetPath;
        shortcut.Description = "Quickly switch the active Python version on PATH.";
        shortcut.Save();
        return shortcutPath;
    }

    public void RemoveDesktopShortcut()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var shortcutPath = Path.Combine(desktop, "Python Version Switch.lnk");
        if (File.Exists(shortcutPath))
        {
            File.Delete(shortcutPath);
        }
    }
}
