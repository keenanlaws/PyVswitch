using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyVSwitch.Models;
using PyVSwitch.Services;

namespace PyVSwitch;

/// <summary>The JSON shapes shared by `--json` CLI output and MCP tool results.</summary>
public static class JsonShapes
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        TypeInfoResolver = CoreJsonContext.Default,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        TypeInfoResolver = CoreJsonContext.Default,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static JsonObject Install(PythonInstall install, ActiveState? state = null)
    {
        var activePath = state?.EffectivePath ?? state?.Selected?.ExecutablePath;
        return new JsonObject
        {
            ["version"] = install.Version,
            ["architecture"] = install.Architecture,
            ["tag"] = install.Tag,
            ["active"] = activePath is not null && activePath.Equals(install.ExecutablePath, StringComparison.OrdinalIgnoreCase),
            ["path"] = install.ExecutablePath,
            ["directory"] = install.InstallDirectory,
            ["scripts"] = install.ScriptsDirectory,
            ["source"] = install.Source,
            ["virtualEnvironment"] = install.IsVirtualEnvironment
        };
    }

    public static JsonObject State(ActiveState state)
    {
        var active = state.Effective ?? (state.IsShadowed ? null : state.Selected);
        return new JsonObject
        {
            ["scope"] = state.Scope.ToString().ToLowerInvariant(),
            ["active"] = active is null ? null : Install(active, state),
            ["effectivePath"] = state.EffectivePath,
            ["selected"] = state.Selected is null ? null : Install(state.Selected, state),
            ["shadowedBySystemPath"] = state.IsShadowed
        };
    }

    public static JsonObject Installs(IReadOnlyList<PythonInstall> installs, ActiveState state)
    {
        return new JsonObject
        {
            ["scope"] = state.Scope.ToString().ToLowerInvariant(),
            ["activePath"] = state.EffectivePath,
            ["installs"] = new JsonArray(installs.Select(install => (JsonNode)Install(install, state)).ToArray())
        };
    }

    public static JsonObject Release(PythonRelease release, IReadOnlyList<PythonInstall>? installs = null)
    {
        return new JsonObject
        {
            ["version"] = release.Version,
            ["series"] = release.Series,
            ["prerelease"] = release.IsPrerelease,
            ["released"] = release.ReleaseDate,
            ["installable"] = release.HasInstaller,
            ["architectures"] = new JsonArray(release.Installers.Select(file => (JsonNode)file.Architecture).ToArray()),
            ["installed"] = installs?.Any(install => install.ParsedVersion.CompareTo(release.ParsedVersion) == 0) ?? false
        };
    }

    public static JsonObject Series(PythonSeries series, IReadOnlyList<PythonInstall> installs)
    {
        var latest = series.LatestInstallable;
        return new JsonObject
        {
            ["series"] = series.Name,
            ["status"] = series.Status,
            ["endOfLife"] = series.EndOfLife,
            ["latest"] = series.Latest?.Version,
            ["latestInstallable"] = latest?.Version,
            ["architectures"] = new JsonArray((latest?.Installers ?? []).Select(file => (JsonNode)file.Architecture).ToArray()),
            ["releaseCount"] = series.Releases.Count,
            ["installed"] = new JsonArray(installs.Where(install => install.Series == series.Name).Select(install => (JsonNode)$"{install.Version} {install.Architecture}").ToArray())
        };
    }

    public static JsonObject Packages(PythonInstall python, IReadOnlyList<InstalledPackage> packages)
    {
        return new JsonObject
        {
            ["python"] = Install(python),
            ["count"] = packages.Count,
            ["packages"] = new JsonArray(packages.Select(package =>
            {
                var item = new JsonObject { ["name"] = package.Name, ["version"] = package.Version };
                if (!string.IsNullOrEmpty(package.LatestVersion))
                {
                    item["latest"] = package.LatestVersion;
                }

                return (JsonNode)item;
            }).ToArray())
        };
    }

    public static JsonObject Pip(PythonInstall python, PipResult result)
    {
        return new JsonObject
        {
            ["python"] = Install(python),
            ["success"] = result.Success,
            ["exitCode"] = result.ExitCode,
            ["output"] = result.Output
        };
    }

    public static JsonObject PyPi(PyPiPackage package)
    {
        return new JsonObject
        {
            ["name"] = package.Name,
            ["latest"] = package.LatestVersion,
            ["summary"] = package.Summary,
            ["homePage"] = package.HomePage,
            ["requiresPython"] = package.RequiresPython,
            ["versions"] = new JsonArray(package.Versions.Select(version => (JsonNode)version).ToArray())
        };
    }

    public static JsonObject Finding(DoctorFinding finding)
    {
        return new JsonObject
        {
            ["id"] = finding.Id,
            ["severity"] = finding.Severity.ToString().ToLowerInvariant(),
            ["title"] = finding.Title,
            ["detail"] = finding.Detail,
            ["fix"] = finding.Fix,
            ["autoFixable"] = finding.AutoFixable
        };
    }

    public static JsonObject AiTarget(AiTargetStatus status)
    {
        return new JsonObject
        {
            ["id"] = status.Id,
            ["name"] = status.Name,
            ["detected"] = status.Detected,
            ["instructionFile"] = status.InstructionFile,
            ["instructionsRegistered"] = status.InstructionsRegistered,
            ["mcpConfigFile"] = status.McpConfigFile,
            ["mcpRegistered"] = status.McpRegistered,
            ["changes"] = new JsonArray(status.Changes.Select(change => (JsonNode)change).ToArray())
        };
    }

    public static JsonObject Backup(PathBackup backup)
    {
        return new JsonObject
        {
            ["createdAt"] = backup.CreatedAt.ToString("o"),
            ["scope"] = backup.Scope.ToString().ToLowerInvariant(),
            ["description"] = backup.DisplayName
        };
    }
}
