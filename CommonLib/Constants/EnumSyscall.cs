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

        /// <summary>Enqueue a public static async method call for its declaring type.</summary>
        StaticAsyncEnqueue = 19,

        /// <summary>Enqueue a public async instance call resolved from the target object's runtime type. Pops a dispatch descriptor constant index and argument count.</summary>
        InstanceVirtualAsyncEnqueue = 20,

        /// <summary>Pop count and primitive array type name, then count elements in reverse; push an array handle.</summary>
        ArrayCreate = 21,
        /// <summary>Pop index and array handle; push the element.</summary>
        ArrayGet = 22,
        /// <summary>Pop value, index and array handle; replace the element.</summary>
        ArraySet = 23,
        /// <summary>Pop array handle; push its fixed length.</summary>
        ArrayLength = 24,

        /// <summary>Pop target descriptor and reference; push the same live compatible handle or throw InvalidCastException.</summary>
        ObjectCast = 25,
        /// <summary>Pop target descriptor and reference; push a compatible handle or Null, then a success bool.</summary>
        ObjectTryCast = 26,

        /// <summary>Pop primitive descriptor and value; preserve a matching scalar or extract a matching box, otherwise throw InvalidCastException. Does not convert.</summary>
        PrimitiveCast = 27,
        /// <summary>Pop primitive type descriptor and value; push the same valid value or Null, then a success bool.</summary>
        PrimitiveTryCast = 28,

        /// <summary>Pop a valid primitive; allocate its immutable box and push the new heap handle.</summary>
        PrimitiveBox = 29,

        /// <summary>Pop primitive descriptor and live box; push its payload. Invalid references fail before extraction; the compiler clears the local only after success.</summary>
        PrimitiveUnbox = 30,
        /// <summary>Pop and return a live heap reference, otherwise throw InvalidReferenceException.</summary>
        ReferenceRequireLive = 31,
        /// <summary>Push the runtime invalid-reference marker without adding a Null constant to AloeBC.</summary>
        ReferenceInvalid = 32,
        /// <summary>Pop index and reference-array owner; clear that element following Tick/query rules.</summary>
        ReferenceArrayInvalidate = 33,
        /// <summary>Pop field index and owner; clear a reference field following Tick/query rules.</summary>
        ReferenceFieldInvalidate = 34,
        /// <summary>Pop field index and owner; read a reference field including its invalid marker.</summary>
        ReferenceFieldGet = 35,
        /// <summary>Append one element to a List while preserving its object identity.</summary>
        ListAdd = 36,
        /// <summary>Remove one indexed element from a List and shift later elements left.</summary>
        ListRemoveAt = 37,
        /// <summary>Add one unique value to a Set while preserving its object identity.</summary>
        SetAdd = 38,
        /// <summary>Remove one value from a Set while preserving its object identity.</summary>
        SetRemove = 39,
        /// <summary>Push whether a Set contains an exactly typed value.</summary>
        SetContains = 40,
        /// <summary>Remove every value from a Set while preserving its object identity.</summary>
        SetClear = 48,
        /// <summary>Merge unique values from another exactly typed Set.</summary>
        SetUnionWith = 49,
        /// <summary>Keep only values present in another exactly typed Set.</summary>
        SetIntersectWith = 50,
        /// <summary>Remove values present in another exactly typed Set.</summary>
        SetExceptWith = 51,
        /// <summary>Pop count, descriptor and key/value pairs; push a new Map handle.</summary>
        MapCreate = 41,
        /// <summary>Pop typed key and Map handle; push the associated value or throw for a missing key.</summary>
        MapGet = 42,
        /// <summary>Pop value, typed key and Map handle; insert or replace the value.</summary>
        MapSet = 43,
        /// <summary>Pop typed key and Map handle; remove the key if present.</summary>
        MapRemove = 44,
        /// <summary>Pop typed key and Map handle; push whether the key exists.</summary>
        MapContainsKey = 45,
        /// <summary>Pop Map handle; push its key count.</summary>
        MapCount = 46,
        /// <summary>Pop int index and Map handle; push the key at that insertion-order position.</summary>
        MapKeyAt = 47,

        // 将来拡張:
        // WriteStderr = 2,
        // GetTime     = 3,
    }
}
