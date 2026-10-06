using Aloe.CommonLib.Constants;
using System;

namespace Aloe.CommonLib
{
    /// <summary>
    /// VM からのシステムコールをホスト側に橋渡しするインターフェイス。
    /// </summary>
    public interface ISystemCallHandler
    {
        /// <summary>
        /// 指定された syscall を実行する。
        /// </summary>
        void Invoke(EnumSyscall id, ReadOnlySpan<AloeValue> args);
    }
}
