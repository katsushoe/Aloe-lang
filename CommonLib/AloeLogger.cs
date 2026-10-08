using System;
using System.Globalization;
using System.IO;

namespace Aloe.CommonLib
{
    public enum AloeLogLevel
    {
        Debug,
        Info,
        Warning,
        Error,
    }

    public sealed class AloeLoggingSettings
    {
        public bool Enabled { get; set; } = true;
        public bool ConsoleEnabled { get; set; } = true;
        public bool FileEnabled { get; set; } = true;
        public string Directory { get; set; } = "logs";
        public string FileName { get; set; } = "aloe.log";
        public bool IncludeTimestamp { get; set; } = true;
        public AloeLogLevel MinimumLevel { get; set; } = AloeLogLevel.Debug;
    }

    public sealed class AloeLogger
    {
        private readonly object _sync = new();
        private readonly string _baseDirectory;

        public AloeLogger(AloeLoggingSettings settings, string? baseDirectory = null)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(baseDirectory);
        }

        public AloeLoggingSettings Settings { get; }

        public string LogDirectoryPath => Path.GetFullPath(
            Path.IsPathRooted(Settings.Directory)
                ? Settings.Directory
                : Path.Combine(_baseDirectory, Settings.Directory));

        public string LogFilePath => Path.Combine(LogDirectoryPath, Settings.FileName);

        public void Debug(string message) => Write(AloeLogLevel.Debug, message);
        public void Info(string message) => Write(AloeLogLevel.Info, message);
        public void Warning(string message) => Write(AloeLogLevel.Warning, message);
        public void Error(string message) => Write(AloeLogLevel.Error, message);

        public void Write(AloeLogLevel level, string message)
        {
            if (!Settings.Enabled || level < Settings.MinimumLevel)
                return;

            var line = FormatLine(level, message ?? string.Empty);
            lock (_sync)
            {
                if (Settings.ConsoleEnabled)
                    Console.WriteLine(line);

                if (Settings.FileEnabled)
                {
                    Directory.CreateDirectory(LogDirectoryPath);
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
            }
        }

        private string FormatLine(AloeLogLevel level, string message)
        {
            var prefix = Settings.IncludeTimestamp
                ? $"[{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)}] "
                : string.Empty;
            return $"{prefix}[{level.ToString().ToUpperInvariant()}] {message}";
        }
    }
}
