using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using PythonVersionSwitch.Models;
using PythonVersionSwitch.Services;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;

namespace PythonVersionSwitch;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly SettingsStore _settingsStore = new();
    private readonly PythonDiscoveryService _discoveryService = new();
    private readonly PathSwitchService _pathSwitchService = new();
    private readonly PythonLauncherConfigService _launcherConfigService = new();
    private readonly ShortcutService _shortcutService = new();
    private readonly WingetService _wingetService = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly UninstallService _uninstallService;
    private TrayPopupWindow? _trayPopup;
    private AppSettings _settings;
    private bool _isExiting;
    private bool _isInitializingControls;
    private bool _isBusy;
    private string _activeTitle = "Checking...";
    private string _activeDetail = "Loading Python environment";
    private string _activePath = "";
    private string _statusMessage = "Ready";
    private string _healthText = "";
    private string _versionCountText = "";
    private AppTheme _effectiveTheme = AppTheme.Dark;

    public ObservableCollection<PythonInstall> PythonInstalls { get; } = [];
    public ObservableCollection<InstallablePythonPackage> InstallableVersions { get; } = [];
    public ObservableCollection<PathBackup> PathBackups { get; } = [];

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public string ActiveTitle
    {
        get => _activeTitle;
        private set => SetField(ref _activeTitle, value);
    }

    public string ActiveDetail
    {
        get => _activeDetail;
        private set => SetField(ref _activeDetail, value);
    }

    public string ActivePath
    {
        get => _activePath;
        private set => SetField(ref _activePath, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string HealthText
    {
        get => _healthText;
        private set => SetField(ref _healthText, value);
    }

    public string VersionCountText
    {
        get => _versionCountText;
        private set => SetField(ref _versionCountText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        _settings = _settingsStore.Load();
        _settings.Theme = AppTheme.Dark;
        _uninstallService = new UninstallService(_settingsStore, _shortcutService);
        InitializeComponent();
        DataContext = this;
        ConfigureControls();
        ApplyDarkTheme();
        ConfigureTrayIcon();
        SourceInitialized += (_, _) => WindowThemeService.ApplyDarkTitleBar(this);
        Closing += MainWindow_Closing;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private void ConfigureControls()
    {
        _isInitializingControls = true;
        ScopeCombo.ItemsSource = Enum.GetValues<PathScope>();
        ScopeCombo.SelectedItem = _settings.Scope;
        StartupCheck.IsChecked = _shortcutService.IsStartupEnabled();
        MinimizeCheck.IsChecked = _settings.MinimizeToTray;
        NotificationsCheck.IsChecked = _settings.ShowNotifications;
        LauncherCheck.IsChecked = _settings.UpdatePythonLauncherDefault;

        PathBackups.Clear();
        foreach (var backup in _settings.PathBackups.OrderByDescending(backup => backup.CreatedAt))
        {
            PathBackups.Add(backup);
        }

        _isInitializingControls = false;
    }

    private void ConfigureTrayIcon()
    {
        _trayIcon.Visible = true;
        _trayIcon.Text = "Python Version Switch";
        _trayIcon.Icon = TrayIconFactory.Create(null, _effectiveTheme);
        _trayIcon.MouseUp += (_, args) =>
        {
            if (args.Button is MouseButtons.Left or MouseButtons.Right)
            {
                ShowTrayPopup();
            }
        };
        _trayIcon.DoubleClick += (_, _) => ShowSwitcher();
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = "Scanning installed Python versions...";

        try
        {
            var installs = await _discoveryService.DiscoverAsync(_settings.CachedInstalls);
            PythonInstalls.Clear();
            foreach (var install in installs)
            {
                PythonInstalls.Add(install);
            }

            _settings.CachedInstalls = installs.ToList();
            SaveSettings();
            UpdateActiveState();
            StatusMessage = installs.Count == 0
                ? "No Python installs found yet."
                : $"Found {installs.Count} Python install{(installs.Count == 1 ? "" : "s")}.";
            _ = RefreshHealthSafelyAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateActiveState()
    {
        var active = _pathSwitchService.FindActiveInstall(PythonInstalls, _settings.Scope);
        var activePath = _pathSwitchService.GetActivePythonPath(_settings.Scope);

        ActiveTitle = active?.DisplayName ?? "No Python selected";
        ActiveDetail = $"{_settings.Scope} PATH is the active editing scope";
        ActivePath = activePath ?? "No python.exe found in this PATH scope.";
        VersionCountText = $"{PythonInstalls.Count} detected install{(PythonInstalls.Count == 1 ? "" : "s")}";

        _trayIcon.Text = active is null ? "Python Version Switch" : $"Active: {active.DisplayName}";
        _trayIcon.Icon?.Dispose();
        _trayIcon.Icon = TrayIconFactory.Create(active, _effectiveTheme);

        if (PythonList is not null && active is not null)
        {
            PythonList.SelectedItem = active;
        }
    }

    private async Task SwitchInstallAsync(PythonInstall install)
    {
        if (IsBusy)
        {
            StatusMessage = "Another operation is already running.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Switching to {install.DisplayName}...";
        AppLog.Info($"Switch requested for {install.DisplayName}: {install.ExecutablePath}");

        try
        {
            var scope = _settings.Scope;
            var knownInstalls = PythonInstalls.ToList();
            var backup = await Task.Run(() => _pathSwitchService.SwitchTo(install, scope, knownInstalls));
            _settings.PathBackups.Insert(0, backup);

            if (_settings.UpdatePythonLauncherDefault)
            {
                try
                {
                    await Task.Run(() => _launcherConfigService.SetDefault(install));
                }
                catch (Exception launcherException)
                {
                    AppLog.Error(launcherException, "PATH switch succeeded, but py launcher default update failed.");
                    StatusMessage = "PATH switched, but updating the Python launcher default failed. See app.log.";
                }
            }

            SaveSettings();
            PathBackups.Insert(0, backup);
            UpdateActiveState();
            if (!StatusMessage.Contains("launcher", StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = $"Active Python changed to {install.DisplayName}. New terminals will pick it up.";
            }

            ShowNotification("Python switched", $"{install.DisplayName} is now first on {_settings.Scope} PATH.");
            _ = RefreshHealthSafelyAsync();
        }
        catch (UnauthorizedAccessException)
        {
            AppLog.Error(new UnauthorizedAccessException("System PATH update denied."), "PATH switch failed.");
            StatusMessage = "System PATH needs administrator permission. Switch PATH scope to User or restart as administrator.";
            WpfMessageBox.Show(this, StatusMessage, "Permission needed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "PATH switch failed.");
            StatusMessage = $"Switch failed: {ex.Message}";
            WpfMessageBox.Show(this, StatusMessage, "Switch failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowTrayPopup()
    {
        _trayPopup?.CloseSafely();
        var active = _pathSwitchService.FindActiveInstall(PythonInstalls, _settings.Scope);
        var popup = new TrayPopupWindow(PythonInstalls.ToList(), active, _settings.Scope);
        _trayPopup = popup;

        popup.SwitchRequested += async (_, install) => await SwitchInstallAsync(install);
        popup.OpenMainRequested += (_, _) => ShowSwitcher();
        popup.RefreshRequested += async (_, _) => await RefreshAsync();
        popup.ExitRequested += (_, _) => ExitApplication();
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayPopup, popup))
            {
                _trayPopup = null;
            }
        };

        var cursor = System.Windows.Forms.Cursor.Position;
        var workArea = SystemParameters.WorkArea;
        popup.Left = Math.Max(workArea.Left + 12, Math.Min(cursor.X - popup.Width + 24, workArea.Right - popup.Width - 12));
        popup.Top = Math.Max(workArea.Top + 12, Math.Min(cursor.Y - popup.Height - 12, workArea.Bottom - popup.Height - 12));
        try
        {
            popup.Show();
            popup.Activate();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("while a Window is closing", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Error(ex, "Tray popup show raced with WPF window closing.");
            _trayPopup = null;
        }
    }

    private async Task RefreshHealthAsync()
    {
        var lines = new List<string>
        {
            $"PATH scope: {_settings.Scope}",
            $"Active path: {ActivePath}",
            "",
            "> where python",
            await RunCommandAsync("where.exe", "python"),
            "",
            "> python --version",
            await RunCommandAsync("python", "--version"),
            "",
            "> pip --version",
            await RunCommandAsync("pip", "--version")
        };

        HealthText = string.Join(Environment.NewLine, lines);
    }

    private async Task RefreshHealthSafelyAsync()
    {
        try
        {
            await RefreshHealthAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Health refresh failed.");
        }
    }

    private static async Task<string> RunCommandAsync(string fileName, string arguments)
    {
        try
        {
            var executable = fileName.Equals("where.exe", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Environment.SystemDirectory, "where.exe")
                : fileName;
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var waitTask = process.WaitForExitAsync();
            var allTasks = Task.WhenAll(waitTask, outputTask, errorTask);
            var completedTask = await Task.WhenAny(allTasks, Task.Delay(2500));
            if (completedTask != allTasks)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch (Exception killException)
                {
                    AppLog.Error(killException, $"Failed to kill timed-out command: {fileName} {arguments}");
                }

                return $"Timed out: {fileName} {arguments}";
            }

            var output = await outputTask;
            var error = await errorTask;
            var text = string.IsNullOrWhiteSpace(output) ? error : output;
            return string.IsNullOrWhiteSpace(text) ? "(no output)" : text.Trim();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void ApplyDarkTheme()
    {
        _effectiveTheme = AppTheme.Dark;

        if (_trayIcon.Visible)
        {
            UpdateActiveState();
        }
    }

    private void SaveSettings()
    {
        _settingsStore.Save(_settings);
    }

    private void ShowNotification(string title, string message)
    {
        if (_settings.ShowNotifications)
        {
            _trayIcon.ShowBalloonTip(2500, title, message, ToolTipIcon.Info);
        }
    }

    private void ShowSwitcher()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    private static void OpenPythonDownloads()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://www.python.org/downloads/windows/",
            UseShellExecute = true
        });
    }

    private void ExitApplication()
    {
        _isExiting = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        WpfApplication.Current.Shutdown();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExiting || !_settings.MinimizeToTray)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            return;
        }

        e.Cancel = true;
        Hide();
        ShowNotification("Still running", "Use the tray icon to switch Python or exit.");
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var aboutWindow = new AboutWindow
        {
            Owner = this
        };
        aboutWindow.ShowDialog();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private async void SwitchButton_Click(object sender, RoutedEventArgs e)
    {
        if (PythonList.SelectedItem is PythonInstall install)
        {
            await SwitchInstallAsync(install);
        }
        else
        {
            StatusMessage = "Select a Python install first.";
        }
    }

    private async void RollbackButton_Click(object sender, RoutedEventArgs e)
    {
        var backup = BackupList.SelectedItem as PathBackup ?? PathBackups.FirstOrDefault();
        if (backup is null)
        {
            StatusMessage = "No PATH backup is available yet.";
            return;
        }

        try
        {
            _pathSwitchService.Restore(backup);
            StatusMessage = $"Restored {backup.Scope} PATH from {backup.CreatedAt.LocalDateTime:g}.";
            ShowNotification("PATH restored", $"{backup.Scope} PATH backup restored.");
            await RefreshAsync();
        }
        catch (UnauthorizedAccessException)
        {
            StatusMessage = "System PATH restore needs administrator permission.";
            WpfMessageBox.Show(this, StatusMessage, "Permission needed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rollback failed: {ex.Message}";
            WpfMessageBox.Show(this, StatusMessage, "Rollback failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DownloadsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenPythonDownloads();
    }

    private async void ListInstallableButton_Click(object sender, RoutedEventArgs e)
    {
        IsBusy = true;
        StatusMessage = "Searching winget for Python packages...";

        try
        {
            var packages = await _wingetService.SearchPythonAsync();
            InstallableVersions.Clear();
            foreach (var package in packages)
            {
                InstallableVersions.Add(package);
            }

            StatusMessage = packages.Count == 0
                ? "winget did not return installable Python packages."
                : $"Found {packages.Count} installable Python package{(packages.Count == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"winget search failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void InstallSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (InstallableList.SelectedItem is not InstallablePythonPackage package)
        {
            StatusMessage = "Select a package first.";
            return;
        }

        _wingetService.Install(package);
        StatusMessage = $"Started winget install for {package.Id}.";
    }

    private async void HealthButton_Click(object sender, RoutedEventArgs e)
    {
        IsBusy = true;
        StatusMessage = "Running health check...";
        await RefreshHealthAsync();
        StatusMessage = "Health check complete.";
        IsBusy = false;
    }

    private void DesktopShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _shortcutService.CreateDesktopShortcut();
            StatusMessage = $"Desktop shortcut created: {path}";
            ShowNotification("Shortcut created", "Python Version Switch shortcut is on the desktop.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Shortcut failed: {ex.Message}";
            WpfMessageBox.Show(this, StatusMessage, "Shortcut failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ScopeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializingControls || ScopeCombo.SelectedItem is not PathScope scope)
        {
            return;
        }

        _settings.Scope = scope;
        UpdateActiveState();
        SaveSettings();
    }

    private void RemoveShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _uninstallService.RemoveStartupAndDesktopShortcut();
            StartupCheck.IsChecked = false;
            _settings.StartWithWindows = false;
            SaveSettings();
            StatusMessage = "Startup entry and desktop shortcut removed.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cleanup failed: {ex.Message}";
            WpfMessageBox.Show(this, StatusMessage, "Cleanup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetAppDataButton_Click(object sender, RoutedEventArgs e)
    {
        var result = WpfMessageBox.Show(
            this,
            "Delete saved settings, cached Python list, and PATH backups for this app?",
            "Delete app data",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _uninstallService.DeleteAppData();
            _settings = new AppSettings { Theme = AppTheme.Dark };
            PathBackups.Clear();
            ConfigureControls();
            StatusMessage = "App data deleted. Current PATH and installed Python versions were not changed.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"App data cleanup failed: {ex.Message}";
            WpfMessageBox.Show(this, StatusMessage, "Cleanup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        var result = WpfMessageBox.Show(
            this,
            "This will remove startup entries, shortcuts, saved app data, close the app, and try to delete the current app folder. It will not uninstall Python or edit PATH. Continue?",
            "Run uninstaller",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _uninstallService.CreateAndRunSelfUninstallScript(removeAppFolder: true);
            ExitApplication();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Uninstaller failed: {ex.Message}";
            WpfMessageBox.Show(this, StatusMessage, "Uninstaller failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializingControls)
        {
            return;
        }

        var enabled = StartupCheck.IsChecked == true;
        _settings.StartWithWindows = enabled;
        _shortcutService.SetStartup(enabled);
        SaveSettings();
    }

    private void SettingsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializingControls)
        {
            return;
        }

        _settings.MinimizeToTray = MinimizeCheck.IsChecked == true;
        _settings.ShowNotifications = NotificationsCheck.IsChecked == true;
        _settings.UpdatePythonLauncherDefault = LauncherCheck.IsChecked == true;
        SaveSettings();
    }

    private void SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
