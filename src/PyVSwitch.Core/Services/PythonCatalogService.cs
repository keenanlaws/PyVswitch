using System.Net.Http.Headers;
using System.Text.Json;
using PyVSwitch.Models;

namespace PyVSwitch.Services;

/// <summary>Builds the list of downloadable Python versions from python.org's own release API.</summary>
public sealed class PythonCatalogService
{
    private const string ReleasesUrl = "https://www.python.org/api/v2/downloads/release/?is_published=true";
    private const string WindowsFilesUrl = "https://www.python.org/api/v2/downloads/release_file/?os=1";
    private const string ReleaseCycleUrl = "https://peps.python.org/api/release-cycle.json";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

    internal static readonly HttpClient Http = CreateHttpClient();

    private readonly string _cachePath;

    public PythonCatalogService(string? cachePath = null)
    {
        _cachePath = cachePath ?? Path.Combine(AppInfo.CacheDirectory, "catalog.json");
    }

    public async Task<PythonCatalog> GetAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        var cached = LoadCache();
        if (!refresh && cached is not null && DateTimeOffset.Now - cached.FetchedAt < CacheLifetime)
        {
            return cached;
        }

        try
        {
            var catalog = await FetchAsync(cancellationToken).ConfigureAwait(false);
            SaveCache(catalog);
            return catalog;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            AppLog.Error(ex, "Fetching the python.org catalog failed.");
            if (cached is not null)
            {
                cached.IsStale = true;
                return cached;
            }

            throw new PyvsException("offline", "Could not reach python.org to list available versions. Check your internet connection.", ex);
        }
    }

    private static async Task<PythonCatalog> FetchAsync(CancellationToken cancellationToken)
    {
        var releasesTask = GetJsonAsync(ReleasesUrl, CoreJsonContext.Default.ListApiRelease, cancellationToken);
        var filesTask = GetJsonAsync(WindowsFilesUrl, CoreJsonContext.Default.ListApiReleaseFile, cancellationToken);
        var cyclesTask = TryGetCyclesAsync(cancellationToken);

        return Build(
            await releasesTask.ConfigureAwait(false) ?? [],
            await filesTask.ConfigureAwait(false) ?? [],
            await cyclesTask.ConfigureAwait(false));
    }

    private static async Task<Dictionary<string, ApiReleaseCycle>?> TryGetCyclesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetJsonAsync(ReleaseCycleUrl, CoreJsonContext.Default.DictionaryStringApiReleaseCycle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Lifecycle labels are a nicety; the catalog still works without them.
            return null;
        }
    }

    private static async Task<T?> GetJsonAsync<T>(string url, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    internal static PythonCatalog Build(List<ApiRelease> apiReleases, List<ApiReleaseFile> apiFiles, Dictionary<string, ApiReleaseCycle>? cycles)
    {
        var filesByRelease = apiFiles.ToLookup(file => file.Release, StringComparer.OrdinalIgnoreCase);
        var releases = new Dictionary<string, PythonRelease>(StringComparer.OrdinalIgnoreCase);

        foreach (var api in apiReleases)
        {
            const string prefix = "Python ";
            if (!api.IsPublished || !api.Name.StartsWith(prefix, StringComparison.Ordinal) ||
                !PythonVersion.TryParse(api.Name[prefix.Length..], out var version))
            {
                continue;
            }

            var release = new PythonRelease
            {
                Version = version.ToString(),
                Series = version.Series,
                IsPrerelease = api.PreRelease || version.IsPrerelease,
                ReleaseDate = api.ReleaseDate is { Length: >= 10 } date ? date[..10] : "",
                ReleaseNotesUrl = api.ReleaseNotesUrl ?? "",
                Installers = filesByRelease[api.ResourceUri]
                    .Select(ToInstaller)
                    .Where(file => file is not null)
                    .Select(file => file!)
                    .GroupBy(file => file.Architecture)
                    .Select(group => group.OrderBy(file => file.Kind == "exe" ? 0 : 1).First())
                    .OrderBy(file => ArchitectureOrder(file.Architecture))
                    .ToList()
            };

            // python.org occasionally lists the same version twice; keep the entry that has installers.
            if (!releases.TryGetValue(release.Version, out var existing) || existing.Installers.Count < release.Installers.Count)
            {
                releases[release.Version] = release;
            }
        }

        var series = releases.Values
            .GroupBy(release => release.Series)
            .Select(group =>
            {
                ApiReleaseCycle? cycle = null;
                cycles?.TryGetValue(group.Key, out cycle);
                return new PythonSeries
                {
                    Name = group.Key,
                    Status = cycle?.Status ?? "end-of-life",
                    EndOfLife = cycle?.EndOfLife ?? "",
                    FirstRelease = cycle?.FirstRelease ?? "",
                    Releases = group.OrderByDescending(release => release.ParsedVersion).ToList()
                };
            })
            .OrderByDescending(item => PythonVersion.ParseOrDefault(item.Name))
            .ToList();

        return new PythonCatalog { Schema = PythonCatalog.CurrentSchema, FetchedAt = DateTimeOffset.Now, Series = series };
    }

    internal static PythonInstallerFile? ToInstaller(ApiReleaseFile file)
    {
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var name = Path.GetFileName(uri.AbsolutePath).ToLowerInvariant();
        var kind = name.EndsWith(".exe", StringComparison.Ordinal) ? "exe" : name.EndsWith(".msi", StringComparison.Ordinal) ? "msi" : null;
        if (kind is null || !name.StartsWith("python-", StringComparison.Ordinal))
        {
            return null;
        }

        // Web installers, embeddable zips, debug symbols, Itanium builds and the install manager are not full offline installers.
        string[] excluded = ["webinstall", "embed", "pdb", "ia64", "manager", "debug", "_d."];
        if (excluded.Any(name.Contains))
        {
            return null;
        }

        // Only the 3.5+ executable installers have an unattended mode; older .exe files are interactive wizards.
        // Legacy releases are installed from their MSI packages instead.
        if (kind == "exe" && (!PythonVersion.TryParse(name["python-".Length..], out var version) || version.CompareTo(new PythonVersion(3, 5, 0, "", 0)) < 0))
        {
            return null;
        }

        return new PythonInstallerFile
        {
            Architecture = name.Contains("arm64") ? "arm64" : name.Contains("amd64") ? "x64" : "x86",
            Kind = kind,
            Url = file.Url,
            Size = file.Filesize,
            Sha256 = file.Sha256Sum ?? "",
            Md5 = file.Md5Sum ?? ""
        };
    }

    /// <summary>Turns a user spec (latest, 3, 3.13, 3.13.5, 3.15.0rc3) into a concrete installable release.</summary>
    public static PythonRelease ResolveRelease(PythonCatalog catalog, string spec, string architecture, bool allowPrerelease)
    {
        spec = spec.Trim();
        bool Installable(PythonRelease release) =>
            release.FindInstaller(architecture) is not null && (allowPrerelease || !release.IsPrerelease);

        if (spec.Equals("latest", StringComparison.OrdinalIgnoreCase) || spec is "3" or "2")
        {
            var major = spec == "2" ? 2 : 3;
            return catalog.AllReleases()
                       .Where(release => release.ParsedVersion.Major == major)
                       .OrderByDescending(release => release.ParsedVersion)
                       .FirstOrDefault(Installable)
                   ?? throw new PyvsException("not_found", $"No installable Python {major} release was found for {architecture}.");
        }

        if (!PythonVersion.TryParse(spec, out var wanted))
        {
            throw new PyvsException("bad_version", $"'{spec}' is not a Python version. Use forms like 3.13, 3.13.5 or latest.");
        }

        var series = catalog.Series.FirstOrDefault(item => item.Name == wanted.Series)
            ?? throw new PyvsException("not_found", $"python.org has no Python {wanted.Series} releases.");

        var isExact = spec.Count(c => c == '.') >= 2;
        if (!isExact)
        {
            return series.Releases.FirstOrDefault(Installable)
                   ?? series.Releases.FirstOrDefault(release => release.FindInstaller(architecture) is not null)
                   ?? throw new PyvsException("no_installer", $"Python {series.Name} has no Windows {architecture} installer on python.org.");
        }

        var exact = series.Releases.FirstOrDefault(release => release.ParsedVersion.CompareTo(wanted) == 0)
            ?? throw new PyvsException("not_found", $"Python {wanted} does not exist on python.org.");

        if (exact.FindInstaller(architecture) is null)
        {
            var fallback = series.Releases.FirstOrDefault(Installable);
            var hint = fallback is null ? "" : $" The newest {series.Name} release with one is {fallback.Version}.";
            var reason = exact.HasInstaller
                ? $"Python {exact.Version} has no {architecture} installer (available: {string.Join(", ", exact.Installers.Select(file => file.Architecture))})."
                : $"Python {exact.Version} is a source-only release, so python.org published no Windows installer for it.";
            throw new PyvsException("no_installer", reason + hint);
        }

        return exact;
    }

    private PythonCatalog? LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return null;
            }

            using var stream = File.OpenRead(_cachePath);
            var catalog = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.PythonCatalog);
            return catalog?.Schema == PythonCatalog.CurrentSchema ? catalog : null;
        }
        catch
        {
            return null;
        }
    }

    private void SaveCache(PythonCatalog catalog)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(catalog, CoreJsonContext.Default.PythonCatalog));
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Could not write the catalog cache.");
        }
    }

    private static int ArchitectureOrder(string architecture) => architecture switch
    {
        "x64" => 0,
        "x86" => 1,
        _ => 2
    };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"pyvswitch/{AppInfo.Version}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
