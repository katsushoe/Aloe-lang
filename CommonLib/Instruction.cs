using System;
using Aloe.CommonLib.Constants;

namespace Aloe.CommonLib
{
    /// <summary>
    /// 1 命令分の情報。
    /// - Opcode … 実行すべきオペコード
    /// - Operand0 / Operand1 … 数値オペランド（インデックスや即値など）
    /// 
    /// とりあえず 2 つまでの int オペランドをサポートしておき、
    /// それ以上必要になったら拡張する方針。
    /// </summary>
    public readonly struct Instruction
    {
        /// <summary>オペコード。</summary>
        public EnumOpcode Opcode { get; }

        /// <summary>第 1 オペランド。</summary>
        public int Operand0 { get; }

        /// <summary>第 2 オペランド。</summary>
        public int Operand1 { get; }

        /// <summary>
        /// 旧実装との互換用プロパティ。
        /// 以前のコードが Instruction.Operand を見ている場合のために、
        /// Operand0 をそのまま返す。
        /// </summary>
        public int Operand => Operand0;

        /// <summary>オペランド無し命令。</summary>
        public Instruction(EnumOpcode opcode)
            : this(opcode, 0, 0)
        {
        }

        /// <summary>1 オペランド命令。</summary>
        public Instruction(EnumOpcode opcode, int operand0)
            : this(opcode, operand0, 0)
        {
        }

        /// <summary>2 オペランド命令。</summary>
        public Instruction(EnumOpcode opcode, int operand0, int operand1)
        {
            Opcode = opcode;
            Operand0 = operand0;
            Operand1 = operand1;
        }

        public override string ToString()
            => $"[{Opcode}, {Operand0}, {Operand1}]";
    }
}
