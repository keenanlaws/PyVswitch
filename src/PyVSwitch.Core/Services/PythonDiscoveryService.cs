using System.Text.RegularExpressions;
using Microsoft.Win32;
using PyVSwitch.Models;

namespace PyVSwitch.Services;

public sealed partial class PythonDiscoveryService
{
    public async Task<IReadOnlyList<PythonInstall>> DiscoverAsync(IEnumerable<PythonInstall>? cachedInstalls = null, CancellationToken cancellationToken = default)
    {
        var installs = new Dictionary<string, PythonInstall>(StringComparer.OrdinalIgnoreCase);

        foreach (var install in DiscoverFromRegistry())
        {
            AddOrUpdate(installs, install);
        }

        var launcherTask = DiscoverFromPythonLauncherAsync(cancellationToken);
        var whereTask = DiscoverFromWhereAsync(cancellationToken);

        foreach (var install in await launcherTask.ConfigureAwait(false))
        {
            AddOrUpdate(installs, install);
        }

        foreach (var install in await whereTask.ConfigureAwait(false))
        {
            AddOrUpdate(installs, install);
        }

        foreach (var install in DiscoverFromCommonDirectories())
        {
            AddOrUpdate(installs, install);
        }

        foreach (var install in cachedInstalls ?? [])
        {
            if (File.Exists(install.ExecutablePath))
            {
                install.IsCached = true;
                install.Source = string.IsNullOrWhiteSpace(install.Source) ? "Cache" : install.Source;
                AddOrUpdate(installs, install);
            }
        }

        await Task.WhenAll(installs.Values
            .Where(install => string.IsNullOrWhiteSpace(install.Version) || install.Architecture == "unknown")
            .Select(install => FillRuntimeDetailsAsync(install, cancellationToken))).ConfigureAwait(false);

        return installs.Values
            .OrderByDescending(install => install.ParsedVersion)
            .ThenBy(install => install.Architecture == "x64" ? 0 : 1)
            .ThenBy(install => install.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Describes a single interpreter, including virtual environments.</summary>
    public static async Task<PythonInstall?> DescribeAsync(string executablePath, string source, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executablePath))
        {
            return null;
        }

        var install = PythonInstall.FromExecutable(executablePath, source);
        install.IsVirtualEnvironment = IsVirtualEnvironment(install.ExecutablePath);
        if (install.IsVirtualEnvironment)
        {
            // Scripts\python.exe in a venv is a redirector; the scripts live beside it.
            install.ScriptsDirectory = install.InstallDirectory;
        }

        if (string.IsNullOrWhiteSpace(install.Version) || install.Architecture == "unknown" || install.IsVirtualEnvironment)
        {
            await FillRuntimeDetailsAsync(install, cancellationToken).ConfigureAwait(false);
        }

        return install;
    }

    private static void AddOrUpdate(Dictionary<string, PythonInstall> installs, PythonInstall install)
    {
        if (!IsUsableInterpreter(install.ExecutablePath))
        {
            return;
        }

        install.ExecutablePath = Path.GetFullPath(install.ExecutablePath);
        install.InstallDirectory = Path.GetDirectoryName(install.ExecutablePath) ?? "";
        var scripts = Path.Combine(install.InstallDirectory, "Scripts");
        install.ScriptsDirectory = Directory.Exists(scripts) ? scripts : "";
        install.Id = install.ExecutablePath.ToUpperInvariant();
        install.LastSeenAt = DateTimeOffset.Now;

        // The PE header is authoritative; registry tags and folder names are only hints.
        var peArchitecture = PythonInstall.InferArchitecture(install.ExecutablePath);
        if (peArchitecture != "unknown")
        {
            install.Architecture = peArchitecture;
        }

        if (installs.TryGetValue(install.ExecutablePath, out var existing))
        {
            if (PythonVersion.ParseOrDefault(install.Version).CompareTo(existing.ParsedVersion) != 0 &&
                CountVersionParts(install.Version) > CountVersionParts(existing.Version))
            {
                existing.Version = install.Version;
            }

            if (!string.IsNullOrWhiteSpace(install.Source) &&
                !existing.Source.Contains(install.Source, StringComparison.OrdinalIgnoreCase) &&
                !install.IsCached)
            {
                existing.Source = $"{existing.Source}, {install.Source}".Trim(',', ' ');
            }
        }
        else
        {
            installs[install.ExecutablePath] = install;
        }
    }

