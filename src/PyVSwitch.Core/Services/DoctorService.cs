using PyVSwitch.Models;

namespace PyVSwitch.Services;

/// <summary>Finds the usual reasons `python` resolves to something other than what was selected.</summary>
public sealed class DoctorService
{
    private readonly PathSwitchService _pathSwitch;
    private readonly PipService _pip;
    private readonly PythonLauncherConfigService _launcher;

    public DoctorService(PathSwitchService pathSwitch, PipService pip, PythonLauncherConfigService launcher)
    {
        _pathSwitch = pathSwitch;
        _pip = pip;
        _launcher = launcher;
    }

    public async Task<List<DoctorFinding>> RunAsync(IReadOnlyList<PythonInstall> installs, PathScope scope, CancellationToken cancellationToken = default)
    {
        var machinePath = PathSwitchService.ReadPath(PathScope.System);
        var userPath = PathSwitchService.ReadPath(PathScope.User);
        var state = _pathSwitch.GetActiveState(installs, scope);
        var findings = AnalyzePath(machinePath, userPath, state, StoreAliasDirectory());

        foreach (var variable in new[] { "PYTHONHOME", "PYTHONPATH" })
        {
            var value = Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.User)
                        ?? Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(value))
            {
                findings.Add(new DoctorFinding
                {
                    Id = "env-" + variable.ToLowerInvariant(),
                    Severity = DoctorSeverity.Warning,
                    Title = $"{variable} is set globally",
                    Detail = $"{variable}={value} is applied to every interpreter and commonly breaks imports after switching versions.",
                    Fix = $"Remove the {variable} environment variable unless you rely on it."
                });
            }
        }

        var active = state.Effective ?? state.Selected;
        if (active is not null)
        {
            var pipVersion = await _pip.GetVersionAsync(active, cancellationToken).ConfigureAwait(false);
            findings.Add(pipVersion is null
                ? new DoctorFinding
                {
                    Id = "pip-missing",
                    Severity = DoctorSeverity.Warning,
                    Title = $"pip is missing from {active.DisplayName}",
                    Detail = "Packages cannot be installed into the active interpreter until pip is bootstrapped.",
                    Fix = "pyvswitch run -- -m ensurepip --upgrade"
                }
                : new DoctorFinding
                {
                    Id = "pip",
                    Severity = DoctorSeverity.Ok,
                    Title = $"pip {pipVersion} is ready",
                    Detail = $"Packages install into {active.DisplayName}."
                });

            var launcherDefault = _launcher.GetDefault();
            if (launcherDefault is not null && !string.IsNullOrEmpty(active.Series) &&
                !launcherDefault.StartsWith(active.Series, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new DoctorFinding
                {
                    Id = "launcher-mismatch",
                    Severity = DoctorSeverity.Info,
                    Title = "The py launcher defaults to a different version",
                    Detail = $"`py` starts Python {launcherDefault} while `python` starts {active.Version}.",
                    Fix = $"pyvswitch use {active.Tag}"
                });
            }
        }

        return findings;
    }

    internal static List<DoctorFinding> AnalyzePath(string machinePath, string userPath, ActiveState state, string? storeAliasDirectory)
    {
        var findings = new List<DoctorFinding>();
        var machineEntries = PathEditor.Split(machinePath);
        var userEntries = PathEditor.Split(userPath);
        var effectiveEntries = machineEntries.Concat(userEntries).ToList();

        if (state.EffectivePath is null)
        {
            findings.Add(new DoctorFinding
            {
                Id = "no-python",
                Severity = DoctorSeverity.Error,
                Title = "No python.exe is on PATH",
                Detail = "New terminals will not find `python`.",
                Fix = "pyvswitch use <version>"
            });
        }
        else if (state.IsShadowed)
        {
            findings.Add(new DoctorFinding
            {
                Id = "shadowed",
                Severity = DoctorSeverity.Warning,
                Title = "A System PATH entry overrides your selection",
                Detail = $"You selected {state.Selected!.DisplayName}, but Windows puts System PATH entries first, so `python` runs {state.EffectivePath}.",
                Fix = $"Run elevated: pyvswitch use {state.Selected.Tag} --scope system   (or remove that folder from the System PATH)"
            });
        }
        else
        {
            findings.Add(new DoctorFinding
            {
                Id = "active",
                Severity = DoctorSeverity.Ok,
                Title = $"`python` resolves to {state.Effective?.DisplayName ?? "Python"}",
                Detail = state.EffectivePath
            });
        }

        if (storeAliasDirectory is not null && IsStoreAliasPresent(storeAliasDirectory))
        {
            var aliasIndex = IndexOfDirectory(effectiveEntries, storeAliasDirectory);
            var pythonIndex = state.EffectivePath is null
                ? int.MaxValue
                : IndexOfDirectory(effectiveEntries, Path.GetDirectoryName(state.EffectivePath)!);
            if (aliasIndex >= 0 && aliasIndex < pythonIndex)
            {
                findings.Add(new DoctorFinding
                {
                    Id = "store-alias",
                    Severity = DoctorSeverity.Warning,
                    Title = "The Microsoft Store alias answers `python` first",
                    Detail = $"{storeAliasDirectory} comes before any real interpreter, so `python` may open the Microsoft Store instead of the version you selected.",
                    Fix = "Switch to a version with pyvswitch, or turn off the python.exe aliases in Settings > Apps > Advanced app settings > App execution aliases."
                });
            }
        }

        var dangling = effectiveEntries
            .Where(entry => LooksLikePythonDirectory(entry) && !Directory.Exists(PathEditor.Normalize(entry)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (dangling.Count > 0)
        {
            var userOwned = dangling.Any(entry => userEntries.Contains(entry, StringComparer.OrdinalIgnoreCase));
            findings.Add(new DoctorFinding
            {
                Id = "dangling",
                Severity = DoctorSeverity.Warning,
                Title = $"{dangling.Count} PATH entr{(dangling.Count == 1 ? "y points" : "ies point")} to a missing Python folder",
                Detail = string.Join(Environment.NewLine, dangling),
                Fix = "pyvswitch doctor --fix",
                AutoFixable = userOwned
            });
        }

        var duplicates = userEntries
            .GroupBy(PathEditor.Normalize, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.First())
            .ToList();
        if (duplicates.Count > 0)
        {
            findings.Add(new DoctorFinding
            {
                Id = "duplicates",
                Severity = DoctorSeverity.Info,
                Title = $"{duplicates.Count} duplicate entr{(duplicates.Count == 1 ? "y" : "ies")} in the User PATH",
                Detail = string.Join(Environment.NewLine, duplicates),
                Fix = "pyvswitch doctor --fix",
                AutoFixable = true
            });
        }

        var msys = effectiveEntries.FirstOrDefault(entry =>
            PythonDiscoveryService.IsMsysPython(PathEditor.Normalize(entry) + "\\") &&
            File.Exists(Path.Combine(PathEditor.Normalize(entry), "python.exe")));
        if (msys is not null && state.EffectivePath is not null &&
            IndexOfDirectory(effectiveEntries, msys) < IndexOfDirectory(effectiveEntries, Path.GetDirectoryName(state.EffectivePath)!))
        {
            findings.Add(new DoctorFinding
            {
                Id = "msys",
                Severity = DoctorSeverity.Info,
                Title = "An MSYS2/Cygwin Python is ahead of the selected interpreter",
                Detail = msys,
                Fix = "Move that folder after your Python entries or remove it from PATH."
            });
        }

        return findings;
    }

    /// <summary>Removes dead Python folders and duplicate entries from the User PATH.</summary>
    public PathBackup? Fix()
    {
        var previous = PathSwitchService.ReadPath(PathScope.User);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = PathEditor.Split(previous)
            .Where(entry => !(LooksLikePythonDirectory(entry) && !Directory.Exists(PathEditor.Normalize(entry))))
            .Where(entry => seen.Add(PathEditor.Normalize(entry)))
            .ToList();

        var newPath = string.Join(';', cleaned);
        if (string.Equals(newPath, string.Join(';', PathEditor.Split(previous)), StringComparison.Ordinal))
        {
            return null;
        }

        _pathSwitch.WritePath(PathScope.User, newPath);
        return new PathBackup { Scope = PathScope.User, PreviousPath = previous, NewPath = newPath, SelectedPython = "" };
    }

    private static bool LooksLikePythonDirectory(string entry)
    {
        var normalized = PathEditor.Normalize(entry);
        var leaf = Path.GetFileName(normalized);
        var parent = Path.GetFileName(Path.GetDirectoryName(normalized) ?? "");
        return leaf.StartsWith("Python", StringComparison.OrdinalIgnoreCase) ||
               (leaf.Equals("Scripts", StringComparison.OrdinalIgnoreCase) && parent.StartsWith("Python", StringComparison.OrdinalIgnoreCase));
    }

    private static int IndexOfDirectory(IReadOnlyList<string> entries, string directory)
    {
        var target = PathEditor.Normalize(directory);
        for (var i = 0; i < entries.Count; i++)
        {
            if (string.Equals(PathEditor.Normalize(entries[i]), target, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string StoreAliasDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
    }

    private static bool IsStoreAliasPresent(string directory)
    {
        try
        {
            var alias = new FileInfo(Path.Combine(directory, "python.exe"));
            return alias.Exists && alias.Length == 0;
        }
        catch
        {
            return false;
        }
    }
}
