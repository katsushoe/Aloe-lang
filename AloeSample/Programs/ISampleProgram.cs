using System;
using Aloe.CommonLib;
using Aloe.CommonLib.Constants;

namespace AloeSample.Programs
{
    public interface ISampleProgram
    {
        /// <summary>
        /// 定数
        /// </summary>
        /// 
        List<AloeValue> Constants { get; }

        /// <summary>
        /// コード
        /// </summary>
        /// 
        List<Instruction> Code { get; }

        /// <summary>
        /// 関数情報
        /// </summary>
        /// 
        List<FunctionInfo> Functions { get; }
    }
}
