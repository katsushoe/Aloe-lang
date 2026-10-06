using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using System;

namespace AloeSample.Programs
{
    public class HelloWorldSampleProgram : ISampleProgram
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
                    AloeValue.FromString("Hello, world")
                };
            } 
        }

        /// <summary>
        /// コード
        /// </summary>
        /// 
        public List<Instruction> Code 
        {
            get
            {
                return new List<Instruction>
                {
                    // const[0] ("Hello, world") をスタックに積む
                    new Instruction(EnumOpcode.PushConst, 0),

                    // Syscall(Print) を呼ぶ
                    new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.Print),

                    // プログラム終了
                    new Instruction(EnumOpcode.Halt)
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
                        entryIp: 0,   // code[0] から
                        parameterCount: 0,
                        localCount: 0
                    )
                };
            }
        }
    }
}
