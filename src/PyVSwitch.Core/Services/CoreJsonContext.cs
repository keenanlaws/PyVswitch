using System.Text.Json.Serialization;
using PyVSwitch.Models;

namespace PyVSwitch.Services;

// Source-generated so the CLI can be trimmed and AOT compiled.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(PythonCatalog))]
[JsonSerializable(typeof(List<ApiRelease>))]
[JsonSerializable(typeof(List<ApiReleaseFile>))]
[JsonSerializable(typeof(Dictionary<string, ApiReleaseCycle>))]
[JsonSerializable(typeof(List<InstalledPackage>))]
public sealed partial class CoreJsonContext : JsonSerializerContext;
