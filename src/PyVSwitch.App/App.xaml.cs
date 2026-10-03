using System.Windows;
using PyVSwitch.App.Services;
using PyVSwitch.App.ViewModels;
using PyVSwitch.Models;
using PyVSwitch.Services;

namespace PyVSwitch.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\pyvswitch-app";
    private const string ShowSignalName = @"Local\pyvswitch-show";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Info($"pyvswitch {AppInfo.Version} starting.");
        HookExceptionHandlers();

        var args = e.Args;
        var demo = args.Contains("--demo");
        var screenshotDirectory = ValueAfter(args, "--screenshots");
        var startInTray = args.Contains("--tray");

        if (!demo && screenshotDirectory is null && !ClaimSingleInstance())
        {
            Shutdown();
            return;
        }

        var engine = new PyEngine();
        var theme = args.Contains("--light") ? AppTheme.Light : args.Contains("--dark") ? AppTheme.Dark : engine.Settings.Load().Theme;
        ThemeService.Apply(theme);

        var viewModel = new MainViewModel(engine, Dispatcher, demo);
        var window = new MainWindow(viewModel, withTray: screenshotDirectory is null);
        MainWindow = window;

        if (screenshotDirectory is not null)
        {
            // Capture runs double as a wiring check: any broken binding lands in app.log.
            System.Diagnostics.PresentationTraceSources.Refresh();
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingLogListener());
            _ = Screenshotter.RunAsync(window, viewModel, screenshotDirectory);
            return;
        }

        if (!startInTray)
        {
            window.Show();
        }

        if (!demo)
        {
            new ShortcutService().MigrateStartupEntry();
        }

        _ = viewModel.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSignal?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Only one tray app runs; launching it again brings the existing window forward.</summary>
    private bool ClaimSingleInstance()
    {
        _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        if (!isFirst)
        {
            _showSignal.Set();
            return false;
        }

        var signal = _showSignal;
        var listener = new Thread(() =>
        {
            try
            {
                while (signal.WaitOne())
                {
                    Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray());
                }
            }
            catch (ObjectDisposedException)
            {
                // The app is shutting down.
            }
        })
        {
            IsBackground = true,
            Name = "pyvswitch-show-listener"
        };
        listener.Start();
        return true;
    }

    private void HookExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (IsWindowClosingRace(args.Exception))
            {
                AppLog.Info("Ignored benign WPF window-close race.");
                return;
            }

            AppLog.Error(args.Exception, "Unhandled dispatcher exception.");
            (MainWindow as MainWindow)?.ViewModel.Toast($"Something went wrong: {args.Exception.Message}", "danger");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                AppLog.Error(exception, "Unhandled app-domain exception.");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };

        RelayCommand.CommandFailed += exception =>
            (MainWindow as MainWindow)?.ViewModel.Toast($"Something went wrong: {exception.Message}", "danger");
    }

    private static bool IsWindowClosingRace(Exception exception)
    {
        return exception is InvalidOperationException &&
               exception.Message.Contains("while a Window is closing", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class BindingLogListener : System.Diagnostics.TraceListener
    {
        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message) => AppLog.Info($"BINDING {message}");
    }

    private static string? ValueAfter(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
