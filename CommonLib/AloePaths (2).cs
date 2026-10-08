using System;
using System.IO;

namespace Aloe.CommonLib
{
    /// <summary>Canonical Windows installation layout for Aloe tools.</summary>
    public static class AloePaths
    {
        public const string InstallRoot = @"E:\Aloe";
        public static string BinDirectory => Path.Combine(InstallRoot, "bin");
        public static string ConfigDirectory => Path.Combine(InstallRoot, "config");
        public static string LogsDirectory => Path.Combine(InstallRoot, "logs");
        public static string DataDirectory => Path.Combine(InstallRoot, "data");

        public static string ConfigFile(string fileName)
            => Path.Combine(ConfigDirectory, fileName);
    }
}
