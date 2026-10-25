using Aloe.CommonLib.Exceptions;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using NUnit.Framework;

namespace Aloe.CompilerLib.Tests
{
    [TestFixture]
    public sealed class PipeTests
    {
        [Test]
        public void BufferedPipe_ForeachConsumesValues()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    source.write(10);
    source.write(20);
    source.close();

    var sum = 0;
    foreach (value in source) {
        sum = sum + value;
    }
    return sum;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();

            Assert.That(vm.Pop().AsInt, Is.EqualTo(30));
        }

        [Test]
        public void PipeWrite_TypeMismatch_IsCompileError()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    source.write("bad");
    return 0;
}
""";

            Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
        }

        [Test]
        public void PipeWrite_AfterClose_IsVmError()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    source.close();
    source.write(1);
    return 0;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<VmException>());
        }

        [Test]
        public void Pipeline_FilterRunsAfterSourceClose()
        {
            const string source = """
filter doubleValues {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) {
            output.write(value * 2);
        }
        output.close();
    }
}

function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source | filter(doubleValues) | result;
    source.write(3);
    source.write(4);
    source.close();
    var sum = 0;
    foreach (value in result) {
        sum = sum + value;
    }
    return sum;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(14));
        }

        [Test]
        public void Pipeline_FilterShorthandAcceptsUserDefinedFilter()
        {
            const string source = """
filter addOne {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value + 1); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source |: addOne | result;
    source.write(40); source.close();
    var sum = 0;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";
            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(41));
        }

        [Test]
        public void Pipeline_StandardToLowerUsesInvariantCaseMappingAndClosesOutput()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create();
    source |: tolower | result;
    source.write("AlOe"); source.write("I"); source.close();
    var matched = 0;
    foreach (value in result) {
        if (value == "aloe") { matched = matched + 1; }
        if (value == "i") { matched = matched + 1; }
    }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
                vm.RunFromEntryPoint();
                Assert.That(vm.Pop().AsInt, Is.EqualTo(2));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = originalCulture;
            }
        }

        [Test]
        public void Pipeline_StandardToUpperUsesInvariantCaseMapping()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create();
    source |: toupper | result;
    source.write("AlOe"); source.close();
    var matched = 0;
    foreach (value in result) { if (value == "ALOE") { matched = matched + 1; } }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(1));
        }

        [Test]
        public void Pipeline_StandardTrimFiltersRemoveLeadingTrailingAndBothSides()
        {
            const string source = """
function main(args: string[]): int {
    let leftInput: pipe<string> = pipe<string>.create();
    let leftOutput: pipe<string> = pipe<string>.create();
    let rightInput: pipe<string> = pipe<string>.create();
    let rightOutput: pipe<string> = pipe<string>.create();
    let bothInput: pipe<string> = pipe<string>.create();
    let bothOutput: pipe<string> = pipe<string>.create();
    leftInput |: ltrim | leftOutput;
    rightInput |: rtrim | rightOutput;
    bothInput |: trim | bothOutput;
    leftInput.write("  Aloe  "); leftInput.close();
    rightInput.write("  Aloe  "); rightInput.close();
    bothInput.write("  Aloe  "); bothInput.close();
    var matched = 0;
    foreach (value in leftOutput) { if (value == "Aloe  ") { matched = matched + 1; } }
    foreach (value in rightOutput) { if (value == "  Aloe") { matched = matched + 1; } }
    foreach (value in bothOutput) { if (value == "Aloe") { matched = matched + 1; } }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(3));
        }

        [Test]
        public void Pipeline_StandardLineSplitHandlesChunkBoundariesCrLfEmptyLinesAndTail()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create(1);
    source |: lineSplit | result;
    source.write("one\r"); source.write("\ntwo\n\nlast"); source.close();
    var matched = 0;
    foreach (value in result) {
        if (value == "one") { matched = matched + 1; }
        if (value == "two") { matched = matched + 1; }
        if (value == "") { matched = matched + 1; }
        if (value == "last") { matched = matched + 1; }
    }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(4));
        }

        [Test]
        public void Pipeline_ParameterizedSubstringFiltersMatchAndReturnSubstringAcrossBoundedOutput()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create(1);
    source |: startswith: "ERROR" |: contains: "detail=" |: strstr: "detail=" | result;
    source.write("ERROR detail=first");
    source.write("INFO detail=ignored");
    source.write("ERROR message=ignored");
    source.write("ERROR detail=second");
    source.close();
    var matched = 0;
    foreach (value in result) {
        if (value == "detail=first") { matched = matched + 1; }
        if (value == "detail=second") { matched = matched + 1; }
    }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(2));
        }

        [Test]
        public void Pipeline_ParameterizedSubstringFilterRequiresStringArgument()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create();
    source |: contains | result;
    return 0;
}
""";
            Assert.That(() => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>().With.Message.Contains("Filter 'contains' requires a string argument"));
        }

        [Test]
        public void Pipeline_JoinConcatenatesInputAndEmitsOneValueToBoundedOutput()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create(1);
    source |: join: "," | result;
    source.write("one"); source.write("two"); source.write("three"); source.close();
    var matched = 0;
    foreach (value in result) { if (value == "one,two,three") { matched = matched + 1; } }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(1));
        }

        [Test]
        public void Pipeline_JoinOnEmptyInputEmitsEmptyString()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create(1);
    source |: join: "," | result;
    source.close();
    var matched = 0;
    foreach (value in result) { if (value == "") { matched = matched + 1; } }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(1));
        }

        [Test]
        public void Pipeline_CaseStyleFiltersNormalizeWhitespaceSeparatedWords()
        {
            const string source = """
