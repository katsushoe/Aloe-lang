using System;
using System.IO;

namespace Aloe.CommonLib
{
    /// <summary>Canonical Windows installation layout for Aloe tools.</summary>
    public static class AloePaths
    {
        public const string InstallRootEnvironmentVariable = "ALOE_INSTALL_ROOT";
        public const string InstallRoot = @"E:\Aloe";
        public static string EffectiveInstallRoot
        {
            get
            {
                var configured = Environment.GetEnvironmentVariable(InstallRootEnvironmentVariable);
                return string.IsNullOrWhiteSpace(configured)
                    ? InstallRoot
                    : Path.GetFullPath(configured);
            }
        }
        public static string BinDirectory => Path.Combine(EffectiveInstallRoot, "bin");
        public static string ConfigDirectory => Path.Combine(EffectiveInstallRoot, "config");
        public static string LogsDirectory => Path.Combine(EffectiveInstallRoot, "logs");
        public static string DataDirectory => Path.Combine(EffectiveInstallRoot, "data");

        public static string ConfigFile(string fileName)
            => Path.Combine(ConfigDirectory, fileName);
    }
}
