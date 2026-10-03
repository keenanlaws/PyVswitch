using System.Text.Json;
using PyVSwitch.Models;
using PyVSwitch.Services;

namespace PyVSwitch.Tests;

public class PythonVersionTests
{
    [Theory]
    [InlineData("3.12.4", 3, 12, 4, "")]
    [InlineData("3.12", 3, 12, 0, "")]
    [InlineData("3.15.0rc3", 3, 15, 0, "rc")]
    [InlineData("2.7.18", 2, 7, 18, "")]
    [InlineData("3.14.0b2", 3, 14, 0, "b")]
    public void Parses(string text, int major, int minor, int patch, string pre)
    {
        Assert.True(PythonVersion.TryParse(text, out var version));
        Assert.Equal((major, minor, patch, pre), (version.Major, version.Minor, version.Patch, version.Pre));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("install manager 25.0")]
    public void RejectsNonVersions(string text)
    {
        Assert.False(PythonVersion.TryParse(text, out _));
    }

    [Fact]
    public void OrdersPrereleasesBeforeFinal()
    {
        var ordered = new[] { "3.15.0", "3.15.0a2", "3.15.0rc1", "3.15.0b1", "3.14.9" }
            .Select(PythonVersion.ParseOrDefault)
            .OrderBy(version => version)
            .Select(version => version.ToString());
        Assert.Equal(["3.14.9", "3.15.0a2", "3.15.0b1", "3.15.0rc1", "3.15.0"], ordered);
    }
}

public class PathEditorTests
{
    [Fact]
    public void SwitchMovesSelectionFirstAndDropsOtherInterpreters()
    {
        const string previous = @"C:\Tools;C:\Py310;C:\Py310\Scripts;C:\Windows;C:\Py312\Scripts;C:\Py312";
        var result = PathEditor.BuildSwitchedPath(
            previous,
            [@"C:\Py312", @"C:\Py312\Scripts"],
            [@"C:\Py310", @"C:\Py310\Scripts", @"C:\Py312", @"C:\Py312\Scripts"]);

        Assert.Equal(@"C:\Py312;C:\Py312\Scripts;C:\Tools;C:\Windows", result);
    }

    [Fact]
    public void SwitchIsCaseAndTrailingSlashInsensitive()
    {
        var result = PathEditor.BuildSwitchedPath(@"c:\PY310\;C:\Keep", [@"C:\Py312"], [@"C:\Py310"]);
        Assert.Equal(@"C:\Py312;C:\Keep", result);
    }

    [Fact]
    public void SwitchKeepsUnexpandedVariablesInUnrelatedEntries()
    {
        var result = PathEditor.BuildSwitchedPath(@"%USERPROFILE%\bin;C:\Py310", [@"C:\Py312"], [@"C:\Py310"]);
        Assert.Equal(@"C:\Py312;%USERPROFILE%\bin", result);
    }

    [Fact]
    public void SwitchIsIdempotent()
    {
        string[] preferred = [@"C:\Py312", @"C:\Py312\Scripts"];
        var once = PathEditor.BuildSwitchedPath(@"C:\A;C:\Py312", preferred, preferred);
        Assert.Equal(once, PathEditor.BuildSwitchedPath(once, preferred, preferred));
    }

    [Fact]
    public void RemoveDirectoriesDropsOnlyMatches()
    {
        Assert.Equal(@"C:\A;C:\B", PathEditor.RemoveDirectories(@"C:\A;C:\Py39\;C:\B;c:\py39\Scripts", [@"C:\Py39", @"C:\Py39\Scripts"]));
    }

    [Fact]
    public void SplitIgnoresEmptyEntries()
    {
        Assert.Equal([@"C:\A", @"C:\B"], PathEditor.Split(@"C:\A;; C:\B ;"));
    }
}

public class VersionSelectorTests
{
    private static readonly PythonInstall[] Installs =
    [
        Make("3.12.10", "x64"), Make("3.12.4", "x86"), Make("3.10.11", "x64"), Make("3.10.11", "x86"), Make("2.7.18", "x64")
    ];

