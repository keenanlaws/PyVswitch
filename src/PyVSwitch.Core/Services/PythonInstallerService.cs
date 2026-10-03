using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PyVSwitch.Models;

namespace PyVSwitch.Services;

/// <summary>Downloads official python.org installers, verifies them and runs them unattended.</summary>
public sealed partial class PythonInstallerService
{
    private const string TrustedPrefix = "https://www.python.org/ftp/python/";

    public static string DefaultArchitecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => "x64"
    };

    public static string NormalizeArchitecture(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "" => DefaultArchitecture,
            "x64" or "64" or "amd64" or "64-bit" or "win64" => "x64",
            "x86" or "32" or "win32" or "32-bit" => "x86",
            "arm64" or "arm" => "arm64",
            var other => throw new PyvsException("bad_arch", $"Unknown architecture '{other}'. Use x64, x86 or arm64.")
        };
    }

    public async Task<string> DownloadAsync(PythonInstallerFile file, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (!file.Url.StartsWith(TrustedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new PyvsException("untrusted_source", $"Refusing to download from {file.Url}: only python.org is trusted.");
        }

        Directory.CreateDirectory(AppInfo.DownloadDirectory);
        var target = Path.Combine(AppInfo.DownloadDirectory, file.FileName);

        if (File.Exists(target) && await MatchesChecksumAsync(target, file, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(new InstallProgress { Stage = "download", Message = "Using cached installer", BytesReceived = file.Size, TotalBytes = file.Size });
            return target;
        }

        var partial = target + ".part";
        try
        {
            using var response = await PythonCatalogService.Http
                .GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? file.Size;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                var lastReport = Environment.TickCount64;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (Environment.TickCount64 - lastReport >= 120)
                    {
                        lastReport = Environment.TickCount64;
                        progress?.Report(new InstallProgress { Stage = "download", Message = $"Downloading {file.FileName}", BytesReceived = received, TotalBytes = total });
                    }
                }

                progress?.Report(new InstallProgress { Stage = "download", Message = $"Downloaded {file.FileName}", BytesReceived = received, TotalBytes = Math.Max(total, received) });
            }

            progress?.Report(new InstallProgress { Stage = "verify", Message = "Verifying checksum" });
            if (!await MatchesChecksumAsync(partial, file, cancellationToken).ConfigureAwait(false))
            {
                throw new PyvsException("checksum_mismatch", $"{file.FileName} did not match the checksum published by python.org. The download was discarded.");
            }

            File.Move(partial, target, true);
            return target;
        }
        catch (HttpRequestException ex)
        {
            throw new PyvsException("download_failed", $"Downloading {file.FileName} failed: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    public async Task<string> RunInstallerAsync(PythonRelease release, PythonInstallerFile file, string installerPath, InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppLog.LogDirectory);
        var logPath = Path.Combine(AppLog.LogDirectory, $"python-{release.Version}-{file.Architecture}-install.log");
        progress?.Report(new InstallProgress { Stage = "install", Message = $"Installing Python {release.Version} ({file.Architecture})" });

        ProcessResult result;
        if (file.Kind == "exe")
        {
            var arguments = new List<string>
            {
                "/quiet",
                $"InstallAllUsers={(options.AllUsers ? 1 : 0)}",
                // pyvswitch owns PATH ordering, so the installer must not add its own entry.
                "PrependPath=0",
                "Include_test=0",
                // An existing launcher is left alone; replacing it can fail when a newer one is installed.
                $"Include_launcher={(IsLauncherInstalled() ? 0 : 1)}",
                "InstallLauncherAllUsers=0",
                "/log",
                logPath
            };
            if (!string.IsNullOrWhiteSpace(options.TargetDirectory))
            {
                arguments.Add($"TargetDir={Path.GetFullPath(options.TargetDirectory)}");
            }

            result = await ProcessRunner.RunAsync(installerPath, arguments, TimeSpan.FromMinutes(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var targetDirectory = string.IsNullOrWhiteSpace(options.TargetDirectory)
                ? LegacyDefaultDirectory(release, file.Architecture)
                : Path.GetFullPath(options.TargetDirectory);
            var arguments = new List<string> { "/i", installerPath, "/qn", "/norestart", $"TARGETDIR={targetDirectory}", "/l*v", logPath };
            if (options.AllUsers)
            {
                arguments.Add("ALLUSERS=1");
            }

            result = await ProcessRunner.RunAsync("msiexec.exe", arguments, TimeSpan.FromMinutes(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (result.TimedOut)
        {
            throw new PyvsException("install_timeout", $"The Python {release.Version} installer did not finish within 30 minutes. Log: {logPath}");
        }

        // 3010 = success, restart required.
        if (result.ExitCode is 0 or 3010)
        {
            return logPath;
        }

        throw new PyvsException("install_failed", DescribeInstallerFailure(release, result.ExitCode) + $" Log: {logPath}");
    }

    private static string DescribeInstallerFailure(PythonRelease release, int exitCode)
    {
        return unchecked((uint)exitCode) switch
        {
            1602 or 0x80070642 => $"The Python {release.Version} installer was cancelled.",
            1638 or 0x80070666 => $"A different build of Python {release.Series} is already installed and blocks this one. Uninstall it first (pyvswitch uninstall {release.Series}), then retry.",
            1618 or 0x80070652 => "Another Windows installation is in progress. Wait for it to finish, then retry.",
            1603 or 0x80070643 => $"The Python {release.Version} installer reported a fatal error.",
            0x80070005 or 5 => $"The Python {release.Version} installer was denied access. Installing for all users needs administrator rights.",
            _ => $"The Python {release.Version} installer exited with code {exitCode} (0x{unchecked((uint)exitCode):X8})."
        };
    }

    public async Task UninstallAsync(PythonInstall install, CancellationToken cancellationToken)
    {
        var entry = FindUninstallEntry(install)
            ?? throw new PyvsException("no_uninstaller",
                $"{install.DisplayName} was not installed by a python.org installer that pyvswitch can drive. Remove it from Windows Settings > Apps > Installed apps.");

        ProcessResult result;
        if (entry.ProductCode is not null)
        {
            result = await ProcessRunner.RunAsync("msiexec.exe", ["/x", entry.ProductCode, "/qn", "/norestart"], TimeSpan.FromMinutes(15), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var (executable, arguments) = SplitCommandLine(entry.QuietUninstallCommand!);
            result = await ProcessRunner.RunAsync(executable, [], TimeSpan.FromMinutes(15), rawArguments: arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (result.TimedOut || result.ExitCode is not (0 or 3010))
        {
            throw new PyvsException("uninstall_failed", $"Uninstalling {install.DisplayName} failed with exit code {result.ExitCode}. {result.CombinedOutput}".Trim());
        }
    }

    public static bool CanUninstall(PythonInstall install) => FindUninstallEntry(install) is not null;

    private sealed record UninstallEntry(string? QuietUninstallCommand, string? ProductCode);

    private static UninstallEntry? FindUninstallEntry(PythonInstall install)
    {
        if (string.IsNullOrWhiteSpace(install.Version) || install.IsVirtualEnvironment)
        {
            return null;
        }

        var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
        foreach (var hive in hives)
        {
            foreach (var view in views)
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    var displayName = key?.GetValue("DisplayName") as string;
                    if (displayName is null)
                    {
                        continue;
                    }

                    var match = UninstallNamePattern().Match(displayName);
                    if (!match.Success || !MatchesInstall(match, install))
                    {
                        continue;
                    }

                    if (key!.GetValue("QuietUninstallString") is string quiet && !string.IsNullOrWhiteSpace(quiet))
                    {
                        return new UninstallEntry(quiet, null);
                    }

                    if (ProductCodePattern().IsMatch(name) && key.GetValue("WindowsInstaller") is 1)
                    {
                        return new UninstallEntry(null, name);
                    }
                }
            }
        }

        return null;
    }

    private static bool MatchesInstall(Match match, PythonInstall install)
    {
        if (!PythonVersion.TryParse(match.Groups["version"].Value, out var version) ||
            version.CompareTo(install.ParsedVersion) != 0)
        {
            return false;
        }

        var arch = match.Groups["arch"].Value.ToLowerInvariant();
        var entryArchitecture = arch.Contains("arm64") ? "arm64" : arch.Contains("64") ? "x64" : "x86";
        return entryArchitecture == install.Architecture;
    }

    internal static (string Executable, string Arguments) SplitCommandLine(string commandLine)
    {
        commandLine = commandLine.Trim();
        if (commandLine.StartsWith('"'))
        {
            var end = commandLine.IndexOf('"', 1);
            if (end > 0)
            {
                return (commandLine[1..end], commandLine[(end + 1)..].Trim());
            }
        }

        var space = commandLine.IndexOf(' ');
        return space < 0 ? (commandLine, "") : (commandLine[..space], commandLine[(space + 1)..].Trim());
    }

    private static bool IsLauncherInstalled()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Launcher", "py.exe")
        };
        return candidates.Any(File.Exists);
    }

    private static string LegacyDefaultDirectory(PythonRelease release, string architecture)
    {
        var version = release.ParsedVersion;
        var suffix = architecture == "x86" ? "-32" : "";
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", $"Python{version.Major}{version.Minor}{suffix}");
    }

    private static async Task<bool> MatchesChecksumAsync(string path, PythonInstallerFile file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (file.Size > 0 && info.Length != file.Size)
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        if (!string.IsNullOrWhiteSpace(file.Sha256))
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(file.Md5))
        {
            // python.org publishes MD5 for older releases; it guards against corruption, HTTPS guards the source.
            var hash = await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).Equals(file.Md5, StringComparison.OrdinalIgnoreCase);
        }

        return info.Length > 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A leftover partial download is harmless and is overwritten next time.
        }
    }

    [GeneratedRegex(@"^Python (?<version>\d+\.\d+(?:\.\d+)?(?:(?:a|b|rc)\d+)?) \((?<arch>[^)]+)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex UninstallNamePattern();

    [GeneratedRegex(@"^\{[0-9A-Fa-f-]{36}\}$")]
    private static partial Regex ProductCodePattern();
}
