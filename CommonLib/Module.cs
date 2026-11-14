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
        /// <summary>Task types in source declaration order; empty for existing bytecode.</summary>
        public IReadOnlyList<AloeTaskTypeInfo> TaskTypes { get; }

        public Module(
            IReadOnlyList<AloeValue> constants,
            IReadOnlyList<Instruction> code,
            IReadOnlyList<FunctionInfo> functions,
            int entryPointIndex) : this(constants, code, functions, entryPointIndex, Array.Empty<AloeTaskTypeInfo>()) { }

        public Module(IReadOnlyList<AloeValue> constants, IReadOnlyList<Instruction> code,
            IReadOnlyList<FunctionInfo> functions, int entryPointIndex, IReadOnlyList<AloeTaskTypeInfo> taskTypes)
        {
            Constants = constants ?? throw new ArgumentNullException(nameof(constants));
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Functions = functions ?? throw new ArgumentNullException(nameof(functions));
            EntryPointIndex = entryPointIndex;
            TaskTypes = taskTypes ?? throw new ArgumentNullException(nameof(taskTypes));
        }
    }
}
