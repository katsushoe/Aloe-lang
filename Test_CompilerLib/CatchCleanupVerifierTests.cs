using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class CatchCleanupVerifierTests
{
    private const string Source = """
function main(args: string[]): int {
    while (true) {
        try { let n: int = 1 / 0; }
        catch (ZeroDivisionException) { break; }
    }
    return 42;
}
""";

    [TestCase(-1)]
    [TestCase(2)]
    public void CatchEnd_RejectsUnknownCleanupMode(int mode)
    {
        Reject(code =>
        {
            var ip = FindCleanup(code);
            code[ip] = new Instruction(EnumOpcode.CatchEnd, mode);
        }, "invalid cleanup mode");
    }

    [Test]
    public void TransferCleanup_RequiresImmediateBranch()
    {
        Reject(code => code[FindCleanup(code) + 1] = new Instruction(EnumOpcode.Nop),
            "immediate branch");
    }

    [Test]
    public void TransferCleanup_RejectsPoppingMoreThanEnclosingCatchDepth()
    {
        Reject(code => code[FindCleanup(code) + 1] = new Instruction(EnumOpcode.CatchEnd, 1),
            "sufficient catch depth");
    }

    [Test]
    public void TransferCleanup_RejectsInstructionOutsideCatch()
    {
        Reject(code => code[0] = new Instruction(EnumOpcode.CatchEnd, 1), "outside a catch region");
    }

    private static int FindCleanup(Instruction[] code) => Array.FindIndex(code,
        instruction => instruction.Opcode == EnumOpcode.CatchEnd && instruction.Operand0 == 1);

    private static void Reject(Action<Instruction[]> mutate, string message)
    {
        var compiled = new AloeCompiler().Compile(Source);
        var code = compiled.Code.ToArray();
        Assert.That(FindCleanup(code), Is.GreaterThanOrEqualTo(0));
        mutate(code);
        var malformed = new Module(compiled.Constants, code, compiled.Functions, compiled.EntryPointIndex);
        var error = Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(malformed));
        Assert.That(error!.Message, Does.Contain(message));
    }
}
