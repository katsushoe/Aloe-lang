using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class AloeLoggerTests
{
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
