using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using System.Buffers.Binary;
using System.Text;

namespace Aloe.Aloe2Wasm;

/// <summary>
/// Minimal AloeBC -> WebAssembly backend for the v0.1 core subset.
/// Supported values: Int(i64), Float(f32), Bool(i32), Byte(i32), Char(i32 UTF-16 code unit). Supported control flow is structured.
/// </summary>
public sealed class AloeWasmCompiler
{
    public byte[] Compile(Module module)
    {
        ArgumentNullException.ThrowIfNull(module);
        ValidateModule(module);

        // Reuse stack-type validation to discover only the typed output imports used by this module.
        var printImports = new List<EnumValueKind>();
        for (var i = 0; i < module.Functions.Count; i++)
            EmitFunctionBody(Stream.Null, module, i, printImports, collectPrintImports: true);

        using var output = new MemoryStream();
        output.Write(new byte[] { 0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00 });

        WriteSection(output, 1, section => WriteTypeSection(section, module, printImports));
        if (printImports.Count > 0)
            WriteSection(output, 2, section => WritePrintImportSection(section, module, printImports));
        WriteSection(output, 3, section => WriteFunctionSection(section, module));
        if (module.Code.Any(instruction => instruction.Opcode == EnumOpcode.Syscall &&
            instruction.Operand0 is (int)EnumSyscall.GCSetDebug or (int)EnumSyscall.GCGetDebug))
            WriteSection(output, 6, WriteGcDebugGlobalSection);
        WriteSection(output, 7, section => WriteExportSection(section, module, printImports.Count));
        WriteSection(output, 10, section => WriteCodeSection(section, module, printImports));
        return output.ToArray();
    }

    private static void ValidateModule(Module module)
    {
        if (module.Functions.Count == 0)
            throw new NotSupportedException("WASM backend requires at least one function.");
        if ((uint)module.EntryPointIndex >= (uint)module.Functions.Count)
            throw new InvalidOperationException("EntryPointIndex is out of range.");

        foreach (var f in module.Functions)
        {
            if (!f.HasTypeMetadata)
                throw new NotSupportedException($"Function '{f.Name}' has no type metadata; aloe2wasm v0.1 requires typed functions.");
            foreach (var t in f.ParameterTypes) EnsureValueType(t, $"parameter of {f.Name}");
            foreach (var t in f.LocalTypes) EnsureValueType(t, $"local of {f.Name}");
            if (f.ReturnType is { } rt) EnsureValueType(rt, $"return type of {f.Name}");
        }
    }

    private static void EnsureValueType(EnumValueKind kind, string where)
    {
        if (kind is not (EnumValueKind.Int or EnumValueKind.Float or EnumValueKind.Bool or EnumValueKind.Byte or EnumValueKind.Char))
            throw new NotSupportedException($"aloe2wasm v0.1 does not support {kind} in {where}.");
    }

    private static void WriteTypeSection(Stream s, Module module, List<EnumValueKind> printImports)
    {
        WriteU32(s, (uint)(module.Functions.Count + printImports.Count));
        foreach (var f in module.Functions)
        {
            s.WriteByte(0x60);
            WriteU32(s, (uint)f.ParameterTypes.Count);
            foreach (var t in f.ParameterTypes) s.WriteByte(ToWasmValueType(t));
            if (f.ReturnType is { } rt)
            {
                WriteU32(s, 1);
                s.WriteByte(ToWasmValueType(rt));
            }
            else
            {
                WriteU32(s, 0);
            }
        }
        foreach (var kind in printImports)
        {
            s.WriteByte(0x60);
            WriteU32(s, 1);
            s.WriteByte(ToWasmValueType(kind));
            WriteU32(s, 0);
        }
    }

    private static void WritePrintImportSection(Stream s, Module module, List<EnumValueKind> printImports)
    {
        WriteU32(s, (uint)printImports.Count);
        for (var i = 0; i < printImports.Count; i++)
        {
            WriteName(s, "aloe");
            WriteName(s, printImports[i] switch
            {
                EnumValueKind.Int => "print_int",
                EnumValueKind.Float => "print_float",
                EnumValueKind.Bool => "print_bool",
                EnumValueKind.Byte => "print_byte",
                EnumValueKind.Char => "print_char",
                _ => throw new InvalidOperationException("Unsupported print import type.")
            });
            s.WriteByte(0x00); // function import; defined function types retain their original indices
            WriteU32(s, (uint)(module.Functions.Count + i));
        }
    }

    private static void WriteFunctionSection(Stream s, Module module)
    {
        WriteU32(s, (uint)module.Functions.Count);
        for (var i = 0; i < module.Functions.Count; i++)
            WriteU32(s, (uint)i);
    }

    private static void WriteExportSection(Stream s, Module module, int importCount)
    {
        var exports = new List<(string Name, int Index)>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < module.Functions.Count; i++)
        {
            var name = string.IsNullOrWhiteSpace(module.Functions[i].Name) ? $"fn{i}" : module.Functions[i].Name;
            if (used.Add(name)) exports.Add((name, i));
        }
        if (used.Add("_start")) exports.Add(("_start", module.EntryPointIndex));

