using System.ComponentModel;
using System.Windows;
using PyVSwitch.App.ViewModels;

namespace PyVSwitch.App;

public partial class TrayPopupWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closeRequested;

    /// <summary>Raised with the page to open, or null to just show the window.</summary>
    public event EventHandler<string?>? OpenMainRequested;
    public event EventHandler? ExitRequested;

    public TrayPopupWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        // The height is only known after layout, so the bottom edge is anchored once the popup has loaded.
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Top = Math.Max(area.Top + 8, Math.Min(AnchorBottom - ActualHeight, area.Bottom - ActualHeight));
        };
    }

    /// <summary>Screen position (in device-independent pixels) the popup's bottom edge should sit at.</summary>
    public double AnchorBottom { get; set; }

    private async void Version_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: InstallItem item })
        {
            CloseSafely();
            await _viewModel.SwitchAsync(item);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        OpenMainRequested?.Invoke(this, null);
        CloseSafely();
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        OpenMainRequested?.Invoke(this, "download");
        CloseSafely();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        CloseSafely();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Window_Deactivated(object? sender, EventArgs e) => CloseSafely();

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
        catch (InvalidOperationException)
        {
            // WPF was already closing the window.
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closeRequested = true;
        base.OnClosing(e);
    }
}
