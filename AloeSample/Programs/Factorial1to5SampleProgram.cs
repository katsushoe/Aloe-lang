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
                    new Instruction(EnumOpcode.PushConst, 0),   // 0: push const[0] (=1)
                    new Instruction(EnumOpcode.StoreLocal, 0),  // 1: result = 1

                    new Instruction(EnumOpcode.PushConst, 0),   // 2: push const[0] (=1)
                    new Instruction(EnumOpcode.StoreLocal, 1),  // 3: i = 1

                    // --- LOOP_START ---
                    // ラベル LOOP_START = 4
                    new Instruction(EnumOpcode.LoadLocal, 0),   // 4: push result
                    new Instruction(EnumOpcode.LoadLocal, 1),   // 5: push i
                    new Instruction(EnumOpcode.Mul),            // 6: result * i
                    new Instruction(EnumOpcode.StoreLocal, 0),  // 7: result = result * i

                    new Instruction(EnumOpcode.LoadLocal, 1),   // 8: push i
                    new Instruction(EnumOpcode.PushConst, 0),   // 9: push 1
                    new Instruction(EnumOpcode.Add),            // 10: i + 1
                    new Instruction(EnumOpcode.StoreLocal, 1),  // 11: i = i + 1

                    // 条件: i < 6 ならループ継続
                    new Instruction(EnumOpcode.LoadLocal, 1),   // 12: push i
                    new Instruction(EnumOpcode.PushConst, 1),   // 13: push 6
                    new Instruction(EnumOpcode.CmpLt),          // 14: (i < 6) を bool で push

                    // false(=6以上)なら END へジャンプ
                    // END の IP は 17
                    new Instruction(EnumOpcode.JumpIfFalse, 17),// 15: if !(i < 6) goto 17

                    // true の場合はループ先頭へ
                    new Instruction(EnumOpcode.Jump, 4),        // 16: goto LOOP_START(4)

                    // --- END ---
                    // ラベル END = 17
                    new Instruction(EnumOpcode.LoadLocal, 0),   // 17: push result
                    new Instruction(
                        EnumOpcode.Syscall,
                        (int)EnumSyscall.Print),             // 18: syscall print(result)

                    new Instruction(EnumOpcode.Return),         // 19: return
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