        WriteU32(s, (uint)exports.Count);
        foreach (var export in exports)
        {
            WriteName(s, export.Name);
            s.WriteByte(0x00); // function export
            WriteU32(s, (uint)(export.Index + importCount));
        }
    }

    private static void WriteGcDebugGlobalSection(Stream s)
    {
        WriteU32(s, 1);
        s.WriteByte(0x7F); // i32
        s.WriteByte(0x01); // mutable; private global 0 stores this instance's GC.debug flag
        s.WriteByte(0x41); // i32.const
        s.WriteByte(0x00); // default VM settings start with debug disabled
        s.WriteByte(0x0B); // initializer end
    }

    private static void WriteCodeSection(Stream s, Module module, List<EnumValueKind> printImports)
    {
        WriteU32(s, (uint)module.Functions.Count);
        for (var functionIndex = 0; functionIndex < module.Functions.Count; functionIndex++)
        {
            using var body = new MemoryStream();
            var function = module.Functions[functionIndex];
            var usesScratch = module.Code.Skip(function.EntryIp).Take(
                (functionIndex + 1 < module.Functions.Count ? module.Functions[functionIndex + 1].EntryIp : module.Code.Count) - function.EntryIp)
                .Any(i => IsBitwise(i.Opcode) || i.Opcode is EnumOpcode.Add or EnumOpcode.Sub or EnumOpcode.Mul or EnumOpcode.Div or EnumOpcode.Mod
                    or EnumOpcode.CmpEq or EnumOpcode.CmpNe or EnumOpcode.CmpLt or EnumOpcode.CmpLe or EnumOpcode.CmpGt or EnumOpcode.CmpGe);
            var usesFloat = function.ParameterTypes.Contains(EnumValueKind.Float) || function.LocalTypes.Contains(EnumValueKind.Float) ||
                function.ReturnType == EnumValueKind.Float || module.Code.Skip(function.EntryIp).Take(
                    (functionIndex + 1 < module.Functions.Count ? module.Functions[functionIndex + 1].EntryIp : module.Code.Count) - function.EntryIp)
                    .Any(i => i.Opcode == EnumOpcode.PushConst && (uint)i.Operand0 < (uint)module.Constants.Count && module.Constants[i.Operand0].Kind == EnumValueKind.Float ||
                        i.Opcode == EnumOpcode.Call && (uint)i.Operand0 < (uint)module.Functions.Count && module.Functions[i.Operand0].ReturnType == EnumValueKind.Float);
            WriteLocals(body, function, usesScratch, usesFloat);
            EmitFunctionBody(body, module, functionIndex, printImports);
            body.WriteByte(0x0B); // function end

            WriteU32(s, (uint)body.Length);
            body.Position = 0;
            body.CopyTo(s);
        }
    }

    private static bool IsBitwise(EnumOpcode opcode) => opcode is EnumOpcode.BitAnd or EnumOpcode.BitOr
        or EnumOpcode.BitXor or EnumOpcode.BitNot or EnumOpcode.BitShiftLeft or EnumOpcode.BitShiftRight;

    private static void WriteLocals(Stream s, FunctionInfo f, bool usesScratch, bool usesFloat)
    {
        var nonParameters = f.LocalTypes.Skip(f.ParameterCount).ToArray();
        var groups = new List<(uint Count, byte Type)>();
        foreach (var t in nonParameters)
        {
            var wt = ToWasmValueType(t);
            if (groups.Count > 0 && groups[^1].Type == wt)
                groups[^1] = (groups[^1].Count + 1, wt);
            else
                groups.Add((1, wt));
        }

        // Scratch locals follow all source locals, including parameters.
        if (usesScratch) groups.Add((2, 0x7E));
        if (usesFloat) groups.Add((5, 0x7D));
        if (usesFloat) groups.Add((2, 0x7C));
        WriteU32(s, (uint)groups.Count);
        foreach (var g in groups)
        {
            WriteU32(s, g.Count);
            s.WriteByte(g.Type);
        }
    }

    private static void EmitFunctionBody(Stream s, Module module, int functionIndex,
        List<EnumValueKind> printImports, bool collectPrintImports = false)
    {
        var function = module.Functions[functionIndex];
        var start = function.EntryIp;
        var end = functionIndex + 1 < module.Functions.Count
            ? module.Functions[functionIndex + 1].EntryIp
            : module.Code.Count;

        EmitScalarParameterGuards(s, function);
        var stack = new Stack<EnumValueKind>();
        var controls = new Stack<int>();
        for (var ip = start; ip < end; ip++)
        {
            var ins = module.Code[ip];
            switch (ins.Opcode)
            {
                case EnumOpcode.Nop:
                    s.WriteByte(0x01);
                    break;
                case EnumOpcode.PushConst:
                    EmitConst(s, module, ins.Operand0, stack);
                    break;
                case EnumOpcode.LoadLocal:
                    EnsureLocal(function, ins.Operand0);
                    s.WriteByte(0x20); WriteU32(s, (uint)ins.Operand0);
                    stack.Push(function.LocalTypes[ins.Operand0]);
                    break;
                case EnumOpcode.StoreLocal:
                    EnsureLocal(function, ins.Operand0);
                    RequirePop(stack, function.LocalTypes[ins.Operand0], "StoreLocal");
                    s.WriteByte(0x21); WriteU32(s, (uint)ins.Operand0);
                    break;
                case EnumOpcode.Add:
                case EnumOpcode.Sub:
                case EnumOpcode.Mul:
                case EnumOpcode.Div:
                case EnumOpcode.Mod:
                    if (stack.Count >= 2 &&
                        (stack.Peek() == EnumValueKind.Float || stack.ElementAt(1) == EnumValueKind.Float))
                        EmitFloatBinary(s, stack, ins.Opcode, function.LocalTypes.Count + 2);
                    else
                        EmitIntBinary(s, stack, ins.Opcode, function.LocalTypes.Count);
                    break;
                case EnumOpcode.BitAnd:
                case EnumOpcode.BitOr:
                case EnumOpcode.BitXor:
                case EnumOpcode.BitNot:
                case EnumOpcode.BitShiftLeft:
                case EnumOpcode.BitShiftRight:
                    EmitBitwise(s, stack, ins.Opcode, function.LocalTypes.Count); break;
                case EnumOpcode.CmpEq: EmitCompare(s, stack, 0x51, 0x46, "CmpEq", function.LocalTypes.Count); break;
                case EnumOpcode.CmpNe: EmitCompare(s, stack, 0x52, 0x47, "CmpNe", function.LocalTypes.Count); break;
                case EnumOpcode.CmpLt: EmitIntCompare(s, stack, 0x53, "CmpLt", function.LocalTypes.Count); break;
                case EnumOpcode.CmpLe: EmitIntCompare(s, stack, 0x57, "CmpLe", function.LocalTypes.Count); break;
                case EnumOpcode.CmpGt: EmitIntCompare(s, stack, 0x55, "CmpGt", function.LocalTypes.Count); break;
                case EnumOpcode.CmpGe: EmitIntCompare(s, stack, 0x59, "CmpGe", function.LocalTypes.Count); break;
                case EnumOpcode.Block:
                    controls.Push(stack.Count); s.WriteByte(0x02); s.WriteByte(0x40); break;
                case EnumOpcode.Loop:
                    controls.Push(stack.Count); s.WriteByte(0x03); s.WriteByte(0x40); break;
                case EnumOpcode.If:
                    RequirePop(stack, EnumValueKind.Bool, "If");
                    controls.Push(stack.Count); s.WriteByte(0x04); s.WriteByte(0x40); break;
                case EnumOpcode.Else:
                    if (controls.Count == 0) throw new InvalidOperationException("ELSE without control frame.");
                    TrimStack(stack, controls.Peek()); s.WriteByte(0x05); break;
                case EnumOpcode.End:
                    if (controls.Count == 0) throw new InvalidOperationException("END without control frame.");
                    TrimStack(stack, controls.Pop()); s.WriteByte(0x0B); break;
                case EnumOpcode.Br:
                    s.WriteByte(0x0C); WriteU32(s, checked((uint)ins.Operand0)); break;
                case EnumOpcode.BrIf:
                    RequirePop(stack, EnumValueKind.Bool, "BrIf");
                    s.WriteByte(0x0D); WriteU32(s, checked((uint)ins.Operand0)); break;
                case EnumOpcode.Call:
                    EmitCall(s, module, ins.Operand0, stack, printImports.Count); break;
                case EnumOpcode.Return:
                    if (function.ReturnType is { } rt) RequirePop(stack, rt, "Return");
                    s.WriteByte(0x0F); break;
                case EnumOpcode.Halt:
                    // HALT is VM-specific. At function scope it lowers to return.
                    s.WriteByte(0x0F); break;
                case EnumOpcode.Syscall:
                    if (ins.Operand0 == (int)EnumSyscall.Print)
                    {
                        if (stack.Count == 0) throw new InvalidOperationException("Print requires a scalar argument.");
                        var kind = stack.Pop();
                        EnsureValueType(kind, "print argument");
                        var importIndex = printImports.IndexOf(kind);
                        if (importIndex < 0)
                        {
                            if (!collectPrintImports) throw new InvalidOperationException("Print import was not discovered.");
                            importIndex = printImports.Count;
                            printImports.Add(kind);
                        }
                        s.WriteByte(0x10); WriteU32(s, (uint)importIndex);
                        break;
                    }
                    if (ins.Operand0 == (int)EnumSyscall.GCSetDebug)
                    {
                        RequirePop(stack, EnumValueKind.Bool, "GC.debug assignment");
                        s.WriteByte(0x24); WriteU32(s, 0); // global.set
                        break;
                    }
                    if (ins.Operand0 == (int)EnumSyscall.GCGetDebug)
                    {
                        s.WriteByte(0x23); WriteU32(s, 0); // global.get
                        stack.Push(EnumValueKind.Bool);
                        break;
                    }
                    if (ins.Operand0 is (int)EnumSyscall.Tick or (int)EnumSyscall.GCRequire or (int)EnumSyscall.GCFinish)
                    {
                        // The direct-WASM subset has no heap objects or async call buffers,
                        // so these barriers have no state to commit, drain, or collect.
                        break;
                    }
                    throw new NotSupportedException("aloe2wasm v0.1 does not yet lower this Syscall; other runtime imports are pending.");
                default:
                    throw new NotSupportedException($"aloe2wasm v0.1 does not support opcode {ins.Opcode}.");
            }
        }
    }

    private static void EmitScalarParameterGuards(Stream s, FunctionInfo function)
    {
        for (var index = 0; index < function.ParameterCount; index++)
        {
            if (function.ParameterTypes[index] == EnumValueKind.Int)
            {
                EmitRangeGuard(s, index, int.MinValue, int.MaxValue);
            }
            else if (function.ParameterTypes[index] is EnumValueKind.Bool or EnumValueKind.Byte or EnumValueKind.Char)
            {
                int maximum = function.ParameterTypes[index] switch
                {
                    EnumValueKind.Bool => 1,
                    EnumValueKind.Byte => byte.MaxValue,
                    _ => char.MaxValue,
                };
                s.WriteByte(0x20); WriteU32(s, (uint)index); // local.get
                s.WriteByte(0x41); WriteS32(s, maximum); // i32.const scalar maximum
                s.WriteByte(0x4B); // i32.gt_u also rejects negative values.
                EmitTrapIf(s);
            }
            else if (function.ParameterTypes[index] == EnumValueKind.Float)
            {
                EmitLocal(s, 0x20, index);
                EmitLocal(s, 0x20, index);
                s.WriteByte(0x5C); // f32.ne detects NaN.
                EmitTrapIf(s);
                EmitLocal(s, 0x20, index);
                s.WriteByte(0x8B); // f32.abs
                s.WriteByte(0x43); WriteF32(s, float.MaxValue);
                s.WriteByte(0x5E); // f32.gt detects infinity.
                EmitTrapIf(s);
            }
        }
    }

    private static void EmitConst(Stream s, Module module, int index, Stack<EnumValueKind> stack)
    {
        if ((uint)index >= (uint)module.Constants.Count) throw new IndexOutOfRangeException("Constant index out of range.");
        var value = module.Constants[index];
        switch (value.Kind)
        {
            case EnumValueKind.Int:
                s.WriteByte(0x42); WriteS64(s, value.AsInt); stack.Push(EnumValueKind.Int); break;
            case EnumValueKind.Bool:
                s.WriteByte(0x41); WriteS32(s, value.AsBool ? 1 : 0); stack.Push(EnumValueKind.Bool); break;
            case EnumValueKind.Byte:
                s.WriteByte(0x41); WriteS32(s, value.AsByte); stack.Push(EnumValueKind.Byte); break;
            case EnumValueKind.Char:
                s.WriteByte(0x41); WriteS32(s, value.AsChar); stack.Push(EnumValueKind.Char); break;
            case EnumValueKind.Float:
                s.WriteByte(0x43); WriteF32(s, (float)value.AsFloat); stack.Push(EnumValueKind.Float); break;
            default:
                throw new NotSupportedException($"aloe2wasm v0.1 does not support {value.Kind} constants.");
        }
    }

    private static void EmitCall(Stream s, Module module, int index, Stack<EnumValueKind> stack, int importCount)
    {
        if ((uint)index >= (uint)module.Functions.Count) throw new IndexOutOfRangeException("Function index out of range.");
        var callee = module.Functions[index];
        for (var i = callee.ParameterTypes.Count - 1; i >= 0; i--)
            RequirePop(stack, callee.ParameterTypes[i], "Call");
        s.WriteByte(0x10); WriteU32(s, (uint)(index + importCount));
        if (callee.ReturnType is { } rt) stack.Push(rt);
    }

    private static void EmitBitwise(Stream s, Stack<EnumValueKind> stack, EnumOpcode opcode, int scratch)
    {
        var unary = opcode == EnumOpcode.BitNot;
        var right = PopIntegerOperand(stack, opcode.ToString());
        var left = unary ? EnumValueKind.Int : PopIntegerOperand(stack, opcode.ToString());
        PromoteTopToInt64(s, right, opcode.ToString());
        EmitLocal(s, 0x21, scratch + 1);
        EmitRangeGuard(s, scratch + 1, int.MinValue, int.MaxValue);
        if (opcode is EnumOpcode.BitShiftLeft or EnumOpcode.BitShiftRight)
            EmitRangeGuard(s, scratch + 1, 0, 31);
        if (!unary)
        {
            PromoteTopToInt64(s, left, opcode.ToString());
            EmitLocal(s, 0x21, scratch);
            EmitRangeGuard(s, scratch, int.MinValue, int.MaxValue);
            EmitLocal(s, 0x20, scratch);
            s.WriteByte(0xA7); // i32.wrap_i64
        }
        EmitLocal(s, 0x20, scratch + 1);
        s.WriteByte(0xA7);
        if (unary) { s.WriteByte(0x41); WriteS32(s, -1); }
        s.WriteByte(opcode switch
        {
            EnumOpcode.BitAnd => 0x71,
            EnumOpcode.BitOr => 0x72,
            EnumOpcode.BitXor or EnumOpcode.BitNot => 0x73,
            EnumOpcode.BitShiftLeft => 0x74,
            EnumOpcode.BitShiftRight => 0x75,
            _ => throw new InvalidOperationException($"Invalid bitwise opcode {opcode}.")
        });
        s.WriteByte(0xAC); // i64.extend_i32_s: preserve the signed int32 result
        stack.Push(EnumValueKind.Int);
    }

    private static void EmitLocal(Stream s, byte opcode, int index)
    {
        s.WriteByte(opcode); WriteU32(s, checked((uint)index));
    }

    private static void EmitRangeGuard(Stream s, int local, long min, long max)
    {
        EmitLocal(s, 0x20, local);
        s.WriteByte(0x42); WriteS64(s, min);
        s.WriteByte(0x53); // i64.lt_s
        EmitLocal(s, 0x20, local);
        s.WriteByte(0x42); WriteS64(s, max);
        s.WriteByte(0x55); // i64.gt_s
        s.WriteByte(0x72); // i32.or
        s.WriteByte(0x04); s.WriteByte(0x40); // if (no result)
        s.WriteByte(0x00); // unreachable: reject invalid input rather than wrapping it
        s.WriteByte(0x0B);
    }

    private static void EmitIntBinary(Stream s, Stack<EnumValueKind> stack, EnumOpcode opcode, int scratch)
    {
        var right = PopIntegerOperand(stack, opcode.ToString());
        var left = PopIntegerOperand(stack, opcode.ToString());
        PromoteTopToInt64(s, right, opcode.ToString());
        EmitLocal(s, 0x21, scratch + 1);
        PromoteTopToInt64(s, left, opcode.ToString());
        EmitLocal(s, 0x21, scratch);
        EmitRangeGuard(s, scratch, int.MinValue, int.MaxValue);
        EmitRangeGuard(s, scratch + 1, int.MinValue, int.MaxValue);
        EmitLocal(s, 0x20, scratch);
        EmitLocal(s, 0x20, scratch + 1);
        // Valid int32 operands fit in i64 intermediates. Signed i64 division
        // truncates toward zero; MinValue / -1 is rejected by the result guard.
        s.WriteByte(opcode switch
        {
            EnumOpcode.Add => 0x7C,
            EnumOpcode.Sub => 0x7D,
            EnumOpcode.Mul => 0x7E,
            EnumOpcode.Div => 0x7F,
            EnumOpcode.Mod => 0x81,
            _ => throw new InvalidOperationException($"Invalid integer opcode {opcode}.")
        });
        EmitLocal(s, 0x22, scratch); // local.tee: retain the result on the stack
        EmitRangeGuard(s, scratch, int.MinValue, int.MaxValue);
        stack.Push(EnumValueKind.Int);
    }

    private static void EmitFloatBinary(Stream s, Stack<EnumValueKind> stack, EnumOpcode opcode, int scratch)
    {
        if (stack.Count < 2) throw new InvalidOperationException($"Stack underflow at {opcode}.");
        var right = stack.Pop();
        var left = stack.Pop();
        if (!IsFloatArithmeticOperand(left) || !IsFloatArithmeticOperand(right))
            throw new NotSupportedException($"WASM float arithmetic requires Float, Int, or Byte operands, got {left} and {right}.");
        var integerScratch = scratch - 2;
        ConvertTopToFloat32(s, right, scratch + 1, integerScratch + 1, opcode.ToString());
        ConvertTopToFloat32(s, left, scratch, integerScratch, opcode.ToString());
        if (opcode == EnumOpcode.Mod)
        {
            EmitFloatModulo(s, stack, scratch);
            return;
        }
        EmitLocal(s, 0x20, scratch);
        EmitLocal(s, 0x20, scratch + 1);
        s.WriteByte(opcode switch
        {
            EnumOpcode.Add => 0x92,
            EnumOpcode.Sub => 0x93,
            EnumOpcode.Mul => 0x94,
            EnumOpcode.Div => 0x95,
            _ => throw new InvalidOperationException($"Invalid float opcode {opcode}.")
        });
        EmitLocal(s, 0x22, scratch + 2); // local.tee leaves the result on the value stack.
        EmitLocal(s, 0x20, scratch + 2);
        EmitLocal(s, 0x20, scratch + 2);
        s.WriteByte(0x5C); // f32.ne detects NaN.
        EmitTrapIf(s);
        EmitLocal(s, 0x20, scratch + 2);
        s.WriteByte(0x8B); // f32.abs
        s.WriteByte(0x43); WriteF32(s, float.MaxValue);
        s.WriteByte(0x5E); // f32.gt detects infinity.
        EmitTrapIf(s);
        if (opcode is EnumOpcode.Mul or EnumOpcode.Div)
        {
            EmitLocal(s, 0x20, scratch + 2);
            s.WriteByte(0x43); WriteF32(s, 0);
            s.WriteByte(0x5B); // result == 0
            EmitLocal(s, 0x20, scratch);
            s.WriteByte(0x43); WriteF32(s, 0);
            s.WriteByte(0x5C); // left != 0
            EmitLocal(s, 0x20, scratch + 1);
            s.WriteByte(0x43); WriteF32(s, 0);
            s.WriteByte(0x5C); // right != 0
            s.WriteByte(0x71); // i32.and
            s.WriteByte(0x71);
            EmitTrapIf(s);
        }
        stack.Push(EnumValueKind.Float);
    }

    private static void EmitFloatModulo(Stream s, Stack<EnumValueKind> stack, int scratch)
    {
        EmitFiniteFloatGuard(s, scratch);
        EmitNaNFloatGuard(s, scratch + 1);

        EmitLocal(s, 0x20, scratch + 1);
        s.WriteByte(0x43); WriteF32(s, 0);
        s.WriteByte(0x5B); // f32.eq
        EmitTrapIf(s);

        EmitLocal(s, 0x20, scratch);
        s.WriteByte(0x8B); // f32.abs
        EmitLocal(s, 0x21, scratch + 2); // remainder = abs(dividend)
        EmitLocal(s, 0x20, scratch + 1);
        s.WriteByte(0x8B); // f32.abs
        EmitLocal(s, 0x21, scratch + 1); // divisor = abs(divisor)

        s.WriteByte(0x02); s.WriteByte(0x40); // block: modulo complete
        s.WriteByte(0x03); s.WriteByte(0x40); // loop: subtract a scaled divisor
        EmitLocal(s, 0x20, scratch + 2);
        EmitLocal(s, 0x20, scratch + 1);
        s.WriteByte(0x5D); // f32.lt
        s.WriteByte(0x0D); WriteU32(s, 1); // br_if modulo complete

        EmitLocal(s, 0x20, scratch + 1);
        EmitLocal(s, 0x21, scratch + 3); // scaled divisor
        s.WriteByte(0x02); s.WriteByte(0x40); // block: scaling complete
        s.WriteByte(0x03); s.WriteByte(0x40); // loop: double while it fits
        EmitLocal(s, 0x20, scratch + 3);
        s.WriteByte(0x43); WriteF32(s, 2);
        s.WriteByte(0x94); // f32.mul
        EmitLocal(s, 0x22, scratch + 4); // doubled divisor
        EmitLocal(s, 0x20, scratch + 2);
        s.WriteByte(0x5F); // f32.le
        s.WriteByte(0x04); s.WriteByte(0x40); // if (fits)
        EmitLocal(s, 0x20, scratch + 4);
        EmitLocal(s, 0x21, scratch + 3);
        s.WriteByte(0x0C); WriteU32(s, 1); // repeat inner loop
        s.WriteByte(0x05); // else: the next doubling would exceed remainder
        s.WriteByte(0x0C); WriteU32(s, 2); // leave scaling block
        s.WriteByte(0x0B); // end if
        s.WriteByte(0x0B); // end scaling loop
        s.WriteByte(0x0B); // end scaling block

        EmitLocal(s, 0x20, scratch + 2);
        EmitLocal(s, 0x20, scratch + 3);
        s.WriteByte(0x93); // f32.sub; exact here because scaled is in [remainder/2, remainder]
        EmitLocal(s, 0x21, scratch + 2);
        s.WriteByte(0x0C); WriteU32(s, 0); // repeat outer loop
        s.WriteByte(0x0B); // end outer loop
        s.WriteByte(0x0B); // end modulo block

        EmitLocal(s, 0x20, scratch + 2);
        EmitLocal(s, 0x20, scratch);
        s.WriteByte(0x98); // f32.copysign preserves the dividend sign, including negative zero
        stack.Push(EnumValueKind.Float);
    }

    private static void EmitFiniteFloatGuard(Stream s, int local)
    {
        EmitLocal(s, 0x20, local);
        EmitLocal(s, 0x20, local);
        s.WriteByte(0x5C); // f32.ne detects NaN
        EmitTrapIf(s);
        EmitLocal(s, 0x20, local);
        s.WriteByte(0x8B); // f32.abs
        s.WriteByte(0x43); WriteF32(s, float.MaxValue);
        s.WriteByte(0x5E); // f32.gt detects infinity
        EmitTrapIf(s);
    }

    private static void EmitNaNFloatGuard(Stream s, int local)
    {
        EmitLocal(s, 0x20, local);
        EmitLocal(s, 0x20, local);
        s.WriteByte(0x5C); // f32.ne detects NaN while allowing an infinite divisor.
        EmitTrapIf(s);
    }

    private static bool IsFloatArithmeticOperand(EnumValueKind kind)
        => kind is EnumValueKind.Float or EnumValueKind.Int or EnumValueKind.Byte;

    private static void ConvertTopToFloat32(Stream s, EnumValueKind kind, int floatLocal, int integerLocal, string name)
    {
        if (kind == EnumValueKind.Float)
        {
            EmitLocal(s, 0x21, floatLocal);
            return;
        }

        PromoteTopToInt64(s, kind, name);
        EmitLocal(s, 0x21, integerLocal);
        EmitRangeGuard(s, integerLocal, kind == EnumValueKind.Byte ? 0 : int.MinValue,
            kind == EnumValueKind.Byte ? byte.MaxValue : int.MaxValue);
        EmitLocal(s, 0x20, integerLocal);
        s.WriteByte(0xB4); // f32.convert_i64_s; matches VM Float promotion to binary32.
        EmitLocal(s, 0x21, floatLocal);
    }

    private static void EmitTrapIf(Stream s)
    {
        s.WriteByte(0x04); s.WriteByte(0x40); // if (no result)
        s.WriteByte(0x00); // unreachable
        s.WriteByte(0x0B);
    }

    private static void EmitIntCompare(Stream s, Stack<EnumValueKind> stack, byte opcode, string name, int scratch)
    {
        if (stack.Count < 2) throw new InvalidOperationException($"Stack underflow at {name}.");
        var right = stack.Pop(); var left = stack.Pop();
        if (IsFloatArithmeticOperand(left) && IsFloatArithmeticOperand(right) &&
            (left == EnumValueKind.Float || right == EnumValueKind.Float))
        {
            EmitFloatComparison(s, left, right, opcode, scratch + 2);
            stack.Push(EnumValueKind.Bool);
            return;
        }
        if (left is not (EnumValueKind.Int or EnumValueKind.Byte or EnumValueKind.Char) ||
            right is not (EnumValueKind.Int or EnumValueKind.Byte or EnumValueKind.Char) ||
            left != right && (left == EnumValueKind.Char || right == EnumValueKind.Char))
            throw new InvalidOperationException($"{name} requires matching Int/Byte/Char or mixed Int/Byte operands, got {left} and {right}.");
        if (left != right)
        {
            EmitPromotedIntComparison(s, left, right, opcode, scratch, name);
            stack.Push(EnumValueKind.Bool);
            return;
        }
        // Byte and Char values are zero-extended, so relational operations use unsigned i32 comparisons.
        // Int comparisons use i64 opcodes; Byte/Char comparisons must use the corresponding i32 opcode.
        s.WriteByte((byte)(left == EnumValueKind.Int ? opcode : (opcode switch
        {
            0x53 => left == EnumValueKind.Char ? 0x49 : 0x48, // i32.lt_u / i32.lt_s
            0x57 => left == EnumValueKind.Char ? 0x4D : 0x4C, // i32.le_u / i32.le_s
            0x55 => left == EnumValueKind.Char ? 0x4B : 0x4A, // i32.gt_u / i32.gt_s
            0x59 => left == EnumValueKind.Char ? 0x4F : 0x4E, // i32.ge_u / i32.ge_s
            _ => throw new InvalidOperationException($"Invalid integer comparison opcode 0x{opcode:X2}.")
        })));
        stack.Push(EnumValueKind.Bool);
    }

    private static void EmitCompare(Stream s, Stack<EnumValueKind> stack, byte i64Opcode, byte i32Opcode, string name, int scratch)
    {
        if (stack.Count < 2) throw new InvalidOperationException($"Stack underflow at {name}.");
        var right = stack.Pop(); var left = stack.Pop();
        if (IsFloatArithmeticOperand(left) && IsFloatArithmeticOperand(right) &&
            (left == EnumValueKind.Float || right == EnumValueKind.Float))
        {
            if (left == EnumValueKind.Float && right == EnumValueKind.Float)
                s.WriteByte(name == "CmpEq" ? (byte)0x5B : (byte)0x5C);
            else
                EmitMixedFloatEquality(s, left, right, name == "CmpEq", scratch + 2);
            stack.Push(EnumValueKind.Bool);
            return;
        }
        if (left is not (EnumValueKind.Int or EnumValueKind.Bool or EnumValueKind.Byte or EnumValueKind.Char) ||
            right is not (EnumValueKind.Int or EnumValueKind.Bool or EnumValueKind.Byte or EnumValueKind.Char) ||
            left != right && (left != EnumValueKind.Int && left != EnumValueKind.Byte || right != EnumValueKind.Int && right != EnumValueKind.Byte))
            throw new NotSupportedException($"{name} requires matching Int, Bool, Byte, or Char operands in aloe2wasm v0.1.");
        if (left != right)
            EmitPromotedIntComparison(s, left, right, i64Opcode, scratch, name);
        else
            s.WriteByte(left == EnumValueKind.Int ? i64Opcode : i32Opcode);
        stack.Push(EnumValueKind.Bool);
    }

    private static void EmitFloatComparison(Stream s, EnumValueKind left, EnumValueKind right, byte opcode, int scratch)
    {
        if (left == EnumValueKind.Float && right == EnumValueKind.Float)
        {
            s.WriteByte(opcode switch
            {
                0x53 => 0x5D, 0x57 => 0x5F, 0x55 => 0x5E, 0x59 => 0x60,
                _ => throw new InvalidOperationException($"Invalid float comparison opcode 0x{opcode:X2}.")
            });
            return;
        }

        EmitMixedFloatCompare(s, left, right, opcode switch
        {
            0x53 => 0x63, 0x57 => 0x65, 0x55 => 0x64, 0x59 => 0x66,
            _ => throw new InvalidOperationException($"Invalid mixed float comparison opcode 0x{opcode:X2}.")
        }, scratch);
    }

    private static void EmitMixedFloatEquality(Stream s, EnumValueKind left, EnumValueKind right, bool equal, int scratch)
        => EmitMixedFloatCompare(s, left, right, equal ? (byte)0x61 : (byte)0x62, scratch);

    private static void EmitMixedFloatCompare(Stream s, EnumValueKind left, EnumValueKind right, byte opcode, int floatScratch)
    {
        var integerScratch = floatScratch - 2;
        var doubleScratch = floatScratch + 5;
        ConvertTopToFloat64(s, right, doubleScratch + 1, integerScratch + 1);
        ConvertTopToFloat64(s, left, doubleScratch, integerScratch);
        EmitLocal(s, 0x20, doubleScratch);
        EmitLocal(s, 0x20, doubleScratch + 1);
        s.WriteByte(opcode);
    }

    private static void ConvertTopToFloat64(Stream s, EnumValueKind kind, int doubleLocal, int integerLocal)
    {
        if (kind == EnumValueKind.Float)
        {
            s.WriteByte(0xBB); // f64.promote_f32
            EmitLocal(s, 0x21, doubleLocal);
            return;
        }

        PromoteTopToInt64(s, kind, "Float comparison");
        EmitLocal(s, 0x21, integerLocal);
        EmitRangeGuard(s, integerLocal, kind == EnumValueKind.Byte ? 0 : int.MinValue,
            kind == EnumValueKind.Byte ? byte.MaxValue : int.MaxValue);
        EmitLocal(s, 0x20, integerLocal);
        s.WriteByte(0xB9); // f64.convert_i64_s; int32 values remain exact.
        EmitLocal(s, 0x21, doubleLocal);
    }

    private static EnumValueKind PopIntegerOperand(Stack<EnumValueKind> stack, string name)
    {
        if (stack.Count == 0) throw new InvalidOperationException($"Stack underflow at {name}.");
        var kind = stack.Pop();
        if (kind is not (EnumValueKind.Int or EnumValueKind.Byte))
            throw new InvalidOperationException($"{name} requires Int or Byte operands, got {kind}.");
        return kind;
    }

    private static void PromoteTopToInt64(Stream s, EnumValueKind kind, string name)
    {
        if (kind == EnumValueKind.Byte) s.WriteByte(0xAD); // i64.extend_i32_u
        else if (kind != EnumValueKind.Int) throw new InvalidOperationException($"{name} requires Int or Byte operands, got {kind}.");
    }

    private static void EmitPromotedIntComparison(Stream s, EnumValueKind left, EnumValueKind right,
        byte opcode, int scratch, string name)
    {
        PromoteTopToInt64(s, right, name);
        EmitLocal(s, 0x21, scratch + 1);
        PromoteTopToInt64(s, left, name);
        EmitLocal(s, 0x21, scratch);
        EmitLocal(s, 0x20, scratch);
        EmitLocal(s, 0x20, scratch + 1);
        s.WriteByte(opcode);
    }

    private static void EnsureLocal(FunctionInfo f, int index)
    {
        if ((uint)index >= (uint)f.LocalTypes.Count) throw new IndexOutOfRangeException("Local index out of range.");
    }

    private static void RequirePop(Stack<EnumValueKind> stack, EnumValueKind expected, string op)
    {
        if (stack.Count == 0) throw new InvalidOperationException($"Stack underflow at {op}.");
        var actual = stack.Pop();
        if (actual != expected) throw new InvalidOperationException($"{op} expected {expected}, got {actual}.");
    }

    private static void TrimStack(Stack<EnumValueKind> stack, int height)
    {
        while (stack.Count > height) stack.Pop();
        if (stack.Count != height) throw new InvalidOperationException("Structured control stack height mismatch.");
    }

    private static byte ToWasmValueType(EnumValueKind kind) => kind switch
    {
        EnumValueKind.Int => 0x7E,  // i64
        EnumValueKind.Float => 0x7D, // f32
        EnumValueKind.Bool => 0x7F, // i32
        EnumValueKind.Byte => 0x7F, // i32, zero-extended byte
        EnumValueKind.Char => 0x7F, // i32, zero-extended UTF-16 code unit
        _ => throw new NotSupportedException($"Unsupported WASM value kind {kind}.")
    };

    private static void WriteSection(Stream output, byte id, Action<Stream> writePayload)
    {
        using var payload = new MemoryStream();
        writePayload(payload);
        output.WriteByte(id);
        WriteU32(output, (uint)payload.Length);
        payload.Position = 0;
        payload.CopyTo(output);
    }

    private static void WriteName(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteU32(s, (uint)bytes.Length); s.Write(bytes);
    }

    private static void WriteU32(Stream s, uint value)
    {
        do { var b = (byte)(value & 0x7F); value >>= 7; if (value != 0) b |= 0x80; s.WriteByte(b); } while (value != 0);
    }

    private static void WriteS32(Stream s, int value)
    {
        var more = true;
        while (more)
        {
            var b = (byte)(value & 0x7F); value >>= 7;
            var sign = (b & 0x40) != 0;
            more = !((value == 0 && !sign) || (value == -1 && sign));
            if (more) b |= 0x80; s.WriteByte(b);
        }
    }

    private static void WriteS64(Stream s, long value)
    {
        var more = true;
        while (more)
        {
            var b = (byte)(value & 0x7F); value >>= 7;
            var sign = (b & 0x40) != 0;
            more = !((value == 0 && !sign) || (value == -1 && sign));
            if (more) b |= 0x80; s.WriteByte(b);
        }
    }

    private static void WriteF32(Stream stream, float value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));
        stream.Write(bytes);
    }
}
