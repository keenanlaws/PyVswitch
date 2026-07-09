using System.IO;

namespace PythonVersionSwitch.Services;

public static class AppLog
{
    private static readonly object Sync = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PythonVersionSwitch",
        "logs");

    public static string LogPath => Path.Combine(LogDirectory, "app.log");

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(Exception exception, string message)
    {
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
