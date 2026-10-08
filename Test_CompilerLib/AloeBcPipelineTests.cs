using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using NUnit.Framework;
using System.Text;

namespace Aloe.CompilerLib.Tests
{
    [TestFixture]
    public sealed class AloeBcPipelineTests
    {
        [Test]
        public void Source_ToAloeBc_ToVm_RoundTripRuns()
        {
            const string source = """
function main(args: string[]): int {
    var x = 20;
    var y = 22;
    print(x + y);
    return 0;
}
""";

            var bytes = new AloeCompiler().CompileToAloeBc(source);
            var output = new StringBuilder();
            var vm = AloeVmLoader.FromAloeBc(bytes);
            vm.OutputWriter = line => output.AppendLine(line);

            vm.RunFromEntryPoint();

            Assert.That(output.ToString().Replace("\r\n", "\n"), Is.EqualTo("42\n"));
            Assert.That(vm.Pop().AsInt, Is.EqualTo(0));
        }
    }
}
