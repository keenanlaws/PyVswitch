using PyVSwitch.Models;
using PyVSwitch.Services;

namespace PyVSwitch;

public sealed class SwitchResult
{
    public required PythonInstall Install { get; init; }
    public required PathBackup Backup { get; init; }
    public required ActiveState State { get; init; }
    public string? Warning { get; init; }
}

/// <summary>
/// The one implementation of every pyvswitch operation. The desktop app, the CLI and the MCP
/// server all call this, so they always behave identically.
/// </summary>
public sealed class PyEngine
{
    public SettingsStore Settings { get; }
    public PythonDiscoveryService Discovery { get; } = new();
    public PathSwitchService PathSwitch { get; } = new();
    public PythonLauncherConfigService Launcher { get; } = new();
    public PythonCatalogService Catalog { get; } = new();
    public PythonInstallerService Installer { get; } = new();
    public PipService Pip { get; } = new();
    public PyPiService PyPi { get; } = new();
    public DoctorService Doctor { get; }
    public AiIntegrationService Ai { get; }

    public PyEngine(SettingsStore? settings = null)
    {
        Settings = settings ?? new SettingsStore();
        Doctor = new DoctorService(PathSwitch, Pip, Launcher);
        Ai = new AiIntegrationService();
    }

    // Interpreters ---------------------------------------------------------------------------

    public async Task<IReadOnlyList<PythonInstall>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var cached = Settings.Load().CachedInstalls;
        var installs = await Discovery.DiscoverAsync(cached, cancellationToken).ConfigureAwait(false);

        // Only write when something changed, so watchers of settings.json are not woken needlessly.
        if (!cached.Select(Fingerprint).SequenceEqual(installs.Select(Fingerprint)))
        {
            Settings.Update(settings => settings.CachedInstalls = installs.ToList());
        }

        return installs;

