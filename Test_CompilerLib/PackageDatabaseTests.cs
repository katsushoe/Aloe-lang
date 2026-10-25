using Aloe.CommonLib;

namespace Aloe.CompilerLib.Tests;

public sealed class PackageDatabaseTests
{
    [Test]
    public void PackageDatabase_AddIndexesMetadataAndListSupportsNameGlob()
    {
        using var directory = new TemporaryDirectory();
        var (abc, meta, database) = CreatePackage(directory.Path, "sample.core", "1.2.3");

        AloePackageDatabase.Add(database, "sample.core", "1.2.3", abc, meta);

        var entries = AloePackageDatabase.List(database, "sample.*");
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Name, Is.EqualTo("sample.core"));
        Assert.That(entries[0].Version, Is.EqualTo("1.2.3"));
        Assert.That(entries[0].AbcPath, Is.EqualTo("sample.core.abc"));
        Assert.That(entries[0].MetaPath, Is.EqualTo("sample.core.meta.json"));
        Assert.That(entries[0].Dependencies, Has.Count.EqualTo(1));
        Assert.That(entries[0].Dependencies[0].Name, Is.EqualTo("aloe.system"));
        Assert.That(entries[0].Dependencies[0].VersionRange, Is.EqualTo(">=0.1.0"));
        Assert.That(AloePackageDatabase.List(database, "missing.*"), Is.Empty);
    }

    [Test]
    public void PackageDatabase_RejectsDuplicateIdentityAndMetadataMismatchWithoutChangingIndex()
    {
        using var directory = new TemporaryDirectory();
        var (abc, meta, database) = CreatePackage(directory.Path, "sample.core", "1.2.3");
        AloePackageDatabase.Add(database, "sample.core", "1.2.3", abc, meta);
        var before = File.ReadAllBytes(database);

        Assert.Throws<InvalidOperationException>(() => AloePackageDatabase.Add(database, "sample.core", "1.2.3", abc, meta));
        Assert.Throws<InvalidDataException>(() => AloePackageDatabase.Add(database, "sample.other", "1.2.3", abc, meta));

        Assert.That(File.ReadAllBytes(database), Is.EqualTo(before));
    }

    [Test]
    public void PackageDatabase_RemoveDeletesOnlyIndexEntryAndRetainsPayloadFiles()
    {
        using var directory = new TemporaryDirectory();
        var (abc, meta, database) = CreatePackage(directory.Path, "sample.core", "1.2.3");
        AloePackageDatabase.Add(database, "sample.core", "1.2.3", abc, meta);

        AloePackageDatabase.Remove(database, "sample.core", "1.2.3");

        Assert.That(AloePackageDatabase.List(database), Is.Empty);
        Assert.That(File.Exists(abc), Is.True);
        Assert.That(File.Exists(meta), Is.True);
        Assert.Throws<KeyNotFoundException>(() => AloePackageDatabase.Remove(database, "sample.core", "1.2.3"));
    }

    [Test]
    public void PackageDatabase_RejectsUnsupportedAndMalformedIndexesBeforeMutation()
    {
        using var directory = new TemporaryDirectory();
        var (abc, meta, database) = CreatePackage(directory.Path, "sample.core", "1.2.3");
        File.WriteAllText(database, "{\"formatVersion\":2,\"packages\":[]}");
        var before = File.ReadAllBytes(database);

        Assert.Throws<InvalidDataException>(() => AloePackageDatabase.Add(database, "sample.core", "1.2.3", abc, meta));
        Assert.Throws<InvalidDataException>(() => AloePackageDatabase.List(database));
        Assert.That(File.ReadAllBytes(database), Is.EqualTo(before));
    }

    [Test]
    public void PackageDatabase_RejectsEntriesWithoutDependencyArrays()
    {
        using var directory = new TemporaryDirectory();
        var database = System.IO.Path.Combine(directory.Path, "package-db.json");
        File.WriteAllText(database, """{"formatVersion":1,"packages":[{"name":"sample.core","version":"1.0.0","abcPath":"a.abc","metaPath":"a.meta.json"}]}""");

        Assert.Throws<InvalidDataException>(() => AloePackageDatabase.List(database));
    }

    private static (string Abc, string Meta, string Database) CreatePackage(string directory, string name, string version)
    {
        var abc = System.IO.Path.Combine(directory, "sample.core.abc");
        var meta = System.IO.Path.Combine(directory, "sample.core.meta.json");
        var database = System.IO.Path.Combine(directory, "package-db.json");
        File.WriteAllBytes(abc, [0x41, 0x42, 0x43]);
        File.WriteAllText(meta, $$"""
        {
          "formatVersion": 1,
          "package": { "name": "{{name}}", "version": "{{version}}" },
          "dependencies": [ { "name": "aloe.system", "versionRange": ">=0.1.0" } ],
          "exports": { "types": [], "functions": [], "methods": [], "filters": [], "annotations": [] }
        }
        """);
        return (abc, meta, database);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aloe-package-db-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
