using System.Text.Json.Serialization;

namespace PyVSwitch.Models;

public sealed class InstalledPackage
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("latest_version")] public string? LatestVersion { get; set; }
    [JsonPropertyName("latest_filetype")] public string? LatestFiletype { get; set; }

    [JsonIgnore]
    public bool IsOutdated => !string.IsNullOrEmpty(LatestVersion) && LatestVersion != Version;
}

public sealed class PyPiPackage
{
    public string Name { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string Summary { get; set; } = "";
    public string HomePage { get; set; } = "";
    public string RequiresPython { get; set; } = "";
    public string License { get; set; } = "";
    /// <summary>Newest first.</summary>
    public List<string> Versions { get; set; } = [];
}

public sealed class PipResult
{
    public int ExitCode { get; init; }
    public string Output { get; init; } = "";
    public bool Success => ExitCode == 0;
}

public enum DoctorSeverity
{
    Ok,
    Info,
    Warning,
    Error
}

public sealed class DoctorFinding
{
    public string Id { get; init; } = "";
    public DoctorSeverity Severity { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Fix { get; init; } = "";
    /// <summary>True when <c>pyvswitch doctor --fix</c> can repair this automatically.</summary>
    public bool AutoFixable { get; init; }
}

public sealed class ActiveState
{
    public PathScope Scope { get; init; }
    /// <summary>The install pyvswitch placed first in the edited PATH scope.</summary>
    public PythonInstall? Selected { get; init; }
    /// <summary>What a new terminal actually resolves <c>python</c> to (Machine PATH wins over User PATH).</summary>
    public string? EffectivePath { get; init; }
    public PythonInstall? Effective { get; init; }

    public bool IsShadowed => Selected is not null && EffectivePath is not null &&
        !string.Equals(Selected.ExecutablePath, EffectivePath, StringComparison.OrdinalIgnoreCase);
}

public sealed class InstallProgress
{
    public string Stage { get; init; } = "";
    public string Message { get; init; } = "";
    public long BytesReceived { get; init; }
    public long TotalBytes { get; init; }
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesReceived / TotalBytes, 0, 1) : 0;
}

public sealed class InstallOptions
{
    public string Architecture { get; set; } = "";
    public string? TargetDirectory { get; set; }
    public bool AllUsers { get; set; }
    public bool Activate { get; set; }
    public bool AllowPrerelease { get; set; }
}

public sealed class InstallOutcome
{
    public required PythonRelease Release { get; init; }
    public required PythonInstallerFile File { get; init; }
    public PythonInstall? Install { get; init; }
    public string LogPath { get; init; } = "";
    public bool AlreadyInstalled { get; init; }
}
