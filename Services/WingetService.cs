using System.Diagnostics;
using System.Text.RegularExpressions;
using PythonVersionSwitch.Models;

namespace PythonVersionSwitch.Services;

public sealed partial class WingetService
{
    public async Task<IReadOnlyList<InstallablePythonPackage>> SearchPythonAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunProcessAsync("winget", "search Python.Python --source winget", cancellationToken, 12000);
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var packages = new List<InstallablePythonPackage>();
        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = WingetLinePattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups["id"].Value.Trim();
            if (!id.StartsWith("Python.Python", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            packages.Add(new InstallablePythonPackage
            {
                Name = match.Groups["name"].Value.Trim(),
                Id = id,
                Version = match.Groups["version"].Value.Trim(),
                Source = "winget"
            });
        }

        return packages
            .GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(package => package.Version)
            .ToList();
    }

    public void Install(InstallablePythonPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.Id))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = $"install --id {package.Id} --source winget",
            UseShellExecute = true
        });
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

    [GeneratedRegex(@"^(?<name>.+?)\s{2,}(?<id>Python\.Python(?:\.\d+(?:\.\d+)?)?)\s{2,}(?<version>[^\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex WingetLinePattern();
}
