namespace PythonVersionSwitch.Models;

public sealed class TrayPythonOption
{
    public required PythonInstall Install { get; init; }
    public bool IsActive { get; init; }

    public string StatusLabel => IsActive ? "ACTIVE" : "SWITCH";
    public string AccentText => Install.Architecture.Equals("x86", StringComparison.OrdinalIgnoreCase) ? "32-bit" : "64-bit";
}
