using System.Diagnostics;
using System.Text;

namespace PyVSwitch.Services;

public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";
    public bool TimedOut { get; init; }

    public bool Success => ExitCode == 0 && !TimedOut;
    public string CombinedOutput => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : string.IsNullOrWhiteSpace(StandardOutput) ? StandardError : $"{StandardOutput}{Environment.NewLine}{StandardError}";
}

public static class ProcessRunner
{
    // Variables that make one interpreter load another interpreter's files.
    private static readonly string[] PythonEnvironmentLeaks =
        ["PYTHONHOME", "PYTHONPATH", "VIRTUAL_ENV", "PYTHONSTARTUP", "__PYVENV_LAUNCHER__", "PIP_REQUIRE_VIRTUALENV", "PIP_TARGET", "PIP_PREFIX"];

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        Action<string>? onOutput = null,
        bool isolatePython = false,
        string? workingDirectory = null,
        string? rawArguments = null,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            process.StartInfo.WorkingDirectory = workingDirectory;
        }

        if (rawArguments is not null)
        {
            // Pre-quoted command lines (for example registry uninstall strings) are passed through untouched.
            process.StartInfo.Arguments = rawArguments;
        }
        else
        {
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
        }

        if (isolatePython)
        {
            foreach (var name in PythonEnvironmentLeaks)
            {
                process.StartInfo.Environment.Remove(name);
            }

            process.StartInfo.Environment["PYTHONUTF8"] = "1";
            process.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            process.StartInfo.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
            process.StartInfo.Environment["PIP_NO_INPUT"] = "1";
            process.StartInfo.Environment["PIP_NO_COLOR"] = "1";
            process.StartInfo.Environment["NO_COLOR"] = "1";
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var sync = new object();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            lock (sync) stdout.AppendLine(args.Data);
            onOutput?.Invoke(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            lock (sync) stderr.AppendLine(args.Data);
            onOutput?.Invoke(args.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new ProcessResult { ExitCode = -1, StandardError = ex.Message };
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is not null)
        {
            timeoutSource.CancelAfter(timeout.Value);
        }

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            TryKill(process);
            if (!timedOut)
            {
                throw;
            }
        }

        if (!timedOut)
        {
            // Flushes the async output readers.
            process.WaitForExit();
        }

        lock (sync)
        {
            return new ProcessResult
            {
                ExitCode = timedOut ? -1 : process.ExitCode,
                StandardOutput = stdout.ToString().TrimEnd(),
                StandardError = stderr.ToString().TrimEnd(),
                TimedOut = timedOut
            };
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Failed to kill a timed-out process.");
        }
    }
}
