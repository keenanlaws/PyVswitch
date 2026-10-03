using System.Text.RegularExpressions;

namespace PyVSwitch.Models;

/// <summary>A CPython version such as 3.12.4, 3.15.0rc1 or the partial 3.12.</summary>
public readonly partial record struct PythonVersion(int Major, int Minor, int Patch, string Pre, int PreNumber)
    : IComparable<PythonVersion>
{
    public bool IsPrerelease => Pre.Length > 0;
    public string Series => $"{Major}.{Minor}";

    public static bool TryParse(string? text, out PythonVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = VersionPattern().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        var pre = match.Groups["pre"].Value.ToLowerInvariant();
        version = new PythonVersion(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0,
            pre == "c" ? "rc" : pre,
            match.Groups["prenum"].Success ? int.Parse(match.Groups["prenum"].Value) : 0);
        return true;
    }

    public static PythonVersion ParseOrDefault(string? text)
    {
        return TryParse(text, out var version) ? version : default;
    }

    public int CompareTo(PythonVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        result = PreRank(Pre).CompareTo(PreRank(other.Pre));
        return result != 0 ? result : PreNumber.CompareTo(other.PreNumber);
    }

    public override string ToString()
    {
        return IsPrerelease ? $"{Major}.{Minor}.{Patch}{Pre}{PreNumber}" : $"{Major}.{Minor}.{Patch}";
    }

    private static int PreRank(string pre) => pre switch
    {
        "a" => 0,
        "b" => 1,
        "rc" => 2,
        _ => 3
    };

    [GeneratedRegex(@"^(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?(?:(?<pre>a|b|rc|c)(?<prenum>\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();
}
