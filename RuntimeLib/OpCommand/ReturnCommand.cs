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

            // もうフレームがなければ VM を停止
            if (vm.CallStack.IsEmpty)
            {
                vm.RequestHalt();
                return;
            }

            // 呼び出し元へ戻るケースをちゃんとやりたければ、
            // ここで return 値の push などを追加する。
        }
    }
}
