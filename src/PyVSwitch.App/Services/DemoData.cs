using PyVSwitch.Models;
using PyVSwitch.Services;

namespace PyVSwitch.App.Services;

/// <summary>
/// Stand-in data for `pyvswitchw --demo`, used to capture documentation screenshots without
/// exposing whatever is installed on the machine taking them.
/// </summary>
public static class DemoData
{
    private const string Root = @"C:\Users\you\AppData\Local\Programs\Python";

    private static PythonInstall Make(string version, string architecture, string folder) => new()
    {
        Id = folder.ToUpperInvariant(),
        Version = version,
        Architecture = architecture,
        ExecutablePath = $@"{folder}\python.exe",
        InstallDirectory = folder,
        ScriptsDirectory = $@"{folder}\Scripts",
        Source = "HKCU registry, Python launcher"
    };

    public static IReadOnlyList<PythonInstall> Installs { get; } =
    [
        Make("3.14.8", "x64", $@"{Root}\Python314"),
        Make("3.13.16", "x64", $@"{Root}\Python313"),
        Make("3.12.10", "x64", $@"{Root}\Python312"),
        Make("3.10.11", "x86", $@"{Root}\Python310-32")
    ];

    public static IReadOnlyList<PythonInstall> Venvs { get; } =
    [
        new()
        {
            Version = "3.13.16",
            Architecture = "x64",
            ExecutablePath = @"D:\Projects\flash-tool\.venv\Scripts\python.exe",
            InstallDirectory = @"D:\Projects\flash-tool\.venv\Scripts",
            IsVirtualEnvironment = true,
            Source = "Virtual environment"
        }
    ];

    public static List<PathBackup> Backups { get; } =
    [
        new() { CreatedAt = DateTimeOffset.Now.AddMinutes(-12), Scope = PathScope.User, SelectedPython = "Python 3.14.8 x64" },
        new() { CreatedAt = DateTimeOffset.Now.AddHours(-3), Scope = PathScope.User, SelectedPython = "Python 3.10.11 x86" },
        new() { CreatedAt = DateTimeOffset.Now.AddDays(-1), Scope = PathScope.User, SelectedPython = "Python 3.12.10 x64" }
    ];

    public static ActiveState State(PythonInstall active) => new()
    {
        Scope = PathScope.User,
        Selected = active,
        Effective = active,
        EffectivePath = active.ExecutablePath
    };

    public static List<InstalledPackage> Packages { get; } =
    [
        new() { Name = "anthropic", Version = "0.71.0" },
        new() { Name = "black", Version = "25.1.0", LatestVersion = "25.9.0" },
        new() { Name = "cantools", Version = "40.2.3" },
        new() { Name = "fastapi", Version = "0.118.0" },
        new() { Name = "httpx", Version = "0.28.1" },
        new() { Name = "numpy", Version = "2.2.6", LatestVersion = "2.3.3" },
        new() { Name = "pandas", Version = "2.3.3" },
        new() { Name = "pip", Version = "25.2" },
        new() { Name = "pydantic", Version = "2.11.9" },
        new() { Name = "pyserial", Version = "3.5" },
        new() { Name = "pytest", Version = "8.4.2" },
        new() { Name = "python-can", Version = "4.6.1" },
        new() { Name = "requests", Version = "2.32.5", LatestVersion = "2.34.2" },
        new() { Name = "rich", Version = "14.1.0" },
        new() { Name = "ruff", Version = "0.13.2" },
        new() { Name = "uvicorn", Version = "0.37.0" }
    ];

    public static PyPiPackage? Lookup(string name) => new()
    {
        Name = name.ToLowerInvariant(),
        LatestVersion = "1.44.2",
        Summary = "Blazingly fast DataFrame library",
        RequiresPython = ">=3.10",
        HomePage = $"https://pypi.org/project/{name.ToLowerInvariant()}/",
        Versions = ["1.44.2", "1.44.1", "1.44.0", "1.43.1", "1.43.0", "1.42.2", "1.42.0"]
    };

    public static List<DoctorFinding> Findings { get; } =
    [
        new() { Id = "active", Severity = DoctorSeverity.Ok, Title = "`python` resolves to Python 3.14.8 x64", Detail = $@"{Root}\Python314\python.exe" },
        new() { Id = "pip", Severity = DoctorSeverity.Ok, Title = "pip 25.2 is ready", Detail = "Packages install into Python 3.14.8 x64." },
        new()
        {
            Id = "dangling",
            Severity = DoctorSeverity.Warning,
            Title = "1 PATH entry points to a missing Python folder",
            Detail = $@"{Root}\Python39\Scripts",
            Fix = "pyvswitch doctor --fix",
            AutoFixable = true
        }
    ];

    public static List<(string Scope, string Path)> PathEntries { get; } =
    [
        ("System", @"C:\Windows\system32"),
        ("System", @"C:\Windows"),
        ("System", @"C:\Program Files\Git\cmd"),
        ("System", @"C:\Program Files\dotnet"),
        ("User", $@"{Root}\Python314"),
        ("User", $@"{Root}\Python314\Scripts"),
        ("User", $@"{Root}\Python39\Scripts"),
        ("User", @"C:\Users\you\AppData\Local\Microsoft\WindowsApps"),
        ("User", @"C:\Users\you\.cargo\bin")
    ];

    public static string Classify(string path, ref bool activeFound)
    {
        if (path.EndsWith("Python314", StringComparison.Ordinal))
        {
            activeFound = true;
            return "active";
        }

        if (path.Contains("Python314", StringComparison.Ordinal)) return "python";
        if (path.Contains("Python39", StringComparison.Ordinal)) return "missing";
        return path.EndsWith("WindowsApps", StringComparison.Ordinal) ? "alias" : "other";
    }

    private static AiTargetStatus Tool(string id, string name, bool detected, bool connected, string? instructions, string? mcp) => new()
    {
        Id = id,
        Name = name,
        Detected = detected,
        InstructionFile = instructions,
        InstructionsRegistered = connected && instructions is not null,
        McpConfigFile = mcp,
        McpRegistered = connected
    };

    public static List<AiTargetStatus> AiTools { get; } =
    [
        Tool("claude-code", "Claude Code", true, true, @"C:\Users\you\.claude\CLAUDE.md", @"C:\Users\you\.claude.json"),
        Tool("claude-desktop", "Claude Desktop", true, true, null, @"C:\Users\you\AppData\Roaming\Claude\claude_desktop_config.json"),
        Tool("codex", "OpenAI Codex", true, true, @"C:\Users\you\.codex\AGENTS.md", @"C:\Users\you\.codex\config.toml"),
        Tool("cursor", "Cursor", true, false, null, @"C:\Users\you\.cursor\mcp.json"),
        Tool("vscode", "VS Code / GitHub Copilot", true, true, @"C:\Users\you\AppData\Roaming\Code\User\prompts\pyvswitch.instructions.md", @"C:\Users\you\AppData\Roaming\Code\User\mcp.json"),
        Tool("gemini", "Gemini CLI", false, false, @"C:\Users\you\.gemini\GEMINI.md", @"C:\Users\you\.gemini\settings.json"),
        Tool("windsurf", "Windsurf", false, false, null, null)
    ];
}
