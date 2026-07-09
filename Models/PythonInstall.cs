using System.Diagnostics;
using System.IO;

namespace PythonVersionSwitch.Models;

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
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.Now;

    public string DisplayName
    {
        get
        {
            var version = string.IsNullOrWhiteSpace(Version) ? "Python" : $"Python {Version}";
            var arch = string.IsNullOrWhiteSpace(Architecture) || Architecture == "unknown" ? "" : $" {Architecture}";
            return $"{version}{arch}";
        }
    }

    public string VersionLabel => string.IsNullOrWhiteSpace(Version) ? "PY" : Version;
    public string Detail => string.IsNullOrWhiteSpace(Source) ? ExecutablePath : $"{Source} - {ExecutablePath}";

    public static PythonInstall FromExecutable(string executablePath, string source, string? versionHint = null, string? architectureHint = null)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var installDir = Path.GetDirectoryName(fullPath) ?? "";
        var scriptsDir = Path.Combine(installDir, "Scripts");

        return new PythonInstall
        {
            Id = fullPath.ToUpperInvariant(),
            Version = versionHint ?? InferVersion(fullPath),
            Architecture = architectureHint ?? InferArchitecture(fullPath),
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
            if (!string.IsNullOrWhiteSpace(info.ProductVersion))
            {
                var pieces = info.ProductVersion.Split('+', ' ', '-')[0].Split('.');
                if (pieces.Length >= 2)
                {
                    return string.Join('.', pieces.Take(Math.Min(3, pieces.Length)));
                }
            }
        }
        catch
        {
            // Best effort. Version probing later can fill this in.
        }

        return "";
    }

    private static string InferArchitecture(string executablePath)
    {
        if (executablePath.Contains("Program Files (x86)", StringComparison.OrdinalIgnoreCase) ||
            executablePath.Contains("-32", StringComparison.OrdinalIgnoreCase))
        {
            return "x86";
        }

        return "x64";
    }
}