    private static PythonInstall Make(string version, string architecture) => new()
    {
        Version = version,
        Architecture = architecture,
        ExecutablePath = $@"C:\Py\{version}-{architecture}\python.exe"
    };

    [Theory]
    [InlineData("3.12", "3.12.10", "x64")]
    [InlineData("3.12-32", "3.12.4", "x86")]
    [InlineData("3.10-64", "3.10.11", "x64")]
    [InlineData("3.10-x86", "3.10.11", "x86")]
    [InlineData("3", "3.12.10", "x64")]
    [InlineData("2", "2.7.18", "x64")]
    [InlineData("3.12.4", "3.12.4", "x86")]
    [InlineData("python3.10", "3.10.11", "x64")]
    public void MatchesSelectors(string selector, string version, string architecture)
    {
        var match = VersionSelector.Match(Installs, selector);
        Assert.NotNull(match);
        Assert.Equal((version, architecture), (match.Version, match.Architecture));
    }

    [Theory]
    [InlineData("3.11")]
    [InlineData("3.12.9")]
    [InlineData("3.12-arm64")]
    [InlineData("banana")]
    public void ReturnsNullWhenNothingMatches(string selector)
    {
        Assert.Null(VersionSelector.Match(Installs, selector));
    }

    [Theory]
    [InlineData(@"C:\Python312\python.exe", true)]
    [InlineData(@".\.venv", true)]
    [InlineData("3.12", false)]
    [InlineData("3.10-32", false)]
    public void DetectsPaths(string selector, bool expected)
    {
        Assert.Equal(expected, VersionSelector.LooksLikePath(selector));
    }
}

public class CatalogTests
{
    private static ApiRelease Release(int id, string name, bool pre = false) => new()
    {
        Name = name,
        IsPublished = true,
        PreRelease = pre,
        ReleaseDate = "2025-01-02T03:04:05Z",
        ResourceUri = $"https://www.python.org/api/v2/downloads/release/{id}/"
    };

    private static ApiReleaseFile File(int release, string fileName) => new()
    {
        Name = fileName,
        Release = $"https://www.python.org/api/v2/downloads/release/{release}/",
        Url = $"https://www.python.org/ftp/python/x/{fileName}",
        Filesize = 100
    };

    private static PythonCatalog Build()
    {
        return PythonCatalogService.Build(
            [
                Release(1, "Python 3.13.7"), Release(2, "Python 3.13.9"), Release(3, "Python 3.14.0rc1", pre: true),
                Release(4, "Python 2.7.18"), Release(5, "Python install manager 25.0"), Release(6, "Python 3.13.5")
            ],
            [
                File(1, "python-3.13.7-amd64.exe"), File(1, "python-3.13.7.exe"), File(1, "python-3.13.7-arm64.exe"),
                File(1, "python-3.13.7-embed-amd64.zip"), File(1, "python-3.13.7-amd64-webinstall.exe"),
                File(3, "python-3.14.0rc1-amd64.exe"),
                File(4, "python-2.7.18.amd64.msi"), File(4, "python-2.7.18.msi"), File(4, "python-2.7.18.amd64-pdb.zip"),
                File(5, "python-manager-25.0.msi"),
                File(6, "python-3.13.5-amd64.exe")
            ],
            new Dictionary<string, ApiReleaseCycle> { ["3.13"] = new() { Status = "bugfix", EndOfLife = "2029-10" } });
    }

    [Fact]
    public void GroupsBySeriesNewestFirstAndSkipsNonPythonReleases()
    {
        var catalog = Build();
        Assert.Equal(["3.14", "3.13", "2.7"], catalog.Series.Select(series => series.Name));
        Assert.Equal(["3.13.9", "3.13.7", "3.13.5"], catalog.Series[1].Releases.Select(release => release.Version));
        Assert.Equal("bugfix", catalog.Series[1].Status);
        Assert.Equal("end-of-life", catalog.Series[2].Status);
    }

