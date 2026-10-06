using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using System;

namespace AloeSample.Programs
{
    public class Sum1to10SampleProgram : ISampleProgram
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
                    AloeValue.FromInt(0),  // index 0
                    AloeValue.FromInt(1),  // index 1
                    AloeValue.FromInt(11), // index 2
                };
            }
        }

        // ==============================
        // 2. 命令列（バイトコード）
        // ==============================
        // ローカル変数:
        //   local[0] = sum
        //   local[1] = i
        //
        // 0:  sum = 0
        // 2:  i   = 1
        //
        // 4: LOOP:
        //        sum = sum + i
        //        i   = i + 1
        //        if (i < 11) goto LOOP
        //
        // 17: END:
        //        print(sum)
        //        return
        public List<Instruction> Code
        {
            get
            {
                return new List<Instruction>
                {
                    // --- 初期化 ---
                    new Instruction(EnumOpcode.PushConst, 0),        // 0: push const[0] (=0)
                    new Instruction(EnumOpcode.StoreLocal, 0),       // 1: sum = 0

                    new Instruction(EnumOpcode.PushConst, 1),        // 2: push const[1] (=1)
                    new Instruction(EnumOpcode.StoreLocal, 1),       // 3: i = 1

                    // --- LOOP 本体 ---
                    // (ラベル) LOOP_START = 4
                    new Instruction(EnumOpcode.LoadLocal, 0),        // 4: push sum
                    new Instruction(EnumOpcode.LoadLocal, 1),        // 5: push i
                    new Instruction(EnumOpcode.Add),                 // 6: sum + i
                    new Instruction(EnumOpcode.StoreLocal, 0),       // 7: sum = sum + i

                    new Instruction(EnumOpcode.LoadLocal, 1),        // 8: push i
                    new Instruction(EnumOpcode.PushConst, 1),        // 9: push 1
                    new Instruction(EnumOpcode.Add),                 // 10: i + 1
                    new Instruction(EnumOpcode.StoreLocal, 1),       // 11: i = i + 1

                    // 条件: i < 11 ならループ継続
                    new Instruction(EnumOpcode.LoadLocal, 1),        // 12: push i
                    new Instruction(EnumOpcode.PushConst, 2),        // 13: push 11
                    new Instruction(EnumOpcode.CmpLt),               // 14: (i < 11) を bool で push

                    // false(=11以上)なら END へジャンプ
                    // END の IP は 17
                    new Instruction(EnumOpcode.JumpIfFalse, 17),     // 15: if !(i < 11) goto 17

                    // true の場合はループ先頭へ
                    new Instruction(EnumOpcode.Jump, 4),             // 16: goto LOOP_START(4)

                    // --- END ---
                    // (ラベル) END = 17
                    new Instruction(EnumOpcode.LoadLocal, 0),        // 17: push sum
                    new Instruction(
                        EnumOpcode.Syscall,
                        (int)EnumSyscall.Print),                  // 18: syscall print(sum)

                    new Instruction(EnumOpcode.Return),              // 19: return
                    // Halt は不要（Return で CallStack が空になれば Run() が抜ける）
                    // new Instruction(EnumOpcode.Halt),            // 20: お好みで
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
                        localCount: 2     // sum, i
                    )
                };
            }
        }
    }
}
