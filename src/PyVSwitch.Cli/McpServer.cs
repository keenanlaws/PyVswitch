using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyVSwitch.Models;
using PyVSwitch.Services;

namespace PyVSwitch.Cli;

/// <summary>
/// A Model Context Protocol server over stdio (newline-delimited JSON-RPC 2.0). It exposes the
/// same engine operations as the CLI, so AI assistants can manage Python without shelling out.
/// </summary>
internal static class McpServer
{
    private const string LatestProtocol = "2025-06-18";
    private static readonly string[] KnownProtocols = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    public static async Task<int> RunAsync(PyEngine engine, CancellationToken cancel)
    {
        // stdout carries protocol frames only, so the streams are opened directly and never share Console state.
        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        await using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        while (!cancel.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                await writer.WriteLineAsync(Error(null, -32700, "Parse error").ToJsonString(JsonShapes.Compact));
                continue;
            }

            var requests = message is JsonArray batch ? batch.OfType<JsonObject>().ToList() : message is JsonObject single ? [single] : [];
            foreach (var request in requests)
            {
                var response = await HandleAsync(engine, request, cancel);
                if (response is not null)
                {
                    await writer.WriteLineAsync(response.ToJsonString(JsonShapes.Compact));
                }
            }
        }

        return 0;
    }

    private static async Task<JsonObject?> HandleAsync(PyEngine engine, JsonObject request, CancellationToken cancel)
    {
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>() ?? "";
        var parameters = request["params"] as JsonObject;

        // Notifications carry no id and never get a reply.
        if (id is null)
        {
            return null;
        }

        try
        {
            switch (method)
            {
                case "initialize":
                    var requested = parameters?["protocolVersion"]?.GetValue<string>();
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = requested is not null && KnownProtocols.Contains(requested) ? requested : LatestProtocol,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = AppInfo.Name, ["title"] = "pyvswitch", ["version"] = AppInfo.Version },
                        ["instructions"] = "Manage Python on this Windows machine: list and switch installed versions, download and install any python.org release, and install packages into one specific interpreter. The `python` argument accepts selectors such as 3.12, 3.12.4, 3.10-32, a python.exe path or a venv folder; omit it to target the active interpreter."
                    });

                case "ping":
                    return Result(id, new JsonObject());

                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = McpTools.Describe() });

                case "tools/call":
                    var name = parameters?["name"]?.GetValue<string>() ?? "";
                    var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
                    return Result(id, await CallAsync(engine, name, arguments, cancel));

                default:
                    return Error(id, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return Error(id, -32602, $"Invalid params: {ex.Message}");
        }
    }

    private static async Task<JsonObject> CallAsync(PyEngine engine, string name, JsonObject arguments, CancellationToken cancel)
    {
        try
        {
            var data = await McpTools.InvokeAsync(engine, name, arguments, cancel);
            return ToolResult(data.ToJsonString(JsonShapes.Indented), isError: false);
        }
        catch (PyvsException ex)
        {
            return ToolResult($"{ex.Code}: {ex.Message}", isError: true);
        }
        catch (OperationCanceledException)
        {
            return ToolResult("cancelled", isError: true);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"MCP tool {name} failed.");
            return ToolResult($"internal_error: {ex.Message}", isError: true);
        }
    }

    private static JsonObject ToolResult(string text, bool isError)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError
        };
    }

    private static JsonObject Result(JsonNode id, JsonObject result)
    {
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    }

    private static JsonObject Error(JsonNode? id, int code, string message)
    {
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    }
}

