using System.Text.Json.Serialization;

namespace PyVSwitch.Models;

/// <summary>Everything python.org offers for Windows, grouped by minor series.</summary>
public sealed class PythonCatalog
{
    /// <summary>Bumped whenever the rules for building the catalog change, so stale caches are discarded.</summary>
    public const int CurrentSchema = 2;

    public int Schema { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public List<PythonSeries> Series { get; set; } = [];

    [JsonIgnore]
    public bool IsStale { get; set; }

    public IEnumerable<PythonRelease> AllReleases() => Series.SelectMany(series => series.Releases);
}

public sealed class PythonSeries
{
    public string Name { get; set; } = "";
    /// <summary>feature, prerelease, bugfix, security, end-of-life or unknown.</summary>
    public string Status { get; set; } = "unknown";
    public string EndOfLife { get; set; } = "";
    public string FirstRelease { get; set; } = "";
    /// <summary>Newest first.</summary>
    public List<PythonRelease> Releases { get; set; } = [];

    [JsonIgnore]
    public PythonRelease? LatestInstallable =>
        Releases.FirstOrDefault(release => release.HasInstaller && !release.IsPrerelease) ??
        Releases.FirstOrDefault(release => release.HasInstaller);

    [JsonIgnore]
    public PythonRelease? Latest => Releases.FirstOrDefault(release => !release.IsPrerelease) ?? Releases.FirstOrDefault();

    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        "bugfix" => "Active",
        "security" => "Security fixes",
        "end-of-life" => "End of life",
        "prerelease" => "Pre-release",
        "feature" => "In development",
        _ => "Legacy"
    };
}

public sealed class PythonRelease
{
    public string Version { get; set; } = "";
    public string Series { get; set; } = "";
    public bool IsPrerelease { get; set; }
    public string ReleaseDate { get; set; } = "";
    public string ReleaseNotesUrl { get; set; } = "";
    public List<PythonInstallerFile> Installers { get; set; } = [];

    [JsonIgnore]
    public bool HasInstaller => Installers.Count > 0;

    [JsonIgnore]
    public PythonVersion ParsedVersion => PythonVersion.ParseOrDefault(Version);

    public PythonInstallerFile? FindInstaller(string architecture)
    {
        return Installers.FirstOrDefault(file => file.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class PythonInstallerFile
{
    /// <summary>x64, x86 or arm64.</summary>
    public string Architecture { get; set; } = "";
    /// <summary>exe (3.5+) or msi (legacy).</summary>
    public string Kind { get; set; } = "";
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string Md5 { get; set; } = "";

    [JsonIgnore]
    public string FileName => Url[(Url.LastIndexOf('/') + 1)..];
}

// Wire formats for python.org and peps.python.org.

public sealed class ApiRelease
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("is_published")] public bool IsPublished { get; set; }
    [JsonPropertyName("pre_release")] public bool PreRelease { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("release_notes_url")] public string? ReleaseNotesUrl { get; set; }
    [JsonPropertyName("resource_uri")] public string ResourceUri { get; set; } = "";
}

public sealed class ApiReleaseFile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("release")] public string Release { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("md5_sum")] public string? Md5Sum { get; set; }
    [JsonPropertyName("sha256_sum")] public string? Sha256Sum { get; set; }
    [JsonPropertyName("filesize")] public long Filesize { get; set; }
}

public sealed class ApiReleaseCycle
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("first_release")] public string? FirstRelease { get; set; }
    [JsonPropertyName("end_of_life")] public string? EndOfLife { get; set; }
}