    [Fact]
    public void KeepsOnlyFullOfflineInstallers()
    {
        var release = Build().Series[1].Releases.Single(item => item.Version == "3.13.7");
        Assert.Equal(["x64", "x86", "arm64"], release.Installers.Select(file => file.Architecture));
        Assert.All(release.Installers, file => Assert.Equal("exe", file.Kind));

        var legacy = Build().Series[2].Releases.Single();
        Assert.Equal(["x64", "x86"], legacy.Installers.Select(file => file.Architecture));
        Assert.All(legacy.Installers, file => Assert.Equal("msi", file.Kind));
    }

    [Theory]
    [InlineData("python-3.5.0-amd64.exe", "exe")]
    [InlineData("python-3.4.4.amd64.msi", "msi")]
    [InlineData("python-2.7.18.msi", "msi")]
    [InlineData("Python-2.3.5.exe", null)]
    [InlineData("python-3.4.0.exe", null)]
    [InlineData("python-3.13.7-amd64-webinstall.exe", null)]
    [InlineData("python-3.13.7-embed-amd64.zip", null)]
    public void OnlyInstallersWithAnUnattendedModeAreOffered(string fileName, string? expectedKind)
    {
        var installer = PythonCatalogService.ToInstaller(File(1, fileName));
        Assert.Equal(expectedKind, installer?.Kind);
    }

    [Fact]
    public void SeriesSpecPicksNewestReleaseThatHasAnInstaller()
    {
        // 3.13.9 is source-only, so "3.13" must fall back to 3.13.7.
        Assert.Equal("3.13.7", PythonCatalogService.ResolveRelease(Build(), "3.13", "x64", false).Version);
        Assert.Equal("3.13.7", PythonCatalogService.ResolveRelease(Build(), "latest", "x64", false).Version);
        Assert.Equal("3.14.0rc1", PythonCatalogService.ResolveRelease(Build(), "latest", "x64", true).Version);
        Assert.Equal("2.7.18", PythonCatalogService.ResolveRelease(Build(), "2", "x86", false).Version);
    }

    [Fact]
    public void ExactSpecExplainsMissingInstallers()
    {
        var sourceOnly = Assert.Throws<PyvsException>(() => PythonCatalogService.ResolveRelease(Build(), "3.13.9", "x64", false));
        Assert.Equal("no_installer", sourceOnly.Code);
        Assert.Contains("3.13.7", sourceOnly.Message);

        var wrongArch = Assert.Throws<PyvsException>(() => PythonCatalogService.ResolveRelease(Build(), "3.13.5", "arm64", false));
        Assert.Equal("no_installer", wrongArch.Code);

        Assert.Equal("not_found", Assert.Throws<PyvsException>(() => PythonCatalogService.ResolveRelease(Build(), "3.13.99", "x64", false)).Code);
        Assert.Equal("bad_version", Assert.Throws<PyvsException>(() => PythonCatalogService.ResolveRelease(Build(), "banana", "x64", false)).Code);
    }
}

public class ParsingTests
{
    [Fact]
    public void ParsesPipListEvenWithLeadingWarnings()
    {
        const string output = """
            WARNING: something noisy
            [{"name": "requests", "version": "2.32.0"}, {"name": "numpy", "version": "1.26.4", "latest_version": "2.1.0", "latest_filetype": "wheel"}]
            """;
        var packages = PipService.ParsePackageList(output);
        Assert.Equal(2, packages.Count);
        Assert.False(packages[0].IsOutdated);
        Assert.True(packages[1].IsOutdated);
        Assert.Equal("2.1.0", packages[1].LatestVersion);
    }

    [Fact]
    public void ParsePipListToleratesGarbage()
    {
        Assert.Empty(PipService.ParsePackageList("No module named pip"));
    }

