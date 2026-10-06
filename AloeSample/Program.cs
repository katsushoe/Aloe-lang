using System;
using System.Collections.Generic;
using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.RuntimeLib;
using AloeSample.Programs;

namespace AloeSample
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            // change program here
            var program =
                //new HelloWorldSampleProgram();
                //new Sum1to10SampleProgram();
                //new Factorial1to5SampleProgram();
                //new FunctionSampleProgram();
                new AddSampleProgram();

            // 4. モジュール作成 (エントリポイントは main(= index 0))
            //   Module のコンストラクタが
            //   (constants, code, functions, entryPointIndex)
            //   の順になっている前提
            var module = new Module(
                program.Constants,
                program.Code,
                program.Functions,
                entryPointIndex: 0
            );

            // 5. VM 作成（内部でオペコードディスパッチテーブルを構築）
            var vm = new AloeVm(module);

            vm.TraceEnabled = true;

            // 6. print システムコール登録
            // AloeVm に RegisterSyscall(SyscallId, Action<AloeVm>) がある前提
            vm.RegisterSyscall(EnumSyscall.Print, vm =>
            {
                // スタックトップの値を取り出してコンソールに出力
                var value = vm.Pop();        // AloeVm に Pop() がある前提
                Console.WriteLine(value);   // AloeValue.ToString() 経由
            });

            // 7. 実行
            vm.RunFromEntryPoint();
        }
    }
}
