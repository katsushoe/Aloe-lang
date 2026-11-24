using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

// Decision 0053: crc32 is pipe<byte> -> pipe<byte>, emitting CRC-32/ISO-HDLC as 4 big-endian bytes.
public sealed class Crc32FilterTests
{
    private static string ByteWrites(string pipe, string text)
    {
        var writes = new System.Text.StringBuilder();
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        for (var i = 0; i < bytes.Length; i++)
            writes.Append($"let byte{i}: byte = {bytes[i]}; {pipe}.write(byte{i});\n");
        return writes.ToString();
    }

    private static long Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var vm = new AloeVm(module);
        vm.RunFromEntryPoint();
        return vm.Pop().AsInt;
    }

    [TestCase("", "00000000")]
    [TestCase("a", "E8B7BE43")]
    [TestCase("abc", "352441C2")]
    [TestCase("123456789", "CBF43926")]
    [TestCase("The quick brown fox jumps over the lazy dog", "414FA339")]
    public void Crc32_KnownVectorsThroughBin2Hex(string input, string expected)
    {
        var source = $$"""
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    source |: crc32 |: bin2hex | result;
    {{ByteWrites("source", input)}}
    source.close();
    var matched = 0;
    var count = 0;
    foreach (value in result) {
        count = count + 1;
        if (value == "{{expected}}") { matched = matched + 1; }
    }
    return matched * 10 + count;
}
""";
        Assert.That(Run(source), Is.EqualTo(11));
    }

    [Test]
    public void Crc32_EmitsExactlyFourBigEndianBytesThenCloses()
    {
        var source = $$"""
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create();
    let digest: pipe<byte> = pipe<byte>.create();
    source |: crc32 | digest;
    {{ByteWrites("source", "123456789")}}
    source.close();
    var count = 0;
    var matched = 0;
    foreach (value in digest) {
        if (count == 0 and value == 203) { matched = matched + 1; }
        if (count == 1 and value == 244) { matched = matched + 1; }
        if (count == 2 and value == 57) { matched = matched + 1; }
        if (count == 3 and value == 38) { matched = matched + 1; }
        count = count + 1;
    }
    return matched * 10 + count;
}
""";
        // 0xCBF43926 in big-endian order: CB F4 39 26.
        Assert.That(Run(source), Is.EqualTo(44));
    }

    [Test]
    public void Crc32_LargeStreamThroughBoundedPipes()
    {
        const string source = """
function main(args: string[]): int {
    let bytes: pipe<byte> = pipe<byte>.create(1);
    let digest: pipe<string> = pipe<string>.create(1);
    bytes |: crc32 |: bin2hex | digest;
    var index = 0;
    while (index < 100000) {
        let value: byte = 97;
        bytes.write(value);
        index = index + 1;
    }
    bytes.close();
    var matched = 0;
    foreach (value in digest) { if (value == "1BE2FA87") { matched = matched + 1; } }
    return matched;
}
""";
        Assert.That(Run(source), Is.EqualTo(1));
    }

    [Test]
    public void Crc32_ChainedAfterAnotherByteFilter()
    {
        // crc32 of the 16-byte MD4 digest of "abc" must match a host-side computation.
        var md4 = Convert.FromHexString("A448017AAF21D8525FC10AE87AA6729D");
        var expected = HostCrc32(md4).ToString("X8");
        var source = $$"""
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    source |: md4 |: crc32 |: bin2hex | result;
    {{ByteWrites("source", "abc")}}
    source.close();
    var matched = 0;
    foreach (value in result) { if (value == "{{expected}}") { matched = matched + 1; } }
    return matched;
}
""";
        Assert.That(Run(source), Is.EqualTo(1));
    }

    [TestCase("pipe<string>", "pipe<byte>")]
    [TestCase("pipe<byte>", "pipe<string>")]
    [TestCase("pipe<int>", "pipe<byte>")]
    public void Crc32_RejectsMismatchedPipeTypes(string inputType, string outputType)
    {
        var source = $$"""
function main(args: string[]): int {
    let source: {{inputType}} = {{inputType}}.create();
    let result: {{outputType}} = {{outputType}}.create();
    source |: crc32 | result;
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
    }

    private static uint HostCrc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
