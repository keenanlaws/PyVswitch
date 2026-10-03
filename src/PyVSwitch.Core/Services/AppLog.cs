namespace PyVSwitch.Services;

public static class AppLog
{
    private static readonly object Sync = new();

    public static string LogDirectory { get; } = Path.Combine(AppInfo.DataDirectory, "logs");

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
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                {
                    File.Move(LogPath, Path.Combine(LogDirectory, "app.previous.log"), true);
                }

                File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
