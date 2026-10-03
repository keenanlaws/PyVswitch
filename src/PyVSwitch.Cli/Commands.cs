using System.Diagnostics;
using System.Text.Json.Nodes;
using PyVSwitch.Models;
using PyVSwitch.Services;

namespace PyVSwitch.Cli;

internal sealed record Context(PyEngine Engine, CommandLine Line, bool Json, CancellationToken Cancel)
{
    public string? Python => Line.Option("--python");
}

internal static class Commands
{
    public static async Task<int> DispatchAsync(CommandLine line, CancellationToken cancel)
    {
        if (line.Command is "version" || (line.Command.Length == 0 && line.Flag("--version")))
        {
            if (line.Flag("--json"))
            {
                Term.Json(new JsonObject { ["name"] = AppInfo.Name, ["version"] = AppInfo.Version });
            }
            else
            {
                Console.WriteLine($"pyvswitch {AppInfo.Version}");
            }

            return 0;
        }

        if (line.Command is "" or "help" || line.Flag("--help"))
        {
            Help.Print();
            return 0;
        }

        var context = new Context(new PyEngine(), line, line.Flag("--json"), cancel);
        return line.Command switch
        {
            "list" or "ls" => await ListAsync(context),
            "current" or "active" => await CurrentAsync(context),
            "use" or "switch" => await UseAsync(context),
            "rollback" => Rollback(context),
            "available" or "releases" => await AvailableAsync(context),
            "install" => await InstallAsync(context),
            "uninstall" => await UninstallAsync(context),
            "which" => await WhichAsync(context),
            "run" => await RunAsync(context),
            "pip" => await PipAsync(context),
            "packages" or "pkgs" => await PackagesAsync(context),
            "add" => await AddAsync(context),
            "remove" or "rm" => await RemoveAsync(context),
            "upgrade" => await UpgradeAsync(context),
            "freeze" => await FreezeAsync(context),
            "info" => await InfoAsync(context),
            "venv" => await VenvAsync(context),
            "doctor" => await DoctorAsync(context),
            "ai" => await AiAsync(context),
            "mcp" => await McpServer.RunAsync(context.Engine, cancel),
            "gui" or "app" => Gui(context),
            _ => throw new PyvsException("usage", $"Unknown command '{line.Command}'. Run `pyvswitch help` to see every command.")
        };
    }

    // Interpreters ---------------------------------------------------------------------------

    private static async Task<int> ListAsync(Context context)
    {
        var installs = await context.Engine.DiscoverAsync(context.Cancel);
        var state = context.Engine.GetActiveState(installs);
        var venvs = context.Line.Flag("--venvs") ? await context.Engine.GetVenvsAsync(context.Cancel) : [];

        if (context.Json)
        {
            var data = JsonShapes.Installs(installs, state);
            if (context.Line.Flag("--venvs"))
            {
                data["virtualEnvironments"] = new JsonArray(venvs.Select(venv => (JsonNode)JsonShapes.Install(venv)).ToArray());
            }

            Term.Json(data);
            return 0;
        }

        if (installs.Count == 0)
        {
            Console.WriteLine("No Python installations found. Install one with: pyvswitch install latest");
            return 0;
        }

        var active = state.EffectivePath ?? state.Selected?.ExecutablePath;
        var rows = installs.Select(install => new[]
        {
            IsSame(install, active) ? "●" : " ",
            install.Version,
            install.Architecture,
            install.Tag,
            install.ExecutablePath
        }).ToList();
        Term.Table(["", "VERSION", "ARCH", "SELECTOR", "PATH"], rows, (row, column, cell) =>
            IsSame(installs[row], active)
                ? column <= 1 ? Term.Green(cell) : Term.Bold(cell)
                : column == 4 ? Term.Dim(cell) : cell);

        if (venvs.Count > 0)
        {
            Console.WriteLine();
            Term.Table(["VENV", "PATH"], venvs.Select(venv => new[] { venv.Version, Path.GetDirectoryName(venv.InstallDirectory) ?? venv.InstallDirectory }).ToList());
        }

        PrintShadowWarning(state);
        return 0;
    }

