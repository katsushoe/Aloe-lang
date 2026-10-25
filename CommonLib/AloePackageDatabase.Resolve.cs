using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aloe.CommonLib;

public static partial class AloePackageDatabase
{
    /// <summary>Resolves an exact-version local dependency closure without modifying packages or their index.</summary>
    public static AloePackageResolutionPlan Resolve(string path, string name, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        RequireExactPackageVersion(version);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var entries = Read(fullPath, missingIsEmpty: false).Packages
            .ToDictionary(entry => new AloePackageIdentity(entry.Name, entry.Version));
        var root = new AloePackageIdentity(name, version);
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        var states = new Dictionary<AloePackageIdentity, byte>();
        var pending = new Dictionary<AloePackageIdentity, AloeResolvedPackage>();
        var result = new List<AloeResolvedPackage>();
        var activePath = new List<AloePackageIdentity>();
        var work = new Stack<(AloePackageIdentity Identity, bool Complete)>();
        work.Push((root, false));
        while (work.TryPop(out var item))
        {
            var identity = item.Identity;
            if (item.Complete)
            {
                activePath.RemoveAt(activePath.Count - 1);
                states[identity] = 2;
                result.Add(pending[identity]);
                continue;
            }
            if (selected.TryGetValue(identity.Name, out var priorVersion) && priorVersion != identity.Version)
                throw new InvalidDataException($"Package version conflict for '{identity.Name}': '{priorVersion}' and '{identity.Version}'.");
            selected[identity.Name] = identity.Version;
            if (states.TryGetValue(identity, out var state))
            {
                if (state == 2) continue;
                var cycle = string.Join(" -> ", activePath.Append(identity).Select(node => $"{node.Name}@{node.Version}"));
                throw new InvalidDataException($"Package dependency cycle: {cycle}");
            }
            if (!entries.TryGetValue(identity, out var entry))
                throw new KeyNotFoundException($"Package '{identity.Name}' version '{identity.Version}' was not found.");
            var abcPath = Path.GetFullPath(entry.AbcPath, directory);
            var metaPath = Path.GetFullPath(entry.MetaPath, directory);
            var metadataBytes = File.ReadAllBytes(metaPath);
            var metadata = ReadMetadata(metaPath, metadataBytes);
            if (metadata.Name != identity.Name || metadata.Version != identity.Version)
                throw new InvalidDataException($"Indexed identity '{identity.Name}@{identity.Version}' does not match metadata '{metaPath}'.");
            var dependencies = metadata.Dependencies.Select(dependency =>
            {
                RequireExactPackageVersion(dependency.VersionRange);
                return new AloePackageIdentity(dependency.Name, dependency.VersionRange);
            }).Distinct().OrderBy(dependency => dependency.Name, StringComparer.Ordinal)
                .ThenBy(dependency => dependency.Version, StringComparer.Ordinal).ToArray();
            using var abcStream = File.OpenRead(abcPath);
            pending[identity] = new AloeResolvedPackage(identity.Name, identity.Version, abcPath, metaPath,
                Convert.ToHexString(SHA256.HashData(abcStream)).ToLowerInvariant(),
                Convert.ToHexString(SHA256.HashData(metadataBytes)).ToLowerInvariant(), dependencies);
            states[identity] = 1;
            activePath.Add(identity);
            work.Push((identity, true));
            for (var index = dependencies.Length - 1; index >= 0; index--)
                work.Push((dependencies[index], false));
        }
        return new AloePackageResolutionPlan(root, result.ToArray());
    }

    private static void RequireExactPackageVersion(string version)
    {
        if (!Regex.IsMatch(version, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            throw new InvalidDataException($"Unsupported package version '{version}'; resolve requires an exact three-part numeric version.");
    }
}

/// <summary>An exact, case-sensitive package identity.</summary>
public sealed record AloePackageIdentity(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

/// <summary>A selected package snapshot and its exact dependency identities.</summary>
public sealed record AloeResolvedPackage(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("abcPath")] string AbcPath,
    [property: JsonPropertyName("metaPath")] string MetaPath,
    [property: JsonPropertyName("abcSha256")] string AbcSha256,
    [property: JsonPropertyName("metaSha256")] string MetaSha256,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<AloePackageIdentity> Dependencies);

/// <summary>A deterministic dependency-first local resolution plan.</summary>
public sealed record AloePackageResolutionPlan(
    [property: JsonPropertyName("root")] AloePackageIdentity Root,
    [property: JsonPropertyName("packages")] IReadOnlyList<AloeResolvedPackage> Packages)
{
    /// <summary>The resolution-plan JSON schema version.</summary>
    [JsonPropertyName("formatVersion"), JsonPropertyOrder(-1)]
    public int FormatVersion => 1;
}
