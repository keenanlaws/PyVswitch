using System.Net;
using System.Text.Json;
using PyVSwitch.Models;

namespace PyVSwitch.Services;

/// <summary>Looks packages up on PyPI so a name can be checked before it is installed.</summary>
public sealed class PyPiService
{
    /// <summary>Well-known packages offered as suggestions; PyPI has no public search API.</summary>
    public static readonly string[] PopularPackages =
    [
        "requests", "numpy", "pandas", "matplotlib", "scipy", "scikit-learn", "pillow", "pytest", "black", "ruff",
        "mypy", "flask", "django", "fastapi", "uvicorn", "pydantic", "sqlalchemy", "httpx", "aiohttp", "beautifulsoup4",
        "lxml", "selenium", "playwright", "jupyter", "notebook", "ipython", "rich", "click", "typer", "tqdm",
        "pyyaml", "python-dotenv", "openpyxl", "pyserial", "pyusb", "cryptography", "paramiko", "psutil", "pywin32", "pyinstaller",
        "setuptools", "wheel", "virtualenv", "poetry", "pipx", "tox", "coverage", "sphinx", "boto3", "anthropic",
        "openai", "torch", "tensorflow", "transformers", "opencv-python", "pygame", "pyqt6", "pyside6", "customtkinter", "capstone",
        "pefile", "construct", "bitstring", "python-can", "cantools", "udsoncan", "intelhex", "crcmod", "scapy", "frida-tools"
    ];

    public async Task<PyPiPackage?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        try
        {
            using var response = await PythonCatalogService.Http
                .GetAsync($"https://pypi.org/pypi/{Uri.EscapeDataString(name)}/json", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Parse(document.RootElement);
        }
        catch (HttpRequestException ex)
        {
            throw new PyvsException("offline", $"Could not reach PyPI: {ex.Message}", ex);
        }
    }

    internal static PyPiPackage Parse(JsonElement root)
    {
        var info = root.GetProperty("info");
        var package = new PyPiPackage
        {
            Name = ReadString(info, "name"),
            LatestVersion = ReadString(info, "version"),
            Summary = ReadString(info, "summary"),
            HomePage = ReadString(info, "home_page"),
            RequiresPython = ReadString(info, "requires_python"),
            License = ReadString(info, "license")
        };

        if (package.License.Length > 60)
        {
            package.License = "";
        }

        if (string.IsNullOrEmpty(package.HomePage) && info.TryGetProperty("project_urls", out var urls) && urls.ValueKind == JsonValueKind.Object)
        {
            foreach (var url in urls.EnumerateObject())
            {
                package.HomePage = url.Value.GetString() ?? "";
                if (url.Name.Contains("home", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }

        if (root.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Object)
        {
            var dated = new List<(string Version, string Uploaded)>();
            foreach (var release in releases.EnumerateObject())
            {
                string? uploaded = null;
                foreach (var file in release.Value.EnumerateArray())
                {
                    if (file.TryGetProperty("yanked", out var yanked) && yanked.ValueKind == JsonValueKind.True)
                    {
                        continue;
                    }

                    var time = ReadString(file, "upload_time_iso_8601");
                    if (uploaded is null || string.CompareOrdinal(time, uploaded) < 0)
                    {
                        uploaded = time;
                    }
                }

                if (uploaded is not null)
                {
                    dated.Add((release.Name, uploaded));
                }
            }

            package.Versions = dated
                .OrderByDescending(item => item.Uploaded, StringComparer.Ordinal)
                .Select(item => item.Version)
                .Take(80)
                .ToList();
        }

        return package;
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }
}
