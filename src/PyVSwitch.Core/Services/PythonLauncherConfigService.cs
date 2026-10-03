using PyVSwitch.Models;

namespace PyVSwitch.Services;

/// <summary>Keeps the <c>py</c> launcher default (py.ini) in step with the active interpreter.</summary>
public sealed class PythonLauncherConfigService
{
    public string ConfigPath { get; }

    public PythonLauncherConfigService(string? configPath = null)
    {
        ConfigPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "py.ini");
    }

    public string? GetDefault()
    {
        if (!File.Exists(ConfigPath))
        {
            return null;
        }

        var inDefaults = false;
        foreach (var raw in File.ReadAllLines(ConfigPath))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inDefaults = line.Equals("[defaults]", StringComparison.OrdinalIgnoreCase);
            }
            else if (inDefaults && line.StartsWith("python=", StringComparison.OrdinalIgnoreCase))
            {
                return line["python=".Length..].Trim();
            }
        }

        return null;
    }

    public void SetDefault(PythonInstall install)
    {
        if (string.IsNullOrWhiteSpace(install.Series))
        {
            return;
        }

        var lines = File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath).ToList() : [];
        File.WriteAllLines(ConfigPath, UpdateDefault(lines, install.Tag));
    }

    /// <summary>Puts back an earlier default, or removes the setting when there was none.</summary>
    public void RestoreDefault(string? previous)
    {
        if (!File.Exists(ConfigPath))
        {
            return;
        }

        var lines = File.ReadAllLines(ConfigPath).ToList();
        File.WriteAllLines(ConfigPath, string.IsNullOrWhiteSpace(previous) ? RemoveDefault(lines) : UpdateDefault(lines, previous));
    }

    internal static List<string> RemoveDefault(List<string> lines)
    {
        var inDefaults = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('['))
            {
                inDefaults = line.Equals("[defaults]", StringComparison.OrdinalIgnoreCase);
            }
            else if (inDefaults && line.StartsWith("python=", StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(i);
                break;
            }
        }

        return lines;
    }

    internal static List<string> UpdateDefault(List<string> lines, string requested)
    {
        var defaultSectionIndex = lines.FindIndex(line => line.Trim().Equals("[defaults]", StringComparison.OrdinalIgnoreCase));
        if (defaultSectionIndex < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add("");
            }

            lines.Add("[defaults]");
            lines.Add($"python={requested}");
            return lines;
        }

        var insertIndex = defaultSectionIndex + 1;
        for (var i = defaultSectionIndex + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('['))
            {
                break;
            }

            if (lines[i].TrimStart().StartsWith("python=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"python={requested}";
                return lines;
            }

            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                insertIndex = i + 1;
            }
        }

        lines.Insert(insertIndex, $"python={requested}");
        return lines;
    }
}
