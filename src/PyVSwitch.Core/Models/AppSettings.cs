using System.Text.Json.Serialization;

namespace PyVSwitch.Models;

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
    public List<VenvRecord> Venvs { get; set; } = [];
}

public sealed class PathBackup
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public PathScope Scope { get; set; }
    public string PreviousPath { get; set; } = "";
    public string NewPath { get; set; } = "";
    public string SelectedPython { get; set; } = "";
    /// <summary>True when this switch also changed the py launcher default, so a rollback must put it back.</summary>
    public bool LauncherChanged { get; set; }
    /// <summary>The launcher default before the switch; null when py.ini had none.</summary>
    public string? PreviousLauncherDefault { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(SelectedPython) ? "PATH cleanup" : $"Switched to {SelectedPython}";

    [JsonIgnore]
    public string Detail => $"{CreatedAt.LocalDateTime:g} · {Scope} PATH";
}

public sealed class VenvRecord
{
    public string Path { get; set; } = "";
    public string BaseVersion { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
