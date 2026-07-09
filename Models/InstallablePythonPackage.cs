namespace PythonVersionSwitch.Models;

public sealed class InstallablePythonPackage
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Source { get; set; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(Version) ? Name : $"{Name} {Version}";
    public string Detail => string.IsNullOrWhiteSpace(Id) ? Source : $"{Id} - {Source}";
}
