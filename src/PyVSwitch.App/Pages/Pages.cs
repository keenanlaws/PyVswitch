using System.Windows.Controls;
using System.Windows.Input;
using PyVSwitch.App.ViewModels;

namespace PyVSwitch.App.Pages;

public partial class OverviewPage : UserControl
{
    public OverviewPage() => InitializeComponent();
}

public partial class VersionsPage : UserControl
{
    public VersionsPage() => InitializeComponent();
}

public partial class DownloadPage : UserControl
{
    public DownloadPage() => InitializeComponent();
}

public partial class PackagesPage : UserControl
{
    public PackagesPage() => InitializeComponent();

    private void NewPackage_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel viewModel && viewModel.InstallPackageCommand.CanExecute(null))
        {
            viewModel.InstallPackageCommand.Execute(null);
            e.Handled = true;
        }
    }
}

public partial class DoctorPage : UserControl
{
    public DoctorPage() => InitializeComponent();
}

public partial class AiPage : UserControl
{
    public AiPage() => InitializeComponent();
}

public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();
}
