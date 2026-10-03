using PyVSwitch.App.Controls;
using PyVSwitch.Models;
using PyVSwitch.Services;

namespace PyVSwitch.App.ViewModels;

public sealed class InstallItem : Observable
{
    private bool _isActive;

    public InstallItem(PythonInstall install)
    {
        Install = install;
    }

    public PythonInstall Install { get; }
    public string Title => string.IsNullOrEmpty(Install.Version) ? "Python" : $"Python {Install.Version}";
    public string Architecture => Install.ArchitectureLabel;
    public string Path => Install.ExecutablePath;
    public string Selector => Install.Tag;
    public bool IsVenv => Install.IsVirtualEnvironment;
    public string Label => IsVenv ? $"{Folder}  ·  Python {Install.Version}" : $"Python {Install.Version}  ·  {Install.ArchitectureLabel}";

    /// <summary>The folder a person thinks of: the install folder, or the venv root.</summary>
    public string Folder => IsVenv
        ? System.IO.Path.GetDirectoryName(Install.InstallDirectory) ?? Install.InstallDirectory
        : Install.InstallDirectory;

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public override string ToString() => Label;
}

public sealed class ReleaseChoice
{
    public ReleaseChoice(PythonRelease release)
    {
        Release = release;
    }

    public PythonRelease Release { get; }
    public string Label => Release.IsPrerelease ? $"{Release.Version}  (pre-release)" : Release.Version;

    public override string ToString() => Label;
}

public sealed class SeriesItem : Observable
{
    private readonly IReadOnlyList<PythonInstall> _installs;
    private readonly string _architecture;
    private ReleaseChoice? _selected;

    public SeriesItem(PythonSeries series, string architecture, IReadOnlyList<PythonInstall> installs, bool includePrereleases)
    {
        Series = series;
        _architecture = architecture;
        _installs = installs;
        Choices = series.Releases
            .Where(release => release.FindInstaller(architecture) is not null)
            .Where(release => includePrereleases || !release.IsPrerelease || series.Status == "prerelease")
            .Select(release => new ReleaseChoice(release))
            .ToList();
        _selected = Choices.FirstOrDefault(choice => !choice.Release.IsPrerelease) ?? Choices.FirstOrDefault();
    }

    public PythonSeries Series { get; }
    public List<ReleaseChoice> Choices { get; }
    public string Title => $"Python {Series.Name}";
    public string StatusLabel => Series.StatusLabel;
    public bool HasChoices => Choices.Count > 0;

    public string StatusTone => Series.Status switch
    {
        "bugfix" => "success",
        "security" => "warning",
        "prerelease" => "accent",
        _ => "neutral"
    };

    public string Subtitle
    {
        get
        {
            var newest = Series.Latest;
            var parts = new List<string>();
            if (newest is not null)
            {
                parts.Add($"Newest release {newest.Version}{(newest.ReleaseDate.Length > 0 ? $" ({newest.ReleaseDate})" : "")}");
            }

            if (Series.EndOfLife.Length > 0)
            {
                parts.Add(Series.Status == "end-of-life" ? $"support ended {Series.EndOfLife}" : $"supported until {Series.EndOfLife}");
            }

            if (!HasChoices)
            {
                parts.Add($"no {_architecture} installer");
            }

            return string.Join("  ·  ", parts);
        }
    }

    private PythonInstall? InstalledSameArchitecture =>
        _installs.FirstOrDefault(install => install.Series == Series.Name && install.Architecture == _architecture);

    public string InstalledText
    {
        get
        {
            var installed = _installs.Where(install => install.Series == Series.Name).ToList();
            return installed.Count == 0 ? "" : "Installed " + string.Join(", ", installed.Select(install => $"{install.Version} {install.Architecture}"));
        }
    }

    public bool HasInstalled => InstalledText.Length > 0;

    public ReleaseChoice? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                Raise(nameof(ActionLabel));
                Raise(nameof(CanInstall));
            }
        }
    }

    private bool SelectedIsInstalled => Selected is not null && InstalledSameArchitecture is { } installed &&
        installed.ParsedVersion.CompareTo(Selected.Release.ParsedVersion) == 0;

    public bool CanInstall => Selected is not null && !SelectedIsInstalled;

    public string ActionLabel
    {
        get
        {
            if (Selected is null) return "Unavailable";
            if (SelectedIsInstalled) return "Installed";
            if (InstalledSameArchitecture is not { } installed) return "Install";
            return Selected.Release.ParsedVersion.CompareTo(installed.ParsedVersion) > 0 ? "Update" : "Replace";
        }
    }
}

