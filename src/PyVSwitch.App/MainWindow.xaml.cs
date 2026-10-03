using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using PyVSwitch.App.Pages;
using PyVSwitch.App.Services;
using PyVSwitch.App.ViewModels;
using WinForms = System.Windows.Forms;

namespace PyVSwitch.App;

public partial class MainWindow : Window, IShell
{
    private readonly Dictionary<string, UserControl> _pages = [];
    private readonly WinForms.NotifyIcon? _trayIcon;
    private TrayPopupWindow? _trayPopup;
    private TaskCompletionSource<bool>? _dialogResult;
    private bool _isExiting;
    private bool _trayHintShown;

    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel, bool withTray = true)
    {
        ViewModel = viewModel;
        viewModel.Shell = this;
        InitializeComponent();
        DataContext = viewModel;

        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
        StateChanged += (_, _) => OnWindowStateChanged();
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        if (withTray)
        {
            _trayIcon = new WinForms.NotifyIcon { Visible = true, Text = "pyvswitch", Icon = TrayIconFactory.Create(null) };
            _trayIcon.MouseUp += (_, args) =>
            {
                if (args.Button is WinForms.MouseButtons.Left or WinForms.MouseButtons.Right)
                {
                    ShowTrayPopup();
                }
            };
            _trayIcon.DoubleClick += (_, _) => ShowFromTray();
            viewModel.ActiveChanged += UpdateTrayIcon;
        }

        ShowPage(viewModel.Page, animate: false);
    }

    // Pages ----------------------------------------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Page))
        {
            ShowPage(ViewModel.Page, animate: true);
        }
    }

    private void ShowPage(string name, bool animate)
    {
        if (!_pages.TryGetValue(name, out var page))
        {
            page = name switch
            {
                "versions" => new VersionsPage(),
                "download" => new DownloadPage(),
                "packages" => new PackagesPage(),
                "doctor" => new DoctorPage(),
                "ai" => new AiPage(),
                "settings" => new SettingsPage(),
                _ => new OverviewPage()
            };
            _pages[name] = page;
        }

        PageHost.Content = page;
        if (!animate)
        {
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)));
        PageShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    // Window chrome --------------------------------------------------------------------------

    private void OnWindowStateChanged()
    {
        // A maximized chromeless window overhangs the screen by the resize border; pad it back in.
        var maximized = WindowState == WindowState.Maximized;
        var frame = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = maximized ? new Thickness(frame.Left + 3, frame.Top + 3, frame.Right + 3, frame.Bottom + 3) : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            ViewModel.RefreshCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ActivityLog_TextChanged(object sender, TextChangedEventArgs e) => ActivityLog.ScrollToEnd();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting || _trayIcon is null || !ViewModel.MinimizeToTray)
        {
            DisposeTray();
            Application.Current.Shutdown();
            return;
        }

        e.Cancel = true;
        Hide();
        if (!_trayHintShown && ViewModel.ShowNotifications)
        {
            _trayHintShown = true;
            ShowBalloon("pyvswitch is still running", "Click the tray icon to switch Python or to exit.");
        }
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Focus();
    }

    public void ExitApplication()
    {
        _isExiting = true;
        DisposeTray();
        Application.Current.Shutdown();
    }

    // Tray -----------------------------------------------------------------------------------

    private void UpdateTrayIcon()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var active = ViewModel.TrayActiveInstall;
        var text = active is null ? "pyvswitch" : $"pyvswitch - {active.DisplayName}";
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
        var previous = _trayIcon.Icon;
        _trayIcon.Icon = TrayIconFactory.Create(active);
        previous?.Dispose();
    }

    private void DisposeTray()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
    }

    private void ShowTrayPopup()
    {
        _trayPopup?.CloseSafely();
        var popup = new TrayPopupWindow(ViewModel);
        _trayPopup = popup;
        popup.OpenMainRequested += (_, page) =>
        {
            if (page is not null)
            {
                ViewModel.Page = page;
            }

            ShowFromTray();
        };
        popup.ExitRequested += (_, _) => ExitApplication();
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayPopup, popup))
            {
                _trayPopup = null;
            }
        };

        // Cursor position is in device pixels; WPF window coordinates are device-independent.
        var cursor = WinForms.Cursor.Position;
        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        var x = cursor.X / scaleX;
        var y = cursor.Y / scaleY;
        var area = SystemParameters.WorkArea;
        popup.Left = Math.Max(area.Left + 4, Math.Min(x - popup.Width + 60, area.Right - popup.Width + 6));
        popup.AnchorBottom = Math.Min(y, area.Bottom) + 6;
        popup.Top = popup.AnchorBottom - 420;

        try
        {
            popup.Show();
            popup.Activate();
        }
        catch (InvalidOperationException)
        {
            _trayPopup = null;
        }
    }

    // IShell ---------------------------------------------------------------------------------

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false)
    {
        _dialogResult?.TrySetResult(false);
        _dialogResult = new TaskCompletionSource<bool>();

        if (!IsVisible)
        {
            ShowFromTray();
        }

        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogConfirm.Content = confirmText;
        DialogConfirm.Style = (Style)FindResource(danger ? "Btn.Danger" : "Btn.Primary");
        DialogCancel.Visibility = confirmText == "OK" ? Visibility.Collapsed : Visibility.Visible;
        DialogLayer.Visibility = Visibility.Visible;
        DialogLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        DialogConfirm.Focus();
        return _dialogResult.Task;
    }

    private void CloseDialog(bool result)
    {
        DialogLayer.Visibility = Visibility.Collapsed;
        _dialogResult?.TrySetResult(result);
        _dialogResult = null;
    }

    private void DialogConfirm_Click(object sender, RoutedEventArgs e) => CloseDialog(true);

    private void DialogCancel_Click(object sender, RoutedEventArgs e) => CloseDialog(false);

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process holds the clipboard open; a second attempt almost always succeeds.
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                }
            });
        }
    }

    public void ShowBalloon(string title, string message)
    {
        _trayIcon?.ShowBalloonTip(2500, title, message, WinForms.ToolTipIcon.Info);
    }
}
