using System;

namespace Aloe.CommonLib.Constants
{
    /// <summary>
    /// バイトコード・オペコード種別。
    /// 
    /// ※ 現時点ではシンプルに連番。
    ///   将来 VM 仕様どおり「上位バイト=カテゴリ / 下位バイト=カテゴリ内連番」に
    ///   する場合は、この値を調整すればよい。
    /// </summary>
    public enum EnumOpcode : ushort
    {
        // 0x00: 基本スタック操作・算術など
        Nop = 0x00,

        // 定数プッシュ
        PushConst = 0x01,

        // 算術演算
        Add = 0x02,
        Sub = 0x03,
        Mul = 0x04,
        Div = 0x05,

        // 比較
        CmpLt = 0x06,

        // ローカル変数
        LoadLocal = 0x10,
        StoreLocal = 0x11,

        // 制御フロー
        Jump = 0x20,
        JumpIfFalse = 0x21,

        // 関数呼び出し
        Call = 0x30,
        Return = 0x31,

        Syscall = 0x0600,

        // VM 制御
        Halt = 0xFF,
    }
}
