using System.IO;
using System.Text.Json;
using PythonVersionSwitch.Models;

namespace PythonVersionSwitch.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string AppDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PythonVersionSwitch");

    public string SettingsPath => Path.Combine(AppDirectory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDirectory);
        settings.PathBackups = settings.PathBackups
            .OrderByDescending(backup => backup.CreatedAt)
            .Take(25)
            .ToList();

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}
