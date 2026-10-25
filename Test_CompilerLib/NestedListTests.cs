using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class NestedListTests
{
    [Test]
    public void NestedLists_DistinguishListAndArrayAtEveryLevel()
    {
        const string source = """
function main(args:string[]):int {
    let rows:int()()=((20,22),());
    var alias=rows[0];rows[1]=(0,);
    let mixed:int[]()=([40,2],);
    let arrayOfLists:int()[]=[(40,2)];
    alias[1]=22;
    if(rows[0]==alias and mixed[0][0]+mixed[0][1]==42 and arrayOfLists[0][0]+arrayOfLists[0][1]==42){return alias[0]+alias[1];}
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void NestedLists_InferDeepAndEmptyChildContexts()
    {
        const string source = """
function identity(rows:int()()):int()(){return rows;}
function main(args:string[]):int {
    var rows=((20,22),());
    let copy:int()()=identity(rows);
    copy[1]=(0,);
    if(copy.Length==2 and copy[1].Length==1 and rows[1].Length==1){return copy[0][0]+copy[0][1];}
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void NestedLists_StructSnapshotsAreWrittenBackAtLeaf()
    {
        const string source = """
struct Point {field x:int;}
function main(args:string[]):int {
    let rows:Point()[]= [(Point {x:41},)];
    var snapshot=rows[0][0];rows[0][0].x=42;
    if(snapshot.x==41){return rows[0][0].x;}return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void NestedLists_ExactCastsAndShapeErrors()
    {
        Assert.Throws<InvalidCastException>(()=>Run("function main(args:string[]):int {let x:object=((42,),);var y=x as int()[];return 0;}"));
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args:string[]):int {let x:int()[]=[(42,)];let y:int[]()=x;return 0;}"));
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args:string[]):int {var x=((42,),(true,));return 0;}"));
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().CompileForWasm("function main(args:string[]):int {let x=((42,),);return x[0][0];}"));
    }

    [Test]
    public void NestedLists_RawDescriptorsRequireExactLiveChildKind()
    {
        var settings=new AloeVmSettings();settings.Logging.FileEnabled=false;settings.Logging.ConsoleEnabled=false;
        var vm=new AloeVm(new AloeCompiler().Compile("function main(args:string[]):int{return 0;}"),settings);
        vm.Push(AloeValue.FromInt(42));vm.Push(AloeValue.FromString("list:int[]"));vm.Push(AloeValue.FromInt(1));vm.InvokeSyscall(EnumSyscall.ArrayCreate);var child=vm.Pop();
        vm.Push(child);vm.Push(AloeValue.FromString("nested:list:list:int[]"));vm.Push(AloeValue.FromInt(1));vm.InvokeSyscall(EnumSyscall.ArrayCreate);var parent=vm.Pop();
        vm.Push(parent);vm.Push(AloeValue.FromInt(0));vm.InvokeSyscall(EnumSyscall.ArrayGet);Assert.That(vm.Pop().AsObjectId,Is.EqualTo(child.AsObjectId));
        vm.Push(AloeValue.FromInt(42));vm.Push(AloeValue.FromString("int[]"));vm.Push(AloeValue.FromInt(1));vm.InvokeSyscall(EnumSyscall.ArrayCreate);var wrongChild=vm.Pop();
        vm.Push(parent);vm.Push(AloeValue.FromInt(0));vm.Push(wrongChild);Assert.Throws<VmException>(()=>vm.InvokeSyscall(EnumSyscall.ArraySet));
        Assert.That(vm.ValueStack.Count,Is.Zero);
    }

    private static (long ExitCode,string Output) Run(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings();settings.Logging.FileEnabled=false;settings.Logging.ConsoleEnabled=false;
        var vm=new AloeVm(module,settings);var output=new StringBuilder();vm.OutputWriter=line=>output.AppendLine(line);
        vm.RunFromEntryPoint();Assert.That(vm.ValueStack.Count,Is.EqualTo(1));return(vm.Pop().AsInt,output.ToString().Replace("\r\n","\n"));
    }
}
