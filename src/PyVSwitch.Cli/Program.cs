using PyVSwitch.Services;

namespace PyVSwitch.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Double-clicked or started from a shortcut: open the desktop app instead of flashing a help screen.
        if (args.Length == 0 && Term.OwnsConsole() && Commands.TryLaunchGui())
        {
            return 0;
        }

        var json = args.TakeWhile(arg => arg != "--").Contains("--json");
        var isMcp = args.Length > 0 && args[0].Equals("mcp", StringComparison.OrdinalIgnoreCase);
        if (!isMcp)
        {
            Term.Initialize();
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return await Commands.DispatchAsync(CommandLine.Parse(args), cancellation.Token);
        }
        catch (PyvsException ex)
        {
            Term.Error(json, ex.Code, ex.Message);
            return ex.Code switch
            {
                "usage" or "bad_version" or "bad_arch" => 2,
                "not_found" or "no_active" => 3,
                _ => 1
            };
        }
        catch (OperationCanceledException)
        {
            Term.Error(json, "cancelled", "Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Unhandled CLI failure.");
            Term.Error(json, "internal_error", ex.Message);
            return 1;
        }
    }
}

internal static class Help
{
    public static void Print()
    {
        var b = Term.Bold;
        var d = Term.Dim;
        Console.WriteLine($"""
            {b("pyvswitch")} {AppInfo.Version} - Python version manager for Windows

            {b("USAGE")}
              pyvswitch <command> [arguments] [--json]

            {b("VERSIONS")}
              list                          Installed interpreters {d("(--venvs to include virtual environments)")}
              current                       The interpreter `python` resolves to
              use <version>                 Make a version the default python {d("(--scope user|system, --no-launcher)")}
              rollback                      Undo the last PATH change {d("(--list shows history)")}
              which [-p <version>]          Print the path of an interpreter

            {b("DOWNLOAD")}
              available                     Versions on python.org {d("(--series 3.12, --all, --pre, --refresh)")}
              install <version>             Download and install {d("(--arch x64|x86|arm64, --use, --all-users, --target-dir)")}
              uninstall <version> --yes     Remove an installed version

            {b("PACKAGES")}  {d("-p picks the interpreter; omitted = the active one")}
              packages [-p <version>]       Installed packages {d("(--outdated)")}
              add <pkg>... [-p <version>]   Install packages {d("(-r requirements.txt, --upgrade)")}
              remove <pkg>... [-p <version>]
              upgrade <pkg>...|--all [-p <version>]
              freeze [-p <version>]         Export requirements {d("(-o requirements.txt)")}
              info <pkg>                    Look a package up on PyPI
              pip [-p <version>] -- <args>  Run pip directly for one interpreter

            {b("RUN")}
              run [-p <version>] -- <args>  Run a specific Python without switching
              venv <folder> [-p <version>]  Create a virtual environment {d("(--list)")}

            {b("TOOLS")}
              doctor                        Diagnose PATH problems {d("(--fix)")}
              ai <status|setup|remove|guide> Tell AI assistants about pyvswitch {d("(--only claude-code,codex,...)")}
              mcp                           Run the MCP server on stdio
              gui                           Open the desktop app

            {b("SELECTORS")}
              3.12   3.12.4   3.10-32   3.10-64   C:\path\python.exe   .\venv-folder

            {b("EXAMPLES")}
              pyvswitch install 3.13 --use
              pyvswitch add requests "numpy<2" -p 3.12
              pyvswitch run -p 3.10-32 -- script.py

            {d(AppInfo.RepoUrl)}
            """);
    }
}
