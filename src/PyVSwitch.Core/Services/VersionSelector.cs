using System.Text.RegularExpressions;
using PyVSwitch.Models;

namespace PyVSwitch.Services;

/// <summary>Matches selectors such as 3.12, 3.12.4, 3.10-32 or 3 against installed interpreters.</summary>
public static partial class VersionSelector
{
    public static bool LooksLikePath(string selector)
    {
        return selector.Contains('\\') || selector.Contains('/') || selector.Contains(':') || selector.StartsWith('.');
    }

    public static PythonInstall? Match(IEnumerable<PythonInstall> installs, string selector)
    {
        var match = SelectorPattern().Match(selector.Trim());
        if (!match.Success)
        {
            return null;
        }

        var parts = match.Groups["version"].Value.Split('.');
        var architecture = match.Groups["arch"].Value.ToLowerInvariant() switch
        {
            "32" or "x86" => "x86",
            "64" or "x64" => "x64",
            "arm64" => "arm64",
            _ => null
        };

        var candidates = installs.Where(install =>
        {
            if (architecture is not null && install.Architecture != architecture)
            {
                return false;
            }

            var version = install.ParsedVersion;
            if (version.Major.ToString() != parts[0])
            {
                return false;
            }

            if (parts.Length > 1 && version.Minor.ToString() != parts[1])
            {
                return false;
            }

            // A full version must match exactly, including any pre-release suffix.
            return parts.Length <= 2 || string.Equals(install.Version, match.Groups["version"].Value, StringComparison.OrdinalIgnoreCase);
        });

        var native = PythonInstallerService.DefaultArchitecture;
        return candidates
            .OrderByDescending(install => install.ParsedVersion)
            .ThenBy(install => install.Architecture == native ? 0 : install.Architecture == "x64" ? 1 : 2)
            .FirstOrDefault();
    }

    [GeneratedRegex(@"^(?:python)?\s*(?<version>\d+(?:\.\d+){0,2}(?:(?:a|b|rc)\d+)?)(?:-(?<arch>32|64|x86|x64|arm64))?$", RegexOptions.IgnoreCase)]
    private static partial Regex SelectorPattern();
}