function main(args: string[]): int {
    let camelInput: pipe<string> = pipe<string>.create();
    let camelOutput: pipe<string> = pipe<string>.create();
    let pascalInput: pipe<string> = pipe<string>.create();
    let pascalOutput: pipe<string> = pipe<string>.create();
    let snakeInput: pipe<string> = pipe<string>.create();
    let snakeOutput: pipe<string> = pipe<string>.create();
    camelInput |: camelcase | camelOutput;
    pascalInput |: pascalcase | pascalOutput;
    snakeInput |: snakecase | snakeOutput;
    camelInput.write("  hello   world  "); camelInput.close();
    pascalInput.write("hello world"); pascalInput.close();
    snakeInput.write("Hello World"); snakeInput.close();
    var matched = 0;
    foreach (value in camelOutput) { if (value == "helloWorld") { matched = matched + 1; } }
    foreach (value in pascalOutput) { if (value == "HelloWorld") { matched = matched + 1; } }
    foreach (value in snakeOutput) { if (value == "hello_world") { matched = matched + 1; } }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(3));
        }

        [Test]
        public void Pipeline_QuoteMetaEscapesRegexMetacharacters()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create();
    source |: quote_meta | result;
    source.write("a.b[c]+d"); source.close();
    var matched = 0;
    foreach (value in result) { if (value == "a\\.b\\[c]\\+d") { matched = matched + 1; } }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(1));
        }

        [Test]
        public void Pipeline_BinToHexEmitsUppercaseHexForTheCompleteByteStream()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create();
    let result: pipe<string> = pipe<string>.create(1);
    source |: bin2hex | result;
    let first: byte = 1;
    let second: byte = 15;
    source.write(first); source.write(second); source.close();
    var matched = 0;
    foreach (value in result) { if (value == "010F") { matched = matched + 1; } }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(1));
        }

        [Test]
        public void Pipeline_BinToHexOnEmptyInputEmitsEmptyString()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create();
    let result: pipe<string> = pipe<string>.create();
    source |: bin2hex | result;
    source.close();
    var matched = 0;
    foreach (value in result) { if (value == "") { matched = matched + 1; } }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(1));
        }

        [Test]
        public void Pipeline_Base64EncodeConsumesWholeStreamAndHandlesEmptyInput()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    source |: base64encode | result;
    let first: byte = 72;
    let second: byte = 105;
    source.write(first); source.write(second); source.close();
    var matched = 0;
    foreach (value in result) { if (value == "SGk=") { matched = matched + 1; } }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(1));

            const string emptySource = """
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create();
    let result: pipe<string> = pipe<string>.create();
    source |: base64encode | result;
    source.close();
    var matched = 0;
    foreach (value in result) { if (value == "") { matched = matched + 1; } }
    return matched;
}
""";
            var emptyVm = new AloeVm(new AloeCompiler().Compile(emptySource));
            emptyVm.RunFromEntryPoint();
            Assert.That(emptyVm.Pop().AsInt, Is.EqualTo(1));
        }

        [Test]
        public void Pipeline_Base64DecodeHandlesChunkedAndEmptyInputAndRejectsInvalidData()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create(1);
    let result: pipe<byte> = pipe<byte>.create(1);
    source |: base64decode | result;
    source.write("S"); source.write("Gk"); source.write("="); source.close();
    var sum = 0;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(177));

            const string emptySource = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<byte> = pipe<byte>.create();
    source |: base64decode | result;
    source.close();
    var count = 0;
    foreach (value in result) { count = count + 1; }
    return count;
}
""";
            var emptyVm = new AloeVm(new AloeCompiler().Compile(emptySource));
            emptyVm.RunFromEntryPoint();
            Assert.That(emptyVm.Pop().AsInt, Is.EqualTo(0));

            const string invalidSource = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<byte> = pipe<byte>.create();
    source |: base64decode | result;
    source.write("not base64"); source.close();
    foreach (value in result) { }
    return 0;
}
""";
            var invalidVm = new AloeVm(new AloeCompiler().Compile(invalidSource));
            Assert.Throws<VmException>(() => invalidVm.RunFromEntryPoint());

        }

        [Test]
        public void Pipeline_Utf8DecodesSplitCodePointsStrictlyAndHandlesEmptyInput()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    source |: utf8 | result;
    let first: byte = 227;
    let second: byte = 129;
    let third: byte = 130;
    source.write(first); source.write(second); source.write(third); source.close();
    var matched = 0;
    foreach (value in result) { if (value == "あ") { matched = matched + 1; } }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(1));

            const string emptySource = """
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create();
    let result: pipe<string> = pipe<string>.create();
    source |: utf8 | result;
    source.close();
    var matched = 0;
    foreach (value in result) { if (value == "") { matched = matched + 1; } }
    return matched;
}
""";
            var emptyVm = new AloeVm(new AloeCompiler().Compile(emptySource));
            emptyVm.RunFromEntryPoint();
            Assert.That(emptyVm.Pop().AsInt, Is.EqualTo(1));

            const string invalidSource = """
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create();
    let result: pipe<string> = pipe<string>.create();
    source |: utf8 | result;
    let invalidByte: byte = 255;
    source.write(invalidByte); source.close();
    foreach (value in result) { }
    return 0;
}
""";
            var invalidVm = new AloeVm(new AloeCompiler().Compile(invalidSource));
            Assert.Throws<VmException>(() => invalidVm.RunFromEntryPoint());

            const string invalidUtf8Source = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create();
    source |: urldecode | result;
    source.write("%FF"); source.close();
    foreach (value in result) { }
    return 0;
}
""";
            var invalidUtf8Vm = new AloeVm(new AloeCompiler().Compile(invalidUtf8Source));
            Assert.Throws<VmException>(() => invalidUtf8Vm.RunFromEntryPoint());
        }

        [Test]
        public void Pipeline_UrlComponentFiltersUseUtf8PercentEscapesAndPreservePlus()
        {
            const string encodeSource = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    source |: urlencode | result;
    source.write("a b/é%~"); source.close();
    var matched = 0;
    foreach (value in result) { if (value == "a%20b%2F%C3%A9%25~") { matched = matched + 1; } }
    return matched;
}
""";
            var encodeVm = new AloeVm(new AloeCompiler().Compile(encodeSource));
            encodeVm.RunFromEntryPoint();
            Assert.That(encodeVm.Pop().AsInt, Is.EqualTo(1));

            const string decodeSource = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    source |: urldecode | result;
    source.write("a+b%2Bc"); source.close();
    var matched = 0;
    foreach (value in result) { if (value == "a+b+c") { matched = matched + 1; } }
    return matched;
}
""";
            var decodeVm = new AloeVm(new AloeCompiler().Compile(decodeSource));
            decodeVm.RunFromEntryPoint();
            Assert.That(decodeVm.Pop().AsInt, Is.EqualTo(1));

            const string invalidSource = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create();
    source |: urldecode | result;
    source.write("bad%2"); source.close();
    foreach (value in result) { }
    return 0;
}
""";
            var invalidVm = new AloeVm(new AloeCompiler().Compile(invalidSource));
            Assert.Throws<VmException>(() => invalidVm.RunFromEntryPoint());
        }

        [Test]
        public void Pipeline_Sha256HashesCompleteByteStreamAndComposesWithBin2Hex()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    source |: sha256 |: bin2hex | result;
    let a: byte = 97;
    let b: byte = 98;
    let c: byte = 99;
    source.write(a); source.write(b); source.write(c); source.close();
    var matched = 0;
    foreach (value in result) {
        if (value == "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")
            { matched = matched + 1; }
    }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(1));

            const string emptySource = """
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create();
    let result: pipe<string> = pipe<string>.create();
    source |: sha256 |: bin2hex | result;
    source.close();
    var matched = 0;
    foreach (value in result) {
        if (value == "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")
            { matched = matched + 1; }
    }
    return matched;
}
""";
            var emptyVm = new AloeVm(new AloeCompiler().Compile(emptySource));
            emptyVm.RunFromEntryPoint();
            Assert.That(emptyVm.Pop().AsInt, Is.EqualTo(1));
        }

        [Test]
        public void Pipeline_Sha1HashesCompleteByteStreamAndComposesWithBin2Hex()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    source |: sha1 |: bin2hex | result;
    let a: byte = 97;
    let b: byte = 98;
    let c: byte = 99;
    source.write(a); source.write(b); source.write(c); source.close();
    var matched = 0;
    foreach (value in result) {
        if (value == "A9993E364706816ABA3E25717850C26C9CD0D89D") { matched = matched + 1; }
    }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(1));

            const string emptySource = """
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create();
    let result: pipe<string> = pipe<string>.create();
    source |: sha1 |: bin2hex | result;
    source.close();
    var matched = 0;
    foreach (value in result) {
        if (value == "DA39A3EE5E6B4B0D3255BFEF95601890AFD80709") { matched = matched + 1; }
    }
    return matched;
}
""";
            var emptyVm = new AloeVm(new AloeCompiler().Compile(emptySource));
            emptyVm.RunFromEntryPoint();
            Assert.That(emptyVm.Pop().AsInt, Is.EqualTo(1));
        }

        [TestCase("sha1", "84983E441C3BD26EBAAE4AA1F95129E5E54670F1")]
        [TestCase("sha256", "248D6A61D20638B8E5C026930C3E6039A33CE45964FF2167F6ECEDD419DB06C1")]
        public void Pipeline_HashesMultiBlockBoundaryVector(string filter, string expectedDigest)
        {
            const string input = "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq";
            var writes = new System.Text.StringBuilder();
            var bytes = System.Text.Encoding.ASCII.GetBytes(input);
            for (var i = 0; i < bytes.Length; i++)
                writes.Append($"let byte{i}: byte = {bytes[i]}; source.write(byte{i});\n");

            var source = $$"""
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    source |: {{filter}} |: bin2hex | result;
    {{writes}}
    source.close();
    var matched = 0;
    foreach (value in result) { if (value == "{{expectedDigest}}") { matched = matched + 1; } }
    return matched;
}
""";

            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(1));
        }

        [TestCase("md4", null)]
        [TestCase("sha1", "C4D4B30851182FC4EB8675494D42FD7F17E29C93")]
        [TestCase("sha256", "6D1CF22D7CC09B085DFC25EE1A1F3AE0265804C607BC2074AD253BCC82FD81EE")]
        public void Pipeline_HashesLargeStreamThroughBoundedPipes(string filter, string? expectedDigest)
        {
            var digestPipeType = expectedDigest == null ? "pipe<byte>" : "pipe<string>";
            var hashPipeline = expectedDigest == null
                ? $"bytes |: {filter} | digest;"
                : $"bytes |: {filter} |: bin2hex | digest;";
            var expectedCondition = expectedDigest == null ? "true" : $"value == \"{expectedDigest}\"";
            var expectedCount = expectedDigest == null ? 16 : 1;
            var source = $$"""
function main(args: string[]): int {
    let bytes: pipe<byte> = pipe<byte>.create(1);
    let digest: {{digestPipeType}} = {{digestPipeType}}.create(1);
    {{hashPipeline}}
    var index = 0;
    while (index < 100000) {
        let value: byte = 97;
        bytes.write(value);
        index = index + 1;
    }
    bytes.close();
    var matched = 0;
    foreach (value in digest) {
        if ({{expectedCondition}}) { matched = matched + 1; }
    }
    return matched;
}
""";

            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(expectedCount));
        }

        [TestCase("", "31D6CFE0D16AE931B73C59D7E0C089C0")]
        [TestCase("a", "BDE52CB31DE33E46245E05FBDBD6FB24")]
        [TestCase("abc", "A448017AAF21D8525FC10AE87AA6729D")]
        [TestCase("message digest", "D9130A8164549FE818874806E1C7014B")]
        [TestCase("abcdefghijklmnopqrstuvwxyz", "D79E1C308AA5BBCDEEA8ED63DF412DA9")]
        [TestCase("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "043F8582F241DB351CE627E153E7F0E4")]
        [TestCase("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "E33B4DDC9C38F2199C3E7B164FCC0536")]
        public void Pipeline_Md4HashesTheCompleteByteStream(string input, string expectedDigest)
        {
            var writes = new System.Text.StringBuilder();
            var bytes = System.Text.Encoding.ASCII.GetBytes(input);
            for (var i = 0; i < bytes.Length; i++)
                writes.Append($"let byte{i}: byte = {bytes[i]}; source.write(byte{i});\n");
            var source = $$"""
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    source |: md4 |: bin2hex | result;
    {{writes}}
    source.close();
    var matched = 0;
    foreach (value in result) { if (value == "{{expectedDigest}}") { matched = matched + 1; } }
    return matched;
}
""";

            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(1));
        }

        [Test]
        public void Pipeline_Md4RejectsNonByteInput()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<byte> = pipe<byte>.create();
    source |: md4 | result;
    return 0;
}
""";
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        }

        [TestCase("sha1", "pipe<string>", "pipe<byte>")]
        [TestCase("sha256", "pipe<string>", "pipe<byte>")]
        [TestCase("sha1", "pipe<byte>", "pipe<string>")]
        [TestCase("sha256", "pipe<byte>", "pipe<string>")]
        public void Pipeline_ShaFiltersRejectMismatchedPipeTypes(string filter, string inputType, string outputType)
        {
            var source = $$"""
function main(args: string[]): int {
    let source: {{inputType}} = {{inputType}}.create();
    let result: {{outputType}} = {{outputType}}.create();
    source |: {{filter}} | result;
    return 0;
}
""";
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        }

        [Test]
        public void Pipeline_HtmlSpecialCharsEscapesAndDecodesCanonicalEntitiesOnly()
        {
            const string source = """
function main(args: string[]): int {
    let input: pipe<string> = pipe<string>.create(1);
    let escaped: pipe<string> = pipe<string>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    input |: htmlspecialchars | escaped;
    escaped |: htmlspecialchars_decode | result;
    input.write("<&>\"'&lt;"); input.close();
    var matched = 0;
    foreach (value in result) { if (value == "<&>\"'&lt;") { matched = matched + 1; } }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(1));

            const string encodeSource = """
function main(args: string[]): int {
    let input: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create();
    input |: htmlspecialchars | result;
    input.write("<&>\"'"); input.close();
    var matched = 0;
    foreach (value in result) { if (value == "&lt;&amp;&gt;&quot;&#39;") { matched = matched + 1; } }
    return matched;
}
""";
            var encodeVm = new AloeVm(new AloeCompiler().Compile(encodeSource));
            encodeVm.RunFromEntryPoint();
            Assert.That(encodeVm.Pop().AsInt, Is.EqualTo(1));

            const string decodeSource = """
function main(args: string[]): int {
    let input: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create();
    input |: htmlspecialchars_decode | result;
    input.write("&amp;lt; &AMP; &#x3C; &#39;"); input.close();
    var matched = 0;
    foreach (value in result) { if (value == "&lt; &AMP; &#x3C; '") { matched = matched + 1; } }
    return matched;
}
""";
            var decodeVm = new AloeVm(new AloeCompiler().Compile(decodeSource));
            decodeVm.RunFromEntryPoint();
            Assert.That(decodeVm.Pop().AsInt, Is.EqualTo(1));
        }

        [Test]
        public void Pipeline_HtmlEntityDecodeHandlesNamedNumericAndUnknownReferences()
        {
            const string source = """
function main(args: string[]): int {
    let input: pipe<string> = pipe<string>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    input |: html_entity_decode | result;
    input.write("&nbsp;&#160;&#x1F600; &amp;lt; &bogus;"); input.close();
    var matched = 0;
    foreach (value in result) { if (value == "  😀 &lt; &bogus;") { matched = matched + 1; } }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(1));
        }

        [Test]
        public void Pipeline_StripTagsRemovesCompleteMarkupButDoesNotSanitizeScriptText()
        {
            const string source = """
function main(args: string[]): int {
    let input: pipe<string> = pipe<string>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    input |: strip_tags | result;
    input.write("<p title=\"a > b\">Hello <b>world</b></p><!-- note -->");
    input.write("<script>plain</script> 2 < 3 <unfinished");
    input.close();
    var matched = 0;
    foreach (value in result) {
        if (value == "Hello world") { matched = matched + 1; }
        if (value == "plain 2 < 3 <unfinished") { matched = matched + 1; }
    }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(2));
        }

        [Test]
        public void Pipeline_CsvEncodeEscapesFieldsAndResumesAcrossBoundedPipes()
        {
            const string source = """
function main(args: string[]): int {
    let input: pipe<string[]> = pipe<string[]>.create(3);
    let result: pipe<string> = pipe<string>.create(1);
    input |: csvenode | result;
    let row: string[] = ["plain", "comma,value", "say \"hi\"", "two\r\nlines", ""];
    let empty: string[] = [];
    let emptyField: string[] = [""];
    input.write(row);
    GC.require(); GC.finish();
    input.write(empty); input.write(emptyField); input.close();
    var matched = 0;
    foreach (record in result) {
        if (record == "plain,\"comma,value\",\"say \"\"hi\"\"\",\"two\r\nlines\",") { matched = matched + 1; }
        if (record == "") { matched = matched + 1; }
        if (record == "\"\"") { matched = matched + 1; }
    }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(3));
        }

        [Test]
        public void Pipeline_CsvDecodeParsesQuotedFieldsAndResumesAcrossBoundedPipes()
        {
            const string source = """
function main(args: string[]): int {
    let input: pipe<string> = pipe<string>.create(3);
    let result: pipe<string[]> = pipe<string[]>.create(1);
    input |: csvdecode | result;
    input.write("plain,\"comma,value\",\"say \"\"hi\"\"\",\"two\r\nlines\",");
    input.write(""); input.write("\"\""); input.close();
    GC.require(); GC.finish();
    var matched = 0;
    foreach (row in result) {
        if (row.Length == 5 and row[0] == "plain" and row[1] == "comma,value" and row[2] == "say \"hi\"" and row[3] == "two\r\nlines" and row[4] == "") { matched = matched + 1; }
        if (row.Length == 0) { matched = matched + 1; }
        if (row.Length == 1 and row[0] == "") { matched = matched + 1; }
    }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(3));
        }

        [TestCase("bad\"quote")]
        [TestCase("\"unterminated")]
        [TestCase("\"closed\"tail")]
        public void Pipeline_CsvDecodeRejectsMalformedQuoting(string record)
        {
            var literal = record.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var source = $$"""
function main(args: string[]): int {
    let input: pipe<string> = pipe<string>.create();
    let result: pipe<string[]> = pipe<string[]>.create();
    input |: csvdecode | result;
    input.write("{{literal}}"); input.close();
    foreach (row in result) { return row.Length; }
    return 0;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<VmException>());
        }

        [TestCase("pipe<int>", "pipe<string[]>", "expects pipe<string>")]
        [TestCase("pipe<string>", "pipe<string>", "expects pipe<string[]>")]
        public void Pipeline_CsvDecodeRequiresStringInputAndStringArrayOutput(
            string inputType, string resultType, string expectedMessage)
        {
            var source = $$"""
function main(args: string[]): int {
    let input: {{inputType}} = {{inputType}}.create();
    let result: {{resultType}} = {{resultType}}.create();
    input |: csvdecode | result;
    return 0;
}
""";
            Assert.That(() => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>().With.Message.Contains(expectedMessage));
        }

        [Test]
        public void Pipeline_ParseStrDecodesQueryPairsIntoMaps()
        {
            const string source = """
function main(args: string[]): int {
    let input: pipe<string> = pipe<string>.create(2);
    let result: pipe<map<string/string>> = pipe<map<string/string>>.create(1);
    input |: parse_str | result;
    input.write("name=Aloe+Lang&city=Tokyo%20JP&utf=%E6%97%A5%E6%9C%AC&dup=first&dup=last&flag&x=a=b&&=empty");
    input.write(""); input.close();
    GC.require(); GC.finish();
    var matched = 0;
    foreach (values in result) {
        if (values.Count == 7 and values["name"] == "Aloe Lang" and values["city"] == "Tokyo JP" and values["utf"] == "日本" and values["dup"] == "last" and values["flag"] == "" and values["x"] == "a=b" and values[""] == "empty") { matched = matched + 1; }
        if (values.Count == 0) { matched = matched + 1; }
    }
    return matched;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(2));
        }

        [TestCase("bad%")]
        [TestCase("bad%2")]
        [TestCase("bad%XZ")]
        [TestCase("bad=%FF")]
        public void Pipeline_ParseStrRejectsMalformedPercentEscapesAndUtf8(string query)
        {
            var literal = query.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var source = $$"""
function main(args: string[]): int {
    let input: pipe<string> = pipe<string>.create();
    let result: pipe<map<string/string>> = pipe<map<string/string>>.create();
    input |: parse_str | result;
    input.write("{{literal}}"); input.close();
    foreach (values in result) { return values.Count; }
    return 0;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<VmException>());
        }

        [Test]
        public void Pipeline_ParseStrRequiresStringInputAndStringMapOutput()
        {
            const string source = """
function main(args: string[]): int {
    let input: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create();
    input |: parse_str | result;
    return 0;
}
""";
            Assert.That(() => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>().With.Message.Contains("expects pipe<map<string/string>>"));
        }


        [Test]
        public void Pipeline_StandardToLowerRequiresStringPipes()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source |: tolower | result;
    return 0;
}
""";
            Assert.That(() => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>().With.Message.Contains("expects pipe<string>"));
        }

        [Test]
        public void Pipeline_StandardToLowerResumesAcrossBoundedPipes()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create(1);
    source |: tolower | result;
    source.write("ONE"); source.write("TWO"); source.write("THREE"); source.close();
    var total = 0;
    foreach (value in result) {
        if (value == "one") { total = total + 1; }
        if (value == "two") { total = total + 1; }
        if (value == "three") { total = total + 1; }
    }
    return total;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(3));
        }

        [Test]
        public void Pipeline_StandardToLowerClosesOutputWhenSourceIsEmpty()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create();
    source |: tolower | result;
    source.close();
    var total = 42;
    foreach (value in result) { total = total + 1; }
    return total;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
        }

        [Test]
        public void StandardFilterNameCannotBeRedeclaredAsFunction()
        {
            const string source = """
function tolower(args: string[]): int { return 0; }
function main(args: string[]): int { return 0; }
""";
            Assert.That(() => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>().With.Message.Contains("Function 'tolower' is already declared"));
        }

        [Test]
        public void Pipeline_FilterShorthandRejectsUnknownFilterName()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source |: missing | result;
    return 0;
}
""";
            Assert.That(() => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>().With.Message.Contains("Unknown user-defined filter 'missing'"));
        }

        [Test]
        public void Pipeline_FilterChainPropagatesClose()
        {
            const string source = """
