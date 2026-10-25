using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class AloeLoggerTests
{
    [Test]
    public void AloePaths_UsesEnvironmentInstallRootAndDefaultsWhenUnset()
    {
        var original = Environment.GetEnvironmentVariable(AloePaths.InstallRootEnvironmentVariable);
        var root = Path.Combine(Path.GetTempPath(), "aloe-install-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(AloePaths.InstallRootEnvironmentVariable, root);
            var effectiveRoot = Path.GetFullPath(root);
            Assert.That(AloePaths.EffectiveInstallRoot, Is.EqualTo(effectiveRoot));
            Assert.That(AloePaths.BinDirectory, Is.EqualTo(Path.Combine(effectiveRoot, "bin")));
            Assert.That(AloePaths.ConfigDirectory, Is.EqualTo(Path.Combine(effectiveRoot, "config")));
            Assert.That(AloePaths.LogsDirectory, Is.EqualTo(Path.Combine(effectiveRoot, "logs")));
            Assert.That(AloePaths.DataDirectory, Is.EqualTo(Path.Combine(effectiveRoot, "data")));

            Environment.SetEnvironmentVariable(AloePaths.InstallRootEnvironmentVariable, "  ");
            Assert.That(AloePaths.EffectiveInstallRoot, Is.EqualTo(AloePaths.InstallRoot));
            Assert.That(AloePaths.BinDirectory, Is.EqualTo(Path.Combine(AloePaths.InstallRoot, "bin")));
            Assert.That(AloePaths.ConfigDirectory, Is.EqualTo(Path.Combine(AloePaths.InstallRoot, "config")));
            Assert.That(AloePaths.LogsDirectory, Is.EqualTo(Path.Combine(AloePaths.InstallRoot, "logs")));
            Assert.That(AloePaths.DataDirectory, Is.EqualTo(Path.Combine(AloePaths.InstallRoot, "data")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AloePaths.InstallRootEnvironmentVariable, original);
        }
    }

    [Test]
    public void Logger_WritesToConfiguredLogsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "aloe-log-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new AloeLogger(new AloeLoggingSettings
            {
                ConsoleEnabled = false,
                FileEnabled = true,
                Directory = "logs",
                FileName = "vm-test.log",
                IncludeTimestamp = false
            }, root);

            logger.Debug("gc plan started");

            var path = Path.Combine(root, "logs", "vm-test.log");
            Assert.That(File.Exists(path), Is.True);
            Assert.That(File.ReadAllText(path), Does.Contain("[DEBUG] gc plan started"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void VmSettings_LoadsLoggingDirectoryFromAloeVmJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "aloe-config-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "aloevm.json");
        File.WriteAllText(path, """
        {
          "debug": true,
          "logging": {
            "directory": "custom-logs",
            "fileName": "custom.log",
            "consoleEnabled": false,
            "fileEnabled": true
          }
        }
        """);

        try
        {
            var settings = AloeVmSettings.Load(path);
            Assert.That(settings.Debug, Is.True);
            Assert.That(settings.Logging.Directory, Is.EqualTo("custom-logs"));
            Assert.That(settings.Logging.FileName, Is.EqualTo("custom.log"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