        static string Fingerprint(PythonInstall install) => $"{install.Id}|{install.Version}|{install.Architecture}";
    }

    public ActiveState GetActiveState(IReadOnlyList<PythonInstall> installs, PathScope? scope = null)
    {
        return PathSwitch.GetActiveState(installs, scope ?? Settings.Load().Scope);
    }

    /// <summary>Resolves a selector (version, tag, python.exe path, venv folder, or empty for the active one).</summary>
    public async Task<PythonInstall> ResolveAsync(IReadOnlyList<PythonInstall> installs, string? selector, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selector) || selector is "active" or "current")
        {
            var state = GetActiveState(installs);
            return state.Effective ?? state.Selected
                ?? throw new PyvsException("no_active", "No Python is active on PATH. Pick one with `pyvswitch use <version>` or pass -p <version>.");
        }

        selector = selector.Trim().Trim('"');
        if (VersionSelector.LooksLikePath(selector) || Directory.Exists(selector) || File.Exists(selector))
        {
            var full = Path.GetFullPath(selector);
            string[] candidates = File.Exists(full)
                ? [full]
                : [Path.Combine(full, "Scripts", "python.exe"), Path.Combine(full, "python.exe")];
            var executable = candidates.FirstOrDefault(File.Exists)
                ?? throw new PyvsException("not_found", $"No python.exe found at '{selector}'.");

            return installs.FirstOrDefault(install => install.ExecutablePath.Equals(executable, StringComparison.OrdinalIgnoreCase))
                ?? await PythonDiscoveryService.DescribeAsync(executable, "Path", cancellationToken).ConfigureAwait(false)
                ?? throw new PyvsException("not_found", $"No python.exe found at '{selector}'.");
        }

        return VersionSelector.Match(installs, selector)
            ?? throw new PyvsException("not_found",
                installs.Count == 0
                    ? $"Python {selector} is not installed. Install it with `pyvswitch install {selector}`."
                    : $"Python {selector} is not installed (installed: {string.Join(", ", installs.Select(install => install.Tag).Distinct())}). Install it with `pyvswitch install {selector}`.");
    }

    public SwitchResult Switch(PythonInstall install, IReadOnlyList<PythonInstall> installs, PathScope? scope = null, bool? updateLauncher = null)
    {
        if (install.IsVirtualEnvironment)
        {
            throw new PyvsException("usage", "A virtual environment cannot be the global default. Activate it in a shell, or target it with -p <folder>.");
        }

        var settings = Settings.Load();
        var effectiveScope = scope ?? settings.Scope;
        var backup = PathSwitch.SwitchTo(install, effectiveScope, installs);

        string? warning = null;
        if (updateLauncher ?? settings.UpdatePythonLauncherDefault)
        {
            try
            {
                var previous = Launcher.GetDefault();
                Launcher.SetDefault(install);
                backup.LauncherChanged = true;
                backup.PreviousLauncherDefault = previous;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "PATH switch succeeded, but the py launcher default could not be updated.");
                warning = "PATH switched, but the py launcher default (py.ini) could not be updated.";
            }
        }

        Settings.Update(current => current.PathBackups.Insert(0, backup));
        var state = PathSwitch.GetActiveState(installs, effectiveScope);
        if (state.IsShadowed)
        {
            warning = $"{install.DisplayName} is first in the User PATH, but a System PATH entry still wins: `python` runs {state.EffectivePath}. Run `pyvswitch doctor` for the fix.";
        }

        return new SwitchResult { Install = install, Backup = backup, State = state, Warning = warning };
    }

    /// <summary>Restores the PATH from the given backup, or from the most recent one.</summary>
    public PathBackup Rollback(PathBackup? backup = null)
    {
        var target = backup ?? Settings.Load().PathBackups.OrderByDescending(item => item.CreatedAt).FirstOrDefault()
            ?? throw new PyvsException("no_backup", "There is no PATH backup to roll back to yet.");

        PathSwitch.Restore(target);
        if (target.LauncherChanged)
        {
            try
            {
                Launcher.RestoreDefault(target.PreviousLauncherDefault);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error(ex, "PATH was restored, but the py launcher default could not be put back.");
            }
        }

        Settings.Update(settings => settings.PathBackups.RemoveAll(item => item.CreatedAt == target.CreatedAt && item.Scope == target.Scope));
        return target;
    }

    // Installing and removing Python ---------------------------------------------------------

    public async Task<InstallOutcome> InstallPythonAsync(string versionSpec, InstallOptions options, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var architecture = PythonInstallerService.NormalizeArchitecture(options.Architecture);
        progress?.Report(new InstallProgress { Stage = "resolve", Message = "Looking up python.org releases" });
        var catalog = await Catalog.GetAsync(false, cancellationToken).ConfigureAwait(false);
        var allowPrerelease = options.AllowPrerelease || (PythonVersion.TryParse(versionSpec, out var asked) && asked.IsPrerelease);
        var release = PythonCatalogService.ResolveRelease(catalog, versionSpec, architecture, allowPrerelease);
        var file = release.FindInstaller(architecture)!;

        var before = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var existing = before.FirstOrDefault(install => install.ParsedVersion.CompareTo(release.ParsedVersion) == 0 && install.Architecture == architecture);
        if (existing is not null)
        {
            if (options.Activate)
            {
                Switch(existing, before);
            }

            return new InstallOutcome { Release = release, File = file, Install = existing, AlreadyInstalled = true };
        }

        var installerPath = await Installer.DownloadAsync(file, progress, cancellationToken).ConfigureAwait(false);
        var logPath = await Installer.RunInstallerAsync(release, file, installerPath, options, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new InstallProgress { Stage = "verify", Message = "Checking the new interpreter" });
        var after = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var installed = after.FirstOrDefault(install => install.ParsedVersion.CompareTo(release.ParsedVersion) == 0 && install.Architecture == architecture)
            ?? throw new PyvsException("install_unverified", $"The installer finished, but Python {release.Version} ({architecture}) was not found afterwards. Log: {logPath}");

        try
        {
            File.Delete(installerPath);
        }
        catch (IOException)
        {
            // The cached installer is only a convenience.
        }

        if (options.Activate)
        {
            Switch(installed, after);
        }

        progress?.Report(new InstallProgress { Stage = "done", Message = $"Python {release.Version} is ready" });
        return new InstallOutcome { Release = release, File = file, Install = installed, LogPath = logPath };
    }

    public async Task UninstallPythonAsync(PythonInstall install, CancellationToken cancellationToken = default)
    {
        await Installer.UninstallAsync(install, cancellationToken).ConfigureAwait(false);

        // Leave no dead entries behind in the User PATH.
        var previous = PathSwitchService.ReadPath(PathScope.User);
        var cleaned = PathEditor.RemoveDirectories(previous, [install.InstallDirectory, Path.Combine(install.InstallDirectory, "Scripts")]);
        if (!string.Equals(cleaned, string.Join(';', PathEditor.Split(previous)), StringComparison.OrdinalIgnoreCase))
        {
            PathSwitch.WritePath(PathScope.User, cleaned);
            Settings.Update(settings => settings.PathBackups.Insert(0, new PathBackup
            {
                Scope = PathScope.User,
                PreviousPath = previous,
                NewPath = cleaned,
                SelectedPython = ""
            }));
        }

        await DiscoverAsync(cancellationToken).ConfigureAwait(false);
    }

    // Virtual environments -------------------------------------------------------------------

    public async Task<PythonInstall> CreateVenvAsync(PythonInstall python, string directory, Action<string>? onOutput = null, CancellationToken cancellationToken = default)
    {
        if (python.ParsedVersion.Major < 3 || (python.ParsedVersion.Major == 3 && python.ParsedVersion.Minor < 3))
        {
            throw new PyvsException("unsupported", $"{python.DisplayName} has no built-in venv module (it needs Python 3.3 or newer).");
        }

        var full = Path.GetFullPath(directory);
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !File.Exists(Path.Combine(full, "pyvenv.cfg")))
        {
            throw new PyvsException("not_empty", $"{full} already exists and is not a virtual environment.");
        }

        var result = await ProcessRunner.RunAsync(python.ExecutablePath, ["-m", "venv", full], TimeSpan.FromMinutes(10), onOutput, isolatePython: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        var executable = Path.Combine(full, "Scripts", "python.exe");
        if (!result.Success || !File.Exists(executable))
        {
            throw new PyvsException("venv_failed", $"Creating the virtual environment failed: {result.CombinedOutput}".Trim());
        }

        Settings.Update(settings =>
        {
            settings.Venvs.RemoveAll(venv => venv.Path.Equals(full, StringComparison.OrdinalIgnoreCase));
            settings.Venvs.Insert(0, new VenvRecord { Path = full, BaseVersion = python.Version, CreatedAt = DateTimeOffset.Now });
        });

        return await PythonDiscoveryService.DescribeAsync(executable, "Virtual environment", cancellationToken).ConfigureAwait(false)
            ?? throw new PyvsException("venv_failed", "The virtual environment was created but its interpreter could not be read.");
    }

    public async Task<IReadOnlyList<PythonInstall>> GetVenvsAsync(CancellationToken cancellationToken = default)
    {
        var records = Settings.Load().Venvs;
        var tasks = records
            .Select(record => Path.Combine(record.Path, "Scripts", "python.exe"))
            .Where(File.Exists)
            .Select(executable => PythonDiscoveryService.DescribeAsync(executable, "Virtual environment", cancellationToken));
        var described = await Task.WhenAll(tasks).ConfigureAwait(false);

        if (records.Any(record => !File.Exists(Path.Combine(record.Path, "Scripts", "python.exe"))))
        {
            Settings.Update(settings => settings.Venvs.RemoveAll(record => !File.Exists(Path.Combine(record.Path, "Scripts", "python.exe"))));
        }

        return described.Where(install => install is not null).Select(install => install!).ToList();
    }

    // Diagnostics ----------------------------------------------------------------------------

    public Task<List<DoctorFinding>> RunDoctorAsync(IReadOnlyList<PythonInstall> installs, CancellationToken cancellationToken = default)
    {
        return Doctor.RunAsync(installs, Settings.Load().Scope, cancellationToken);
    }

    public PathBackup? FixPath()
    {
        var backup = Doctor.Fix();
        if (backup is not null)
        {
            Settings.Update(settings => settings.PathBackups.Insert(0, backup));
        }

        return backup;
    }
}
