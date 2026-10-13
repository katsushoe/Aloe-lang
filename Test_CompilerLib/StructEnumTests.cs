using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class StructEnumTests
{
    [Test]
    public void StructEnums_DocumentedFieldsConstructorsMethodsAndGettersKeepEnumTypes()
    {
        const string source = """
enum Color { Red, Blue }
bitfield enum Feature { Read, Write }
struct Options {
    field color: Color; field features: Feature;
    construct(color: Color, features: Feature) { this.color = color; this.features = features; }
    public property Color: Color { get { return this.color; } }
    public property Features: Feature { get { return this.features; } }
    method add(value: Feature): Feature { return this.features | value; }
    method choose(value: Color): Color { return value; }
}
function main(args: string[]): int {
    var original = new Options(Color.Red, Feature.Read);
    var copy = original;
    with (copy) {
        .color = Color.Blue;
        .features = .add(Feature.Write);
        if (.Color != Color.Blue or .Features != (Feature.Read | Feature.Write) or .choose(Color.Red) != Color.Red) { return 0; }
    }
    if (original.Color == Color.Red and original.Features == Feature.Read) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo(42));
    }

    [Test]
    public void StructEnums_ForwardTypesNestedCopiesAndSignedInt32BoundariesSurviveRoundTrip()
    {
        const string source = """
struct Limits {
    field number: Number; field mask: Flags;
    construct(number: Number, mask: Flags) { this.number = number; this.mask = mask; }
    public property Number: Number { get { return this.number; } }
    public property Mask: Flags { get { return this.mask; } }
    method withNumber(value: Number): Limits { return Limits { number: value, mask: this.mask }; }
}
struct Nested { field value: Limits; }
enum Number { Low = -2147483648, High = 2147483647 }
bitfield enum Flags { One = 1, High = 1 << 31, All = -1 }
function change(value: Nested): Nested { value.value.number = Number.High; return value; }
function main(args: string[]): int {
    var original = Nested { value: new Limits(Number.Low, Flags.One) };
    var copy = change(original);
    with (copy) {
        .value.mask = .value.Mask << 31;
        if (.value.Mask != Flags.High or (.value.Mask >> 31) != Flags.All or
            .value.Number != Number.High or .value.withNumber(Number.Low).Number != Number.Low) { return 0; }
    }
    GC.require(); GC.finish();
    if (original.value.number == Number.Low and original.value.mask == Flags.One and copy.value.mask == Flags.High) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo(42));
    }

    [Test]
    public void StructEnums_ClassStorageAndQueuedArgumentsKeepIndependentSnapshots()
    {
        const string source = """
enum Color { Red, Blue }
bitfield enum Flags { Read, Write }
struct Options { field color: Color; field flags: Flags; }
class Box {
    field options: Options;
    construct(options: Options) { this.options = options; }
    public property Snapshot: Options { get { return this.options; } }
    public async method save(options: Options): void { this.options = options; }
}
function main(args: string[]): int {
    var original = Options { color: Color.Red, flags: Flags.Read };
    var box = new Box(original);
    original.color = Color.Blue;
    box.save(original);
    original.flags = original.flags | Flags.Write;
    var before = box.Snapshot;
    GC.require(); GC.finish(); tick();
    var after = box.Snapshot;
    after.color = Color.Red;
    var stored = box.Snapshot;
    if (before.color == Color.Red and before.flags == Flags.Read and stored.color == Color.Blue and
        stored.flags == Flags.Read and original.flags == (Flags.Read | Flags.Write) and after.color == Color.Red) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo(42));
    }

    [Test]
    public void StructEnums_RejectIntegersOtherEnumTypesAndInvalidReturns()
    {
        const string declarations = """
enum Color { Red, Blue }
enum Other { Red, Blue }
bitfield enum Flags { Read, Write }
bitfield enum OtherFlags { Read, Write }
struct Options {
    field color: Color; field flags: Flags;
    construct(color: Color, flags: Flags) { this.color = color; this.flags = flags; }
    public property Color: Color { get { return this.color; } }
    public property Flags: Flags { get { return this.flags; } }
    method choose(value: Color): Color { return value; }
    method add(value: Flags): Flags { return this.flags | value; }
}
""";
        Assert.DoesNotThrow(() => new AloeCompiler().Compile(declarations + "function main(args: string[]): int { return 0; }"));
        string[] statements =
        [
            "var invalid = Options { color: 0, flags: Flags.Read };",
            "var invalid = Options { color: Other.Red, flags: Flags.Read };",
            "var invalid = Options { color: Color.Red, flags: OtherFlags.Read };",
            "var invalid = new Options(0, Flags.Read);",
            "var invalid = new Options(Other.Red, Flags.Read);",
            "var invalid = new Options(Color.Red, Color.Red);",
            "value.color = 0;",
            "value.color = Other.Red;",
            "value.flags = OtherFlags.Read;",
            "value.flags = value.flags | Color.Red;",
            "var answer = value.choose(0);",
            "var answer = value.choose(Other.Red);",
            "var answer = value.add(OtherFlags.Read);",
            "let answer: int = value.Color;",
            "let answer: Other = value.Color;",
            "let answer: OtherFlags = value.Flags;",
            "with (value) { .color = Other.Red; }",
            "with (value) { var answer = .add(1); }",
            "with (value) { let answer: Other = .choose(Color.Red); }",
            "var answer = value.Color == Other.Red;",
            "var answer = value.Flags == OtherFlags.Read;"
        ];
        foreach (var statement in statements)
        {
            var source = declarations + "function main(args: string[]): int { var value = new Options(Color.Red, Flags.Read); " + statement + " return 0; }";
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source), statement);
        }
        string[] invalidMembers =
        [
            "field color: Color; construct(value: Other) { this.color = value; }",
            "method wrong(value: Color): Other { return value; }",
            "method wrong(value: Color): int { return value; }",
            "public property Wrong: Color { get { return Other.Red; } }",
            "public property Wrong: Color { get { return 0; } }",
            "field flags: Flags; public property Wrong: OtherFlags { get { return this.flags; } }"
        ];
        foreach (var member in invalidMembers)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + "struct Invalid { " + member + " } function main(args: string[]): int { return 0; }"), member);
    }

    private static long Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return vm.Pop().AsInt;
    }
}
