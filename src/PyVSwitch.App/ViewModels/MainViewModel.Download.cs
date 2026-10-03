using System.Collections.ObjectModel;
using System.Windows.Input;
using PyVSwitch.Models;
using PyVSwitch.Services;

namespace PyVSwitch.App.ViewModels;

public sealed partial class MainViewModel
{
    private PythonCatalog? _catalog;
    private string _architecture = PythonInstallerService.DefaultArchitecture;
    private string _seriesFilter = "supported";
    private bool _showPrereleases;
    private bool _isCatalogLoading;
    private string _catalogMessage = "";

    public ObservableCollection<SeriesItem> SeriesItems { get; } = [];
    public ICommand InstallReleaseCommand { get; private set; } = null!;
    public ICommand ReloadCatalogCommand { get; private set; } = null!;

    private void InitializeDownloadCommands()
    {
        InstallReleaseCommand = new RelayCommand(parameter => InstallReleaseAsync(parameter as SeriesItem));
        ReloadCatalogCommand = new RelayCommand(_ => LoadCatalogAsync(refresh: true));
    }

    /// <summary>x64, x86 or arm64.</summary>
    public string Architecture
    {
        get => _architecture;
        set
        {
            if (Set(ref _architecture, value))
            {
                RebuildSeries();
            }
        }
    }

    /// <summary>supported or all.</summary>
    public string SeriesFilter
    {
        get => _seriesFilter;
        set
        {
            if (Set(ref _seriesFilter, value))
            {
                RebuildSeries();
            }
        }
    }

    public bool ShowPrereleases
    {
        get => _showPrereleases;
        set
        {
            if (Set(ref _showPrereleases, value))
            {
                RebuildSeries();
            }
        }
    }

    public bool IsCatalogLoading
    {
        get => _isCatalogLoading;
        private set => Set(ref _isCatalogLoading, value);
    }

    public string CatalogMessage
    {
        get => _catalogMessage;
        private set => Set(ref _catalogMessage, value);
    }

    private Task EnsureCatalogAsync()
    {
        return _catalog is null ? LoadCatalogAsync(refresh: false) : Task.CompletedTask;
    }

    private async Task LoadCatalogAsync(bool refresh)
    {
        if (IsCatalogLoading)
        {
            return;
        }

        IsCatalogLoading = true;
        CatalogMessage = "";
        try
        {
            _catalog = await Task.Run(() => Engine.Catalog.GetAsync(refresh));
            CatalogMessage = _catalog.IsStale
                ? $"python.org could not be reached. Showing the list saved on {_catalog.FetchedAt.LocalDateTime:g}."
                : "";
            RebuildSeries();
        }
        catch (PyvsException ex)
        {
            CatalogMessage = ex.Message;
        }
        finally
        {
            IsCatalogLoading = false;
        }
    }

    private void RebuildSeries()
    {
        if (_catalog is null)
        {
            return;
        }

        // Keep each card's chosen patch version across rebuilds.
        var previous = SeriesItems.ToDictionary(item => item.Series.Name, item => item.Selected?.Release.Version);
        SeriesItems.Clear();
        foreach (var series in _catalog.Series)
        {
            var installed = _installs.Any(install => install.Series == series.Name);
            var supported = series.Status is "bugfix" or "security";
            var prerelease = series.Status is "prerelease" or "feature";
            if (prerelease && !ShowPrereleases)
            {
                continue;
            }

            if (SeriesFilter == "supported" && !supported && !installed && !prerelease)
            {
                continue;
            }

            var item = new SeriesItem(series, Architecture, _installs, ShowPrereleases);
            if (!item.HasChoices && SeriesFilter == "supported")
            {
                continue;
            }

            if (previous.TryGetValue(series.Name, out var version) && version is not null)
            {
                item.Selected = item.Choices.FirstOrDefault(choice => choice.Release.Version == version) ?? item.Selected;
            }

            SeriesItems.Add(item);
        }
    }

    private async Task InstallReleaseAsync(SeriesItem? item)
    {
        if (item?.Selected is null)
        {
            return;
        }

        var release = item.Selected.Release;
        var architecture = Architecture;
        var replaced = _installs.FirstOrDefault(install => install.Series == release.Series && install.Architecture == architecture);
        if (replaced is not null)
        {
            var newer = release.ParsedVersion.CompareTo(replaced.ParsedVersion) > 0;
            var message = newer
                ? $"Python {replaced.Version} ({architecture}) is updated in place to {release.Version}. Your installed packages are kept."
                : $"Python {replaced.Version} ({architecture}) is newer than {release.Version}. Uninstall it from the Versions page first, then install {release.Version}.";
            if (!newer)
            {
                await Shell.ConfirmAsync($"Python {release.Series} is already installed", message, "OK");
                return;
            }

            if (!await Shell.ConfirmAsync($"Update Python {release.Series}?", message, "Update"))
            {
                return;
            }
        }

        if (IsDemo)
        {
            Toast($"Python {release.Version} installed (demo)", "success");
            return;
        }

        await RunActivityAsync($"Installing Python {release.Version}", $"{architecture}  ·  from python.org", async (log, cancel) =>
        {
            var progress = new Progress<InstallProgress>(update =>
            {
                Activity.SetStatus(
                    update.Stage == "download" && update.TotalBytes > 0
                        ? $"{update.Message}  ·  {update.BytesReceived / 1048576.0:0.0} of {update.TotalBytes / 1048576.0:0.0} MB"
                        : update.Message,
                    update.Stage == "download" && update.TotalBytes > 0 ? update.Fraction : null);
            });

            MarkOwnWrite();
            log($"Source: {release.FindInstaller(architecture)?.Url}");
            var outcome = await Engine.InstallPythonAsync(release.Version, new InstallOptions { Architecture = architecture, AllowPrerelease = true }, progress, cancel);
            log(outcome.AlreadyInstalled ? "Already installed." : $"Installed to {outcome.Install?.InstallDirectory}");
            await RefreshAsync(quiet: true);
            return $"Python {release.Version} is installed";
        });
    }
}
