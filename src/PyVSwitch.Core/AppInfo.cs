using System.Reflection;

namespace PyVSwitch;

public static class AppInfo
{
    public const string Name = "pyvswitch";
    public const string Publisher = "ecuunlock.com";
    public const string RepoUrl = "https://github.com/keenanlaws/PyVswitch";
    public const string CliExeName = "pyvswitch.exe";
    public const string GuiExeName = "pyvswitchw.exe";

    public static string Version { get; } = ReadVersion();

    // Kept from the 0.x releases so existing settings and PATH backups carry over.
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PythonVersionSwitch");

    public static string CacheDirectory => Path.Combine(DataDirectory, "cache");
    public static string DownloadDirectory => Path.Combine(CacheDirectory, "downloads");
    public static string UserAgent => $"pyvswitch/{Version} (+{RepoUrl})";

    private static string ReadVersion()
    {
        var value = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0";
        var plus = value.IndexOf('+');
        return plus > 0 ? value[..plus] : value;
    }
}

public sealed class PyvsException : Exception
{
    public string Code { get; }

    public PyvsException(string code, string message, Exception? inner = null) : base(message, inner)
    {
        Code = code;
    }
}
