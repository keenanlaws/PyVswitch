using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PythonVersionSwitch.Models;

namespace PythonVersionSwitch.Services;

public sealed partial class PythonDiscoveryService
{
    public async Task<IReadOnlyList<PythonInstall>> DiscoverAsync(IEnumerable<PythonInstall> cachedInstalls, CancellationToken cancellationToken = default)
    {
        var installs = new Dictionary<string, PythonInstall>(StringComparer.OrdinalIgnoreCase);

        foreach (var install in DiscoverFromRegistry())
        {
            AddOrUpdate(installs, install);
        }

        foreach (var install in await DiscoverFromPythonLauncherAsync(cancellationToken))
        {
            AddOrUpdate(installs, install);
        }

        foreach (var install in await DiscoverFromWhereAsync(cancellationToken))
        {
            AddOrUpdate(installs, install);
        }

        foreach (var install in cachedInstalls)
        {
            if (File.Exists(install.ExecutablePath))
            {
                install.IsCached = true;
                install.Source = string.IsNullOrWhiteSpace(install.Source) ? "Cache" : install.Source;
                AddOrUpdate(installs, install);
            }
        }

        foreach (var install in installs.Values)
        {
            if (string.IsNullOrWhiteSpace(install.Version) || install.Architecture == "unknown")
            {
                await FillRuntimeDetailsAsync(install, cancellationToken);
            }
        }

        return installs.Values
            .OrderByDescending(install => ParseVersion(install.Version))
            .ThenBy(install => install.Architecture)
            .ThenBy(install => install.ExecutablePath)
            .ToList();
    }

    private static void AddOrUpdate(Dictionary<string, PythonInstall> installs, PythonInstall install)
    {
        if (!File.Exists(install.ExecutablePath))
        {
            return;
        }

        install.ExecutablePath = Path.GetFullPath(install.ExecutablePath);
        if (IsMsysPython(install.ExecutablePath))
        {
            AppLog.Info($"Ignoring MSYS2 Python during discovery: {install.ExecutablePath}");
            return;
        }

        install.InstallDirectory = Path.GetDirectoryName(install.ExecutablePath) ?? "";
        var scripts = Path.Combine(install.InstallDirectory, "Scripts");
        install.ScriptsDirectory = Directory.Exists(scripts) ? scripts : install.ScriptsDirectory;
        install.Id = install.ExecutablePath.ToUpperInvariant();
        install.LastSeenAt = DateTimeOffset.Now;

        if (installs.TryGetValue(install.ExecutablePath, out var existing))
        {
            if (string.IsNullOrWhiteSpace(existing.Version) && !string.IsNullOrWhiteSpace(install.Version))
            {
                existing.Version = install.Version;
            }

            if ((string.IsNullOrWhiteSpace(existing.Architecture) || existing.Architecture == "unknown") &&
                !string.IsNullOrWhiteSpace(install.Architecture))
            {
                existing.Architecture = install.Architecture;
            }

            if (!existing.Source.Contains(install.Source, StringComparison.OrdinalIgnoreCase))
            {
                existing.Source = $"{existing.Source}, {install.Source}".Trim(',', ' ');
            }
        }
        else
        {
            installs[install.ExecutablePath] = install;
        }
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

                        var version = ReadRegistryString(tagKey, "SysVersion") ?? VersionPattern().Match(tagName).Value;
                        var architecture = ReadRegistryString(tagKey, "SysArchitecture") ??
                                           ReadRegistryString(installPathKey, "SysArchitecture") ??
                                           (view == RegistryView.Registry32 ? "x86" : "x64");

                        yield return PythonInstall.FromExecutable(
                            executable,
                            $"{hive.ToString().Replace("LocalMachine", "HKLM").Replace("CurrentUser", "HKCU")} registry",
                            version,
                            NormalizeArchitecture(architecture));
                    }
                }
            }
        }
    }

    private static async Task<IEnumerable<PythonInstall>> DiscoverFromPythonLauncherAsync(CancellationToken cancellationToken)
    {
        var output = await RunProcessAsync("py", "-0p", cancellationToken, 3000);
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var installs = new List<PythonInstall>();
        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pathMatch = PythonPathPattern().Match(line);
            if (!pathMatch.Success)
            {
                continue;
            }

            var version = VersionPattern().Match(line).Value;
            var architecture = line.Contains("-32", StringComparison.OrdinalIgnoreCase) ? "x86" :
                line.Contains("-64", StringComparison.OrdinalIgnoreCase) ? "x64" : null;

            installs.Add(PythonInstall.FromExecutable(pathMatch.Value, "Python launcher", version, architecture));
        }

        return installs;
    }

    private static async Task<IEnumerable<PythonInstall>> DiscoverFromWhereAsync(CancellationToken cancellationToken)
    {
        var output = await RunProcessAsync("where.exe", "python", cancellationToken, 3000);
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        return output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(File.Exists)
            .Select(path => PythonInstall.FromExecutable(path, "PATH"));
    }

    private static async Task FillRuntimeDetailsAsync(PythonInstall install, CancellationToken cancellationToken)
    {
        var script = "import platform,sys; print('.'.join(map(str, sys.version_info[:3]))); print(platform.architecture()[0])";
        var output = await RunProcessAsync(install.ExecutablePath, $"-c \"{script}\"", cancellationToken, 3000);
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 0 && VersionPattern().IsMatch(lines[0]))
        {
            install.Version = VersionPattern().Match(lines[0]).Value;
        }

        if (lines.Length > 1)
        {
            install.Architecture = NormalizeArchitecture(lines[1]);
        }
    }

    private static async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken, int timeoutMilliseconds)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var completedTask = await Task.WhenAny(waitTask, Task.Delay(timeoutMilliseconds, cancellationToken));
            if (completedTask != waitTask && !process.HasExited)
            {
                process.Kill(true);
            }

            var output = await outputTask;
            var error = await errorTask;
            return string.IsNullOrWhiteSpace(output) ? error : output;
        }
        catch
        {
            return "";
        }
    }

    private static string? ReadRegistryString(RegistryKey? key, string valueName)
    {
        return key?.GetValue(valueName) as string;
    }

    private static string NormalizeArchitecture(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        if (value.Contains("32", StringComparison.OrdinalIgnoreCase) || value.Contains("x86", StringComparison.OrdinalIgnoreCase))
        {
            return "x86";
        }

        return "x64";
    }

    private static Version ParseVersion(string version)
    {
        if (Version.TryParse(version, out var parsed))
        {
            return parsed;
        }

        return new Version(0, 0);
    }

    private static bool IsMsysPython(string executablePath)
    {
        return executablePath.StartsWith(@"C:\msys64\", StringComparison.OrdinalIgnoreCase) ||
               executablePath.Contains(@"\msys64\mingw", StringComparison.OrdinalIgnoreCase) ||
               executablePath.Contains(@"\msys64\usr\", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^\r\n]*?python(?:\.exe)?", RegexOptions.IgnoreCase)]
    private static partial Regex PythonPathPattern();

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?")]
    private static partial Regex VersionPattern();
}