public sealed class PackageItem : Observable
{
    private string? _latestVersion;

    public PackageItem(InstalledPackage package)
    {
        Name = package.Name;
        Version = package.Version;
        _latestVersion = package.LatestVersion;
    }

    public string Name { get; }
    public string Version { get; }

    /// <summary>pip itself must stay, or nothing else can be managed.</summary>
    public bool CanRemove => !Name.Equals("pip", StringComparison.OrdinalIgnoreCase);

    public string? LatestVersion
    {
        get => _latestVersion;
        set
        {
            if (Set(ref _latestVersion, value))
            {
                Raise(nameof(IsOutdated));
                Raise(nameof(UpdateLabel));
            }
        }
    }

    public bool IsOutdated => !string.IsNullOrEmpty(LatestVersion) && LatestVersion != Version;
    public string UpdateLabel => IsOutdated ? $"{LatestVersion} available" : "";
}

public sealed class FindingItem
{
    public FindingItem(DoctorFinding finding)
    {
        Finding = finding;
    }

    public DoctorFinding Finding { get; }
    // The engine words findings for the terminal; the app drops the code-span backticks.
    public string Title => Finding.Title.Replace("`", "");
    public string Detail => Finding.Detail.Replace("`", "");
    public string Fix => Finding.Fix;
    public bool HasFix => Finding.Fix.Length > 0 && Finding.Severity != DoctorSeverity.Ok;

    public string Tone => Finding.Severity switch
    {
        DoctorSeverity.Ok => "success",
        DoctorSeverity.Info => "accent",
        DoctorSeverity.Warning => "warning",
        _ => "danger"
    };

    public string Glyph => Finding.Severity switch
    {
        DoctorSeverity.Ok => Glyphs.Check,
        DoctorSeverity.Info => Glyphs.Info,
        DoctorSeverity.Warning => Glyphs.Warning,
        _ => Glyphs.Error
    };
}

public sealed class PathEntryItem
{
    public required int Order { get; init; }
    public required string Scope { get; init; }
    public required string Path { get; init; }
    /// <summary>active, python, alias, missing or other.</summary>
    public required string Kind { get; init; }

    public string Badge => Kind switch
    {
        "active" => "python runs from here",
        "python" => "Python",
        "alias" => "Store alias",
        "missing" => "missing folder",
        _ => ""
    };

    public string Tone => Kind switch
    {
        "active" => "success",
        "python" => "accent",
        "alias" => "warning",
        "missing" => "danger",
        _ => "neutral"
    };

    public bool HasBadge => Badge.Length > 0;
    public bool IsHighlighted => Kind != "other";
}

public sealed class AiToolItem
{
    public AiToolItem(AiTargetStatus status)
    {
        Status = status;
    }

    public AiTargetStatus Status { get; }
    public string Name => Status.Name;
    public bool Detected => Status.Detected;
    public bool IsConnected => Status.InstructionsRegistered || Status.McpRegistered;
    public bool HasInstructions => Status.InstructionFile is not null;
    public string InstructionsLabel => Status.InstructionsRegistered ? "Knows the CLI" : "CLI note not added";
    public string InstructionsTone => Status.InstructionsRegistered ? "success" : "neutral";
    public string McpLabel => Status.McpRegistered ? "MCP server connected" : "MCP not connected";
    public string McpTone => Status.McpRegistered ? "success" : "neutral";
    public string ActionLabel => IsConnected ? "Disconnect" : "Connect";
    public string Detail => !Detected
        ? "Not installed on this PC"
        : Status.InstructionFile ?? Status.McpConfigFile ?? "";
}

public sealed class BackupItem
{
    public BackupItem(PathBackup backup)
    {
        Backup = backup;
    }

    public PathBackup Backup { get; }
    public string Title => Backup.DisplayName;
    public string Detail => Backup.Detail;
}

public sealed class CommandExample
{
    public required string Title { get; init; }
    public required string Command { get; init; }
}
