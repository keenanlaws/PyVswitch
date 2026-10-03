using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using PyVSwitch.App.Services;
using PyVSwitch.Models;
using PyVSwitch.Services;

namespace PyVSwitch.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly DispatcherTimer _lookupTimer;
    private readonly ObservableCollection<PackageItem> _packages = [];
    private CancellationTokenSource? _lookupCancellation;
    private InstallItem? _selectedTarget;
    private string? _pendingTarget;
    private string? _loadedTarget;
    private string _packageFilter = "";
    private string _newPackage = "";
    private string _lookupState = "idle";
    private PyPiPackage? _lookup;
    private string? _lookupVersion;
    private bool _isLoadingPackages;
    private string _packagesMessage = "";

    public ObservableCollection<InstallItem> PackageTargets { get; } = [];
    public ObservableCollection<string> Suggestions { get; } = [];
    public ICollectionView PackagesView { get; private set; } = null!;

    public ICommand InstallPackageCommand { get; private set; } = null!;
    public ICommand UninstallPackageCommand { get; private set; } = null!;
    public ICommand UpgradePackageCommand { get; private set; } = null!;
    public ICommand UpgradeAllCommand { get; private set; } = null!;
    public ICommand CheckUpdatesCommand { get; private set; } = null!;
    public ICommand ImportRequirementsCommand { get; private set; } = null!;
    public ICommand ExportRequirementsCommand { get; private set; } = null!;
    public ICommand PickSuggestionCommand { get; private set; } = null!;
    public ICommand ReloadPackagesCommand { get; private set; } = null!;

    private void InitializePackageCommands()
    {
        PackagesView = CollectionViewSource.GetDefaultView(_packages);
        PackagesView.Filter = item => item is PackageItem package &&
            (PackageFilter.Length == 0 || package.Name.Contains(PackageFilter, StringComparison.OrdinalIgnoreCase));

        InstallPackageCommand = new RelayCommand(_ => InstallPackagesAsync(), _ => NewPackage.Trim().Length > 0 && SelectedTarget is not null);
        UninstallPackageCommand = new RelayCommand(parameter => UninstallPackageAsync(parameter as PackageItem));
        UpgradePackageCommand = new RelayCommand(parameter => UpgradePackagesAsync(parameter is PackageItem package ? [package.Name] : []));
        UpgradeAllCommand = new RelayCommand(
            _ => UpgradePackagesAsync(_packages.Where(package => package.IsOutdated).Select(package => package.Name).ToList()),
            _ => OutdatedCount > 0);
        CheckUpdatesCommand = new RelayCommand(_ => CheckUpdatesAsync(), _ => SelectedTarget is not null && !IsLoadingPackages);
        ImportRequirementsCommand = new RelayCommand(_ => ImportRequirementsAsync(), _ => SelectedTarget is not null);
        ExportRequirementsCommand = new RelayCommand(_ => ExportRequirementsAsync(), _ => SelectedTarget is not null);
        PickSuggestionCommand = new RelayCommand(parameter => NewPackage = parameter as string ?? "");
        ReloadPackagesCommand = new RelayCommand(_ => LoadPackagesAsync());
        RefreshSuggestions();
    }

    public InstallItem? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (Set(ref _selectedTarget, value) && value is not null && Page == "packages")
            {
                _ = LoadPackagesAsync();
            }
        }
    }

    public string PackageFilter
    {
        get => _packageFilter;
        set
        {
            if (Set(ref _packageFilter, value))
            {
                PackagesView.Refresh();
                Raise(nameof(PackageCountText));
            }
        }
    }

    public string NewPackage
    {
        get => _newPackage;
        set
        {
            if (Set(ref _newPackage, value))
            {
                RefreshSuggestions();
                _lookupTimer.Stop();
                _lookupTimer.Start();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>idle, searching, found, missing or error.</summary>
    public string LookupState
    {
        get => _lookupState;
        private set => Set(ref _lookupState, value);
    }

    public PyPiPackage? Lookup
    {
        get => _lookup;
        private set
        {
            if (Set(ref _lookup, value))
            {
                Raise(nameof(LookupVersions));
                Raise(nameof(LookupRequires));
            }
        }
    }

    public IReadOnlyList<string> LookupVersions => Lookup?.Versions ?? [];
    public string LookupRequires => string.IsNullOrEmpty(Lookup?.RequiresPython) ? "" : $"Requires Python {Lookup.RequiresPython}";

    public string? LookupVersion
    {
        get => _lookupVersion;
        set => Set(ref _lookupVersion, value);
    }

    public bool IsLoadingPackages
    {
        get => _isLoadingPackages;
        private set => Set(ref _isLoadingPackages, value);
    }

    public string PackagesMessage
    {
        get => _packagesMessage;
        private set => Set(ref _packagesMessage, value);
    }

    public int OutdatedCount => _packages.Count(package => package.IsOutdated);
    public string UpgradeAllLabel => OutdatedCount > 0 ? $"Update all ({OutdatedCount})" : "Update all";

    public string PackageCountText
    {
        get
        {
            if (SelectedTarget is null) return "";
            var shown = PackagesView.Cast<object>().Count();
            return shown == _packages.Count ? $"{_packages.Count} packages" : $"{shown} of {_packages.Count} packages";
        }
    }

    private void RebuildPackageTargets()
    {
        var wanted = _pendingTarget ?? SelectedTarget?.Path ?? ActiveInstall?.ExecutablePath;
        _pendingTarget = null;
        PackageTargets.Clear();
        foreach (var item in Installs.Concat(Venvs))
        {
            PackageTargets.Add(item);
        }

        _selectedTarget = PackageTargets.FirstOrDefault(item => item.Path.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ?? PackageTargets.FirstOrDefault();
        Raise(nameof(SelectedTarget));
        if (Page == "packages" && _selectedTarget?.Path != _loadedTarget)
        {
            _ = LoadPackagesAsync();
        }
    }

    private Task EnsurePackagesAsync()
    {
        return SelectedTarget is not null && SelectedTarget.Path != _loadedTarget ? LoadPackagesAsync() : Task.CompletedTask;
    }

    private async Task LoadPackagesAsync()
    {
        var target = SelectedTarget;
        if (target is null)
        {
            _packages.Clear();
            return;
        }

        IsLoadingPackages = true;
        PackagesMessage = "";
        try
        {
            var packages = IsDemo ? DemoData.Packages : await Engine.Pip.ListAsync(target.Install);
            if (SelectedTarget != target)
            {
                return;
            }

            _packages.Clear();
            foreach (var package in packages)
            {
                _packages.Add(new PackageItem(package));
            }

            _loadedTarget = target.Path;
        }
        catch (PyvsException ex)
        {
            _packages.Clear();
            _loadedTarget = null;
            PackagesMessage = ex.Code == "no_pip"
                ? $"{target.Title} has no pip yet. Install any package to bootstrap it, or run: python -m ensurepip"
                : FirstLine(ex.Message);
        }
        finally
        {
            IsLoadingPackages = false;
            RaisePackageCounts();
            RefreshSuggestions();
        }
    }

    private void RaisePackageCounts()
    {
        Raise(nameof(OutdatedCount));
        Raise(nameof(UpgradeAllLabel));
        Raise(nameof(PackageCountText));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task CheckUpdatesAsync()
    {
        var target = SelectedTarget;
        if (target is null)
        {
            return;
        }

        IsLoadingPackages = true;
        try
        {
            var outdated = IsDemo
                ? DemoData.Packages.Where(package => package.LatestVersion is not null).ToList()
                : await Engine.Pip.ListAsync(target.Install, outdatedOnly: true);
            foreach (var package in _packages)
            {
                package.LatestVersion = outdated.FirstOrDefault(item => item.Name.Equals(package.Name, StringComparison.OrdinalIgnoreCase))?.LatestVersion;
            }

            Toast(outdated.Count == 0 ? "Every package is up to date" : $"{outdated.Count} package{(outdated.Count == 1 ? " has" : "s have")} an update", outdated.Count == 0 ? "success" : "accent");
        }
        catch (PyvsException ex)
        {
            Toast(FirstLine(ex.Message), "danger");
        }
        finally
        {
            IsLoadingPackages = false;
            RaisePackageCounts();
        }
    }

    private async Task InstallPackagesAsync()
    {
        var target = SelectedTarget;
        var specs = NewPackage.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (target is null || specs.Count == 0)
        {
            return;
        }

        // A specific version picked from the PyPI card applies when a single bare name was typed.
        if (specs.Count == 1 && Lookup is not null && LookupVersion is not null && LookupVersion != Lookup.LatestVersion &&
            specs[0].Equals(Lookup.Name, StringComparison.OrdinalIgnoreCase))
        {
            specs[0] = $"{Lookup.Name}=={LookupVersion}";
        }

        var subject = string.Join(", ", specs);
        await RunPipActivityAsync($"Installing {subject}", target, async (log, cancel) =>
        {
            if (await Engine.Pip.GetVersionAsync(target.Install, cancel) is null)
            {
                log("pip is missing; bootstrapping it with ensurepip.");
                await Engine.Pip.EnsurePipAsync(target.Install, log, cancel);
            }

            return await Engine.Pip.InstallAsync(target.Install, specs, upgrade: false, log, cancel);
        }, $"Installed {subject} in {target.Title}");
        NewPackage = "";
    }

    private async Task UninstallPackageAsync(PackageItem? package)
    {
        var target = SelectedTarget;
        if (package is null || target is null)
        {
            return;
        }

        if (!await Shell.ConfirmAsync($"Remove {package.Name}?", $"{package.Name} {package.Version} is removed from {target.Title}. Other interpreters are not affected.", "Remove", danger: true))
        {
            return;
        }

        await RunPipActivityAsync($"Removing {package.Name}", target,
            (log, cancel) => Engine.Pip.UninstallAsync(target.Install, [package.Name], log, cancel),
            $"Removed {package.Name} from {target.Title}");
    }

    private async Task UpgradePackagesAsync(IReadOnlyCollection<string> names)
    {
        var target = SelectedTarget;
        if (target is null || names.Count == 0)
        {
            return;
        }

        var subject = names.Count == 1 ? names.First() : $"{names.Count} packages";
        await RunPipActivityAsync($"Updating {subject}", target,
            (log, cancel) => Engine.Pip.InstallAsync(target.Install, names, upgrade: true, log, cancel),
            $"Updated {subject} in {target.Title}");
    }

    private async Task ImportRequirementsAsync()
    {
        var target = SelectedTarget;
        var file = Shell.PickOpenFile("Choose a requirements file", "Requirements (*.txt)|*.txt|All files (*.*)|*.*");
        if (target is null || file is null)
        {
            return;
        }

        await RunPipActivityAsync($"Installing from {Path.GetFileName(file)}", target,
            (log, cancel) => Engine.Pip.InstallRequirementsAsync(target.Install, file, log, cancel),
            $"Installed {Path.GetFileName(file)} into {target.Title}");
    }

    private async Task ExportRequirementsAsync()
    {
        var target = SelectedTarget;
        if (target is null)
        {
            return;
        }

        var file = Shell.PickSaveFile("Save requirements", "Requirements (*.txt)|*.txt", "requirements.txt");
        if (file is null)
        {
            return;
        }

        try
        {
            var text = IsDemo
                ? string.Join(Environment.NewLine, DemoData.Packages.Select(package => $"{package.Name}=={package.Version}"))
                : await Engine.Pip.FreezeAsync(target.Install);
            await File.WriteAllTextAsync(file, text + Environment.NewLine);
            Toast($"Saved {Path.GetFileName(file)}", "success");
        }
        catch (Exception ex) when (ex is PyvsException or IOException or UnauthorizedAccessException)
        {
            Toast(FirstLine(ex.Message), "danger");
        }
    }

    private async Task RunPipActivityAsync(string title, InstallItem target, Func<Action<string>, CancellationToken, Task<PipResult>> work, string successMessage)
    {
        if (IsDemo)
        {
            Toast($"{successMessage} (demo)", "success");
            return;
        }

        await RunActivityAsync(title, $"{target.Title}  ·  {target.Path}", async (log, cancel) =>
        {
            var result = await work(log, cancel);
            if (!result.Success)
            {
                throw new PyvsException("pip_failed", $"pip failed with exit code {result.ExitCode}. Open the log for details.");
            }

            _loadedTarget = null;
            await LoadPackagesAsync();
            return successMessage;
        });
    }

    // PyPI lookup ----------------------------------------------------------------------------

    private async void OnLookupTimer(object? sender, EventArgs e)
    {
        _lookupTimer.Stop();
        _lookupCancellation?.Cancel();

        var match = PackageNamePattern().Match(NewPackage.Trim());
        var hasSeveral = NewPackage.Trim().Contains(' ') || NewPackage.Contains(',');
        if (!match.Success || hasSeveral)
        {
            Lookup = null;
            LookupState = "idle";
            return;
        }

        var cancellation = _lookupCancellation = new CancellationTokenSource();
        LookupState = "searching";
        try
        {
            var package = IsDemo ? DemoData.Lookup(match.Value) : await Engine.PyPi.GetAsync(match.Value, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            Lookup = package;
            LookupVersion = package?.LatestVersion;
            LookupState = package is null ? "missing" : "found";
        }
        catch (OperationCanceledException)
        {
        }
        catch (PyvsException)
        {
            Lookup = null;
            LookupState = "error";
        }
    }

    private void RefreshSuggestions()
    {
        var typed = NewPackage.Trim();
        var installed = _packages.Select(package => package.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = PyPiService.PopularPackages
            .Where(name => !installed.Contains(name))
            .Where(name => typed.Length == 0 || (name.Contains(typed, StringComparison.OrdinalIgnoreCase) && !name.Equals(typed, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(name => typed.Length > 0 && name.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Take(typed.Length == 0 ? 10 : 8);

        Suggestions.Clear();
        foreach (var name in matches)
        {
            Suggestions.Add(name);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*")]
    private static partial Regex PackageNamePattern();
}
