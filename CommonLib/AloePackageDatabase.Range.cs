using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aloe.CommonLib;

public static partial class AloePackageDatabase
{
    /// <summary>Selects the highest root in a numeric interval, then resolves its exact local closure.</summary>
    public static AloePackageRangeResolution ResolveRange(string path, string name, string versionRange)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(versionRange);
        var bounds = versionRange.Split(' ');
        if (bounds.Length != 2 || !bounds[0].StartsWith(">=", StringComparison.Ordinal) ||
            !bounds[1].StartsWith('<'))
            throw new InvalidDataException("Root version range requires exactly '>=M.m.p <N.n.p'.");
        var lower = bounds[0][2..];
        var upper = bounds[1][1..];
        RequireExactPackageVersion(lower);
        RequireExactPackageVersion(upper);
        if (ComparePackageVersions(lower, upper) >= 0)
            throw new InvalidDataException("Root version range lower bound must be below its upper bound.");

        var fullPath = Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(fullPath);
        using var stream = new MemoryStream(bytes, writable: false);
        var database = ReadSnapshot(stream, fullPath);
        var excluded = new List<string>();
        string? selected = null;
        foreach (var entry in database.Packages.Where(entry => entry.Name == name))
        {
            if (!IsExactPackageVersion(entry.Version))
                excluded.Add(entry.Version);
            else if (ComparePackageVersions(entry.Version, lower) >= 0 &&
                ComparePackageVersions(entry.Version, upper) < 0 &&
                (selected is null || ComparePackageVersions(entry.Version, selected) > 0))
                selected = entry.Version;
        }
        if (selected is null)
            throw new KeyNotFoundException($"Package '{name}' has no indexed version in '{versionRange}'.");
        var plan = ResolveSnapshot(fullPath, name, selected, database);
        return new AloePackageRangeResolution(name, versionRange, plan.Root,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            excluded.Order(StringComparer.Ordinal).ToArray(), plan);
    }

    private static bool IsExactPackageVersion(string version) =>
        Regex.IsMatch(version, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static int ComparePackageVersions(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < a.Length; i++)
        {
            var order = a[i].Length.CompareTo(b[i].Length);
            if (order == 0) order = string.CompareOrdinal(a[i], b[i]);
            if (order != 0) return order;
        }
        return 0;
    }
}

/// <summary>A root interval selection and its unchanged formatVersion 1 exact dependency plan.</summary>
public sealed record AloePackageRangeResolution(
    [property: JsonPropertyName("requestedName")] string RequestedName,
    [property: JsonPropertyName("requestedRange")] string RequestedRange,
    [property: JsonPropertyName("selectedRoot")] AloePackageIdentity SelectedRoot,
    [property: JsonPropertyName("indexSha256")] string IndexSha256,
    [property: JsonPropertyName("excludedVersions")] IReadOnlyList<string> ExcludedVersions,
    [property: JsonPropertyName("plan")] AloePackageResolutionPlan Plan)
{
    /// <summary>The root-selection wrapper schema version.</summary>
    [JsonPropertyName("formatVersion"), JsonPropertyOrder(-1)]
    public int FormatVersion => 2;
}