filter doubleValues {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value * 2); }
        output.close();
    }
}
filter plusOne {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value + 1); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source | filter(doubleValues) | filter(plusOne) | result;
    source.write(5);
    source.close();
    var sum = 0;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";
            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(11));
        }

        [Test]
        public void Pipeline_FilterDrainsAfterSourceClose()
        {
            const string source = """
filter doubleValues {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value * 2); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source | filter(doubleValues) | result;

    var sum = 0;
    source.write(3);
    source.write(4);
    source.close();
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(14));
        }

        [Test]
        public void Pipeline_FilterChainDrainsAfterSourceClose()
        {
            const string source = """
filter doubleValues {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value * 2); }
        output.close();
    }
}
filter plusOne {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value + 1); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source | filter(doubleValues) | filter(plusOne) | result;

    source.write(5);
    source.close();
    var sum = 0;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(11));
        }

        [Test]
        public void Pipeline_FilterPreservesLocalsAcrossBufferedWrites()
        {
            const string source = """
filter runningTotal {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        var total = 0;
        foreach (value in input) {
            total = total + value;
            output.write(total);
        }
        output.close();
    }
}

function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source | filter(runningTotal) | result;

    var sum = 0;
    source.write(2);
    source.write(3);
    source.close();
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(7));
        }


        [Test]
        public void BoundedPipe_FilterWriterResumesAfterConsumerRead()
        {
            const string source = """
filter doubleValues {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value * 2); }
        output.close();
    }
}

