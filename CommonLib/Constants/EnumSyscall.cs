using System;

namespace Aloe.CommonLib.Constants
{
    /// <summary>
    /// VM から呼び出すシステムコール ID 一覧。
    /// </summary>
    public enum EnumSyscall : int
    {
        /// <summary>
        /// スタックトップの値を文字列化して標準出力に書き出す。
        /// </summary>
        Print = 1,

        /// <summary>Request one normal budgeted GC pass.</summary>
        GCRequire = 2,

        /// <summary>Finish the requested GC cycle synchronously.</summary>
        GCFinish = 3,

        /// <summary>Run the Aloe tick barrier without performing physical GC work.</summary>
        Tick = 4,

        /// <summary>Set GC diagnostic logging on or off.</summary>
        GCSetDebug = 5,

        /// <summary>Read the current GC diagnostic logging flag.</summary>
        GCGetDebug = 6,

        /// <summary>Allocate an Aloe heap object. Pops fieldCount and class typeName, then pushes its stable handle.</summary>
        ObjectAllocate = 7,

        /// <summary>Read an object field. Pops fieldIndex and object handle, then pushes the field value.</summary>
        ObjectFieldGet = 8,

        /// <summary>Write an object field. Pops value, fieldIndex, and object handle.</summary>
        ObjectFieldSet = 9,

        /// <summary>Enqueue a public async instance method call for the target Object CallBuffer.</summary>
        InstanceAsyncEnqueue = 10,

        /// <summary>Synchronously execute a public instance property getter against Committed State.</summary>
        InstancePropertyGet = 11,

        /// <summary>Read one line from the host input and push it as a string.</summary>
        HostReadLine = 12,

        /// <summary>Sleep on the host for the requested number of milliseconds.</summary>
        HostSleep = 13,

        /// <summary>Create a buffered pipe. Pops the pipe type name and pushes a pipe handle.</summary>
        PipeCreate = 14,

        /// <summary>Write one value to a pipe. Pops value and pipe handle.</summary>
        PipeWrite = 15,

        /// <summary>Close a pipe. Pops the pipe handle.</summary>
        PipeClose = 16,

        /// <summary>Try to read one value. Pops pipe handle, pushes value then bool(hasValue).</summary>
        PipeTryRead = 17,

        /// <summary>Bind a filter function between an input and output pipe.</summary>
        PipeBindFilter = 18,

        // 将来拡張:
        // WriteStderr = 2,
        // GetTime     = 3,
    }
}