internal static class McpTools
{
    private sealed record Tool(string Name, string Description, JsonObject Properties, string[] Required, bool ReadOnly, bool Destructive = false);

    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };
    private static JsonObject List(string description) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };

    private const string PythonHelp = "Interpreter selector: 3.12, 3.12.4, 3.10-32, a python.exe path or a venv folder. Omit for the active interpreter.";

    private static IEnumerable<Tool> Tools()
    {
        yield return new Tool("list_pythons", "List every Python interpreter installed on this machine and which one is active on PATH.",
            new JsonObject(), [], ReadOnly: true);
        yield return new Tool("get_active_python", "Show which interpreter the `python` command resolves to in new terminals.",
            new JsonObject(), [], ReadOnly: true);
        yield return new Tool("switch_python", "Make an installed Python version the default `python` by moving it to the front of PATH. Applies to new terminals; a PATH backup is saved.",
            new JsonObject { ["version"] = Text("Version to activate, e.g. 3.12 or 3.10-32."), ["scope"] = Text("PATH scope: user (default) or system (needs admin).") },
            ["version"], ReadOnly: false);
        yield return new Tool("list_available_pythons", "List Python versions that can be downloaded from python.org. Without `series` returns one row per minor series; with `series` returns every patch release in it.",
            new JsonObject { ["series"] = Text("Minor series such as 3.12 to list all of its releases."), ["include_prereleases"] = Bool("Include alpha/beta/rc series.") },
            [], ReadOnly: true);
        yield return new Tool("install_python", "Download an official python.org installer and install it silently for the current user. Accepts a series (3.13 = newest 3.13.x), an exact version (3.12.4) or `latest`.",
            new JsonObject { ["version"] = Text("Version to install, e.g. 3.13, 3.12.4 or latest."), ["architecture"] = Text("x64 (default), x86 or arm64."), ["activate"] = Bool("Also make it the default python.") },
            ["version"], ReadOnly: false);
        yield return new Tool("uninstall_python", "Uninstall a Python version that was installed with a python.org installer.",
            new JsonObject { ["version"] = Text("Installed version to remove, e.g. 3.9 or 3.10-32.") },
            ["version"], ReadOnly: false, Destructive: true);
        yield return new Tool("list_packages", "List the packages installed in one interpreter.",
            new JsonObject { ["python"] = Text(PythonHelp), ["outdated_only"] = Bool("Only packages with a newer release, including the latest version.") },
            [], ReadOnly: true);
        yield return new Tool("install_packages", "Install packages into one specific interpreter with pip.",
            new JsonObject { ["packages"] = List("Requirement specifiers, e.g. [\"requests\", \"numpy<2\"]."), ["python"] = Text(PythonHelp), ["upgrade"] = Bool("Upgrade if already installed.") },
            ["packages"], ReadOnly: false);
        yield return new Tool("uninstall_packages", "Uninstall packages from one specific interpreter.",
            new JsonObject { ["packages"] = List("Package names."), ["python"] = Text(PythonHelp) },
            ["packages"], ReadOnly: false, Destructive: true);
        yield return new Tool("upgrade_packages", "Upgrade named packages, or every outdated package, in one interpreter.",
            new JsonObject { ["packages"] = List("Package names. Omit when using `all`."), ["all"] = Bool("Upgrade every outdated package."), ["python"] = Text(PythonHelp) },
            [], ReadOnly: false);
        yield return new Tool("package_info", "Look a package up on PyPI: latest version, summary, required Python and recent versions.",
            new JsonObject { ["name"] = Text("Package name on PyPI.") },
            ["name"], ReadOnly: true);
        yield return new Tool("run_python", "Run a specific interpreter with arguments and capture its output, without changing PATH. Example args: [\"-c\", \"import sys; print(sys.version)\"] or [\"script.py\"].",
            new JsonObject { ["args"] = List("Arguments passed to python.exe."), ["python"] = Text(PythonHelp), ["cwd"] = Text("Working directory."), ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Kill the process after this many seconds (default 120)." } },
            ["args"], ReadOnly: false);
        yield return new Tool("create_venv", "Create a virtual environment with a chosen Python version.",
            new JsonObject { ["directory"] = Text("Folder for the environment, e.g. .venv."), ["python"] = Text(PythonHelp) },
            ["directory"], ReadOnly: false);
        yield return new Tool("doctor", "Diagnose why `python` may not resolve to the selected version (PATH order, Store aliases, dead entries, missing pip).",
            new JsonObject { ["fix"] = Bool("Remove dead and duplicate Python entries from the User PATH first.") },
            [], ReadOnly: false);
    }

    public static JsonArray Describe()
    {
        return new JsonArray(Tools().Select(tool => (JsonNode)new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = tool.Properties,
                ["required"] = new JsonArray(tool.Required.Select(name => (JsonNode)name).ToArray())
            },
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = tool.ReadOnly,
                ["destructiveHint"] = tool.Destructive,
                ["openWorldHint"] = tool.Name is "list_available_pythons" or "install_python" or "install_packages" or "upgrade_packages" or "package_info"
            }
        }).ToArray());
    }

    public static async Task<JsonNode> InvokeAsync(PyEngine engine, string name, JsonObject args, CancellationToken cancel)
    {
        string? Str(string key) => args[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
        bool Flag(string key) => args[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
        List<string> Strings(string key) => args[key] is JsonArray array
            ? array.Select(item => item?.GetValue<string>() ?? "").Where(item => item.Length > 0).ToList()
            : [];
        string Required(string key) => Str(key) ?? throw new PyvsException("usage", $"Missing required argument '{key}'.");

        var installs = await engine.DiscoverAsync(cancel);

        switch (name)
        {
            case "list_pythons":
                return JsonShapes.Installs(installs, engine.GetActiveState(installs));

            case "get_active_python":
                return JsonShapes.State(engine.GetActiveState(installs));

            case "switch_python":
            {
                var install = await engine.ResolveAsync(installs, Required("version"), cancel);
                PathScope? scope = Str("scope")?.ToLowerInvariant() switch
                {
                    null or "user" => null,
                    "system" or "machine" => PathScope.System,
                    var other => throw new PyvsException("usage", $"Unknown scope '{other}'.")
                };
                var result = engine.Switch(install, installs, scope);
                var data = JsonShapes.State(result.State);
                data["warning"] = result.Warning;
                data["note"] = "New terminals use this version. Already-open shells keep their previous PATH; use run_python to call it immediately.";
                return data;
            }

            case "list_available_pythons":
            {
                var catalog = await engine.Catalog.GetAsync(false, cancel);
                var series = Str("series");
                if (series is not null)
                {
                    var wanted = PythonVersion.TryParse(series, out var parsed) ? parsed.Series : series;
                    var match = catalog.Series.FirstOrDefault(item => item.Name == wanted)
                        ?? throw new PyvsException("not_found", $"python.org has no Python {series} releases.");
                    return new JsonArray(match.Releases.Select(release => (JsonNode)JsonShapes.Release(release, installs)).ToArray());
                }

                return new JsonArray(catalog.Series
                    .Where(item => item.LatestInstallable is not null)
                    .Where(item => Flag("include_prereleases") || item.Releases.Any(release => !release.IsPrerelease))
                    .Select(item => (JsonNode)JsonShapes.Series(item, installs)).ToArray());
            }

            case "install_python":
            {
                var outcome = await engine.InstallPythonAsync(Required("version"),
                    new InstallOptions { Architecture = Str("architecture") ?? "", Activate = Flag("activate") }, null, cancel);
                return new JsonObject
                {
                    ["installed"] = outcome.Install is null ? null : JsonShapes.Install(outcome.Install),
                    ["alreadyInstalled"] = outcome.AlreadyInstalled,
                    ["activated"] = Flag("activate")
                };
            }

            case "uninstall_python":
            {
                var install = await engine.ResolveAsync(installs, Required("version"), cancel);
                await engine.UninstallPythonAsync(install, cancel);
                return new JsonObject { ["uninstalled"] = JsonShapes.Install(install) };
            }

            case "list_packages":
            {
                var python = await engine.ResolveAsync(installs, Str("python"), cancel);
                return JsonShapes.Packages(python, await engine.Pip.ListAsync(python, Flag("outdated_only"), cancel));
            }

            case "install_packages":
            {
                var python = await engine.ResolveAsync(installs, Str("python"), cancel);
                return PipOutcome(python, await engine.Pip.InstallAsync(python, Strings("packages"), Flag("upgrade"), null, cancel));
            }

            case "uninstall_packages":
            {
                var python = await engine.ResolveAsync(installs, Str("python"), cancel);
                return PipOutcome(python, await engine.Pip.UninstallAsync(python, Strings("packages"), null, cancel));
            }

            case "upgrade_packages":
            {
                var python = await engine.ResolveAsync(installs, Str("python"), cancel);
                var names = Strings("packages");
                if (Flag("all"))
                {
                    names = (await engine.Pip.ListAsync(python, outdatedOnly: true, cancel)).Select(package => package.Name).ToList();
                    if (names.Count == 0)
                    {
                        return JsonShapes.Pip(python, new PipResult { ExitCode = 0, Output = "Everything is already up to date." });
                    }
                }

                return PipOutcome(python, await engine.Pip.InstallAsync(python, names, upgrade: true, null, cancel));
            }

            case "package_info":
            {
                var package = await engine.PyPi.GetAsync(Required("name"), cancel)
                    ?? throw new PyvsException("not_found", $"PyPI has no package named '{Str("name")}'.");
                return JsonShapes.PyPi(package);
            }

            case "run_python":
            {
                var python = await engine.ResolveAsync(installs, Str("python"), cancel);
                var timeout = args["timeout_seconds"] is JsonValue seconds && seconds.TryGetValue<int>(out var value) && value > 0 ? value : 120;
                var result = await ProcessRunner.RunAsync(python.ExecutablePath, Strings("args"), TimeSpan.FromSeconds(timeout), workingDirectory: Str("cwd"), cancellationToken: cancel);
                return new JsonObject
                {
                    ["python"] = JsonShapes.Install(python),
                    ["exitCode"] = result.ExitCode,
                    ["timedOut"] = result.TimedOut,
                    ["stdout"] = result.StandardOutput,
                    ["stderr"] = result.StandardError
                };
            }

            case "create_venv":
            {
                var python = await engine.ResolveAsync(installs, Str("python"), cancel);
                var venv = await engine.CreateVenvAsync(python, Required("directory"), null, cancel);
                return new JsonObject { ["venv"] = JsonShapes.Install(venv), ["basePython"] = JsonShapes.Install(python) };
            }

            case "doctor":
            {
                var repaired = Flag("fix") && engine.FixPath() is not null;
                if (repaired)
                {
                    installs = await engine.DiscoverAsync(cancel);
                }

                var findings = await engine.RunDoctorAsync(installs, cancel);
                return new JsonObject
                {
                    ["healthy"] = findings.All(finding => finding.Severity is DoctorSeverity.Ok or DoctorSeverity.Info),
                    ["fixed"] = repaired,
                    ["findings"] = new JsonArray(findings.Select(finding => (JsonNode)JsonShapes.Finding(finding)).ToArray())
                };
            }

            default:
                throw new PyvsException("not_found", $"Unknown tool '{name}'.");
        }
    }

    private static JsonNode PipOutcome(PythonInstall python, PipResult result)
    {
        if (!result.Success)
        {
            throw new PyvsException("pip_failed", $"pip exited with code {result.ExitCode}.\n{result.Output}");
        }

        return JsonShapes.Pip(python, result);
    }
}
