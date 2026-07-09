using System.Reflection;
using System.Windows;
using PythonVersionSwitch.Services;

namespace PythonVersionSwitch;

public partial class AboutWindow : Window
{
    public string VersionText { get; }

    public AboutWindow()
    {
        VersionText = $"Version {GetVersion()}";
        InitializeComponent();
        DataContext = this;
        SourceInitialized += (_, _) => WindowThemeService.ApplyDarkTitleBar(this);
    }

    private static string GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.4.0";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
