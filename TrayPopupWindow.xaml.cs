using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using PythonVersionSwitch.Models;
using PythonVersionSwitch.Services;

namespace PythonVersionSwitch;

public partial class TrayPopupWindow : Window
{
    private bool _closeRequested;

    public ObservableCollection<TrayPythonOption> Options { get; }
    public string HeaderDetail { get; }

    public event EventHandler<PythonInstall>? SwitchRequested;
    public event EventHandler? OpenMainRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? ExitRequested;

    public TrayPopupWindow(IEnumerable<PythonInstall> installs, PythonInstall? activeInstall, PathScope scope)
    {
        Options = new ObservableCollection<TrayPythonOption>(
            installs.Take(12).Select(install => new TrayPythonOption
            {
                Install = install,
                IsActive = activeInstall is not null &&
                           string.Equals(activeInstall.ExecutablePath, install.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            }));

        HeaderDetail = activeInstall is null
            ? $"{scope} PATH has no active Python"
            : $"{activeInstall.DisplayName} on {scope} PATH";

        InitializeComponent();
        DataContext = this;
    }

    private void VersionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PythonInstall install })
        {
            SwitchRequested?.Invoke(this, install);
            CloseSafely();
        }
    }

    private void OpenMainButton_Click(object sender, RoutedEventArgs e)
    {
        OpenMainRequested?.Invoke(this, EventArgs.Empty);
        CloseSafely();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
        CloseSafely();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
        CloseSafely();
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        CloseSafely();
    }

    public void CloseSafely()
    {
        if (_closeRequested)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(CloseSafely);
            return;
        }

        _closeRequested = true;
        try
        {
            Close();
        }
        catch (InvalidOperationException ex) when (IsWindowClosingRace(ex))
        {
            AppLog.Info("Ignored duplicate tray popup close while WPF was already closing the window.");
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closeRequested = true;
        base.OnClosing(e);
    }

    private static bool IsWindowClosingRace(InvalidOperationException exception)
    {
        return exception.Message.Contains("while a Window is closing", StringComparison.OrdinalIgnoreCase);
    }
}
