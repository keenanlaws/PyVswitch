using System.Diagnostics;
using System.IO;

namespace PythonVersionSwitch.Services;

public sealed class UninstallService
{
    private readonly SettingsStore _settingsStore;
    private readonly ShortcutService _shortcutService;

    public UninstallService(SettingsStore settingsStore, ShortcutService shortcutService)
    {
        _settingsStore = settingsStore;
        _shortcutService = shortcutService;
    }

    public void RemoveStartupAndDesktopShortcut()
    {
        _shortcutService.SetStartup(false);
        _shortcutService.RemoveDesktopShortcut();
    }

    public void DeleteAppData()
    {
        if (Directory.Exists(_settingsStore.AppDirectory))
        {
            Directory.Delete(_settingsStore.AppDirectory, true);
        }
    }

    public string CreateAndRunSelfUninstallScript(bool removeAppFolder)
    {
        RemoveStartupAndDesktopShortcut();
        DeleteAppData();

        var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"PythonVersionSwitch-Uninstall-{Environment.ProcessId}.ps1");
        var script = $$"""
$ErrorActionPreference = 'SilentlyContinue'
Start-Sleep -Milliseconds 700
Wait-Process -Id {{Environment.ProcessId}}
Remove-Item -LiteralPath "$env:USERPROFILE\Desktop\Python Version Switch.lnk" -Force
Remove-Item -LiteralPath "$env:APPDATA\PythonVersionSwitch" -Recurse -Force
{{(removeAppFolder ? $"Remove-Item -LiteralPath \"{appDirectory}\" -Recurse -Force" : "# App folder removal skipped")}}
Remove-Item -LiteralPath "$PSCommandPath" -Force
""";

        File.WriteAllText(scriptPath, script);
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        return scriptPath;
    }
}
