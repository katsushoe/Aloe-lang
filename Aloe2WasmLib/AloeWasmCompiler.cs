using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using System.Text;

namespace Aloe.Aloe2Wasm;

/// <summary>
/// Minimal AloeBC -> WebAssembly backend for the v0.1 core subset.
/// Supported values: Int(i64), Bool(i32). Supported control flow is structured.
/// </summary>
public sealed class AloeWasmCompiler
{
    public byte[] Compile(Module module)
    {
        ArgumentNullException.ThrowIfNull(module);
        ValidateModule(module);

        using var output = new MemoryStream();
        output.Write(new byte[] { 0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00 });

        WriteSection(output, 1, section => WriteTypeSection(section, module));
        WriteSection(output, 3, section => WriteFunctionSection(section, module));
        WriteSection(output, 7, section => WriteExportSection(section, module));
        WriteSection(output, 10, section => WriteCodeSection(section, module));
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
        if (kind is not (EnumValueKind.Int or EnumValueKind.Bool))
            throw new NotSupportedException($"aloe2wasm v0.1 does not support {kind} in {where}.");
    }

    private static void WriteTypeSection(Stream s, Module module)
    {
        WriteU32(s, (uint)module.Functions.Count);
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
    }

    private static void WriteFunctionSection(Stream s, Module module)
    {
        WriteU32(s, (uint)module.Functions.Count);
        for (var i = 0; i < module.Functions.Count; i++)
            WriteU32(s, (uint)i);
    }

    private static void WriteExportSection(Stream s, Module module)
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
            WriteU32(s, (uint)export.Index);
        }
    }

    private static void WriteCodeSection(Stream s, Module module)
    {
        WriteU32(s, (uint)module.Functions.Count);
        for (var functionIndex = 0; functionIndex < module.Functions.Count; functionIndex++)
        {
            using var body = new MemoryStream();
            var function = module.Functions[functionIndex];
            WriteLocals(body, function);
            EmitFunctionBody(body, module, functionIndex);
            body.WriteByte(0x0B); // function end

            WriteU32(s, (uint)body.Length);
            body.Position = 0;
            body.CopyTo(s);
        }
    }

    private static void WriteLocals(Stream s, FunctionInfo f)
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

        WriteU32(s, (uint)groups.Count);
        foreach (var g in groups)
        {
            WriteU32(s, g.Count);
            s.WriteByte(g.Type);
        }
    }

    private static void EmitFunctionBody(Stream s, Module module, int functionIndex)
    {
        var function = module.Functions[functionIndex];
        var start = function.EntryIp;
        var end = functionIndex + 1 < module.Functions.Count
            ? module.Functions[functionIndex + 1].EntryIp
            : module.Code.Count;

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
                case EnumOpcode.Add: EmitIntBinary(s, stack, 0x7C, "Add"); break;
                case EnumOpcode.Sub: EmitIntBinary(s, stack, 0x7D, "Sub"); break;
                case EnumOpcode.Mul: EmitIntBinary(s, stack, 0x7E, "Mul"); break;
                case EnumOpcode.Div: EmitIntBinary(s, stack, 0x7F, "Div"); break;
                case EnumOpcode.Mod: EmitIntBinary(s, stack, 0x81, "Mod"); break;
                case EnumOpcode.CmpEq: EmitCompare(s, stack, 0x51, 0x46, "CmpEq"); break;
                case EnumOpcode.CmpNe: EmitCompare(s, stack, 0x52, 0x47, "CmpNe"); break;
                case EnumOpcode.CmpLt: EmitIntCompare(s, stack, 0x53, "CmpLt"); break;
                case EnumOpcode.CmpLe: EmitIntCompare(s, stack, 0x57, "CmpLe"); break;
                case EnumOpcode.CmpGt: EmitIntCompare(s, stack, 0x55, "CmpGt"); break;
                case EnumOpcode.CmpGe: EmitIntCompare(s, stack, 0x59, "CmpGe"); break;
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
                    EmitCall(s, module, ins.Operand0, stack); break;
                case EnumOpcode.Return:
                    if (function.ReturnType is { } rt) RequirePop(stack, rt, "Return");
                    s.WriteByte(0x0F); break;
                case EnumOpcode.Halt:
                    // HALT is VM-specific. At function scope it lowers to return.
                    s.WriteByte(0x0F); break;
                case EnumOpcode.Syscall:
                    throw new NotSupportedException("aloe2wasm v0.1 does not yet lower Syscall; runtime imports are pending.");
                default:
                    throw new NotSupportedException($"aloe2wasm v0.1 does not support opcode {ins.Opcode}.");
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
            default:
                throw new NotSupportedException($"aloe2wasm v0.1 does not support {value.Kind} constants.");
        }
    }

    private static void EmitCall(Stream s, Module module, int index, Stack<EnumValueKind> stack)
    {
        if ((uint)index >= (uint)module.Functions.Count) throw new IndexOutOfRangeException("Function index out of range.");
        var callee = module.Functions[index];
        for (var i = callee.ParameterTypes.Count - 1; i >= 0; i--)
            RequirePop(stack, callee.ParameterTypes[i], "Call");
        s.WriteByte(0x10); WriteU32(s, (uint)index);
        if (callee.ReturnType is { } rt) stack.Push(rt);
    }

    private static void EmitIntBinary(Stream s, Stack<EnumValueKind> stack, byte opcode, string name)
    {
        RequirePop(stack, EnumValueKind.Int, name); RequirePop(stack, EnumValueKind.Int, name);
        s.WriteByte(opcode); stack.Push(EnumValueKind.Int);
    }

    private static void EmitIntCompare(Stream s, Stack<EnumValueKind> stack, byte opcode, string name)
    {
        RequirePop(stack, EnumValueKind.Int, name); RequirePop(stack, EnumValueKind.Int, name);
        s.WriteByte(opcode); stack.Push(EnumValueKind.Bool);
    }

    private static void EmitCompare(Stream s, Stack<EnumValueKind> stack, byte i64Opcode, byte i32Opcode, string name)
    {
        if (stack.Count < 2) throw new InvalidOperationException($"Stack underflow at {name}.");
        var right = stack.Pop(); var left = stack.Pop();
        if (left != right || left is not (EnumValueKind.Int or EnumValueKind.Bool))
            throw new NotSupportedException($"{name} requires matching Int or Bool operands in aloe2wasm v0.1.");
        s.WriteByte(left == EnumValueKind.Int ? i64Opcode : i32Opcode);
        stack.Push(EnumValueKind.Bool);
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
        EnumValueKind.Bool => 0x7F, // i32
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
}
