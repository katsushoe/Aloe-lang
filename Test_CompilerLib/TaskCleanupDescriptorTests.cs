using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class TaskCleanupDescriptorTests
{
    private const string Main = " function main(args: string[]): int { var worker=new Worker(); worker.start(); tick(); return 0; }";

    [TestCase(false)]
    [TestCase(true)]
    public void SourceCatchAndCleanupHaveDistinctDescriptorsWithUnchangedExecution(bool fault)
    {
        var body = fault ? "var bad=1/0;" : "print(1);";
        var module = Compile("task Worker { method run(): void { try { " + body + " } catch(ZeroDivisionException) { print(2); } finally { print(3); } this.stop(); } }" + Main);
        Assert.That(module.Code.Any(x => x.Opcode == EnumOpcode.TryCatch && x.Operand1 == AloeCatchDescriptor.Cleanup), Is.True);
        Assert.That(module.Code.Any(x => x.Opcode == EnumOpcode.TryCatch && x.Operand1 > 0), Is.True);
        Assert.That(Run(module), Is.EqualTo(fault ? new[] { "2", "3" } : new[] { "1", "3" }));
    }

    [Test]
    public void OrdinaryModuleRetainsLegacyFinallyEncoding()
    {
        var module = Compile("function main(args: string[]): int { try { print(1); } finally { print(2); } return 0; }");
        Assert.That(module.Code.Where(x => x.Opcode == EnumOpcode.TryCatch).Select(x => x.Operand1), Is.EqualTo(new[] { 0 }));
        Assert.That(Run(module), Is.EqualTo(new[] { "1", "2" }));
    }

    [Test]
    public void NestedSelfEntryCleanupRunsBeforeCallerCatchAndCleanup()
    {
        var module = Compile("task Worker { method fail(): void { try { var bad=1/0; } finally { print(7); } } method run(): void { try { this.fail(); } catch(ZeroDivisionException) { print(8); } finally { print(9); } this.stop(); } }" + Main);
        Assert.That(Run(module), Is.EqualTo(new[] { "7", "8", "9" }));
    }

    [Test]
    public void SynchronousHelperCleanupIsMarkedInTaskModule()
    {
        var module = Compile("function helper(): void { try { var bad=1/0; } finally { print(7); } } task Worker { method run(): void { try { helper(); } catch(ZeroDivisionException) { print(8); } finally { print(9); } this.stop(); } }" + Main);
        Assert.That(module.Code.Count(x => x.Opcode == EnumOpcode.TryCatch && x.Operand1 == AloeCatchDescriptor.Cleanup), Is.EqualTo(2));
        Assert.That(Run(module), Is.EqualTo(new[] { "7", "8", "9" }));
    }

    [Test]
    public void ConstructorAndNestedReturnCleanupArePreserved()
    {
        var module = Compile("task Worker { construct() { try { print(1); } finally { print(2); } } method run(): void { try { try { return; } finally { print(3); } } finally { print(4); } } }" + Main);
        Assert.That(Run(module), Is.EqualTo(new[] { "1", "2", "3", "4" }));
    }

    [Test]
    public void CodecRoundTripRetainsCleanupMarker()
    {
        var original = Compile("task Worker { method run(): void { try { print(1); } finally { print(2); } this.stop(); } }" + Main);
        var module = AloeBcCodec.Read(AloeBcCodec.Write(original));
        Assert.That(module.Code.Count(x => x.Opcode == EnumOpcode.TryCatch && x.Operand1 == AloeCatchDescriptor.Cleanup), Is.EqualTo(1));
        Assert.That(Run(module), Is.EqualTo(new[] { "1", "2" }));
    }

    [Test]
    public void CleanupWithoutTaskMetadataIsRejected()
    {
        var module = Compile("task Worker { method run(): void { try { print(1); } finally { print(2); } } }" + Main);
        var plain = new Module(module.Constants, module.Code, module.Functions, module.EntryPointIndex);
        Assert.That(Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(plain))!.Message, Does.Contain("cleanup descriptor"));
    }

    [TestCase(-2)]
    [TestCase(int.MinValue)]
    public void UnknownNegativeDescriptorsAreRejected(int descriptor)
    {
        var module = Compile("task Worker { method run(): void { try { print(1); } finally { print(2); } } }" + Main);
        var code = module.Code.ToArray();
        var index = Array.FindIndex(code, x => x.Opcode == EnumOpcode.TryCatch);
        code[index] = new Instruction(EnumOpcode.TryCatch, code[index].Operand0, descriptor);
        Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(new Module(module.Constants, code, module.Functions, module.EntryPointIndex, module.TaskTypes)));
    }

    [Test]
    public void CleanupCannotBeCombinedWithUserCatchDescriptors()
    {
        var module = Compile("task Worker { method run(): void { try { print(1); } catch(ZeroDivisionException) { print(2); } catch(Exception) { print(3); } } }" + Main);
        var code = module.Code.ToArray();
        var index = Array.FindIndex(code, x => x.Opcode == EnumOpcode.TryCatch);
        code[index] = new Instruction(EnumOpcode.TryCatch, code[index].Operand0, AloeCatchDescriptor.Cleanup);
        Assert.That(Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(new Module(module.Constants, code, module.Functions, module.EntryPointIndex, module.TaskTypes)))!.Message, Does.Contain("cleanup descriptor"));
    }

    private static Module Compile(string source) => new AloeCompiler().Compile(source);

    private static List<string> Run(Module module)
    {
        var vm = new AloeVm(module); var output = new List<string>(); vm.OutputWriter = output.Add;
        vm.RunFromEntryPoint(); Assert.That(vm.Pop().AsInt, Is.Zero); return output;
    }
}
