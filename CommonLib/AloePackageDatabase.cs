using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aloe.CommonLib;

/// <summary>Reads and updates the local package-location index used by Aloe build tools.</summary>
public static partial class AloePackageDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false
    };

    public static IReadOnlyList<AloePackageEntry> List(string path, string? namePattern = null)
    {
        var database = Read(path, missingIsEmpty: true);
        if (namePattern is null) return database.Packages;

        var pattern = "^" + Regex.Escape(namePattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        var matcher = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        return database.Packages.Where(package => matcher.IsMatch(package.Name)).ToArray();
    }

    public static void Add(string path, string name, string version, string abcPath, string metaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var abcFullPath = Path.GetFullPath(abcPath);
        var metaFullPath = Path.GetFullPath(metaPath);
        if (!File.Exists(abcFullPath)) throw new FileNotFoundException("Aloe bytecode package was not found.", abcFullPath);
        if (!File.Exists(metaFullPath)) throw new FileNotFoundException("Aloe package metadata was not found.", metaFullPath);
        if (!abcFullPath.EndsWith(".abc", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The package bytecode path must end in .abc.", nameof(abcPath));
        if (!metaFullPath.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The package metadata path must end in .meta.json.", nameof(metaPath));

        var metadata = ReadMetadata(metaFullPath);
        if (!string.Equals(metadata.Name, name, StringComparison.Ordinal) ||
            !string.Equals(metadata.Version, version, StringComparison.Ordinal))
            throw new InvalidDataException("The requested package name/version does not match .meta.json.");

        var fullDatabasePath = Path.GetFullPath(path);
        var database = Read(fullDatabasePath, missingIsEmpty: true);
        if (database.Packages.Any(package =>
                string.Equals(package.Name, name, StringComparison.Ordinal) &&
                string.Equals(package.Version, version, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Package '{name}' version '{version}' is already indexed.");

        var directory = Path.GetDirectoryName(fullDatabasePath)!;
        database.Packages.Add(new AloePackageEntry
        {
            Name = name,
            Version = version,
            AbcPath = ToDatabasePath(directory, abcFullPath),
            MetaPath = ToDatabasePath(directory, metaFullPath),
            Dependencies = metadata.Dependencies
        });
        WriteAtomically(fullDatabasePath, database);
    }

    public static void Remove(string path, string name, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var fullDatabasePath = Path.GetFullPath(path);
        var database = Read(fullDatabasePath, missingIsEmpty: false);
        var index = database.Packages.FindIndex(package =>
            string.Equals(package.Name, name, StringComparison.Ordinal) &&
            string.Equals(package.Version, version, StringComparison.Ordinal));
        if (index < 0) throw new KeyNotFoundException($"Package '{name}' version '{version}' was not found.");

        database.Packages.RemoveAt(index);
        WriteAtomically(fullDatabasePath, database);
    }

    private static AloePackageDatabaseDocument Read(string path, bool missingIsEmpty)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            if (missingIsEmpty) return new AloePackageDatabaseDocument();
            throw new FileNotFoundException("Aloe package database was not found.", fullPath);
        }

        try
        {
            using var stream = File.OpenRead(fullPath);
            using var json = JsonDocument.Parse(stream);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("formatVersion", out var formatVersion) ||
                formatVersion.ValueKind != JsonValueKind.Number || !formatVersion.TryGetInt32(out var format) || format != 1 ||
                !json.RootElement.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Unsupported package database formatVersion or missing packages array.");
            foreach (var entry in packages.EnumerateArray())
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Every package database entry must contain a dependencies array.");

            var database = json.RootElement.Deserialize<AloePackageDatabaseDocument>(JsonOptions)
                ?? throw new InvalidDataException("Aloe package database JSON is null.");
            if (database.FormatVersion != 1 || database.Packages is null)
                throw new InvalidDataException("Unsupported package database formatVersion or missing packages array.");
            var identities = new HashSet<(string Name, string Version)>();
            foreach (var package in database.Packages)
            {
                if (package is null || string.IsNullOrWhiteSpace(package.Name) || string.IsNullOrWhiteSpace(package.Version) ||
                    string.IsNullOrWhiteSpace(package.AbcPath) || string.IsNullOrWhiteSpace(package.MetaPath) ||
                    package.Dependencies is null)
                    throw new InvalidDataException("Package database entries require name, version, abcPath, metaPath, and dependencies.");
                if (!identities.Add((package.Name, package.Version)))
                    throw new InvalidDataException($"Package database contains duplicate identity '{package.Name}' version '{package.Version}'.");
                foreach (var dependency in package.Dependencies)
                    if (dependency is null || string.IsNullOrWhiteSpace(dependency.Name) || string.IsNullOrWhiteSpace(dependency.VersionRange))
                        throw new InvalidDataException($"Package '{package.Name}' contains an invalid dependency.");
            }
            return database;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Package database '{fullPath}' contains invalid JSON.", exception);
        }
    }

    private static AloePackageMetadata ReadMetadata(string path)
        => ReadMetadata(path, File.ReadAllBytes(path));

    private static AloePackageMetadata ReadMetadata(string path, ReadOnlyMemory<byte> content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("formatVersion", out var formatVersion) ||
                formatVersion.ValueKind != JsonValueKind.Number || !formatVersion.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("package", out var package) || package.ValueKind != JsonValueKind.Object ||
                !package.TryGetProperty("name", out var packageName) || packageName.ValueKind != JsonValueKind.String ||
                !package.TryGetProperty("version", out var packageVersion) || packageVersion.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Metadata must use formatVersion 1 and contain package.name/package.version strings.");

            var dependencies = new List<AloePackageDependency>();
            if (!root.TryGetProperty("dependencies", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Package metadata must contain a dependencies array.");
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("name", out var dependencyName) || dependencyName.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("versionRange", out var dependencyVersion) || dependencyVersion.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(dependencyName.GetString()) || string.IsNullOrWhiteSpace(dependencyVersion.GetString()))
                    throw new InvalidDataException("Package metadata contains an invalid dependency entry.");
                dependencies.Add(new AloePackageDependency { Name = dependencyName.GetString()!, VersionRange = dependencyVersion.GetString()! });
            }
            return new AloePackageMetadata(packageName.GetString()!, packageVersion.GetString()!, dependencies);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Package metadata '{path}' contains invalid JSON.", exception);
        }
    }

    private static string ToDatabasePath(string databaseDirectory, string filePath)
        => Path.GetRelativePath(databaseDirectory, filePath).Replace(Path.DirectorySeparatorChar, '/');

    private static void WriteAtomically(string path, AloePackageDatabaseDocument database)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, database, JsonOptions);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

public sealed class AloePackageDatabaseDocument
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("packages")]
    public List<AloePackageEntry> Packages { get; set; } = [];
}

public sealed class AloePackageEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("abcPath")]
    public string AbcPath { get; set; } = "";

    [JsonPropertyName("metaPath")]
    public string MetaPath { get; set; } = "";

    [JsonPropertyName("dependencies")]
    public List<AloePackageDependency> Dependencies { get; set; } = [];
}

public sealed class AloePackageDependency
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("versionRange")]
    public string VersionRange { get; set; } = "";
}

internal sealed record AloePackageMetadata(string Name, string Version, List<AloePackageDependency> Dependencies);
