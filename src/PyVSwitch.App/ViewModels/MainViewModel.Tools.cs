using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using PyVSwitch.App.Services;
using PyVSwitch.Models;
using PyVSwitch.Services;

namespace PyVSwitch.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly ShortcutService _shortcuts = new();
    private bool _isDoctorRunning;
    private bool _isAiBusy;

    public ObservableCollection<FindingItem> Findings { get; } = [];
    public ObservableCollection<PathEntryItem> PathEntries { get; } = [];
    public ObservableCollection<AiToolItem> AiTools { get; } = [];

    public ICommand RunDoctorCommand { get; private set; } = null!;
    public ICommand FixPathCommand { get; private set; } = null!;
    public ICommand ConnectAllAiCommand { get; private set; } = null!;
    public ICommand ToggleAiCommand { get; private set; } = null!;
    public ICommand CopyAiGuideCommand { get; private set; } = null!;
    public ICommand OpenLogsCommand { get; private set; } = null!;
    public ICommand DesktopShortcutCommand { get; private set; } = null!;
    public ICommand ResetAppDataCommand { get; private set; } = null!;
    public ICommand OpenRepoCommand { get; private set; } = null!;
    public ICommand OpenAppsSettingsCommand { get; private set; } = null!;
    public ICommand OpenLinkCommand { get; private set; } = null!;

    private void InitializeToolCommands()
    {
        RunDoctorCommand = new RelayCommand(_ => RunDoctorAsync(quiet: false), _ => !IsDoctorRunning);
        FixPathCommand = new RelayCommand(_ => FixPathAsync(), _ => CanAutoFix);
        ConnectAllAiCommand = new RelayCommand(_ => ChangeAiAsync(null, connect: true), _ => !IsAiBusy);
        ToggleAiCommand = new RelayCommand(parameter => parameter is AiToolItem tool ? ChangeAiAsync(tool, !tool.IsConnected) : Task.CompletedTask, _ => !IsAiBusy);
        CopyAiGuideCommand = new RelayCommand(_ => Copy(AiGuide.FullGuide));
        OpenLogsCommand = new RelayCommand(_ =>
        {
            Directory.CreateDirectory(AppLog.LogDirectory);
            OpenFolderPath(AppLog.LogDirectory);
        });
        DesktopShortcutCommand = new RelayCommand(_ =>
        {
            _shortcuts.CreateDesktopShortcut();
            Toast("Desktop shortcut created", "success");
        });
        ResetAppDataCommand = new RelayCommand(_ => ResetAppDataAsync());
        OpenRepoCommand = new RelayCommand(_ => OpenUrl(AppInfo.RepoUrl));
        OpenAppsSettingsCommand = new RelayCommand(_ => OpenUrl("ms-settings:appsfeatures"));
        OpenLinkCommand = new RelayCommand(parameter =>
        {
            // Links come from PyPI metadata, so only plain web addresses are ever opened.
            if (parameter is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            {
                OpenUrl(uri.AbsoluteUri);
            }
        });
    }

    // Doctor ---------------------------------------------------------------------------------

    public bool IsDoctorRunning
    {
        get => _isDoctorRunning;
        private set => Set(ref _isDoctorRunning, value);
    }

    public bool CanAutoFix => Findings.Any(finding => finding.Finding.AutoFixable);
    public int IssueCount => Findings.Count(finding => finding.Finding.Severity is DoctorSeverity.Warning or DoctorSeverity.Error);
    public bool IsHealthy => IssueCount == 0;
    public string HealthTitle => Findings.Count == 0 ? "…" : IsHealthy ? "Healthy" : IssueCount == 1 ? "1 issue" : $"{IssueCount} issues";
    public string HealthCaption => Findings.Count == 0 ? "Checking your setup" : IsHealthy ? "PATH and pip look right" : "Open Doctor to fix";
    public string HealthTone => Findings.Count == 0 ? "neutral" : IsHealthy ? "success" : "warning";
    public string DoctorHeadline => Findings.Count == 0
        ? "Checking your Python setup…"
        : IsHealthy ? "Everything looks right" : IssueCount == 1 ? "1 thing needs attention" : $"{IssueCount} things need attention";
    public string DoctorSubline => IsHealthy
        ? "python, pip and the py launcher all point where you expect."
        : "Each finding below explains what is wrong and how to fix it.";

    private async Task RunDoctorAsync(bool quiet)
    {
        if (IsDoctorRunning)
        {
            return;
        }

        IsDoctorRunning = !quiet;
        try
        {
            var findings = IsDemo ? DemoData.Findings : await Engine.RunDoctorAsync(_installs);
            Findings.Clear();
            foreach (var finding in findings.OrderByDescending(finding => finding.Severity))
            {
                Findings.Add(new FindingItem(finding));
            }

            RebuildPathEntries();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Doctor failed.");
        }
        finally
        {
            IsDoctorRunning = false;
            foreach (var name in new[] { nameof(CanAutoFix), nameof(IssueCount), nameof(IsHealthy), nameof(HealthTitle), nameof(HealthCaption), nameof(HealthTone), nameof(DoctorHeadline), nameof(DoctorSubline) })
            {
                Raise(name);
            }

            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void RebuildPathEntries()
    {
        PathEntries.Clear();
        var entries = IsDemo
            ? DemoData.PathEntries
            : PathEditor.Split(PathSwitchService.ReadPath(PathScope.System)).Select(path => ("System", path))
                .Concat(PathEditor.Split(PathSwitchService.ReadPath(PathScope.User)).Select(path => ("User", path)))
                .ToList();

        var activeFound = false;
        var order = 1;
        foreach (var (scope, path) in entries)
        {
            var kind = "other";
            if (IsDemo)
            {
                kind = DemoData.Classify(path, ref activeFound);
            }
            else
            {
                var directory = PathEditor.Normalize(path);
                var python = new FileInfo(Path.Combine(directory, "python.exe"));
                if (python.Exists && python.Length == 0)
                {
                    kind = "alias";
                }
                else if (python.Exists)
                {
                    kind = activeFound ? "python" : "active";
                    activeFound = true;
                }
                else if (!Directory.Exists(directory) && Path.GetFileName(directory).StartsWith("Python", StringComparison.OrdinalIgnoreCase))
                {
                    kind = "missing";
                }
                else if (Path.GetFileName(directory).Equals("Scripts", StringComparison.OrdinalIgnoreCase) &&
                         File.Exists(Path.Combine(Path.GetDirectoryName(directory) ?? "", "python.exe")))
                {
                    kind = "python";
                }
            }

            PathEntries.Add(new PathEntryItem { Order = order++, Scope = scope, Path = path, Kind = kind });
        }
    }

    private async Task FixPathAsync()
    {
        if (!await Shell.ConfirmAsync("Clean up the User PATH?", "Entries that point to missing Python folders and exact duplicates are removed. A backup is saved, so you can undo it from Settings.", "Clean up"))
        {
            return;
        }

        try
        {
            MarkOwnWrite();
            var backup = await Task.Run(Engine.FixPath);
            Toast(backup is null ? "Nothing needed fixing" : "User PATH cleaned up", "success");
            await RefreshAsync(quiet: true);
        }
        catch (PyvsException ex)
        {
            Toast(ex.Message, "danger");
        }
    }

    // AI integration -------------------------------------------------------------------------

    public bool IsAiBusy
    {
        get => _isAiBusy;
        private set => Set(ref _isAiBusy, value);
    }

    public string CliPath => Engine.Ai.CliPath;
    public string McpSnippet => $$"""
        {
          "mcpServers": {
            "pyvswitch": {
              "command": "{{Engine.Ai.CliPath.Replace("\\", "\\\\")}}",
              "args": ["mcp"]
            }
          }
        }
        """;

    public IReadOnlyList<CommandExample> CommandExamples { get; } =
    [
        new() { Title = "See what is installed", Command = "pyvswitch list --json" },
        new() { Title = "Switch the default python", Command = "pyvswitch use 3.12" },
        new() { Title = "Download and install a version", Command = "pyvswitch install 3.13 --use" },
        new() { Title = "Install a library into one version", Command = "pyvswitch add requests -p 3.12" },
        new() { Title = "Run a script with a specific version", Command = "pyvswitch run -p 3.10-32 -- script.py" },
        new() { Title = "Print the full guide for an AI", Command = "pyvswitch ai guide" }
    ];

    public string AiSummary
    {
        get
        {
            var detected = AiTools.Count(tool => tool.Detected);
            var connected = AiTools.Count(tool => tool.Detected && tool.IsConnected);
            return detected == 0
                ? "No supported AI tools were found on this PC yet."
                : $"{connected} of {detected} AI tools on this PC know about pyvswitch.";
        }
    }

    private async Task RefreshAiAsync()
    {
        var statuses = IsDemo ? DemoData.AiTools : await Task.Run(Engine.Ai.GetStatus);
        ShowAi(statuses);
    }

    private void ShowAi(IEnumerable<AiTargetStatus> statuses)
    {
        AiTools.Clear();
        foreach (var status in statuses.OrderByDescending(status => status.Detected))
        {
            AiTools.Add(new AiToolItem(status));
        }

        Raise(nameof(AiSummary));
    }

    private async Task ChangeAiAsync(AiToolItem? tool, bool connect)
    {
        if (IsDemo)
        {
            Toast("Connected (demo)", "success");
            return;
        }

        IsAiBusy = true;
        try
        {
            string[]? only = tool is null ? null : [tool.Status.Id];
            var results = connect
                ? await Task.Run(() => Engine.Ai.SetupAsync(only))
                : await Task.Run(() => Engine.Ai.RemoveAsync(only));
            var changes = results.Sum(result => result.Changes.Count);
            Toast(changes == 0
                    ? connect ? "Already connected" : "Nothing to remove"
                    : connect ? "Connected. Restart the AI tool so it picks this up." : "Disconnected",
                "success");
            await RefreshAiAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "AI integration change failed.");
            Toast(FirstLine(ex.Message), "danger");
        }
        finally
        {
            IsAiBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // Settings -------------------------------------------------------------------------------

    public PathScope PathScope
    {
        get => _settings.Scope;
        set => ChangeSetting(settings => settings.Scope = value, refresh: true);
    }

    public AppTheme Theme
    {
        get => _settings.Theme;
        set
        {
            ChangeSetting(settings => settings.Theme = value);
            ThemeService.Apply(value);
        }
    }

    public bool StartWithWindows
    {
        get => _shortcuts.IsStartupEnabled();
        set
        {
            _shortcuts.SetStartup(value);
            ChangeSetting(settings => settings.StartWithWindows = value);
        }
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set => ChangeSetting(settings => settings.MinimizeToTray = value);
    }

    public bool ShowNotifications
    {
        get => _settings.ShowNotifications;
        set => ChangeSetting(settings => settings.ShowNotifications = value);
    }

    public bool UpdateLauncherDefault
    {
        get => _settings.UpdatePythonLauncherDefault;
        set => ChangeSetting(settings => settings.UpdatePythonLauncherDefault = value);
    }

    private void ChangeSetting(Action<AppSettings> change, bool refresh = false)
    {
        change(_settings);
        if (!IsDemo)
        {
            MarkOwnWrite();
            Engine.Settings.Update(change);
        }

        RaiseSettingsProperties();
        if (refresh)
        {
            _ = RefreshAsync(quiet: true);
        }
    }

    private void RaiseSettingsProperties()
    {
        foreach (var name in new[] { nameof(PathScope), nameof(Theme), nameof(StartWithWindows), nameof(MinimizeToTray), nameof(ShowNotifications), nameof(UpdateLauncherDefault), nameof(ActiveScopeText), nameof(ActiveSummary) })
        {
            Raise(name);
        }
    }

    private async Task ResetAppDataAsync()
    {
        if (!await Shell.ConfirmAsync("Delete pyvswitch data?", "Saved settings, the cached version list and PATH backups are deleted. Your Python installations and the current PATH are not touched.", "Delete", danger: true))
        {
            return;
        }

        try
        {
            MarkOwnWrite();
            _settingsWatcher?.Dispose();
            _settingsWatcher = null;
            if (Directory.Exists(AppInfo.DataDirectory))
            {
                Directory.Delete(AppInfo.DataDirectory, true);
            }

            await RefreshAsync(quiet: true);
            WatchForExternalChanges();
            Toast("App data deleted", "success");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast($"Could not delete app data: {ex.Message}", "danger");
        }
    }

    private void OpenFolderPath(string folder) => Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });

    private void OpenUrl(string url) => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
}
