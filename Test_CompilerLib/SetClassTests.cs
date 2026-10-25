using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class SetClassTests
{
    [Test]
    public void Sets_ClassReferencesUseObjectIdentityAndRemainGcRoots()
    {
        const string source = """
class Item {
    field n:int; construct(n:int){this.n=n;}
    public property N:int {get{return this.n;}}
}
function main(args:string[]):int {
    var first=new Item(20);var equalButDistinct=new Item(20);
    let values:Item{}={first,first,equalButDistinct};
    var alias=values;GC.require();GC.finish();
    var total=0;foreach(item in alias){total=total+item.N;}
    alias.Remove(first);
    if(values.Count==1 and values.Contains(equalButDistinct) and not values.Contains(first) and alias==values){return total;}
    return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo(40));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("struct Point{field x:int;}function main(args:string[]):int{let points:Point{}={Point{x:1}};return 0;}"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("function main(args:string[]):int{let values:object{}={new Object()};return 0;}"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm("class Item{}function main(args:string[]):int{let values:Item{}={new Item()};return 0;}"));
    }

    private static long Run(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings();settings.Logging.FileEnabled=false;settings.Logging.ConsoleEnabled=false;
        var vm=new AloeVm(module,settings);vm.RunFromEntryPoint();Assert.That(vm.ValueStack.Count,Is.EqualTo(1));return vm.Pop().AsInt;
    }
}
