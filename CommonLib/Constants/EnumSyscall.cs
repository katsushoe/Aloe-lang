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

        // 将来拡張:
        // WriteStderr = 2,
        // GetTime     = 3,
    }
}
