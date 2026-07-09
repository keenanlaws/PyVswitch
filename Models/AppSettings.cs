namespace PythonVersionSwitch.Models;

public enum AppTheme
{
    Dark,
    Light,
    System
}

public enum PathScope
{
    User,
    System
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public PathScope Scope { get; set; } = PathScope.User;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool UpdatePythonLauncherDefault { get; set; } = true;
    public List<PythonInstall> CachedInstalls { get; set; } = [];
    public List<PathBackup> PathBackups { get; set; } = [];
}
