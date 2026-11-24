using System.Runtime.InteropServices;
using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    // Decision 0060: constants whose values come from the executing VM / host.
    private static AloeValue GetRuntimeConstant(string name) => name switch
    {
        "ALOE_OS" => AloeValue.FromString(RuntimeOsFamily()),
        "ALOE_TARGET" => AloeValue.FromString(RuntimeTarget()),
        "ALOE_BINDIR" => AloeValue.FromString(OperatingSystem.IsBrowser() ? string.Empty : AppContext.BaseDirectory.TrimEnd('\\', '/')),
        "ALOE_PLUGINDIR" => AloeValue.FromString(OperatingSystem.IsBrowser() ? string.Empty
            : Path.Combine(AppContext.BaseDirectory.TrimEnd('\\', '/'), "plugins")),
        "ALOE_DIR_SEPARATOR" => AloeValue.FromString(OperatingSystem.IsBrowser() ? string.Empty : Path.DirectorySeparatorChar.ToString()),
        "ALOE_MAX_PATHLEN" => AloeValue.FromInt(OperatingSystem.IsBrowser() ? 0 : OperatingSystem.IsWindows() ? 32767 : 4096),
        _ => throw new VmException($"Unknown runtime constant '{name}'.")
    };

    private static string RuntimeOsFamily()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsLinux()) return "Linux";
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()) return "Darwin";
        if (OperatingSystem.IsFreeBSD()) return "BSD";
        return "Unknown";
    }

    private static string RuntimeTarget()
    {
        if (OperatingSystem.IsBrowser()) return "wasm";
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x64",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            Architecture.Wasm => "wasm",
            _ => "Unknown"
        };
    }
}
