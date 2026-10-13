using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class PrimitiveArrayTests
{
    [Test]
    public void PrimitiveArrays_DocumentedByteCharFloatDecimalExample()
    {
        const string source = """
function main(args: string[]): int {
    let bytes: byte[] = [0, 255];
    var chars = ['a', 'z'];
    var floats = [1.5, 2.5];
    var decimals = [0.1:d, 0.2:d];
    bytes[0] = 42;
    chars[0] = 'b';
    var total = 0;
    foreach (value in bytes) { if (value == 42) { total = total + value; } }
    if (chars[0] == 'b' and floats[1] == 2.5 and decimals[0] + decimals[1] == 0.3:d) { return total; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void PrimitiveArrays_EmptyContextsByteBoundariesAndFunctionAliasesPreserveElementTypes()
    {
        const string source = """
function byteValue(value: byte): byte { return value; }
function echo(values: decimal[]): decimal[] { return values; }
function chars(): char[] { return ['a', 'z']; }
function main(args: string[]): int {
    let bytes: byte[] = [];
    bytes = [0, 255]; var alias = bytes; alias[0] = 42;
    var inferred = [byteValue(1), byteValue(255)]; inferred[0] = 0;
    let emptyChars: char[] = []; let emptyFloats: float[] = []; let emptyDecimals: decimal[] = [];
    foreach (value in emptyChars) { return 0; }
    foreach (value in emptyFloats) { return 0; }
    foreach (value in emptyDecimals) { return 0; }
    var original = [0.1:d, 0.2:d]; var shared = echo(original); shared[0] = 0.3:d;
    var count = 0; foreach (value in chars()) { count = count + 1; }
    GC.require(); GC.finish();
    if (bytes[0] == 42 and bytes[1] == 255 and inferred[0] == 0 and inferred[1] == 255 and
        original[0] == 0.3:d and chars()[1] == 'z' and count == 2 and emptyFloats.Length == 0) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void PrimitiveArrays_FloatClassFieldsAndTickReadsKeepSharedCurrentAndCommittedValues()
    {
        const string source = """
class Box {
    field values: float[];
    construct(values: float[]) { this.values = values; }
    public property First: float { get { return this.values[0]; } }
    public async method update(query: Box): void {
        var values = this.values; values[0] = 2.5; print(query.First); print(values[0]);
    }
}
function main(args: string[]): int {
    var values = [1.5]; var box = new Box(values); box.update(box);
    GC.require(); GC.finish(); tick();
    if (box.First == 2.5 and values[0] == 2.5) { return 42; }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1.5\n2.5\n"));
    }

    [Test]
    public void PrimitiveArrays_RejectMixedKindsWrongArraysInvalidByteContextsAndUnsupportedReferences()
    {
        string[] statements =
        [
            "let values: byte[] = [256];", "let values: byte[] = [-1];", "let values: byte[] = [1 + 2];",
            "let values: byte[] = [0]; values[0] = 256;", "let values: byte[] = [0]; let number: int = 1; values[0] = number;",
            "let values: char[] = [1];", "var values = ['a']; values[0] = true;",
            "let values: float[] = [1];", "var values = [1.5]; values[0] = 1;",
            "let values: decimal[] = [1.0];", "var values = [0.1:d]; values[0] = 1.0;",
            "var values = [0.1:d, 1.0];", "var values = ['a', false];", "var values = [1, 1.5];",
            "var ints = [1]; let bytes: byte[] = ints;", "var floats = [1.0]; let decimals: decimal[] = floats;",
            "let bytes: byte[] = [1]; var result = bytes[0] << 1;",
            "let values: float[][] = [[true]];", "let values: string[] = [1];",
            "foreach (value in [1.5]) { let number: int = value; }"
        ];
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("function main(args: string[]): int { " + statement + " return 0; }"), statement);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm("function main(args: string[]): int { var values = [1.5]; return 0; }"));
    }

    [Test]
    public void PrimitiveArrays_RuntimeRejectsWrongKindsAndNonFiniteFloatCreationAndWrites()
    {
        foreach (var name in new[] { "byte[]", "char[]", "float[]", "decimal[]" })
        {
            var vm = CreateVm(new AloeCompiler().Compile("function main(args: string[]): int { return 0; }"));
            vm.Push(AloeValue.FromInt(1)); vm.Push(AloeValue.FromString(name)); vm.Push(AloeValue.FromInt(1));
            Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayCreate), name);
            Assert.That(vm.Heap.ObjectTable.Count, Is.EqualTo(0));
        }
        foreach (var number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue })
        {
            var vm = CreateVm(new AloeCompiler().Compile("function main(args: string[]): int { return 0; }"));
            vm.Push(AloeValue.FromFloat(number)); vm.Push(AloeValue.FromString("float[]")); vm.Push(AloeValue.FromInt(1));
            Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayCreate), number.ToString());
            Assert.That(vm.Heap.ObjectTable.Count, Is.EqualTo(0));
        }
        var valid = CreateVm(new AloeCompiler().Compile("function main(args: string[]): int { return 0; }"));
        valid.Push(AloeValue.FromFloat(1.25)); valid.Push(AloeValue.FromString("float[]")); valid.Push(AloeValue.FromInt(1));
        valid.InvokeSyscall(EnumSyscall.ArrayCreate); var array = valid.Pop();
        valid.Push(array); valid.Push(AloeValue.FromInt(0)); valid.Push(AloeValue.FromFloat(double.NaN));
        Assert.Throws<VmException>(() => valid.InvokeSyscall(EnumSyscall.ArraySet));
        valid.Push(array); valid.Push(AloeValue.FromInt(0)); valid.InvokeSyscall(EnumSyscall.ArrayGet);
        Assert.That(valid.Pop().AsFloat, Is.EqualTo(1.25));
    }

    private static AloeVm CreateVm(Module module)
    {
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        return new AloeVm(module, settings);
    }

    private static (long ExitCode, string Output) Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var output = new StringBuilder(); var vm = CreateVm(module);
        vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