    [Fact]
    public void ParsesPyPiMetadataNewestFirstWithoutYankedReleases()
    {
        const string json = """
            {"info": {"name": "demo", "version": "2.0", "summary": "A demo", "home_page": "", "requires_python": ">=3.9",
                      "project_urls": {"Docs": "https://docs.example", "Homepage": "https://home.example"}},
             "releases": {
               "1.0": [{"upload_time_iso_8601": "2023-01-01T00:00:00Z", "yanked": false}],
               "2.0": [{"upload_time_iso_8601": "2024-01-01T00:00:00Z", "yanked": false}],
               "1.5": [{"upload_time_iso_8601": "2023-06-01T00:00:00Z", "yanked": true}],
               "0.1": []}}
            """;
        using var document = JsonDocument.Parse(json);
        var package = PyPiService.Parse(document.RootElement);
        Assert.Equal("demo", package.Name);
        Assert.Equal("https://home.example", package.HomePage);
        Assert.Equal(["2.0", "1.0"], package.Versions);
    }

    [Fact]
    public void SplitsRegistryUninstallCommands()
    {
        Assert.Equal((@"C:\Cache Dir\python-3.12.4-amd64.exe", "/uninstall /quiet"),
            PythonInstallerService.SplitCommandLine("\"C:\\Cache Dir\\python-3.12.4-amd64.exe\" /uninstall /quiet"));
        Assert.Equal(("MsiExec.exe", "/I{ABC}"), PythonInstallerService.SplitCommandLine("MsiExec.exe /I{ABC}"));
    }

    [Theory]
    [InlineData("", "x64")]
    [InlineData("32", "x86")]
    [InlineData("AMD64", "x64")]
    [InlineData("arm64", "arm64")]
    public void NormalizesArchitectures(string input, string expected)
    {
        if (input.Length == 0)
        {
            Assert.Equal(PythonInstallerService.DefaultArchitecture, PythonInstallerService.NormalizeArchitecture(input));
        }
        else
        {
            Assert.Equal(expected, PythonInstallerService.NormalizeArchitecture(input));
        }
    }
}

public class LauncherConfigTests
{
    [Fact]
    public void AddsDefaultsSectionWhenMissing()
    {
        Assert.Equal(["[defaults]", "python=3.12-64"], PythonLauncherConfigService.UpdateDefault([], "3.12-64"));
    }

    [Fact]
    public void ReplacesExistingDefaultAndKeepsOtherSettings()
    {
        var lines = PythonLauncherConfigService.UpdateDefault(["[defaults]", "python=3.10-32", "python3=3.11", "", "[commands]", "x=y"], "3.12-64");
        Assert.Equal(["[defaults]", "python=3.12-64", "python3=3.11", "", "[commands]", "x=y"], lines);
    }

    [Fact]
    public void RemovesOnlyThePythonDefault()
    {
        var lines = PythonLauncherConfigService.RemoveDefault(["[defaults]", "python=3.12-64", "python3=3.11", "", "[commands]", "python=keep"]);
        Assert.Equal(["[defaults]", "python3=3.11", "", "[commands]", "python=keep"], lines);
    }

    [Fact]
    public void RestoreDefaultRoundTripsThroughTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pyvswitch-pyini-{Guid.NewGuid():N}.ini");
        try
        {
            File.WriteAllLines(path, ["[defaults]", "python=3.10-32", "", "[commands]", "; keep me"]);
            var service = new PythonLauncherConfigService(path);
            service.SetDefault(new PythonInstall { Version = "3.12.4", Architecture = "x64" });
            Assert.Equal("3.12-64", service.GetDefault());

            service.RestoreDefault("3.10-32");
            Assert.Equal(["[defaults]", "python=3.10-32", "", "[commands]", "; keep me"], File.ReadAllLines(path));

            service.RestoreDefault(null);
            Assert.Null(service.GetDefault());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InsertsIntoDefaultsSectionThatLacksPythonKey()
    {
        var lines = PythonLauncherConfigService.UpdateDefault(["[defaults]", "python3=3.11", "", "[commands]"], "3.12-64");
        Assert.Equal(["[defaults]", "python3=3.11", "python=3.12-64", "", "[commands]"], lines);
    }
}

public class AiIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pyvswitch-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _appData;

