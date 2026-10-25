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
            var sleepCalls = 0;
            var vm = new AloeVm(
                module,
                new AloeVmSettings { AllowedHostCapabilities = AloeHostCapability.None })
            {
                SleepHandler = _ => sleepCalls++
            };

            var exception = Assert.Throws<VmException>(() => vm.RunFromEntryPoint());

            Assert.That(exception!.Message, Does.Contain("Timer"));
            Assert.That(sleepCalls, Is.Zero);
        }

        [Test]
        public void ReadLine_IsDeniedWithoutConsoleInputCapabilityBeforeCallingHost()
        {
            const string source = """
function main(args: string[]): int {
    let line: string = readLine();
    return 0;
}
""";

            var module = new AloeCompiler().Compile(source);
            var inputCalls = 0;
            var vm = new AloeVm(
                module,
                new AloeVmSettings { AllowedHostCapabilities = AloeHostCapability.None })
            {
                InputReader = () =>
                {
                    inputCalls++;
                    return "must not be read";
                }
            };

            var exception = Assert.Throws<VmException>(() => vm.RunFromEntryPoint());

            Assert.That(exception!.Message, Does.Contain("ConsoleInput"));
            Assert.That(inputCalls, Is.Zero);
        }

        [Test]
        public void ReadLine_EndOfInputReturnsEmptyString()
        {
            const string source = """
function main(args: string[]): int {
    let line: string = readLine();
    print(line);
    return 0;
}
""";

            var output = new List<string>();
            var vm = new AloeVm(new AloeCompiler().Compile(source))
            {
                InputReader = () => null,
                OutputWriter = line => output.Add(line)
            };

            vm.RunFromEntryPoint();

            Assert.That(output, Is.EqualTo(new[] { string.Empty }));
            Assert.That(vm.Pop().AsInt, Is.Zero);
        }

        [Test]
        public void Sleep_NegativeDurationIsRejectedWithoutCallingHost()
        {
            const string source = """
function main(args: string[]): int {
    sleep(-1);
    return 0;
}
""";

            var sleepCalls = 0;
            var vm = new AloeVm(
                new AloeCompiler().Compile(source),
                new AloeVmSettings { AllowedHostCapabilities = AloeHostCapability.Timer })
            {
                SleepHandler = _ => sleepCalls++
            };

            var exception = Assert.Throws<VmException>(() => vm.RunFromEntryPoint());

            Assert.That(exception!.Message, Does.Contain("non-negative"));
            Assert.That(sleepCalls, Is.Zero);
        }
    }
}
