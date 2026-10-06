# AloeSample Execution Sample (Minimal AloeVM Demo)

This directory contains a **minimal implementation of AloeVM** and a **bytecode execution sample** that runs on it.  
This document explains the sample in the Japanese version of `AloeSample/Program.cs`, now translated into English.

## Overview of the Sample

In `Program.cs`, we build **hand-written bytecode** using a **simple builder (`BytecodeBuilder`)**  
and execute it on AloeVM. It corresponds to the following pseudo code:

```csharp
int i = 0;
while (i < 5)
{
    print(i);
    i = i + 1;
}
print("Done");
```

The output looks like this:

```text
0
1
2
3
4
Done
```

## Files and Components

- `Aloe.Runtime` namespace
  - `AloeVm` : VM core (implements instructions via `switch (Opcode)`)
  - `Opcode` : Instruction set of the VM (`Add`, `Sub`, `Jump`, `JumpIfFalse`, etc.)
  - `AloeValue` / `ValueKind` : In-VM value representation (int / bool / string / null)
  - `OperandStack` : Operand stack
  - `Module` / `FunctionInfo` / `CallFrame` : Bytecode module and call frames
  - `BytecodeReader` : Utility for reading `byte` / `int32` from the bytecode array

- `AloeSample/Program.cs`
  - `Main` method
    - Builds a constant table (`List<AloeValue>`)
    - Uses `BytecodeBuilder` to construct bytecode for a while-loop + `print`
    - Creates `FunctionInfo` and `Module`, then runs them on `AloeVm`
  - `BytecodeBuilder`
    - Uses `Emit(Opcode)` / `EmitInt32(int)` to append bytes
    - Records label positions via `MarkLabel("name")`
    - Outputs jump instructions with `EmitJump(Opcode, "label")`, and patches offsets later
    - Resolves labels and writes relative jump offsets when `ToArray()` is called

## Bytecode Flow (Rough Overview)

1. Prepare the constant table
   - `const[0] = 0`   (initial value of `i`)
   - `const[1] = 5`   (loop upper bound)
   - `const[2] = 1`   (increment)
   - `const[3] = "Done"` (string printed after the loop)

2. Local variables
   - Only one local: `local[0] = i`

3. What the bytecode represents

   - `i = 0;`
     - `PushConst 0` → `StoreLocal 0`

   - Mark label `loop_start`

   - Loop condition `i < 5`
     - `LoadLocal 0`
     - `PushConst 1`
     - `CmpLt`
     - `JumpIfFalse loop_end`

   - Loop body
     - `LoadLocal 0`
     - `Print`
     - `LoadLocal 0`
     - `PushConst 2`
     - `Add`
     - `StoreLocal 0`

   - `Jump loop_start`

   - Mark label `loop_end`

   - After the loop
     - `PushConst 3`
     - `Print`
     - `Halt`

4. When `BytecodeBuilder.ToArray()` is called:
   - It looks up positions of `loop_start` / `loop_end`,
   - And automatically patches relative offsets for `Jump` / `JumpIfFalse`.

## How to Build and Run (Example)

1. Create a .NET console application project, and add the source files under the `Aloe.Runtime` namespace plus `AloeSample/Program.cs` to the same solution.
2. Target framework can be `.NET 8.0` (or similar).
3. Build and run from the console, and you should see:

```text
0
1
2
3
4
Done
```

## Future Expansion Ideas

This sample is intended as a **“minimal AloeVM core + while-loop test”**.  
Future directions might include:

- Implementing additional opcodes (comparisons, logical ops, function calls, etc.)
- Generating this VM bytecode from the Aloe language frontend compiler
- Adding debug traces (IP, operand stack contents, etc.)
- Adding a dedicated test project to verify the behavior of each opcode

This sample is meant to be a starting point for gradually expanding the AloeVM instruction set and execution model.
