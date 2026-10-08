using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aloe.CommonLib;

namespace Aloe.RuntimeLib
{
    public sealed class AloeVmSettings
    {
        public bool Debug { get; set; }
        public string DataDirectory { get; set; } = AloePaths.DataDirectory;
        public AloeHostCapability AllowedHostCapabilities { get; set; } = AloeHostCapability.All;
        public AloeLoggingSettings Logging { get; set; } = new()
        {
            Directory = AloePaths.LogsDirectory,
            FileName = "aloevm.log"
        };

        [JsonIgnore]
        public string ConfigDirectory { get; private set; } = AloePaths.ConfigDirectory;

        public static AloeVmSettings Load(string fileName = "aloevm.json")
        {
            var path = ResolveConfigPath(fileName);
            if (path == null)
                return new AloeVmSettings();

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            options.Converters.Add(new JsonStringEnumConverter());

            var settings = JsonSerializer.Deserialize<AloeVmSettings>(File.ReadAllText(path), options)
                ?? new AloeVmSettings();
            settings.Logging ??= new AloeLoggingSettings
            {
                Directory = AloePaths.LogsDirectory,
                FileName = "aloevm.log"
            };
            if (string.IsNullOrWhiteSpace(settings.Logging.Directory))
                settings.Logging.Directory = AloePaths.LogsDirectory;
            if (string.IsNullOrWhiteSpace(settings.Logging.FileName))
                settings.Logging.FileName = "aloevm.log";
            if (string.IsNullOrWhiteSpace(settings.DataDirectory))
                settings.DataDirectory = AloePaths.DataDirectory;
            settings.ConfigDirectory = Path.GetDirectoryName(path) ?? AloePaths.ConfigDirectory;
            return settings;
        }

        private static string? ResolveConfigPath(string fileName)
        {
            if (Path.IsPathRooted(fileName))
                return File.Exists(fileName) ? Path.GetFullPath(fileName) : null;

            var installed = AloePaths.ConfigFile(fileName);
            if (File.Exists(installed)) return installed;

            var localConfig = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "config", fileName));
            if (File.Exists(localConfig)) return localConfig;

            var current = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, fileName));
            if (File.Exists(current)) return current;

            var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, fileName));
            return File.Exists(app) ? app : null;
        }
    }
}
