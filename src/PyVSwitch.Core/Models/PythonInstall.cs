using System.Diagnostics;
using System.Text.Json.Serialization;

namespace PyVSwitch.Models;

public sealed class PythonInstall
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Architecture { get; set; } = "unknown";
    public string ExecutablePath { get; set; } = "";
    public string InstallDirectory { get; set; } = "";
    public string ScriptsDirectory { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsCached { get; set; }
    public bool IsVirtualEnvironment { get; set; }
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public PythonVersion ParsedVersion => PythonVersion.ParseOrDefault(Version);

    [JsonIgnore]
    public string Series => PythonVersion.TryParse(Version, out var parsed) ? parsed.Series : "";

    /// <summary>Selector tag in the same style the py launcher uses, e.g. 3.12-64.</summary>
    [JsonIgnore]
    public string Tag => string.IsNullOrEmpty(Series) ? "" : $"{Series}-{ArchitectureSuffix}";

    [JsonIgnore]
    public string ArchitectureSuffix => Architecture switch
    {
        "x86" => "32",
        "arm64" => "arm64",
        _ => "64"
    };

    [JsonIgnore]
    public string ArchitectureLabel => Architecture switch
    {
        "x86" => "32-bit",
        "arm64" => "ARM64",
        "x64" => "64-bit",
        _ => "unknown"
    };

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var version = string.IsNullOrWhiteSpace(Version) ? "Python" : $"Python {Version}";
            if (IsVirtualEnvironment)
            {
                return $"{version} venv";
            }

            var arch = string.IsNullOrWhiteSpace(Architecture) || Architecture == "unknown" ? "" : $" {Architecture}";
            return $"{version}{arch}";
        }
    }

    public static PythonInstall FromExecutable(string executablePath, string source, string? versionHint = null, string? architectureHint = null)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var installDir = Path.GetDirectoryName(fullPath) ?? "";
        var scriptsDir = Path.Combine(installDir, "Scripts");

        return new PythonInstall
        {
            Id = fullPath.ToUpperInvariant(),
            Version = string.IsNullOrWhiteSpace(versionHint) ? InferVersion(fullPath) : versionHint,
            Architecture = string.IsNullOrWhiteSpace(architectureHint) ? InferArchitecture(fullPath) : architectureHint,
            ExecutablePath = fullPath,
            InstallDirectory = installDir,
            ScriptsDirectory = Directory.Exists(scriptsDir) ? scriptsDir : "",
            Source = source,
            LastSeenAt = DateTimeOffset.Now
        };
    }

    private static string InferVersion(string executablePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            if (PythonVersion.TryParse(info.ProductVersion, out var parsed))
            {
                return parsed.ToString();
            }
        }
        catch
        {
            // Best effort. Version probing later can fill this in.
        }

        return "";
    }

    /// <summary>Reads the PE header so the answer does not depend on folder naming.</summary>
    public static string InferArchitecture(string executablePath)
    {
        try
        {
            using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
            {
                return "unknown";
            }

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 > stream.Length)
            {
                return "unknown";
            }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550)
            {
                return "unknown";
            }

            return reader.ReadUInt16() switch
            {
                0x8664 => "x64",
                0x014C => "x86",
                0xAA64 => "arm64",
                _ => "unknown"
            };
        }
        catch
        {
            return "unknown";
        }
    }
}
