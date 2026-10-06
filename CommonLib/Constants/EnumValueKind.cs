using System;

namespace Aloe.CommonLib.Constants
{
    public enum EnumValueKind
    {
        /// <summary>未設定／不明な値。</summary>
        Unknown = 0,

        /// <summary>null 値。</summary>
        Null = 1,

        /// <summary>int（整数）。</summary>
        Int = 2,

        /// <summary>float（単精度浮動小数）。</summary>
        Float = 3,

        /// <summary>decimal（10 進高精度）。</summary>
        Decimal = 4,

        /// <summary>bool（真偽値）。</summary>
        Bool = 5,

        /// <summary>string（文字列）。</summary>
        String = 6,

        /// <summary>byte（0–255）。</summary>
        Byte = 7,

        /// <summary>char（1 文字）。</summary>
        Char = 8,

        /// <summary>
        /// 配列 / リスト / セット / マップ / struct / class / enum など、
        /// 「参照型オブジェクト」全般（実装側でまとめて扱いたいとき用）。
        /// </summary>
        Object = 9,

        // 以降は将来拡張用（Task / Pipe / Result など）
        // Task   = 10,
        // Pipe   = 11,
        // Result = 12,
    }
}
