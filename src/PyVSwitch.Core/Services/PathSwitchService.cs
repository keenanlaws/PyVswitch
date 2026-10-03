using Microsoft.Win32;
using PyVSwitch.Models;

namespace PyVSwitch.Services;

/// <summary>Pure PATH string manipulation, kept separate so it can be unit tested.</summary>
public static class PathEditor
{
    public static IReadOnlyList<string> Split(string? path)
    {
        return (path ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .ToList();
    }

    public static string Normalize(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try
        {
            return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return expanded.TrimEnd('\\', '/');
        }
    }

    /// <summary>
    /// Moves the selected interpreter's directories to the front and drops the directories of every
    /// other known interpreter, leaving unrelated entries in their original order.
    /// </summary>
    public static string BuildSwitchedPath(string? previousPath, IEnumerable<string> preferredDirectories, IEnumerable<string> managedDirectories)
    {
        var managed = new HashSet<string>(managedDirectories.Where(d => !string.IsNullOrWhiteSpace(d)).Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var preferred = preferredDirectories.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        foreach (var directory in preferred)
        {
            managed.Add(Normalize(directory));
        }

        var kept = Split(previousPath).Where(entry => !managed.Contains(Normalize(entry)));
        return string.Join(';', preferred.Concat(kept).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static string RemoveDirectories(string? path, IEnumerable<string> directories)
    {
        var remove = new HashSet<string>(directories.Where(d => !string.IsNullOrWhiteSpace(d)).Select(Normalize), StringComparer.OrdinalIgnoreCase);
        return string.Join(';', Split(path).Where(entry => !remove.Contains(Normalize(entry))));
    }

    /// <summary>First <c>python.exe</c> found walking the entries in order.</summary>
    public static string? FindFirstPython(IEnumerable<string> entries)
    {
        foreach (var entry in entries)
        {
            try
            {
                // Zero-byte files are Microsoft Store alias stubs, not interpreters; Doctor reports those separately.
                var python = new FileInfo(Path.Combine(Normalize(entry), "python.exe"));
                if (python.Exists && python.Length > 0)
                {
                    return python.FullName;
                }
            }
            catch
            {
                // Malformed PATH entries are skipped, exactly as the shell does.
            }
        }

        return null;
    }
}

public sealed class PathSwitchService
{
    private const string UserEnvironmentKey = "Environment";
    private const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>
    /// Reads the raw registry value. Unlike Environment.GetEnvironmentVariable this keeps
    /// %VARIABLE% references intact, so rewriting PATH never flattens them.
    /// </summary>
    public static string ReadPath(PathScope scope)
    {
        try
        {
            using var key = OpenEnvironmentKey(scope, writable: false);
            return key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"Failed to read {scope} PATH.");
            return "";
        }
    }

    private static RegistryKey? OpenEnvironmentKey(PathScope scope, bool writable)
    {
        return scope == PathScope.System
            ? Registry.LocalMachine.OpenSubKey(MachineEnvironmentKey, writable)
            : writable ? Registry.CurrentUser.CreateSubKey(UserEnvironmentKey) : Registry.CurrentUser.OpenSubKey(UserEnvironmentKey);
    }

    public string? GetActivePythonPath(PathScope scope)
    {
        return PathEditor.FindFirstPython(PathEditor.Split(ReadPath(scope)));
    }

    /// <summary>Windows builds a new process PATH as Machine entries followed by User entries.</summary>
    public string? GetEffectivePythonPath()
    {
        return PathEditor.FindFirstPython(
            PathEditor.Split(ReadPath(PathScope.System)).Concat(PathEditor.Split(ReadPath(PathScope.User))));
    }

    public ActiveState GetActiveState(IReadOnlyCollection<PythonInstall> installs, PathScope scope)
    {
        var selectedPath = GetActivePythonPath(scope);
        var effectivePath = GetEffectivePythonPath();
        return new ActiveState
        {
            Scope = scope,
            Selected = FindByPath(installs, selectedPath),
            EffectivePath = effectivePath,
            Effective = FindByPath(installs, effectivePath)
        };
    }

    public PathBackup SwitchTo(PythonInstall selected, PathScope scope, IReadOnlyCollection<PythonInstall> knownInstalls)
    {
        if (!File.Exists(selected.ExecutablePath))
        {
            throw new PyvsException("not_found", $"python.exe was not found at {selected.ExecutablePath}.");
        }

        var previousPath = ReadPath(scope);
        var preferred = new List<string> { selected.InstallDirectory };
        var scripts = Path.Combine(selected.InstallDirectory, "Scripts");
        if (Directory.Exists(scripts))
        {
            preferred.Add(scripts);
        }

        var managed = knownInstalls.SelectMany(install => new[] { install.InstallDirectory, Path.Combine(install.InstallDirectory, "Scripts") });
        var newPath = PathEditor.BuildSwitchedPath(previousPath, preferred, managed);

        AppLog.Info($"Switching {scope} PATH to {selected.DisplayName} at {selected.ExecutablePath}");
        WritePath(scope, newPath);

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
        AppLog.Info($"Restoring {backup.Scope} PATH from backup created {backup.CreatedAt:u}");
        WritePath(backup.Scope, backup.PreviousPath);
    }

    public void WritePath(PathScope scope, string newPath)
    {
        try
        {
            using var key = OpenEnvironmentKey(scope, writable: true)
                ?? throw new PyvsException("path_unavailable", $"The {scope} environment key could not be opened.");
            var kind = newPath.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String;
            key.SetValue("Path", newPath, kind);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            throw new PyvsException("needs_admin", "Changing the System PATH needs administrator rights. Use the User scope or run elevated.", ex);
        }

        var machine = scope == PathScope.System ? newPath : ReadPath(PathScope.System);
        var user = scope == PathScope.User ? newPath : ReadPath(PathScope.User);
        var processPath = string.Join(';', PathEditor.Split(machine).Concat(PathEditor.Split(user))
            .Select(Environment.ExpandEnvironmentVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("Path", processPath, EnvironmentVariableTarget.Process);
        EnvironmentBroadcaster.BroadcastEnvironmentChanged();
    }

    private static PythonInstall? FindByPath(IEnumerable<PythonInstall> installs, string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : installs.FirstOrDefault(install => string.Equals(install.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
    }

}
