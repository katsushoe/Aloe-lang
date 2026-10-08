using Aloe.CompilerLib;
using Aloe.RuntimeLib;


namespace AloeSample
{
    internal static class Program
    {
        private const string DefaultSource = """
function main(args: string[]): int {
    var sum = 0;
    let i: int = 1;


    while (i < 11) {
        sum = sum + i;
        i = i + 1;
    }


    print(sum);
    return 0;
}
""";


        private static int Main(string[] args)
        {
            var source = args.Length > 0
                ? File.ReadAllText(args[0])
                : DefaultSource;


            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();


            if (vm.ValueStack.Count == 0)
                throw new InvalidOperationException(
                    "main did not leave an int return value on the VM stack.");


            var result = vm.Pop();
            if (!result.IsInt)
                throw new InvalidOperationException(
                    $"main returned {result.Kind}, expected int.");


            return checked((int)result.AsInt);
        }
    }
}