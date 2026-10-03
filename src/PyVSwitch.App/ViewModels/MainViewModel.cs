using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using PyVSwitch.App.Services;
using PyVSwitch.Models;
using PyVSwitch.Services;

namespace PyVSwitch.App.ViewModels;

/// <summary>What the view model needs from the window: dialogs, pickers and the clipboard.</summary>
public interface IShell
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false);
    string? PickFolder(string title);
    string? PickOpenFile(string title, string filter);
    string? PickSaveFile(string title, string filter, string defaultName);
    void CopyToClipboard(string text);
    void ShowBalloon(string title, string message);
}

public sealed partial class MainViewModel : Observable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _externalChangeTimer;
    private FileSystemWatcher? _settingsWatcher;
    private IReadOnlyList<PythonInstall> _installs = [];
    private ActiveState? _state;
    private AppSettings _settings = new();
    private string _page = "overview";
    private bool _isBusy;
    private bool _hasLoaded;

    public MainViewModel(PyEngine engine, Dispatcher dispatcher, bool isDemo = false)
    {
        Engine = engine;
        IsDemo = isDemo;
        _dispatcher = dispatcher;
        Activity = new ActivityViewModel(dispatcher);
        _externalChangeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, OnExternalChange, dispatcher) { IsEnabled = false };
        _lookupTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(380), DispatcherPriority.Background, OnLookupTimer, dispatcher) { IsEnabled = false };

        RefreshCommand = new RelayCommand(_ => RefreshAsync());
        NavigateCommand = new RelayCommand(parameter => Page = parameter as string ?? "overview");
        SwitchCommand = new RelayCommand(parameter => SwitchAsync(parameter as InstallItem));
        OpenPackagesCommand = new RelayCommand(parameter => OpenPackagesFor(parameter as InstallItem));
        OpenTerminalCommand = new RelayCommand(parameter => OpenTerminal(parameter as InstallItem ?? ActiveItem));
        OpenFolderCommand = new RelayCommand(parameter => OpenFolder((parameter as InstallItem ?? ActiveItem)?.Folder));
        CopyCommand = new RelayCommand(parameter => Copy(parameter as string));
        CopyPathCommand = new RelayCommand(parameter => Copy((parameter as InstallItem ?? ActiveItem)?.Path));
        CreateVenvCommand = new RelayCommand(parameter => CreateVenvAsync(parameter as InstallItem));
        UninstallPythonCommand = new RelayCommand(parameter => UninstallPythonAsync(parameter as InstallItem));
        ForgetVenvCommand = new RelayCommand(parameter => ForgetVenvAsync(parameter as InstallItem));
        UndoLastSwitchCommand = new RelayCommand(_ => RollbackAsync(null), _ => Backups.Count > 0);
        RestoreBackupCommand = new RelayCommand(parameter => RollbackAsync(parameter as BackupItem));
        DismissToastCommand = new RelayCommand(parameter =>
        {
            if (parameter is ToastItem toast) Toasts.Remove(toast);
        });

        InitializeDownloadCommands();
        InitializePackageCommands();
        InitializeToolCommands();
    }

    public PyEngine Engine { get; }
    public bool IsDemo { get; }
    public IShell Shell { get; set; } = null!;
    public ActivityViewModel Activity { get; }
    public ObservableCollection<ToastItem> Toasts { get; } = [];
    public ObservableCollection<InstallItem> Installs { get; } = [];
    public ObservableCollection<InstallItem> Venvs { get; } = [];
    public ObservableCollection<BackupItem> Backups { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand SwitchCommand { get; }
    public ICommand OpenPackagesCommand { get; }
    public ICommand OpenTerminalCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand CreateVenvCommand { get; }
    public ICommand UninstallPythonCommand { get; }
    public ICommand ForgetVenvCommand { get; }
    public ICommand UndoLastSwitchCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand DismissToastCommand { get; }

    /// <summary>Raised whenever the active interpreter may have changed, so the tray icon can follow.</summary>
    public event Action? ActiveChanged;

    public string AppVersion => $"v{AppInfo.Version}";

    public string Page
    {
        get => _page;
        set
        {
            if (Set(ref _page, value))
            {
                _ = OnPageChangedAsync(value);
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public bool HasLoaded
    {
        get => _hasLoaded;
        private set => Set(ref _hasLoaded, value);
    }

    // Active interpreter ---------------------------------------------------------------------

    public InstallItem? ActiveItem => Installs.FirstOrDefault(item => item.IsActive);
    public PythonInstall? ActiveInstall => _state?.Effective ?? (_state?.IsShadowed == true ? null : _state?.Selected);
    public bool HasActive => ActiveInstall is not null;
    public bool HasInstalls => Installs.Count > 0;
    public bool HasVenvs => Venvs.Count > 0;
    public bool HasBackups => Backups.Count > 0;
    public IEnumerable<BackupItem> RecentBackups => Backups.Take(3);
    public string ActiveTitle => ActiveInstall is { } active ? $"Python {active.Version}" : HasLoaded ? "No active Python" : "Looking for Python…";
    public string ActiveArchitecture => ActiveInstall?.ArchitectureLabel ?? "";
    public string ActivePath => _state?.EffectivePath ?? (HasLoaded ? "Nothing on PATH answers to `python` yet." : "");
    public string ActiveScopeText => $"{_settings.Scope} PATH";
    public string ActiveSummary => ActiveInstall is { } active ? $"{active.ArchitectureLabel}  ·  {_settings.Scope} PATH" : "Not set";
    public bool IsShadowed => _state?.IsShadowed == true;
    public string ShadowMessage => _state is { IsShadowed: true, Selected: not null }
        ? $"You selected {_state.Selected.DisplayName}, but a System PATH entry comes first, so python runs the version above. Open Doctor for the fix."
        : "";
    public string InstallCountText => Installs.Count.ToString();
    public string InstallCountCaption => Installs.Count == 1 ? "Python version installed" : "Python versions installed";

    public PythonInstall? TrayActiveInstall => ActiveInstall;
    public IReadOnlyList<PythonInstall> CurrentInstalls => _installs;
    public PathScope Scope => _settings.Scope;

    // Loading --------------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        await RefreshAsync();
        WatchForExternalChanges();
    }

    public async Task RefreshAsync(bool quiet = false)
    {
        if (!quiet)
        {
            IsBusy = true;
        }

        try
        {
            IReadOnlyList<PythonInstall> installs;
            IReadOnlyList<PythonInstall> venvs;
            if (IsDemo)
            {
                installs = DemoData.Installs;
                venvs = DemoData.Venvs;
                _settings = new AppSettings { PathBackups = DemoData.Backups };
                _state = DemoData.State(_demoActive ?? installs[0]);
            }
            else
            {
                installs = await Task.Run(() => Engine.DiscoverAsync());
                venvs = await Task.Run(() => Engine.GetVenvsAsync());
                _settings = await Task.Run(Engine.Settings.Load);
                _state = Engine.GetActiveState(installs, _settings.Scope);
            }

            _installs = installs;
            var activePath = ActiveInstall?.ExecutablePath;

            Sync(Installs, installs, activePath);
            Sync(Venvs, venvs, null);

            Backups.Clear();
            foreach (var backup in _settings.PathBackups.OrderByDescending(backup => backup.CreatedAt))
            {
                Backups.Add(new BackupItem(backup));
            }

            HasLoaded = true;
            RaiseActiveProperties();
            RaiseSettingsProperties();
            RebuildPackageTargets();
            RebuildSeries();
            ActiveChanged?.Invoke();
            _ = RefreshBackgroundFactsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Refresh failed.");
            Toast($"Refresh failed: {ex.Message}", "danger");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Sync(ObservableCollection<InstallItem> target, IReadOnlyList<PythonInstall> source, string? activePath)
    {
        target.Clear();
        foreach (var install in source)
        {
            target.Add(new InstallItem(install)
            {
                IsActive = activePath is not null && install.ExecutablePath.Equals(activePath, StringComparison.OrdinalIgnoreCase)
            });
        }
    }

    private void RaiseActiveProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(ActiveItem), nameof(ActiveInstall), nameof(HasActive), nameof(HasInstalls), nameof(HasVenvs), nameof(HasBackups), nameof(RecentBackups),
                     nameof(ActiveTitle), nameof(ActiveArchitecture), nameof(ActivePath), nameof(ActiveScopeText), nameof(ActiveSummary),
                     nameof(IsShadowed), nameof(ShadowMessage), nameof(InstallCountText), nameof(InstallCountCaption)
                 })
        {
            Raise(name);
        }
    }

    /// <summary>Slower facts for the overview tiles; they fill in after the page is already usable.</summary>
    private async Task RefreshBackgroundFactsAsync()
    {
        try
        {
            await RunDoctorAsync(quiet: true);
            if (IsDemo)
            {
                ActivePackageCount = DemoData.Packages.Count.ToString();
            }
            else if (ActiveInstall is { } active)
            {
                var packages = await Engine.Pip.ListAsync(active);
                ActivePackageCount = packages.Count.ToString();
            }
            else
            {
                ActivePackageCount = "–";
            }
        }
        catch (Exception ex)
        {
            ActivePackageCount = "–";
            AppLog.Error(ex, "Background refresh failed.");
        }
    }

    private string _activePackageCount = "…";

    public string ActivePackageCount
    {
        get => _activePackageCount;
        private set => Set(ref _activePackageCount, value);
    }

    private async Task OnPageChangedAsync(string page)
    {
        switch (page)
        {
            case "download":
                await EnsureCatalogAsync();
                break;
            case "packages":
                await EnsurePackagesAsync();
                break;
            case "doctor":
                await RunDoctorAsync(quiet: Findings.Count > 0);
                break;
            case "ai":
                await RefreshAiAsync();
                break;
        }
    }

    // The CLI and MCP server write settings.json too; follow their changes live.
    private void WatchForExternalChanges()
    {
        if (IsDemo)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(AppInfo.DataDirectory);
            _settingsWatcher = new FileSystemWatcher(AppInfo.DataDirectory, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler handler = (_, _) => _dispatcher.BeginInvoke(() =>
            {
                _externalChangeTimer.Stop();
                _externalChangeTimer.Start();
            });
            _settingsWatcher.Changed += handler;
            _settingsWatcher.Created += handler;
            _settingsWatcher.Renamed += (_, _) => handler(this, null!);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Could not watch settings.json.");
        }
    }

    private async void OnExternalChange(object? sender, EventArgs e)
    {
        _externalChangeTimer.Stop();
        if (!IsBusy && !Activity.IsRunning && DateTime.UtcNow - _lastOwnWrite > TimeSpan.FromSeconds(2))
        {
            await RefreshAsync(quiet: true);
        }
    }

    private DateTime _lastOwnWrite = DateTime.MinValue;

    private void MarkOwnWrite() => _lastOwnWrite = DateTime.UtcNow;

    // Switching ------------------------------------------------------------------------------

    private PythonInstall? _demoActive;

    public async Task SwitchAsync(InstallItem? item)
    {
        if (item is null || item.IsActive)
        {
            return;
        }

        try
        {
            string? warning = null;
            if (IsDemo)
            {
                _demoActive = item.Install;
            }
            else
            {
                MarkOwnWrite();
                var result = await Task.Run(() => Engine.Switch(item.Install, _installs));
                warning = result.Warning;
            }

            await RefreshAsync(quiet: true);
            Toast($"{item.Title} ({item.Architecture}) is now the default python", "success");
            if (warning is not null)
            {
                Toast(warning, "warning");
            }

            if (_settings.ShowNotifications)
            {
                Shell.ShowBalloon("Python switched", $"{item.Install.DisplayName} is now first on the {_settings.Scope} PATH.");
            }
        }
        catch (PyvsException ex)
        {
            Toast(ex.Message, "danger");
        }
    }

    private async Task RollbackAsync(BackupItem? item)
    {
        var target = item ?? Backups.FirstOrDefault();
        if (target is null)
        {
            return;
        }

        if (!await Shell.ConfirmAsync("Restore this PATH?", $"The {target.Backup.Scope} PATH goes back to how it was before \"{target.Title}\" ({target.Backup.CreatedAt.LocalDateTime:g}).", "Restore"))
        {
            return;
        }

        try
        {
            MarkOwnWrite();
            await Task.Run(() => Engine.Rollback(target.Backup));
            await RefreshAsync(quiet: true);
            Toast("PATH restored", "success");
        }
        catch (PyvsException ex)
        {
            Toast(ex.Message, "danger");
        }
    }

    // Per-interpreter actions ----------------------------------------------------------------

    private void OpenPackagesFor(InstallItem? item)
    {
        var target = item ?? ActiveItem;
        if (target is not null)
        {
            _pendingTarget = target.Path;
        }

        Page = "packages";
        if (target is not null)
        {
            SelectedTarget = PackageTargets.FirstOrDefault(candidate => candidate.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase)) ?? SelectedTarget;
        }
    }

    private void OpenTerminal(InstallItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            var directory = item.Install.InstallDirectory;
            var scripts = item.IsVenv ? directory : Path.Combine(directory, "Scripts");
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            start.ArgumentList.Add("/k");
            start.ArgumentList.Add($"title {item.Title} && python --version");
            start.Environment["PATH"] = $"{directory};{scripts};{Environment.GetEnvironmentVariable("PATH")}";
            start.Environment.Remove("PYTHONHOME");
            Process.Start(start);
        }
        catch (Exception ex)
        {
            Toast($"Could not open a terminal: {ex.Message}", "danger");
        }
    }

    private void OpenFolder(string? folder)
    {
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
    }

    private void Copy(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Shell.CopyToClipboard(text);
            Toast("Copied to clipboard", "accent");
        }
    }

    private async Task CreateVenvAsync(InstallItem? item)
    {
        if (item is null)
        {
            return;
        }

        var folder = Shell.PickFolder($"Choose the project folder for a {item.Title} virtual environment");
        if (folder is null)
        {
            return;
        }

        var target = Path.Combine(folder, ".venv");
        if (!await Shell.ConfirmAsync("Create virtual environment?", $"{target}\n\nIt will use {item.Title} ({item.Architecture}).", "Create"))
        {
            return;
        }

        await RunActivityAsync($"Creating virtual environment", target, async (log, cancel) =>
        {
            MarkOwnWrite();
            await Engine.CreateVenvAsync(item.Install, target, log, cancel);
            await RefreshAsync(quiet: true);
            return $"Virtual environment ready at {target}";
        });
    }

    private async Task ForgetVenvAsync(InstallItem? item)
    {
        if (item is null)
        {
            return;
        }

        MarkOwnWrite();
        await Task.Run(() => Engine.Settings.Update(settings =>
            settings.Venvs.RemoveAll(venv => venv.Path.Equals(item.Folder, StringComparison.OrdinalIgnoreCase))));
        await RefreshAsync(quiet: true);
        Toast("Removed from the list (the folder was not deleted)", "accent");
    }

    private async Task UninstallPythonAsync(InstallItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!await Shell.ConfirmAsync($"Uninstall {item.Title}?", $"{item.Folder}\n\nThe interpreter and every package installed in it are removed. Virtual environments built from it stop working.", "Uninstall", danger: true))
        {
            return;
        }

        await RunActivityAsync($"Uninstalling {item.Title}", item.Folder, async (_, cancel) =>
        {
            MarkOwnWrite();
            await Engine.UninstallPythonAsync(item.Install, cancel);
            await RefreshAsync(quiet: true);
            return $"{item.Title} was uninstalled";
        });
    }

    // Shared helpers -------------------------------------------------------------------------

    public void Toast(string message, string tone = "success")
    {
        var toast = new ToastItem { Message = message, Tone = tone };
        Toasts.Add(toast);
        while (Toasts.Count > 4)
        {
            Toasts.RemoveAt(0);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(tone is "danger" or "warning" ? 8 : 4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Toasts.Remove(toast);
        };
        timer.Start();
    }

    /// <summary>Runs one long task in the activity bar, with a live log, a cancel button and a result toast.</summary>
    private async Task RunActivityAsync(string title, string detail, Func<Action<string>, CancellationToken, Task<string>> work)
    {
        if (Activity.IsRunning)
        {
            Toast("Another task is still running. Wait for it to finish first.", "warning");
            return;
        }

        var cancel = Activity.Begin(title, detail);
        try
        {
            var message = await work(Activity.Append, cancel);
            Activity.Complete(true, message);
            Toast(message, "success");
        }
        catch (OperationCanceledException)
        {
            Activity.Complete(false, "Cancelled");
        }
        catch (PyvsException ex)
        {
            Activity.Append(ex.Message);
            Activity.Complete(false, FirstLine(ex.Message));
            Toast(FirstLine(ex.Message), "danger");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"Activity failed: {title}");
            Activity.Append(ex.ToString());
            Activity.Complete(false, ex.Message);
            Toast(ex.Message, "danger");
        }
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        return index < 0 ? text : text[..index];
    }
}
