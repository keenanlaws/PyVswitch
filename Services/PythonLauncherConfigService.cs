using System.IO;
using PythonVersionSwitch.Models;

namespace PythonVersionSwitch.Services;

public sealed class PythonLauncherConfigService
{
    public string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "py.ini");

    public void SetDefault(PythonInstall install)
    {
        if (string.IsNullOrWhiteSpace(install.Version))
        {
            return;
        }

        var majorMinor = string.Join('.', install.Version.Split('.').Take(2));
        var archSuffix = install.Architecture == "x86" ? "-32" : "-64";
        var requested = $"{majorMinor}{archSuffix}";
        var lines = new List<string>();

        if (File.Exists(ConfigPath))
        {
            lines.AddRange(File.ReadAllLines(ConfigPath));
        }

        var defaultSectionIndex = lines.FindIndex(line => line.Trim().Equals("[defaults]", StringComparison.OrdinalIgnoreCase));
        if (defaultSectionIndex < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add("");
            }

            lines.Add("[defaults]");
            lines.Add($"python={requested}");
        }
        else
        {
            var insertIndex = defaultSectionIndex + 1;
            var replaced = false;
            for (var i = defaultSectionIndex + 1; i < lines.Count; i++)
            {
                if (lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    break;
                }

                if (lines[i].TrimStart().StartsWith("python=", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"python={requested}";
                    replaced = true;
                    break;
                }

                insertIndex = i + 1;
            }

            if (!replaced)
            {
                lines.Insert(insertIndex, $"python={requested}");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllLines(ConfigPath, lines);
    }
}
