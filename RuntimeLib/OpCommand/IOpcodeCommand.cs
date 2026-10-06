using Aloe.CommonLib;
using Aloe.CommonLib.Constants;

namespace Aloe.RuntimeLib.OpCommand
{
    /// <summary>
    /// 1 つのバイトコード命令種別（オペコード）に対応する
    /// コマンドクラスの共通インターフェイス。
    /// 
    /// Command パターンで、
    ///   EnumOpcode → IOpcodeCommand
    /// のディスパッチを行うために使用する。
    /// </summary>
    public interface IOpcodeCommand
    {
        /// <summary>
        /// 1 命令分の処理を実行する。
        /// 
        /// ・評価スタック操作（vm.ValueStack, vm.Push/Pop など）
        /// ・ローカル変数の読み書き（frame.Locals / frame.GetLocal / frame.SetLocal）
        /// ・IP の書き換え（frame.Ip や vm.Jump(...)）
        /// ・CallFrame の push/pop（vm.PushFrame / vm.PopFrame）
        /// などは、ここから行う。
        /// </summary>
        /// <param name="vm">実行中の VM 本体。</param>
        /// <param name="frame">現在のコールフレーム。</param>
        /// <param name="instruction">現在実行中の命令。</param>
        void Execute(AloeVm vm, CallFrame frame, in Instruction instruction);
    }
}
