using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using System;

namespace AloeSample.Programs
{
    public class FunctionSampleProgram : ISampleProgram
    {
        /// <summary>
        /// 定数
        /// </summary>
        /// 
        public List<AloeValue> Constants
        {
            get
            {
                return new List<AloeValue>
                {
                    AloeValue.FromInt(3), // index 0
                    AloeValue.FromInt(4), // index 1
                };
            }
        }

        // ==============================
        // 2. 命令列（バイトコード）
        // ==============================
        //
        // main():
        //   print(square(3));
        //   print(square(4));
        //   return;
        //
        // square(x):
        //   return x * x;
        /// 
        public List<Instruction> Code
        {
            get
            {
                return new List<Instruction>
                {
                     // ===== main (function index 0) =====
                    // IP 0: push 3
                    new Instruction(EnumOpcode.PushConst, 0),                        // 0
                    // IP 1: square(3)
                    new Instruction(EnumOpcode.Call, 1),                             // 1
                    // IP 2: print(result)
                    new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.Print),   // 2

                    // IP 3: push 4
                    new Instruction(EnumOpcode.PushConst, 1),                        // 3
                    // IP 4: square(4)
                    new Instruction(EnumOpcode.Call, 1),                             // 4
                    // IP 5: print(result)
                    new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.Print),   // 5

                    // IP 6: return from main (VM 停止)
                    new Instruction(EnumOpcode.Return),                              // 6

                    // ===== square(x) (function index 1, entryIp = 7) =====
                    // 返り値として x * x をスタックに積んで Return
                    new Instruction(EnumOpcode.LoadLocal, 0),                        // 7: push x
                    new Instruction(EnumOpcode.LoadLocal, 0),                        // 8: push x
                    new Instruction(EnumOpcode.Mul),                                 // 9: x * x
                    new Instruction(EnumOpcode.Return),                              // 10: return
                };
            }
        }


        /// <summary>
        /// 関数情報
        /// </summary>
        /// 
        public List<FunctionInfo> Functions
        {
            get
            {
                return new List<FunctionInfo>
                {
                    // main
                    new FunctionInfo(
                        name: "main",
                        entryIp: 0,
                        parameterCount: 0,
                        localCount: 0
                    ),

                    // square(x)
                    new FunctionInfo(
                        name: "square",
                        entryIp: 7,  // 上の code の 7 行目がエントリポイント
                        parameterCount: 1,
                        localCount: 1 // local[0] に x を入れる想定
                    ),
                };
            }
        }
    }
}
