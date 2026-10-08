using System;

namespace Aloe.RuntimeLib
{
    [Flags]
    public enum AloeHostCapability
    {
        None = 0,
        ConsoleInput = 1 << 0,
        Timer = 1 << 1,
        All = ConsoleInput | Timer,
    }
}
