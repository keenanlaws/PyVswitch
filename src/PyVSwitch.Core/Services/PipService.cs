using System.Text.Json;
using PyVSwitch.Models;

namespace PyVSwitch.Services;

/// <summary>Runs pip inside one specific interpreter, isolated from every other Python on the machine.</summary>
public sealed class PipService
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ChangeTimeout = TimeSpan.FromMinutes(30);

    public Task<ProcessResult> RunAsync(PythonInstall python, IEnumerable<string> pipArguments, Action<string>? onOutput = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        string[] arguments = ["-m", "pip", .. pipArguments];
        return ProcessRunner.RunAsync(python.ExecutablePath, arguments, timeout ?? ChangeTimeout, onOutput, isolatePython: true, cancellationToken: cancellationToken);
    }

    public async Task<List<InstalledPackage>> ListAsync(PythonInstall python, bool outdatedOnly = false, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "list", "--format=json", "--disable-pip-version-check" };
        if (outdatedOnly)
        {
            arguments.Add("--outdated");
        }

        var result = await RunAsync(python, arguments, null, QueryTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw PipFailure(python, result);
        }

        return ParsePackageList(result.StandardOutput)
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static List<InstalledPackage> ParsePackageList(string output)
    {
        // pip may print warnings before the JSON document.
        var start = output.IndexOf('[');
        var end = output.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(output[start..(end + 1)], CoreJsonContext.Default.ListInstalledPackage) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<PipResult> InstallAsync(PythonInstall python, IReadOnlyCollection<string> specs, bool upgrade = false, Action<string>? onOutput = null, CancellationToken cancellationToken = default)
    {
        ValidateSpecs(specs);
        var arguments = new List<string> { "install" };
        if (upgrade)
        {
            arguments.Add("--upgrade");
        }

        arguments.AddRange(specs);
        return ToPipResult(await RunAsync(python, arguments, onOutput, null, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PipResult> InstallRequirementsAsync(PythonInstall python, string requirementsFile, Action<string>? onOutput = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(requirementsFile))
        {
            throw new PyvsException("not_found", $"Requirements file not found: {requirementsFile}");
        }

        return ToPipResult(await RunAsync(python, ["install", "-r", Path.GetFullPath(requirementsFile)], onOutput, null, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PipResult> UninstallAsync(PythonInstall python, IReadOnlyCollection<string> names, Action<string>? onOutput = null, CancellationToken cancellationToken = default)
    {
        ValidateSpecs(names);
        string[] arguments = ["uninstall", "-y", .. names];
        return ToPipResult(await RunAsync(python, arguments, onOutput, null, cancellationToken).ConfigureAwait(false));
    }

    public async Task<string> FreezeAsync(PythonInstall python, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(python, ["freeze"], null, QueryTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw PipFailure(python, result);
        }

        return result.StandardOutput;
    }

    public async Task<string?> GetVersionAsync(PythonInstall python, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(python, ["--version"], null, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        // "pip 24.0 from C:\...\site-packages\pip (python 3.12)"
        var parts = result.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : result.StandardOutput.Trim();
    }

    public async Task<PipResult> EnsurePipAsync(PythonInstall python, Action<string>? onOutput = null, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(python.ExecutablePath, ["-m", "ensurepip", "--upgrade"], ChangeTimeout, onOutput, isolatePython: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ToPipResult(result);
    }

    private static void ValidateSpecs(IReadOnlyCollection<string> specs)
    {
        if (specs.Count == 0)
        {
            throw new PyvsException("usage", "Name at least one package.");
        }

        var option = specs.FirstOrDefault(spec => spec.StartsWith('-'));
        if (option is not null)
        {
            throw new PyvsException("usage", $"'{option}' looks like a pip option. Use `pyvswitch pip -p <version> -- <pip arguments>` to pass options straight to pip.");
        }
    }

    private static PipResult ToPipResult(ProcessResult result)
    {
        return new PipResult
        {
            ExitCode = result.TimedOut ? -1 : result.ExitCode,
            Output = result.TimedOut ? result.CombinedOutput + Environment.NewLine + "pip timed out." : result.CombinedOutput
        };
    }

    private static PyvsException PipFailure(PythonInstall python, ProcessResult result)
    {
        var output = result.CombinedOutput;
        if (output.Contains("No module named pip", StringComparison.OrdinalIgnoreCase))
        {
            return new PyvsException("no_pip", $"{python.DisplayName} has no pip. Bootstrap it with: pyvswitch run -p \"{python.ExecutablePath}\" -- -m ensurepip --upgrade");
        }

        return new PyvsException("pip_failed", result.TimedOut ? "pip timed out." : $"pip failed (exit code {result.ExitCode}): {output}".Trim());
    }
}
