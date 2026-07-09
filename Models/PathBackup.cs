namespace PythonVersionSwitch.Models;

public sealed class PathBackup
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public PathScope Scope { get; set; }
    public string PreviousPath { get; set; } = "";
    public string NewPath { get; set; } = "";
    public string SelectedPython { get; set; } = "";

    public string DisplayName => $"{CreatedAt.LocalDateTime:g} - {Scope} PATH";
    public string Detail => string.IsNullOrWhiteSpace(SelectedPython) ? "Manual backup" : SelectedPython;
}
