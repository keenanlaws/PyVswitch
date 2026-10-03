using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PyVSwitch.Services;

public sealed class AiTargetStatus
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Detected { get; init; }
    public string? InstructionFile { get; init; }
    public bool InstructionsRegistered { get; init; }
    public string? McpConfigFile { get; init; }
    public bool McpRegistered { get; init; }
    public List<string> Changes { get; init; } = [];
}

/// <summary>
/// Tells AI coding assistants that pyvswitch is installed: a short note in each tool's global
/// instruction file plus an MCP server entry. Every edit is confined to a marked block or a single
/// "pyvswitch" key, and the original file is backed up first.
/// </summary>
public sealed class AiIntegrationService
{
    private const string ServerName = "pyvswitch";
    private const string TomlBegin = "# pyvswitch:begin";
    private const string TomlEnd = "# pyvswitch:end";

    private readonly string _home;
    private readonly string _appData;
    private readonly string _cliPath;

    public AiIntegrationService(string? cliPath = null, string? home = null, string? appData = null)
    {
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _appData = appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cliPath = cliPath ?? ResolveCliPath();
    }

    public string CliPath => _cliPath;

    /// <summary>A copy of the full guide that any tool can be pointed at.</summary>
    public string GuidePath => Path.Combine(AppInfo.DataDirectory, "AI_GUIDE.md");

    private sealed record Target(string Id, string Name, string DetectPath, string? InstructionFile, string? McpFile, string McpKind);

    private IEnumerable<Target> Targets()
    {
        yield return new Target("claude-code", "Claude Code", Path.Combine(_home, ".claude"),
            Path.Combine(_home, ".claude", "CLAUDE.md"), Path.Combine(_home, ".claude.json"), "claude-cli");
        yield return new Target("claude-desktop", "Claude Desktop", Path.Combine(_appData, "Claude"),
            null, Path.Combine(_appData, "Claude", "claude_desktop_config.json"), "json:mcpServers");
        yield return new Target("codex", "OpenAI Codex", Path.Combine(_home, ".codex"),
            Path.Combine(_home, ".codex", "AGENTS.md"), Path.Combine(_home, ".codex", "config.toml"), "toml");
        yield return new Target("cursor", "Cursor", Path.Combine(_home, ".cursor"),
            null, Path.Combine(_home, ".cursor", "mcp.json"), "json:mcpServers");
        yield return new Target("gemini", "Gemini CLI", Path.Combine(_home, ".gemini"),
            Path.Combine(_home, ".gemini", "GEMINI.md"), Path.Combine(_home, ".gemini", "settings.json"), "json:mcpServers");
        yield return new Target("vscode", "VS Code / GitHub Copilot", Path.Combine(_appData, "Code", "User"),
            Path.Combine(_appData, "Code", "User", "prompts", "pyvswitch.instructions.md"), Path.Combine(_appData, "Code", "User", "mcp.json"), "json:servers");
        yield return new Target("windsurf", "Windsurf", Path.Combine(_home, ".codeium", "windsurf"),
            Path.Combine(_home, ".codeium", "windsurf", "memories", "global_rules.md"), Path.Combine(_home, ".codeium", "windsurf", "mcp_config.json"), "json:mcpServers");
    }

    public List<AiTargetStatus> GetStatus()
    {
        return Targets().Select(target => Describe(target, [])).ToList();
    }

    public async Task<List<AiTargetStatus>> SetupAsync(IReadOnlyCollection<string>? onlyIds = null, CancellationToken cancellationToken = default)
    {
        WriteGuideFile();
        var results = new List<AiTargetStatus>();
        foreach (var target in Select(onlyIds))
        {
            var changes = new List<string>();
            if (Directory.Exists(target.DetectPath))
            {
                if (target.InstructionFile is not null && UpsertInstructionBlock(target))
                {
                    changes.Add($"Updated {target.InstructionFile}");
                }

                var registered = target.McpKind == "claude-cli"
                    ? await RegisterWithClaudeCliAsync(target, cancellationToken).ConfigureAwait(false)
                    : target.McpFile is not null && RegisterMcp(target);
                if (registered)
                {
                    changes.Add($"Registered MCP server in {target.McpFile}");
                }
            }

            results.Add(Describe(target, changes));
        }

        return results;
    }

    public async Task<List<AiTargetStatus>> RemoveAsync(IReadOnlyCollection<string>? onlyIds = null, CancellationToken cancellationToken = default)
    {
        var results = new List<AiTargetStatus>();
        foreach (var target in Select(onlyIds))
        {
            var changes = new List<string>();
            if (target.InstructionFile is not null && RemoveInstructionBlock(target))
            {
                changes.Add($"Removed note from {target.InstructionFile}");
            }

            var unregistered = target.McpKind == "claude-cli"
                ? await UnregisterWithClaudeCliAsync(target, cancellationToken).ConfigureAwait(false)
                : target.McpFile is not null && UnregisterMcp(target);
            if (unregistered)
            {
                changes.Add($"Removed MCP server from {target.McpFile}");
            }

            results.Add(Describe(target, changes));
        }

        return results;
    }

