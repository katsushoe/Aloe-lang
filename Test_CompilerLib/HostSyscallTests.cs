using Aloe.CommonLib.Exceptions;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using NUnit.Framework;
using System.Text;

namespace Aloe.CompilerLib.Tests
{
    [TestFixture]
    public sealed class HostSyscallTests
    {
        [Test]
        public void ReadLine_UsesInjectedHostInput()
        {
            const string source = """
function main(args: string[]): int {
    let line: string = readLine();
    print(line);
    return 0;
}
""";

            var module = new AloeCompiler().Compile(source);
            var output = new StringBuilder();
            var vm = new AloeVm(module)
            {
                InputReader = () => "hello from host",
                OutputWriter = line => output.AppendLine(line)
            };

            vm.RunFromEntryPoint();

            Assert.That(output.ToString().Replace("\r\n", "\n"), Is.EqualTo("hello from host\n"));
            Assert.That(vm.Pop().AsInt, Is.EqualTo(0));
        }

        [Test]
        public void Sleep_UsesInjectedTimerHandler()
        {
            const string source = """
function main(args: string[]): int {
    sleep(25);
    return 0;
}
""";

            var module = new AloeCompiler().Compile(source);
            var observedMilliseconds = -1;
            var vm = new AloeVm(
                module,
                new AloeVmSettings { AllowedHostCapabilities = AloeHostCapability.Timer })
            {
                SleepHandler = milliseconds => observedMilliseconds = milliseconds
            };

            vm.RunFromEntryPoint();

            Assert.That(observedMilliseconds, Is.EqualTo(25));
            Assert.That(vm.Pop().AsInt, Is.EqualTo(0));
        }

        [Test]
        public void Sleep_IsDeniedWithoutTimerCapability()
        {
            const string source = """
function main(args: string[]): int {
    sleep(1);
    return 0;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(
                module,
                new AloeVmSettings { AllowedHostCapabilities = AloeHostCapability.None });

            Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<VmException>());
        }
    }
}
