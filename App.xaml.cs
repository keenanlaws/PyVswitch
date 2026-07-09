namespace PythonVersionSwitch;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        PythonVersionSwitch.Services.AppLog.Info("Application starting.");

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (IsWindowClosingRace(args.Exception))
            {
                PythonVersionSwitch.Services.AppLog.Info("Ignored benign WPF window-close race.");
                return;
            }

            PythonVersionSwitch.Services.AppLog.Error(args.Exception, "Unhandled dispatcher exception.");
            System.Windows.MessageBox.Show(
                args.Exception.Message,
                "Python Version Switch recovered from an error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                PythonVersionSwitch.Services.AppLog.Error(exception, "Unhandled app-domain exception.");
            }
            else
            {
                PythonVersionSwitch.Services.AppLog.Info($"Unhandled app-domain exception object: {args.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            PythonVersionSwitch.Services.AppLog.Error(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    private static bool IsWindowClosingRace(Exception exception)
    {
        return exception is InvalidOperationException &&
               exception.Message.Contains("while a Window is closing", StringComparison.OrdinalIgnoreCase);
    }
}
