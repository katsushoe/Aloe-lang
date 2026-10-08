using System;


namespace Aloe.CommonLib.Constants
{
    /// <summary>
    /// Bytecode opcode kinds used by the current C# prototype.
    ///
    /// Values are still provisional and intentionally preserve the existing
    /// prototype numbering. Mnemonics follow the VM opcode specification.
    /// </summary>
    public enum EnumOpcode : ushort
    {
        Nop = 0x00,
        PushConst = 0x01,


        Add = 0x02,
        Sub = 0x03,
        Mul = 0x04,
        Div = 0x05,


        CmpLt = 0x06,
        Mod = 0x07,


        CmpEq = 0x08,
        CmpNe = 0x09,
        CmpLe = 0x0A,
        CmpGt = 0x0B,
        CmpGe = 0x0C,


        LoadLocal = 0x10,
        StoreLocal = 0x11,


        // Structured control flow (AloeBC v0.1 core)
        Block = 0x20,
        Loop = 0x21,
        If = 0x22,
        Else = 0x23,
        End = 0x24,
        Br = 0x25,
        BrIf = 0x26,


        Call = 0x30,
        Return = 0x31,


        Syscall = 0x0600,


        Halt = 0xFF,
    }
}