function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create(1);
    source | filter(doubleValues) | result;

    source.write(1);
    source.write(2);

    source.close();
    var sum = 0;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(6));
        }

        [Test]
        public void BoundedPipe_MainWriterSuspendsAndResumes()
        {
            const string source = """
filter doubleValues {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value * 2); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create(1);
    let result: pipe<int> = pipe<int>.create();
    source | filter(doubleValues) | result;
    source.write(2);
    source.write(3);
    source.close();
    var sum = 0;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(10));
        }

        [Test]
        public void BoundedPipe_MainWriterMaintainsValueAcrossMultipleResumes()
        {
            const string source = """
filter forward {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create(1);
    let result: pipe<int> = pipe<int>.create();
    source | filter(forward) | result;
    source.write(1);
    source.write(2);
    source.write(3);
    source.close();
    var sum = 0;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(6));
        }

        [Test]
        public void Pipeline_MainReaderWaitsUntilClose_ReportsDeadlock()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    foreach (value in source) { }
    source.close();
    return 0;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<VmException>()
                .With.Message.Contains("deadlock"));
        }

        [Test]
        public void Pipeline_FilterWaitingAfterMainReturns_ReportsDeadlock()
        {
            const string source = """
filter forward {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source | filter(forward) | result;
    source.write(1);
    return 0;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<VmException>()
                .With.Message.Contains("deadlock"));
        }

        [Test]
        public void Scheduler_RunAfterCompletion_DoesNotLoseReturnValue()
        {
            const string source = """
function main(args: string[]): int {
    return 42;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            vm.Run();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
        }

        [Test]
        public void Pipeline_EmptySourceClose_PropagatesToOutput()
        {
            const string source = """
filter forward {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source | filter(forward) | result;
    source.close();
    var sum = 42;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
        }

        [Test]
        public void Pipeline_TwoBoundedStages_ResumesBlockedFilterAndMain()
        {
            const string source = """
filter forward {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create(1);
    let middle: pipe<int> = pipe<int>.create(1);
    let result: pipe<int> = pipe<int>.create(1);
    source | filter(forward) | middle;
    middle | filter(forward) | result;
    source.write(1);
    source.write(2);
    source.write(3);
    source.close();
    var sum = 0;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(6));
        }

        [Test]
        public void Scheduler_DeadlockIncludesBlockedReaderCounts()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    foreach (value in source) { }
    return 0;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<VmException>()
                .With.Message.Contains("readers=1"));
        }

        [Test]
        public void Scheduler_RunWithoutFrames_IsNoOp()
        {
            const string source = """
function main(args: string[]): int { return 9; }
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.DoesNotThrow(() => vm.Run());
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(9));
        }

        [Test]
        public void PipeCreate_NonIntCapacity_IsCompileError()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create("bad");
    return 0;
}
""";

            Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
        }

        [Test]
        public void Scheduler_BlockingPipeWriteInsideTick_IsVmError()
        {
            // tick() runs instance calls synchronously; they cannot be suspended,
            // so a write to a full pipe must fail instead of retrying forever.
            const string source = """
class Writer {
    construct() { }
    public async method push(target: pipe<int>, value: int): void {
        target.write(value);
    }
}
function main(args: string[]): int {
    let p: pipe<int> = pipe<int>.create(1);
    var w = new Writer();
    w.push(p, 1);
    w.push(p, 2);
    tick();
    return 0;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.That(() => vm.RunFromEntryPoint(),
                Throws.TypeOf<VmException>().With.Message.Contains("synchronous instance call"));
        }

        [Test]
        public void Scheduler_BlockingPipeReadInsideTick_IsVmError()
        {
            const string source = """
class Reader {
    field total: int;
    construct() { this.total = 0; }
    public async method drain(source: pipe<int>): void {
        foreach (value in source) {
            this.total = this.total + value;
        }
    }
}
function main(args: string[]): int {
    let p: pipe<int> = pipe<int>.create();
    var r = new Reader();
    p.write(1);
    r.drain(p);
    tick();
    return 0;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.That(() => vm.RunFromEntryPoint(),
                Throws.TypeOf<VmException>().With.Message.Contains("synchronous instance call"));
        }

        [Test]
        public void Scheduler_NonBlockingPipeOpsInsideTick_AreAllowed()
        {
            const string source = """
class Writer {
    construct() { }
    public async method push(target: pipe<int>, value: int): void {
        target.write(value);
    }
}
function main(args: string[]): int {
    let p: pipe<int> = pipe<int>.create(2);
    var w = new Writer();
    w.push(p, 5);
    w.push(p, 6);
    tick();
    p.close();
    var sum = 0;
    foreach (value in p) {
        sum = sum + value;
    }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(11));
        }

        [Test]
        public void Scheduler_SuspendedFilterLocalSurvivesMainGc()
        {
            // The filter is suspended on an empty input while main runs tick/GC.
            // Its frame-local Node must remain a GC root and still be readable afterwards.
            const string source = """
class Node {
    field value: int;
    construct() { this.value = 7; }
    public property Value: int { get { return this.value; } }
}
filter keep {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        var node = new Node();
        foreach (value in input) { output.write(value); }
        output.write(node.Value);
        output.close();
    }
}
function main(args: string[]): int {
    let s: pipe<int> = pipe<int>.create();
    let r: pipe<int> = pipe<int>.create();
    s | filter(keep) | r;
    s.write(1);
    var sum = 0;
    var first = true;
    foreach (value in r) {
        sum = sum + value;
        if (first) {
            first = false;
            tick();
            GC.require();
            GC.finish();
            s.close();
        }
    }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(8));
        }

        [Test]
        public void BoundedPipe_LargeStreamThroughFilterChain_DoesNotLoseValues()
        {
            // Exceeds InstructionQuantum many times and forces repeated reader/writer handoffs.
            const string source = """
filter plusOne {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value + 1); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create(2);
    let result: pipe<int> = pipe<int>.create();
    source | filter(plusOne) | filter(plusOne) | result;
    var i = 0;
    while (i < 2000) {
        source.write(i);
        i = i + 1;
    }
    source.close();
    var sum = 0;
    foreach (value in result) {
        sum = sum + value;
    }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            // sum(i + 2) for i = 0..1999
            Assert.That(vm.Pop().AsInt, Is.EqualTo(2003000));
        }

        [Test]
        public void NumberFormat_FormatsDecimalItemsInvariantlyWithBoundedOutput()
        {
            const string source = """
function main(args: string[]): int {
    let values: pipe<decimal> = pipe<decimal>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    values |: number_format: "#,##0.00" | result;
    values.write(1234.5:d);
    values.write(-0.05:d);
    values.close();
    var matched = 0;
    var position = 0;
    foreach (value in result) {
        if (position == 0 and value == "1,234.50") { matched = matched + 1; }
        if (position == 1 and value == "-0.05") { matched = matched + 1; }
        position = position + 1;
    }
    if (matched == 2 and position == 2) { return 2; }
    return 0;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(2));
        }

        [Test]
        public void NumberFormat_InvalidFormatRaisesVmException()
        {
            const string source = """
function main(args: string[]): int {
    let values: pipe<decimal> = pipe<decimal>.create();
    let result: pipe<string> = pipe<string>.create();
    values |: number_format: "Q" | result;
    values.write(1.25:d);
    values.close();
    foreach (value in result) {}
    return 0;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<VmException>());
        }
    }
}
