using Aloe.Aloe2Wasm;
using Aloe.CommonLib;
using Aloe.CommonLib.Constants;

namespace Aloe.CompilerLib.Tests;

public sealed class Aloe2WasmTests
{
    [Test]
    public void Compile_IntFunction_ProducesWasmModule()
    {
        var module = new Module(
            constants: new[] { AloeValue.FromInt(40), AloeValue.FromInt(2) },
            code: new[]
            {
                new Instruction(EnumOpcode.PushConst, 0),
                new Instruction(EnumOpcode.PushConst, 1),
                new Instruction(EnumOpcode.Add),
                new Instruction(EnumOpcode.Return),
            },
            functions: new[]
            {
                new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Int)
            },
            entryPointIndex: 0);

        var wasm = new AloeWasmCompiler().Compile(module);

        Assert.That(wasm.Take(8).ToArray(), Is.EqualTo(new byte[] { 0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00 }));
        Assert.That(wasm.Length, Is.GreaterThan(8));
    }

    [Test]
    public void Compile_StringConstant_IsRejectedExplicitly()
    {
        var module = new Module(
            constants: new[] { AloeValue.FromString("hello") },
            code: new[] { new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.Halt) },
            functions: new[]
            {
                new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), null)
            },
            entryPointIndex: 0);

        Assert.Throws<NotSupportedException>(() => new AloeWasmCompiler().Compile(module));
    }
}