    public AiIntegrationTests()
    {
        _home = Path.Combine(_root, "home");
        _appData = Path.Combine(_root, "appdata");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_appData);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private AiIntegrationService Service() => new(@"C:\Program Files\pyvswitch\pyvswitch.exe", _home, _appData);

    [Fact]
    public void BlockIsAppendedReplacedAndRemovedWithoutTouchingOtherText()
    {
        const string original = "# My rules\n\nBe concise.\n";
        var added = AiIntegrationService.UpsertBlock(original, "<!-- b -->one<!-- e -->", "<!-- b -->", "<!-- e -->");
        Assert.StartsWith(original, added);
        Assert.Contains("one", added);

        var replaced = AiIntegrationService.UpsertBlock(added, "<!-- b -->two<!-- e -->", "<!-- b -->", "<!-- e -->");
        Assert.DoesNotContain("one", replaced);
        Assert.Contains("two", replaced);

        Assert.Equal(original, AiIntegrationService.RemoveBlock(replaced, "<!-- b -->", "<!-- e -->"));
    }

    [Fact]
    public async Task SetupSkipsToolsThatAreNotInstalled()
    {
        var statuses = await Service().SetupAsync();
        Assert.All(statuses, status => Assert.False(status.Detected));
        Assert.All(statuses, status => Assert.Empty(status.Changes));
        Assert.False(Directory.Exists(Path.Combine(_home, ".codex")));
    }

    [Fact]
    public async Task SetupRegistersCodexAndPreservesExistingConfig()
    {
        var codex = Path.Combine(_home, ".codex");
        Directory.CreateDirectory(codex);
        File.WriteAllText(Path.Combine(codex, "AGENTS.md"), "# Existing\n");
        File.WriteAllText(Path.Combine(codex, "config.toml"), "model = \"x\"\n");

        var status = (await Service().SetupAsync(["codex"])).Single();
        Assert.True(status.InstructionsRegistered);
        Assert.True(status.McpRegistered);

        var agents = File.ReadAllText(Path.Combine(codex, "AGENTS.md"));
        Assert.StartsWith("# Existing\n", agents);
        Assert.Contains("pyvswitch use 3.12", agents);

        var toml = File.ReadAllText(Path.Combine(codex, "config.toml"));
        Assert.StartsWith("model = \"x\"\n", toml);
        Assert.Contains("[mcp_servers.pyvswitch]", toml);
        Assert.Contains(@"command = 'C:\Program Files\pyvswitch\pyvswitch.exe'", toml);
        Assert.True(File.Exists(Path.Combine(codex, "config.toml.pyvswitch.bak")));

        // Running setup again changes nothing.
        Assert.Empty((await Service().SetupAsync(["codex"])).Single().Changes);

        var removed = (await Service().RemoveAsync(["codex"])).Single();
        Assert.False(removed.InstructionsRegistered);
        Assert.False(removed.McpRegistered);
        Assert.Equal("# Existing\n", File.ReadAllText(Path.Combine(codex, "AGENTS.md")));
        Assert.Equal("model = \"x\"\n", File.ReadAllText(Path.Combine(codex, "config.toml")));
    }

    [Fact]
    public async Task SetupMergesIntoJsonConfigWithoutDroppingOtherServers()
    {
        var claude = Path.Combine(_appData, "Claude");
        Directory.CreateDirectory(claude);
        var config = Path.Combine(claude, "claude_desktop_config.json");
        File.WriteAllText(config, """{ "mcpServers": { "other": { "command": "x" } }, "theme": "dark" }""");

        var status = (await Service().SetupAsync(["claude-desktop"])).Single();
        Assert.True(status.McpRegistered);

        using var document = JsonDocument.Parse(File.ReadAllText(config));
        var servers = document.RootElement.GetProperty("mcpServers");
        Assert.Equal("x", servers.GetProperty("other").GetProperty("command").GetString());
        Assert.Equal(@"C:\Program Files\pyvswitch\pyvswitch.exe", servers.GetProperty("pyvswitch").GetProperty("command").GetString());
        Assert.Equal("mcp", servers.GetProperty("pyvswitch").GetProperty("args")[0].GetString());
        Assert.Equal("dark", document.RootElement.GetProperty("theme").GetString());

        await Service().RemoveAsync(["claude-desktop"]);
        using var after = JsonDocument.Parse(File.ReadAllText(config));
        Assert.False(after.RootElement.GetProperty("mcpServers").TryGetProperty("pyvswitch", out _));
        Assert.True(after.RootElement.GetProperty("mcpServers").TryGetProperty("other", out _));
    }

