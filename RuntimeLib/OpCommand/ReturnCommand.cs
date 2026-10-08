using Aloe.CommonLib;

namespace Aloe.RuntimeLib.OpCommand
{
    /// <summary>
    /// 関数からの return。
    /// 戻り値は（必要であれば）VM のスタック経由で実装する前提。
    /// </summary>
    public sealed class ReturnCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            // 今のフレームを落とす
            vm.CallStack.Pop();

            // コールスタックが空になった場合も停止要求は出さない。
            // スケジューラは空のコールスタックを「この実行コンテキストの完了」として扱う。
            // ここで RequestHalt() すると、filter の return が main など
            // 他のコンテキストまで停止させてしまう。

            // 呼び出し元へ戻るケースをちゃんとやりたければ、
            // ここで return 値の push などを追加する。
        }
    }
}
