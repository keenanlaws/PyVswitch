using System.IO;
using PythonVersionSwitch.Models;

namespace PythonVersionSwitch.Services;

public sealed class PathSwitchService
{
    public string? GetActivePythonPath(PathScope scope)
    {
        var path = Environment.GetEnvironmentVariable("Path", ToTarget(scope)) ?? "";
        foreach (var entry in SplitPath(path))
        {
            var python = Path.Combine(entry, "python.exe");
            if (File.Exists(python))
            {
                return Path.GetFullPath(python);
            }
        }

        return null;
    }

    public PythonInstall? FindActiveInstall(IEnumerable<PythonInstall> installs, PathScope scope)
    {
        var activePath = GetActivePythonPath(scope);
        if (string.IsNullOrWhiteSpace(activePath))
        {
            return null;
        }

        return installs.FirstOrDefault(install =>
            string.Equals(Path.GetFullPath(install.ExecutablePath), Path.GetFullPath(activePath), StringComparison.OrdinalIgnoreCase));
    }

    public PathBackup SwitchTo(PythonInstall selected, PathScope scope, IReadOnlyCollection<PythonInstall> knownInstalls)
    {
        if (!File.Exists(selected.ExecutablePath))
        {
            throw new FileNotFoundException("Selected python.exe was not found.", selected.ExecutablePath);
        }

        var target = ToTarget(scope);
        var previousPath = Environment.GetEnvironmentVariable("Path", target) ?? "";
        var removeSet = BuildManagedPathSet(knownInstalls);
        var entries = SplitPath(previousPath)
            .Where(entry => !removeSet.Contains(NormalizePath(entry)))
            .ToList();

        var preferred = new List<string> { selected.InstallDirectory };
        if (!string.IsNullOrWhiteSpace(selected.ScriptsDirectory) && Directory.Exists(selected.ScriptsDirectory))
        {
            preferred.Add(selected.ScriptsDirectory);
        }

        var newEntries = preferred
            .Concat(entries)
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var newPath = string.Join(';', newEntries);
        var processPath = BuildProcessPath(scope, newPath);
        AppLog.Info($"Switching {scope} PATH to {selected.DisplayName} at {selected.ExecutablePath}");
        Environment.SetEnvironmentVariable("Path", newPath, target);
        Environment.SetEnvironmentVariable("Path", processPath, EnvironmentVariableTarget.Process);
        EnvironmentBroadcaster.BroadcastEnvironmentChanged();

        return new PathBackup
        {
            CreatedAt = DateTimeOffset.Now,
            Scope = scope,
            PreviousPath = previousPath,
            NewPath = newPath,
            SelectedPython = selected.DisplayName
        };
    }

    public void Restore(PathBackup backup)
    {
        Environment.SetEnvironmentVariable("Path", backup.PreviousPath, ToTarget(backup.Scope));
        Environment.SetEnvironmentVariable("Path", BuildProcessPath(backup.Scope, backup.PreviousPath), EnvironmentVariableTarget.Process);
        EnvironmentBroadcaster.BroadcastEnvironmentChanged();
    }

    public static IReadOnlyList<string> SplitPath(string path)
    {
        return path
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .ToList();
    }

    private static EnvironmentVariableTarget ToTarget(PathScope scope)
    {
        return scope == PathScope.System ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User;
    }

    private static string BuildProcessPath(PathScope editedScope, string editedPath)
    {
        var otherTarget = editedScope == PathScope.User
            ? EnvironmentVariableTarget.Machine
            : EnvironmentVariableTarget.User;
        var otherPath = Environment.GetEnvironmentVariable("Path", otherTarget) ?? "";

        return string.Join(';',
            SplitPath(editedPath)
                .Concat(SplitPath(otherPath))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static HashSet<string> BuildManagedPathSet(IEnumerable<PythonInstall> knownInstalls)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var install in knownInstalls)
        {
            AddIfPresent(set, install.InstallDirectory);
            AddIfPresent(set, install.ScriptsDirectory);
        }

        return set;
    }

    private static void AddIfPresent(HashSet<string> set, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            set.Add(NormalizePath(path));
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch
        {
            return path.Trim().TrimEnd('\\', '/');
        }
    }
}