    internal static bool IsUsableInterpreter(string executablePath)
    {
        try
        {
            var info = new FileInfo(executablePath);
            if (!info.Exists)
            {
                return false;
            }

            // Microsoft Store "app execution alias" stubs are zero-byte reparse points that open the Store.
            if (info.Length == 0)
            {
                return false;
            }

            var fullPath = info.FullName;
            if (IsMsysPython(fullPath))
            {
                return false;
            }

            return !IsVirtualEnvironment(fullPath);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsVirtualEnvironment(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(directory);
        return File.Exists(Path.Combine(directory, "pyvenv.cfg")) ||
               (parent is not null && File.Exists(Path.Combine(parent, "pyvenv.cfg")));
    }

    private static IEnumerable<PythonInstall> DiscoverFromRegistry()
    {
        var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

        foreach (var hive in hives)
        {
            foreach (var view in views)
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var pythonRoot = baseKey.OpenSubKey(@"Software\Python");
                if (pythonRoot is null)
                {
                    continue;
                }

                foreach (var companyName in pythonRoot.GetSubKeyNames())
                {
                    if (companyName.Equals("PyLauncher", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using var companyKey = pythonRoot.OpenSubKey(companyName);
                    if (companyKey is null)
                    {
                        continue;
                    }

                    foreach (var tagName in companyKey.GetSubKeyNames())
                    {
                        using var tagKey = companyKey.OpenSubKey(tagName);
                        using var installPathKey = tagKey?.OpenSubKey("InstallPath");
                        if (installPathKey is null)
                        {
                            continue;
                        }

                        var installDir = ReadRegistryString(installPathKey, "") ?? "";
                        var executable = ReadRegistryString(installPathKey, "ExecutablePath");
                        if (string.IsNullOrWhiteSpace(executable) && !string.IsNullOrWhiteSpace(installDir))
                        {
                            executable = Path.Combine(installDir, "python.exe");
                        }

                        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                        {
                            continue;
                        }

                        var hiveLabel = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
                        yield return PythonInstall.FromExecutable(executable, $"{hiveLabel} registry");
                    }
                }
            }
        }
    }

    private static async Task<IEnumerable<PythonInstall>> DiscoverFromPythonLauncherAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("py", ["-0p"], TimeSpan.FromSeconds(4), cancellationToken: cancellationToken).ConfigureAwait(false);
        var output = result.CombinedOutput;
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var installs = new List<PythonInstall>();
        foreach (var line in SplitLines(output))
        {
            var pathMatch = PythonPathPattern().Match(line);
            if (pathMatch.Success && File.Exists(pathMatch.Value))
            {
                installs.Add(PythonInstall.FromExecutable(pathMatch.Value, "Python launcher"));
            }
        }

        return installs;
    }

    private static async Task<IEnumerable<PythonInstall>> DiscoverFromWhereAsync(CancellationToken cancellationToken)
    {
        var where = Path.Combine(Environment.SystemDirectory, "where.exe");
        var result = await ProcessRunner.RunAsync(where, ["python"], TimeSpan.FromSeconds(4), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return [];
        }

        return SplitLines(result.StandardOutput)
            .Where(IsUsableInterpreter)
            .Select(path => PythonInstall.FromExecutable(path, "PATH"))
            .ToList();
    }

    private static IEnumerable<PythonInstall> DiscoverFromCommonDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

        var roots = new (string Root, string Pattern, string Source)[]
        {
            (Path.Combine(localAppData, "Programs", "Python"), "Python*", "Install folder"),
            (programFiles, "Python*", "Install folder"),
            (programFilesX86, "Python*", "Install folder"),
            (systemDrive, "Python*", "Install folder"),
            (Path.Combine(userProfile, ".pyenv", "pyenv-win", "versions"), "*", "pyenv-win"),
        };

        foreach (var (root, pattern, source) in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }

            string[] candidates;
            try
            {
                candidates = Directory.GetDirectories(root, pattern);
            }
            catch
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                var executable = Path.Combine(candidate, "python.exe");
                if (File.Exists(executable))
                {
                    yield return PythonInstall.FromExecutable(executable, source);
                }
            }
        }
    }

    private static async Task FillRuntimeDetailsAsync(PythonInstall install, CancellationToken cancellationToken)
    {
        const string script = "import platform,sys; print('.'.join(map(str, sys.version_info[:3]))); print(platform.architecture()[0]); print(platform.machine())";
        var result = await ProcessRunner.RunAsync(
            install.ExecutablePath, ["-c", script], TimeSpan.FromSeconds(5), isolatePython: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return;
        }

        var lines = SplitLines(result.StandardOutput);
        if (lines.Length > 0 && PythonVersion.TryParse(lines[0], out var parsed) &&
            (string.IsNullOrWhiteSpace(install.Version) || install.IsVirtualEnvironment))
        {
            install.Version = parsed.ToString();
        }

        if (install.Architecture == "unknown" && lines.Length > 1)
        {
            var machine = lines.Length > 2 ? lines[2] : "";
            install.Architecture = machine.Contains("ARM", StringComparison.OrdinalIgnoreCase) ? "arm64" :
                lines[1].Contains("32", StringComparison.Ordinal) ? "x86" : "x64";
        }
    }

    private static string[] SplitLines(string text)
    {
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int CountVersionParts(string version)
    {
        return string.IsNullOrWhiteSpace(version) ? 0 : version.Split('.').Length;
    }

    private static string? ReadRegistryString(RegistryKey? key, string valueName)
    {
        return key?.GetValue(valueName) as string;
    }

    internal static bool IsMsysPython(string executablePath)
    {
        return executablePath.Contains(@"\msys64\", StringComparison.OrdinalIgnoreCase) ||
               executablePath.Contains(@"\msys32\", StringComparison.OrdinalIgnoreCase) ||
               executablePath.Contains(@"\cygwin", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^\r\n*]*?python(?:\d[\d.]*)?\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex PythonPathPattern();
}