    private IEnumerable<Target> Select(IReadOnlyCollection<string>? onlyIds)
    {
        var targets = Targets().ToList();
        if (onlyIds is null || onlyIds.Count == 0)
        {
            return targets;
        }

        var unknown = onlyIds.FirstOrDefault(id => targets.All(target => !target.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
        if (unknown is not null)
        {
            throw new PyvsException("usage", $"Unknown AI tool '{unknown}'. Known: {string.Join(", ", targets.Select(target => target.Id))}.");
        }

        return targets.Where(target => onlyIds.Contains(target.Id, StringComparer.OrdinalIgnoreCase));
    }

    private AiTargetStatus Describe(Target target, List<string> changes)
    {
        return new AiTargetStatus
        {
            Id = target.Id,
            Name = target.Name,
            Detected = Directory.Exists(target.DetectPath),
            InstructionFile = target.InstructionFile,
            InstructionsRegistered = target.InstructionFile is not null && HasInstructionBlock(target.InstructionFile),
            McpConfigFile = target.McpFile,
            McpRegistered = target.McpFile is not null && IsMcpRegistered(target),
            Changes = changes
        };
    }

    private void WriteGuideFile()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDirectory);
            File.WriteAllText(GuidePath, AiGuide.FullGuide);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Could not write the AI guide file.");
        }
    }

    // Instruction files ----------------------------------------------------------------------

    private static bool HasInstructionBlock(string file)
    {
        try
        {
            return File.Exists(file) && File.ReadAllText(file).Contains(AiGuide.BeginMarker, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool UpsertInstructionBlock(Target target)
    {
        var file = target.InstructionFile!;
        var existing = File.Exists(file) ? File.ReadAllText(file) : "";
        var block = AiGuide.InstructionBlock;
        if (target.Id == "vscode" && existing.Length == 0)
        {
            // VS Code applies user instruction files according to this front matter.
            existing = "---\napplyTo: \"**\"\ndescription: pyvswitch Python version manager\n---\n";
        }

        var updated = UpsertBlock(existing, block, AiGuide.BeginMarker, AiGuide.EndMarker);
        if (updated == existing)
        {
            return false;
        }

        WriteWithBackup(file, updated);
        return true;
    }

    private static bool RemoveInstructionBlock(Target target)
    {
        var file = target.InstructionFile!;
        if (!File.Exists(file))
        {
            return false;
        }

        var existing = File.ReadAllText(file);
        var updated = RemoveBlock(existing, AiGuide.BeginMarker, AiGuide.EndMarker);
        if (updated == existing)
        {
            return false;
        }

        if (target.Id == "vscode")
        {
            // This file exists only for pyvswitch.
            File.Delete(file);
            return true;
        }

        WriteWithBackup(file, updated);
        return true;
    }

    internal static string UpsertBlock(string existing, string block, string beginMarker, string endMarker)
    {
        var begin = existing.IndexOf(beginMarker, StringComparison.Ordinal);
        var end = existing.IndexOf(endMarker, StringComparison.Ordinal);
        if (begin >= 0 && end > begin)
        {
            return existing[..begin] + block + existing[(end + endMarker.Length)..];
        }

        if (existing.Length == 0)
        {
            return block + "\n";
        }

        var separator = existing.EndsWith("\n\n", StringComparison.Ordinal) ? "" : existing.EndsWith('\n') ? "\n" : "\n\n";
        return existing + separator + block + "\n";
    }

    internal static string RemoveBlock(string existing, string beginMarker, string endMarker)
    {
        var begin = existing.IndexOf(beginMarker, StringComparison.Ordinal);
        var end = existing.IndexOf(endMarker, StringComparison.Ordinal);
        if (begin < 0 || end <= begin)
        {
            return existing;
        }

        var before = existing[..begin].TrimEnd('\r', '\n');
        var after = existing[(end + endMarker.Length)..].TrimStart('\r', '\n');
        if (before.Length == 0)
        {
            return after;
        }

        return after.Length == 0 ? before + "\n" : before + "\n\n" + after;
    }

    // MCP registration -----------------------------------------------------------------------

    private bool IsMcpRegistered(Target target)
    {
        try
        {
            if (!File.Exists(target.McpFile))
            {
                return false;
            }

            if (target.McpKind == "toml")
            {
                return File.ReadAllText(target.McpFile!).Contains($"[mcp_servers.{ServerName}]", StringComparison.Ordinal);
            }

            return ReadRegisteredCommand(target) is not null;
        }
        catch
        {
            return false;
        }
    }

    private bool RegisterMcp(Target target)
    {
        var file = target.McpFile!;
        if (target.McpKind == "toml")
        {
            var existing = File.Exists(file) ? File.ReadAllText(file) : "";
            // TOML literal string: backslashes need no escaping.
            var block = $"{TomlBegin}\n[mcp_servers.{ServerName}]\ncommand = '{_cliPath}'\nargs = [\"mcp\"]\n{TomlEnd}";
            if (!existing.Contains(TomlBegin, StringComparison.Ordinal) && existing.Contains($"[mcp_servers.{ServerName}]", StringComparison.Ordinal))
            {
                // The user already defined this server by hand; leave their definition alone.
                return false;
            }

            var updated = UpsertBlock(existing, block, TomlBegin, TomlEnd);
            if (updated == existing)
            {
                return false;
            }

            WriteWithBackup(file, updated);
            return true;
        }

        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        var root = text.Trim().Length == 0 ? new JsonObject() : ParseJsonObject(text);
        if (root is null)
        {
            AppLog.Info($"Skipped MCP registration: {file} is not a JSON object.");
            return false;
        }

        var sectionName = SectionName(target);
        if (root[sectionName] is not JsonObject section)
        {
            section = new JsonObject();
            root[sectionName] = section;
        }

        var entry = new JsonObject { ["command"] = _cliPath, ["args"] = new JsonArray("mcp") };
        if (target.Id == "vscode")
        {
            entry.Insert(0, "type", "stdio");
        }

        if (section[ServerName] is JsonObject current && JsonNode.DeepEquals(current, entry))
        {
            return false;
        }

        section[ServerName] = entry;
        WriteWithBackup(file, root.ToJsonString(IndentedJson) + "\n");
        return true;
    }

    private static bool UnregisterMcp(Target target)
    {
        var file = target.McpFile!;
        if (!File.Exists(file))
        {
            return false;
        }

        var existing = File.ReadAllText(file);
        if (target.McpKind == "toml")
        {
            var updated = RemoveBlock(existing, TomlBegin, TomlEnd);
            if (updated == existing)
            {
                return false;
            }

            WriteWithBackup(file, updated);
            return true;
        }

        var root = ParseJsonObject(existing);
        if (root?[SectionName(target)] is not JsonObject section || !section.Remove(ServerName))
        {
            return false;
        }

        WriteWithBackup(file, root.ToJsonString(IndentedJson) + "\n");
        return true;
    }

    private static string SectionName(Target target) => target.McpKind.StartsWith("json:", StringComparison.Ordinal) ? target.McpKind["json:".Length..] : "mcpServers";

    private static string? ReadRegisteredCommand(Target target)
    {
        var root = ParseJsonObject(File.ReadAllText(target.McpFile!));
        return root?[SectionName(target)] is JsonObject section && section[ServerName] is JsonObject entry
            ? entry["command"]?.GetValue<string>() ?? ""
            : null;
    }

    // Claude Code keeps live state in ~/.claude.json, so it is changed through the claude CLI
    // (which coordinates with running sessions) instead of being rewritten here.

    private async Task<bool> RegisterWithClaudeCliAsync(Target target, CancellationToken cancellationToken)
    {
        string? current = null;
        try
        {
            current = File.Exists(target.McpFile) ? ReadRegisteredCommand(target) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            AppLog.Error(ex, "Could not read Claude Code's MCP configuration.");
        }

        if (string.Equals(current, _cliPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (current is not null)
        {
            await RunClaudeAsync(["mcp", "remove", "--scope", "user", ServerName], cancellationToken).ConfigureAwait(false);
        }

        var result = await RunClaudeAsync(["mcp", "add", "--scope", "user", ServerName, "--", _cliPath, "mcp"], cancellationToken).ConfigureAwait(false);
        return result?.Success == true;
    }

    private async Task<bool> UnregisterWithClaudeCliAsync(Target target, CancellationToken cancellationToken)
    {
        if (!IsMcpRegistered(target))
        {
            return false;
        }

        var result = await RunClaudeAsync(["mcp", "remove", "--scope", "user", ServerName], cancellationToken).ConfigureAwait(false);
        return result?.Success == true;
    }

    private static async Task<ProcessResult?> RunClaudeAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var claude = FindOnPath("claude.exe") ?? FindOnPath("claude.cmd");
        if (claude is null)
        {
            return null;
        }

        var isScript = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var result = isScript
            ? await ProcessRunner.RunAsync("cmd.exe", ["/c", claude, .. arguments], TimeSpan.FromSeconds(45), cancellationToken: cancellationToken).ConfigureAwait(false)
            : await ProcessRunner.RunAsync(claude, arguments, TimeSpan.FromSeconds(45), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            AppLog.Info($"claude {string.Join(' ', arguments)} failed: {result.CombinedOutput}");
        }

        return result;
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        TypeInfoResolver = CoreJsonContext.Default,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static JsonObject? ParseJsonObject(string text)
    {
        try
        {
            return JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteWithBackup(string file, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (File.Exists(file))
        {
            File.Copy(file, file + ".pyvswitch.bak", true);
        }

        File.WriteAllText(file, content, new UTF8Encoding(false));
    }

    private static string ResolveCliPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, AppInfo.CliExeName);
        if (File.Exists(beside))
        {
            return beside;
        }

        var process = Environment.ProcessPath;
        return process is not null && Path.GetFileName(process).Equals(AppInfo.CliExeName, StringComparison.OrdinalIgnoreCase)
            ? process
            : AppInfo.Name;
    }
}