    [Fact]
    public async Task UnknownToolIdIsRejected()
    {
        var error = await Assert.ThrowsAsync<PyvsException>(() => Service().SetupAsync(["nope"]));
        Assert.Equal("usage", error.Code);
    }
}

public class DoctorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pyvswitch-doctor-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private string MakeInterpreter(string name, int size = 16)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "python.exe"), new byte[size]);
        return directory;
    }

    private static ActiveState State(string? selected, string? effective) => new()
    {
        Scope = PathScope.User,
        Selected = selected is null ? null : new PythonInstall { Version = "3.12.1", Architecture = "x64", ExecutablePath = Path.Combine(selected, "python.exe") },
        EffectivePath = effective is null ? null : Path.Combine(effective, "python.exe"),
        Effective = effective is null ? null : new PythonInstall { Version = "3.12.1", Architecture = "x64", ExecutablePath = Path.Combine(effective, "python.exe") }
    };

    [Fact]
    public void HealthyPathReportsActiveInterpreter()
    {
        var python = MakeInterpreter("Python312");
        var findings = DoctorService.AnalyzePath("", python, State(python, python), null);
        Assert.Equal("active", Assert.Single(findings).Id);
    }

    [Fact]
    public void ReportsMissingPython()
    {
        var findings = DoctorService.AnalyzePath(@"C:\Windows", "", State(null, null), null);
        Assert.Contains(findings, finding => finding.Id == "no-python" && finding.Severity == DoctorSeverity.Error);
    }

    [Fact]
    public void ReportsSystemPathShadowingTheUserSelection()
    {
        var system = MakeInterpreter("Python310");
        var user = MakeInterpreter("Python312");
        var findings = DoctorService.AnalyzePath(system, user, State(user, system), null);
        Assert.Contains(findings, finding => finding.Id == "shadowed" && finding.Severity == DoctorSeverity.Warning);
    }

    [Fact]
    public void ReportsStoreAliasAheadOfRealInterpreter()
    {
        var alias = MakeInterpreter("WindowsApps", size: 0);
        var python = MakeInterpreter("Python312");
        var ahead = DoctorService.AnalyzePath("", $"{alias};{python}", State(python, python), alias);
        Assert.Contains(ahead, finding => finding.Id == "store-alias");

        var behind = DoctorService.AnalyzePath("", $"{python};{alias}", State(python, python), alias);
        Assert.DoesNotContain(behind, finding => finding.Id == "store-alias");
    }

    [Fact]
    public void ReportsDeadPythonFoldersAndDuplicates()
    {
        var python = MakeInterpreter("Python312");
        var dead = Path.Combine(_root, "Python39");
        var findings = DoctorService.AnalyzePath("", $"{python};{dead};{dead}\\Scripts;{python}", State(python, python), null);
        Assert.Contains(findings, finding => finding.Id == "dangling" && finding.AutoFixable);
        Assert.Contains(findings, finding => finding.Id == "duplicates" && finding.AutoFixable);
    }

    [Fact]
    public void FindFirstPythonSkipsStoreAliasStubs()
    {
        var alias = MakeInterpreter("WindowsApps", size: 0);
        var python = MakeInterpreter("Python312");
        Assert.Equal(Path.Combine(python, "python.exe"), PathEditor.FindFirstPython([alias, python]));
    }
}
