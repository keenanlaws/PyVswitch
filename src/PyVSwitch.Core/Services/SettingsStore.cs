using System.Text.Json;
using PyVSwitch.Models;

namespace PyVSwitch.Services;

public sealed class SettingsStore
{
    private const string MutexName = @"Local\pyvswitch-settings";

    public string AppDirectory { get; }

    public string SettingsPath => Path.Combine(AppDirectory, "settings.json");

    public SettingsStore(string? appDirectory = null)
    {
        AppDirectory = appDirectory ?? AppInfo.DataDirectory;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.AppSettings) ?? new AppSettings();
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

        var json = JsonSerializer.Serialize(settings, CoreJsonContext.Default.AppSettings);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, SettingsPath, true);
    }

    /// <summary>
    /// Read-modify-write under a cross-process lock, so the tray app, the CLI and the MCP server
    /// never overwrite each other's changes.
    /// </summary>
    public AppSettings Update(Action<AppSettings> change)
    {
        using var mutex = new Mutex(false, MutexName);
        var owned = false;
        try
        {
            try
            {
                owned = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            var settings = Load();
            change(settings);
            Save(settings);
            return settings;
        }
        finally
        {
            if (owned)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
