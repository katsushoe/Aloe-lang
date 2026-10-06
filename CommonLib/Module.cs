using System;
using System.Collections.Generic;
using Aloe.CommonLib;

namespace Aloe.CommonLib
{
    public sealed class Module
    {
        public IReadOnlyList<AloeValue> Constants { get; }

        public IReadOnlyList<Instruction> Code { get; }

        public IReadOnlyList<FunctionInfo> Functions { get; }

        public int EntryPointIndex { get; }

        public Module(
            IReadOnlyList<AloeValue> constants,
            IReadOnlyList<Instruction> code,
            IReadOnlyList<FunctionInfo> functions,
            int entryPointIndex)
        {
            Constants = constants ?? throw new ArgumentNullException(nameof(constants));
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Functions = functions ?? throw new ArgumentNullException(nameof(functions));
            EntryPointIndex = entryPointIndex;
        }
    }
}