    private static async Task<int> CurrentAsync(Context context)
    {
        var installs = await context.Engine.DiscoverAsync(context.Cancel);
        var state = context.Engine.GetActiveState(installs);
        if (context.Json)
        {
            Term.Json(JsonShapes.State(state));
            return state.EffectivePath is null ? 3 : 0;
        }

        if (state.EffectivePath is null)
        {
            Console.WriteLine("No Python is active on PATH. Pick one with: pyvswitch use <version>");
            return 3;
        }

        var name = state.Effective?.DisplayName ?? "Python";
        Console.WriteLine($"{Term.Green("●")} {Term.Bold(name)}");
        Console.WriteLine($"  {Term.Dim(state.EffectivePath)}");
        PrintShadowWarning(state);
        return 0;
    }

    private static async Task<int> UseAsync(Context context)
    {
        var selector = context.Line.Positionals.FirstOrDefault() ?? context.Python
            ?? throw new PyvsException("usage", "Usage: pyvswitch use <version>   (for example: pyvswitch use 3.12)");
        var installs = await context.Engine.DiscoverAsync(context.Cancel);
        var install = await context.Engine.ResolveAsync(installs, selector, context.Cancel);
        var result = context.Engine.Switch(install, installs, ParseScope(context), context.Line.Flag("--no-launcher") ? false : null);

        if (context.Json)
        {
            var data = JsonShapes.State(result.State);
            data["switchedTo"] = JsonShapes.Install(install, result.State);
            data["warning"] = result.Warning;
            data["note"] = "New terminals use this version. Already-open shells keep their previous PATH.";
            Term.Json(data);
            return 0;
        }

        Console.WriteLine($"{Term.Green("✓")} {Term.Bold(install.DisplayName)} is now the default python on the {result.Backup.Scope} PATH.");
        Console.WriteLine(Term.Dim("  New terminals pick it up. This shell keeps its current PATH; use `pyvswitch run` to call it right away."));
        if (result.Warning is not null)
        {
            Console.WriteLine($"{Term.Yellow("!")} {result.Warning}");
        }

        return 0;
    }

    private static int Rollback(Context context)
    {
        if (context.Line.Flag("--list"))
        {
            var backups = context.Engine.Settings.Load().PathBackups.OrderByDescending(backup => backup.CreatedAt).ToList();
            if (context.Json)
            {
                Term.Json(new JsonArray(backups.Select(backup => (JsonNode)JsonShapes.Backup(backup)).ToArray()));
            }
            else if (backups.Count == 0)
            {
                Console.WriteLine("No PATH backups yet.");
            }
            else
            {
                Term.Table(["WHEN", "SCOPE", "CHANGE"], backups.Select(backup => new[]
                {
                    backup.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), backup.Scope.ToString(), backup.DisplayName
                }).ToList());
            }

            return 0;
        }

        var restored = context.Engine.Rollback();
        if (context.Json)
        {
            Term.Json(JsonShapes.Backup(restored));
        }
        else
        {
            Console.WriteLine($"{Term.Green("✓")} Restored the {restored.Scope} PATH to its state before \"{restored.DisplayName}\" ({restored.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm}).");
        }

