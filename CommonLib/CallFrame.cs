using System;

namespace Aloe.CommonLib
{
    /// <summary>
    /// 単一関数呼び出しの実行コンテキスト。
    /// - Module       : 実行中モジュール
    /// - Function     : このフレームが対象としている関数
    /// - Ip           : 関数内の現在の命令インデックス
    /// - Locals       : ローカル変数スロット
    /// - ReturnAddress: 呼び出し元に戻る際の再開 IP
    /// </summary>
    public sealed class CallFrame
    {
        /// <summary>このフレームが属するモジュール。</summary>
        public Module Module { get; }

        /// <summary>このフレームが表す関数情報。</summary>
        public FunctionInfo Function { get; }

        /// <summary>
        /// 現在の命令インデックス（Instruction[] の添字）。
        /// 実行ループ側でインクリメント／ジャンプ書き換えを行う。
        /// </summary>
        public int Ip { get; set; }

        /// <summary>ローカル変数スロット。</summary>
        public AloeValue[] Locals { get; }

        /// <summary>ローカル変数の数。</summary>
        public int LocalCount => Locals.Length;

        /// <summary>
        /// 呼び出し元に戻る際に再開する命令インデックス。
        /// -1 は「戻り先なし（エントリポイント）」を意味する。
        /// </summary>
        public int ReturnAddress { get; }

        public CallFrame(
            Module module,
            FunctionInfo function,
            int ip,
            AloeValue[] locals,
            int returnAddress = -1)
        {
            Module = module ?? throw new ArgumentNullException(nameof(module));
            Function = function ?? throw new ArgumentNullException(nameof(function));
            Ip = ip;
            Locals = locals ?? Array.Empty<AloeValue>();
            ReturnAddress = returnAddress;
        }

        /// <summary>ローカル変数の取得。</summary>
        public AloeValue GetLocal(int index)
        {
            if ((uint)index >= (uint)Locals.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    $"Local index out of range. index={index}, count={Locals.Length}");
            }

            return Locals[index];
        }

        /// <summary>ローカル変数の設定。</summary>
        public void SetLocal(int index, AloeValue value)
        {
            if ((uint)index >= (uint)Locals.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    $"Local index out of range. index={index}, count={Locals.Length}");
            }

            Locals[index] = value;
        }
    }
}
