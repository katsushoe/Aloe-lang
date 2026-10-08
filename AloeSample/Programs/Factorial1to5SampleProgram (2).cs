using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using System;

namespace AloeSample.Programs
{
    public class Factorial1to5SampleProgram : ISampleProgram
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
                    AloeValue.FromInt(1), // index 0
                    AloeValue.FromInt(6), // index 1
                    AloeValue.FromBool(false), // index 2
                };
            }
        }

        // ==============================
        // 2. 命令列（バイトコード）
        // ==============================
        // ローカル:
        //   local[0] = result
        //   local[1] = i
        //
        // 0:  result = 1
        // 2:  i      = 1
        //
        // 4:  LOOP_START:
        //       result = result * i
        //       i = i + 1
        //       if (i < 6) goto LOOP_START
        //
        // 17: END:
        //       print(result)
        //       return
        public List<Instruction> Code
        {
            get
            {
                return new List<Instruction>
                {
                    // --- 初期化 ---
                    new Instruction(EnumOpcode.PushConst, 0),   // 0: result = 1 seed
                    new Instruction(EnumOpcode.StoreLocal, 0),  // 1
                    new Instruction(EnumOpcode.PushConst, 0),   // 2: i = 1 seed
                    new Instruction(EnumOpcode.StoreLocal, 1),  // 3

                    // --- structured loop ---
                    new Instruction(EnumOpcode.Block),          // 4: break target
                    new Instruction(EnumOpcode.Loop),           // 5: continue target

                    new Instruction(EnumOpcode.LoadLocal, 0),   // 6
                    new Instruction(EnumOpcode.LoadLocal, 1),   // 7
                    new Instruction(EnumOpcode.Mul),            // 8
                    new Instruction(EnumOpcode.StoreLocal, 0),  // 9: result *= i

                    new Instruction(EnumOpcode.LoadLocal, 1),   // 10
                    new Instruction(EnumOpcode.PushConst, 0),   // 11: 1
                    new Instruction(EnumOpcode.Add),            // 12
                    new Instruction(EnumOpcode.StoreLocal, 1),  // 13: i += 1

                    new Instruction(EnumOpcode.LoadLocal, 1),   // 14
                    new Instruction(EnumOpcode.PushConst, 1),   // 15: 6
                    new Instruction(EnumOpcode.CmpLt),          // 16: i < 6
                    new Instruction(EnumOpcode.PushConst, 2),   // 17: false
                    new Instruction(EnumOpcode.CmpEq),          // 18: !(i < 6)
                    new Instruction(EnumOpcode.BrIf, 1),        // 19: break BLOCK
                    new Instruction(EnumOpcode.Br, 0),          // 20: continue LOOP
                    new Instruction(EnumOpcode.End),            // 21: LOOP
                    new Instruction(EnumOpcode.End),            // 22: BLOCK

                    new Instruction(EnumOpcode.LoadLocal, 0),   // 23
                    new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.Print), // 24
                    new Instruction(EnumOpcode.Return),         // 25
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
                    new FunctionInfo(
                        name: "main",
                        entryIp: 0,
                        parameterCount: 0,
                        localCount: 2    // result, i
                    )
                };
            }
        }
    }
}