        return 0;
    }

    private static async Task<int> WhichAsync(Context context)
    {
        var installs = await context.Engine.DiscoverAsync(context.Cancel);
        var install = await context.Engine.ResolveAsync(installs, context.Python ?? context.Line.Positionals.FirstOrDefault(), context.Cancel);
        if (context.Json)
        {
            Term.Json(JsonShapes.Install(install, context.Engine.GetActiveState(installs)));
        }
        else
        {
            Console.WriteLine(install.ExecutablePath);
        }

        return 0;
    }

    // Downloading and installing Python ------------------------------------------------------

    private static async Task<int> AvailableAsync(Context context)
    {
        var catalog = await context.Engine.Catalog.GetAsync(context.Line.Flag("--refresh"), context.Cancel);
        var installs = await context.Engine.DiscoverAsync(context.Cancel);
        var seriesFilter = context.Line.Option("--series") ?? context.Line.Positionals.FirstOrDefault();
        var includePre = context.Line.Flag("--pre");

        if (seriesFilter is not null || context.Line.Flag("--all"))
        {
            var releases = catalog.Series
                .Where(series => seriesFilter is null || series.Name == NormalizeSeries(seriesFilter))
                .SelectMany(series => series.Releases)
                .Where(release => includePre || !release.IsPrerelease || seriesFilter is not null)
                .ToList();
            if (releases.Count == 0)
            {
                throw new PyvsException("not_found", $"python.org has no Python {seriesFilter} releases.");
            }

            if (context.Json)
            {
                Term.Json(new JsonArray(releases.Select(release => (JsonNode)JsonShapes.Release(release, installs)).ToArray()));
                return 0;
            }

            Term.Table(["VERSION", "RELEASED", "INSTALLER", ""], releases.Select(release => new[]
            {
                release.Version,
                release.ReleaseDate,
                release.HasInstaller ? string.Join(", ", release.Installers.Select(file => file.Architecture)) : "source only",
                installs.Any(install => install.ParsedVersion.CompareTo(release.ParsedVersion) == 0) ? "installed" : ""
            }).ToList(), (row, column, cell) =>
                column == 3 ? Term.Green(cell) : !releases[row].HasInstaller ? Term.Dim(cell) : cell);
            return 0;
        }

        var visible = catalog.Series
            .Where(series => series.LatestInstallable is not null)
            .Where(series => includePre || series.Releases.Any(release => !release.IsPrerelease))
            .ToList();

        if (context.Json)
        {
            Term.Json(new JsonObject
            {
                ["fetchedAt"] = catalog.FetchedAt.ToString("o"),
                ["stale"] = catalog.IsStale,
                ["series"] = new JsonArray(visible.Select(series => (JsonNode)JsonShapes.Series(series, installs)).ToArray())
            });
            return 0;
        }

        Term.Table(["SERIES", "LATEST", "STATUS", "END OF LIFE", "ARCH", "INSTALLED"], visible.Select(series =>
        {
            var latest = series.LatestInstallable!;
            return new[]
            {
                series.Name,
                latest.Version,
                series.StatusLabel,
                series.EndOfLife,
                string.Join(", ", latest.Installers.Select(file => file.Architecture)),
                string.Join(", ", installs.Where(install => install.Series == series.Name).Select(install => $"{install.Version} {install.Architecture}"))
            };
        }).ToList(), (row, column, cell) => column switch
        {
            0 => Term.Bold(cell),
            2 => visible[row].Status switch
            {
                "bugfix" => Term.Green(cell),
                "security" => Term.Yellow(cell),
                "prerelease" => Term.Cyan(cell),
                _ => Term.Dim(cell)
            },
            5 => Term.Green(cell),
            _ => cell
        });
        Console.WriteLine();
        Console.WriteLine(Term.Dim("Install with `pyvswitch install <series|version>`. Every patch release: `pyvswitch available --series 3.12`."));
        if (catalog.IsStale)
        {
            Console.WriteLine(Term.Yellow("python.org could not be reached; showing the cached list."));
        }

        return 0;
    }

    private static async Task<int> InstallAsync(Context context)
    {
        var spec = context.Line.Positionals.FirstOrDefault()
            ?? throw new PyvsException("usage", "Usage: pyvswitch install <version>   (for example: pyvswitch install 3.13, pyvswitch install 3.12.4 --arch x86)");
        var options = new InstallOptions
        {
            Architecture = context.Line.Option("--arch") ?? "",
            TargetDirectory = context.Line.Option("--target-dir"),
            AllUsers = context.Line.Flag("--all-users"),
            Activate = context.Line.Flag("--use"),
            AllowPrerelease = context.Line.Flag("--pre")
        };

        var progress = context.Json ? null : new ConsoleProgress();
        InstallOutcome outcome;
        try
        {
            outcome = await context.Engine.InstallPythonAsync(spec, options, progress, context.Cancel);
        }
        finally
        {
            progress?.CloseBar();
        }

        if (context.Json)
        {
            Term.Json(new JsonObject
            {
                ["installed"] = outcome.Install is null ? null : JsonShapes.Install(outcome.Install),
                ["alreadyInstalled"] = outcome.AlreadyInstalled,
                ["activated"] = options.Activate,
                ["source"] = outcome.File.Url
            });
            return 0;
        }

        var name = outcome.Install?.DisplayName ?? $"Python {outcome.Release.Version}";
        Console.WriteLine(outcome.AlreadyInstalled
            ? $"{Term.Green("✓")} {Term.Bold(name)} is already installed."
            : $"{Term.Green("✓")} Installed {Term.Bold(name)}.");
        Console.WriteLine($"  {Term.Dim(outcome.Install?.ExecutablePath ?? "")}");
        Console.WriteLine(options.Activate
            ? Term.Dim("  It is now the default python for new terminals.")
            : Term.Dim($"  Make it the default with: pyvswitch use {outcome.Install?.Tag}"));
        return 0;
    }

    private static async Task<int> UninstallAsync(Context context)
    {
        var selector = context.Line.Positionals.FirstOrDefault()
            ?? throw new PyvsException("usage", "Usage: pyvswitch uninstall <version> --yes");
        var installs = await context.Engine.DiscoverAsync(context.Cancel);
        var install = await context.Engine.ResolveAsync(installs, selector, context.Cancel);

        if (!context.Line.Flag("--yes"))
        {
            if (context.Json || Console.IsInputRedirected)
            {
                throw new PyvsException("confirmation_required", $"This removes {install.DisplayName} at {install.InstallDirectory}. Re-run with --yes to confirm.");
            }

            Console.Write($"Uninstall {Term.Bold(install.DisplayName)} from {install.InstallDirectory}? [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Cancelled.");
                return 1;
            }
        }

        if (!context.Json)
        {
            Console.WriteLine($"  {Term.Dim("›")} Uninstalling {install.DisplayName}");
        }

        await context.Engine.UninstallPythonAsync(install, context.Cancel);
        if (context.Json)
        {
            Term.Json(new JsonObject { ["uninstalled"] = JsonShapes.Install(install) });
        }
        else
        {
            Console.WriteLine($"{Term.Green("✓")} Removed {Term.Bold(install.DisplayName)}.");
        }

        return 0;
    }

    // Running Python and pip -----------------------------------------------------------------

    private static async Task<int> RunAsync(Context context)
    {
        var install = await ResolvePythonAsync(context);
        return await ExecuteAsync(context, install, context.Line.Rest);
    }

    private static async Task<int> PipAsync(Context context)
    {
        if (context.Line.Rest.Count == 0)
        {
            throw new PyvsException("usage", "Usage: pyvswitch pip [-p <version>] -- <pip arguments>   (for example: pyvswitch pip -p 3.12 -- install -e .)");
        }

        var install = await ResolvePythonAsync(context);
        return await ExecuteAsync(context, install, ["-m", "pip", .. context.Line.Rest]);
    }

    /// <summary>Runs the interpreter attached to this console so prompts and live output behave normally.</summary>
    private static async Task<int> ExecuteAsync(Context context, PythonInstall install, IEnumerable<string> arguments)
    {
        if (context.Json)
        {
            var captured = await ProcessRunner.RunAsync(install.ExecutablePath, arguments, null, cancellationToken: context.Cancel);
            Term.Json(new JsonObject
            {
                ["python"] = JsonShapes.Install(install),
                ["exitCode"] = captured.ExitCode,
                ["stdout"] = captured.StandardOutput,
                ["stderr"] = captured.StandardError
            });
            return captured.ExitCode;
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo { FileName = install.ExecutablePath, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        // The child (and anything it spawns) should see this interpreter first, and must not inherit another one's home.
        process.StartInfo.Environment.Remove("PYTHONHOME");
        var scripts = install.IsVirtualEnvironment ? install.InstallDirectory : Path.Combine(install.InstallDirectory, "Scripts");
        process.StartInfo.Environment["PATH"] = $"{install.InstallDirectory};{scripts};{Environment.GetEnvironmentVariable("PATH")}";

        process.Start();
        try
        {
            await process.WaitForExitAsync(context.Cancel);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C reaches the child directly because it shares the console; just wait for it to stop.
            await process.WaitForExitAsync();
        }

        return process.ExitCode;
    }

    // Packages -------------------------------------------------------------------------------

    private static async Task<int> PackagesAsync(Context context)
    {
        var install = await ResolvePythonAsync(context);
        var outdatedOnly = context.Line.Flag("--outdated");
        var packages = await context.Engine.Pip.ListAsync(install, outdatedOnly, context.Cancel);
        if (context.Json)
        {
            Term.Json(JsonShapes.Packages(install, packages));
            return 0;
        }

        Console.WriteLine($"{Term.Bold(install.DisplayName)}  {Term.Dim(install.ExecutablePath)}");
        if (packages.Count == 0)
        {
            Console.WriteLine(outdatedOnly ? "Everything is up to date." : "No packages installed.");
            return 0;
        }

        if (outdatedOnly)
        {
            Term.Table(["PACKAGE", "INSTALLED", "LATEST"], packages.Select(package => new[] { package.Name, package.Version, package.LatestVersion ?? "" }).ToList(),
                (_, column, cell) => column == 2 ? Term.Green(cell) : cell);
        }
        else
        {
            Term.Table(["PACKAGE", "VERSION"], packages.Select(package => new[] { package.Name, package.Version }).ToList());
        }

        Console.WriteLine(Term.Dim($"{packages.Count} package{(packages.Count == 1 ? "" : "s")}"));
        return 0;
    }

    private static async Task<int> AddAsync(Context context)
    {
        var install = await ResolvePythonAsync(context);
        var requirements = context.Line.Option("--requirement");
        if (requirements is null && context.Line.Positionals.Count == 0)
        {
            throw new PyvsException("usage", "Usage: pyvswitch add <package>... [-p <version>]   or   pyvswitch add -r requirements.txt [-p <version>]");
        }

        Announce(context, $"Installing into {install.DisplayName}");
        var result = requirements is not null
            ? await context.Engine.Pip.InstallRequirementsAsync(install, requirements, Streamer(context), context.Cancel)
            : await context.Engine.Pip.InstallAsync(install, context.Line.Positionals, context.Line.Flag("--upgrade"), Streamer(context), context.Cancel);
        return Finish(context, install, result, "Installed", requirements is null ? string.Join(", ", context.Line.Positionals) : Path.GetFileName(requirements), "into");
    }

    private static async Task<int> RemoveAsync(Context context)
    {
        var install = await ResolvePythonAsync(context);
        Announce(context, $"Removing from {install.DisplayName}");
        var result = await context.Engine.Pip.UninstallAsync(install, context.Line.Positionals, Streamer(context), context.Cancel);
        return Finish(context, install, result, "Removed", string.Join(", ", context.Line.Positionals), "from");
    }

    private static async Task<int> UpgradeAsync(Context context)
    {
        var install = await ResolvePythonAsync(context);
        IReadOnlyCollection<string> names = context.Line.Positionals;
        if (context.Line.Flag("--all"))
        {
            Announce(context, $"Checking {install.DisplayName} for outdated packages");
            names = (await context.Engine.Pip.ListAsync(install, outdatedOnly: true, context.Cancel)).Select(package => package.Name).ToList();
            if (names.Count == 0)
            {
                if (context.Json)
                {
                    Term.Json(JsonShapes.Pip(install, new PipResult { ExitCode = 0, Output = "Everything is already up to date." }));
                }
                else
                {
                    Console.WriteLine($"{Term.Green("✓")} Everything in {install.DisplayName} is already up to date.");
                }

                return 0;
            }
        }
        else if (names.Count == 0)
        {
            throw new PyvsException("usage", "Usage: pyvswitch upgrade <package>... [-p <version>]   or   pyvswitch upgrade --all [-p <version>]");
        }

        Announce(context, $"Upgrading in {install.DisplayName}");
        var result = await context.Engine.Pip.InstallAsync(install, names, upgrade: true, Streamer(context), context.Cancel);
        return Finish(context, install, result, "Upgraded", string.Join(", ", names));
    }

    private static async Task<int> FreezeAsync(Context context)
    {
        var install = await ResolvePythonAsync(context);
        var text = await context.Engine.Pip.FreezeAsync(install, context.Cancel);
        var output = context.Line.Option("--output");
        if (output is not null)
        {
            await File.WriteAllTextAsync(output, text + Environment.NewLine, context.Cancel);
        }

        if (context.Json)
        {
            Term.Json(new JsonObject { ["python"] = JsonShapes.Install(install), ["requirements"] = text, ["file"] = output is null ? null : Path.GetFullPath(output) });
        }
        else if (output is not null)
        {
            Console.WriteLine($"{Term.Green("✓")} Wrote {Path.GetFullPath(output)}");
        }
        else
        {
            Console.WriteLine(text);
        }

        return 0;
    }

    private static async Task<int> InfoAsync(Context context)
    {
        var name = context.Line.Positionals.FirstOrDefault()
            ?? throw new PyvsException("usage", "Usage: pyvswitch info <package>");
        var package = await context.Engine.PyPi.GetAsync(name, context.Cancel)
            ?? throw new PyvsException("not_found", $"PyPI has no package named '{name}'.");
        if (context.Json)
        {
            Term.Json(JsonShapes.PyPi(package));
            return 0;
        }

        Console.WriteLine($"{Term.Bold(package.Name)} {Term.Green(package.LatestVersion)}");
        Console.WriteLine($"  {package.Summary}");
        if (package.RequiresPython.Length > 0) Console.WriteLine($"  {Term.Dim("Requires Python")} {package.RequiresPython}");
        if (package.HomePage.Length > 0) Console.WriteLine($"  {Term.Dim(package.HomePage)}");
        Console.WriteLine($"  {Term.Dim("Recent versions:")} {string.Join(", ", package.Versions.Take(8))}");
        return 0;
    }

    private static async Task<int> VenvAsync(Context context)
    {
        if (context.Line.Flag("--list"))
        {
            var venvs = await context.Engine.GetVenvsAsync(context.Cancel);
            if (context.Json)
            {
                Term.Json(new JsonArray(venvs.Select(venv => (JsonNode)JsonShapes.Install(venv)).ToArray()));
            }
            else if (venvs.Count == 0)
            {
                Console.WriteLine("No virtual environments created with pyvswitch yet.");
            }
            else
            {
                Term.Table(["PYTHON", "FOLDER"], venvs.Select(venv => new[] { venv.Version, Path.GetDirectoryName(venv.InstallDirectory) ?? "" }).ToList());
            }

            return 0;
        }

        var directory = context.Line.Positionals.FirstOrDefault()
            ?? throw new PyvsException("usage", "Usage: pyvswitch venv <folder> [-p <version>]");
        var installs = await context.Engine.DiscoverAsync(context.Cancel);
        var python = await context.Engine.ResolveAsync(installs, context.Python, context.Cancel);
        Announce(context, $"Creating a virtual environment with {python.DisplayName}");
        var venv = await context.Engine.CreateVenvAsync(python, directory, null, context.Cancel);

        if (context.Json)
        {
            Term.Json(new JsonObject { ["venv"] = JsonShapes.Install(venv), ["basePython"] = JsonShapes.Install(python) });
            return 0;
        }

        var folder = Path.GetDirectoryName(venv.InstallDirectory)!;
        Console.WriteLine($"{Term.Green("✓")} Created {Term.Bold(folder)}");
        Console.WriteLine(Term.Dim($"  Activate:      {Path.Combine(folder, "Scripts", "activate")}"));
        Console.WriteLine(Term.Dim($"  Add packages:  pyvswitch add <package> -p \"{folder}\""));
        return 0;
    }

    // Diagnostics and AI ---------------------------------------------------------------------

    private static async Task<int> DoctorAsync(Context context)
    {
        PathBackup? fixedBackup = null;
        if (context.Line.Flag("--fix"))
        {
            fixedBackup = context.Engine.FixPath();
        }

        var installs = await context.Engine.DiscoverAsync(context.Cancel);
        var findings = await context.Engine.RunDoctorAsync(installs, context.Cancel);
        var healthy = findings.All(finding => finding.Severity is DoctorSeverity.Ok or DoctorSeverity.Info);

        if (context.Json)
        {
            Term.Json(new JsonObject
            {
                ["healthy"] = healthy,
                ["fixed"] = fixedBackup is not null,
                ["findings"] = new JsonArray(findings.Select(finding => (JsonNode)JsonShapes.Finding(finding)).ToArray())
            });
            return healthy ? 0 : 1;
        }

        if (fixedBackup is not null)
        {
            Console.WriteLine($"{Term.Green("✓")} Cleaned the User PATH (undo with `pyvswitch rollback`).");
        }

        foreach (var finding in findings)
        {
            var mark = finding.Severity switch
            {
                DoctorSeverity.Ok => Term.Green("✓"),
                DoctorSeverity.Info => Term.Blue("i"),
                DoctorSeverity.Warning => Term.Yellow("!"),
                _ => Term.Red("✗")
            };
            Console.WriteLine($"{mark} {Term.Bold(finding.Title)}");
            foreach (var detailLine in finding.Detail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                Console.WriteLine($"  {Term.Dim(detailLine)}");
            }

            if (finding.Fix.Length > 0 && finding.Severity != DoctorSeverity.Ok)
            {
                Console.WriteLine($"  {Term.Cyan("fix:")} {finding.Fix}");
            }
        }

        return healthy ? 0 : 1;
    }

    private static async Task<int> AiAsync(Context context)
    {
        var action = context.Line.Positionals.FirstOrDefault() ?? "status";
        var only = context.Line.Option("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        switch (action)
        {
            case "guide":
                var text = context.Line.Flag("--short") ? AiGuide.InstructionBlock : AiGuide.FullGuide;
                if (context.Json)
                {
                    Term.Json(new JsonObject { ["guide"] = text });
                }
                else
                {
                    Console.WriteLine(text);
                }

                return 0;

            case "setup":
            case "remove":
            case "status":
                var statuses = action switch
                {
                    "setup" => await context.Engine.Ai.SetupAsync(only, context.Cancel),
                    "remove" => await context.Engine.Ai.RemoveAsync(only, context.Cancel),
                    _ => context.Engine.Ai.GetStatus()
                };

                if (context.Json)
                {
                    Term.Json(new JsonObject
                    {
                        ["cli"] = context.Engine.Ai.CliPath,
                        ["guideFile"] = context.Engine.Ai.GuidePath,
                        ["tools"] = new JsonArray(statuses.Select(status => (JsonNode)JsonShapes.AiTarget(status)).ToArray())
                    });
                    return 0;
                }

                Term.Table(["AI TOOL", "FOUND", "INSTRUCTIONS", "MCP SERVER"], statuses.Select(status => new[]
                {
                    status.Name,
                    status.Detected ? "yes" : "not installed",
                    status.InstructionFile is null ? "n/a" : status.InstructionsRegistered ? "registered" : "-",
                    status.McpRegistered ? "registered" : "-"
                }).ToList(), (row, column, cell) =>
                    !statuses[row].Detected ? Term.Dim(cell) : cell.StartsWith("registered", StringComparison.Ordinal) ? Term.Green(cell) : cell);

                foreach (var change in statuses.SelectMany(status => status.Changes))
                {
                    Console.WriteLine($"{Term.Green("✓")} {change}");
                }

                if (action == "status")
                {
                    Console.WriteLine();
                    Console.WriteLine(Term.Dim("Register with every detected tool: pyvswitch ai setup      Print the guide: pyvswitch ai guide"));
                }
                else if (action == "setup")
                {
                    Console.WriteLine(Term.Dim("Restart the AI tool so it picks up the change."));
                }

                return 0;

            default:
                throw new PyvsException("usage", "Usage: pyvswitch ai <status|setup|remove|guide> [--only claude-code,codex,...]");
        }
    }

    private static int Gui(Context context)
    {
        var gui = Path.Combine(AppContext.BaseDirectory, AppInfo.GuiExeName);
        if (!File.Exists(gui))
        {
            throw new PyvsException("not_found", $"The desktop app ({AppInfo.GuiExeName}) was not found next to the CLI.");
        }

        Process.Start(new ProcessStartInfo { FileName = gui, UseShellExecute = true });
        if (context.Json)
        {
            Term.Json(new JsonObject { ["started"] = gui });
        }

        return 0;
    }

    public static bool TryLaunchGui()
    {
        var gui = Path.Combine(AppContext.BaseDirectory, AppInfo.GuiExeName);
        if (!File.Exists(gui))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo { FileName = gui, UseShellExecute = true });
        return true;
    }

    // Helpers --------------------------------------------------------------------------------

    private static async Task<PythonInstall> ResolvePythonAsync(Context context)
    {
        var installs = await context.Engine.DiscoverAsync(context.Cancel);
        return await context.Engine.ResolveAsync(installs, context.Python, context.Cancel);
    }

    private static Action<string>? Streamer(Context context)
    {
        return context.Json ? null : line => Console.WriteLine($"  {Term.Dim(line)}");
    }

    private static void Announce(Context context, string message)
    {
        if (!context.Json)
        {
            Console.WriteLine($"{Term.Blue("›")} {message}");
        }
    }

    private static int Finish(Context context, PythonInstall install, PipResult result, string verb, string subject, string preposition = "in")
    {
        if (context.Json)
        {
            var data = JsonShapes.Pip(install, result);
            if (!result.Success)
            {
                Console.Out.WriteLine(new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = new JsonObject { ["code"] = "pip_failed", ["message"] = $"pip exited with code {result.ExitCode}." },
                    ["data"] = data
                }.ToJsonString(JsonShapes.Indented));
                return 1;
            }

            Term.Json(data);
            return 0;
        }

        Console.WriteLine(result.Success
            ? $"{Term.Green("✓")} {verb} {Term.Bold(subject)} {preposition} {install.DisplayName}."
            : $"{Term.Red("✗")} pip failed with exit code {result.ExitCode}.");
        return result.Success ? 0 : 1;
    }

    private static PathScope? ParseScope(Context context)
    {
        return context.Line.Option("--scope")?.ToLowerInvariant() switch
        {
            null => null,
            "user" => PathScope.User,
            "system" or "machine" => PathScope.System,
            var other => throw new PyvsException("usage", $"Unknown scope '{other}'. Use user or system.")
        };
    }

    private static string NormalizeSeries(string value)
    {
        return PythonVersion.TryParse(value, out var version) ? version.Series : value;
    }

    private static bool IsSame(PythonInstall install, string? path)
    {
        return path is not null && install.ExecutablePath.Equals(path, StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintShadowWarning(ActiveState state)
    {
        if (state.IsShadowed)
        {
            Console.WriteLine();
            Console.WriteLine($"{Term.Yellow("!")} You selected {state.Selected!.DisplayName}, but a System PATH entry wins: `python` runs {state.EffectivePath}. Run `pyvswitch doctor`.");
        }
    }
}
