using Aloe.CompilerLib.Lexer;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class TaskDeclarationTests
{
    [Test]
    public void TaskKeywordDoesNotConsumeIdentifierPrefixesOrStrings()
    {
        var tokens=AloeLexer.Parse("task taskWorker task2 \"task\" // task\n");
        Assert.That(tokens.Count(token => token.Kind==TokenKind.Keyword && token.Lexeme=="task"),Is.EqualTo(1));
        Assert.That(tokens.Where(token => token.Kind==TokenKind.Identifier).Select(token => token.Lexeme),Is.EqualTo(new[]{"taskWorker","task2"}));
    }

    [TestCase("int","int")]
    [TestCase("bool","bool")]
    [TestCase("boolean","bool")]
    [TestCase("byte","byte")]
    [TestCase("char","char")]
    [TestCase("float","float")]
    [TestCase("decimal","decimal")]
    [TestCase("string","string")]
    [TestCase("object","object")]
    [TestCase("State","State")]
    public void InitialSignatureTypesAreRetained(string type,string expected)
    {
        var task=Read($$"""
task Worker { field stored: {{type}}; construct(input: {{type}}) { this.stored=input; }
 method notify(input: {{type}}): void { } method run(): void { } }
class State { }
""").Single();
        Assert.That(task.Fields.Single().TypeName,Is.EqualTo(expected));
        Assert.That(task.ConstructorParameters.Single().TypeName,Is.EqualTo(expected));
        Assert.That(task.Methods.Single(method=>method.Name=="notify").Parameters.Single().TypeName,Is.EqualTo(expected));
    }

    [Test]
    public void DeclarationOrderAndDefaultConstructorAreRetained()
    {
        var tasks=Read("task First { method run(): void { } } class State { } task Second { method run(): void { } }");
        Assert.That(tasks.Select(task=>task.Name),Is.EqualTo(new[]{"First","Second"}));
        Assert.That(tasks[0].ConstructorBody,Is.Null);
        Assert.That(tasks[0].ConstructorParameters,Is.Empty);
    }

    [TestCase("class Worker { } task Worker { method run(): void { } }")]
    [TestCase("task Worker { method run(): void { } } class Worker { }")]
    [TestCase("enum Worker { One } task Worker { method run(): void { } }")]
    public void TaskNamesCannotCollideWithOtherTopLevelTypes(string source)=>Reject(source,"unique");

    [TestCase("int")]
    [TestCase("Integer")]
    [TestCase("ExceptionInfo")]
    [TestCase("void")]
    public void BuiltinTaskNamesAreRejected(string name)=>Reject("task "+name+" { method run(): void { } }","built-in");

    [TestCase("int[]")]
    [TestCase("int{}")]
    [TestCase("int()")]
    [TestCase("map<int>")]
    [TestCase("E")]
    [TestCase("Point")]
    [TestCase("I")]
    [TestCase("pipe<int>")]
    public void UnsupportedSignatureTypesAreRejected(string type)=>Reject(
        "enum E { One } struct Point { field x: int; } interface I { method emit(): void; } task Worker { field stored: "+type+"; method run(): void { } }");

    [TestCase("field other: Worker;")]
    [TestCase("construct(other: Worker) { }")]
    [TestCase("method notify(other: Worker): void { }")]
    public void TaskHandlesAreRejectedInTaskFieldsAndParameters(string member)=>Reject("task Worker { "+member+" method run(): void { } }","Task handles");

    [TestCase("int")]
    [TestCase("object")]
    [TestCase("Worker")]
    public void InitialTaskMethodsCannotReturnValues(string type)=>Reject("task Worker { method notify(): "+type+" { } method run(): void { } }","return void");

    [TestCase("start")]
    [TestCase("stop")]
    public void LifecycleMethodsCannotBeRedeclared(string name)=>Reject("task Worker { method "+name+"(): void { } method run(): void { } }","built-in");

    [TestCase("task Worker { }")]
    [TestCase("task Worker { method run(n: int): void { } }")]
    [TestCase("task Worker { method run(): void { } method run(): void { } }")]
    public void SchedulerRunSignatureIsRequiredAndUnique(string source)=>Reject(source);

    [TestCase("field count: int; field count: int;")]
    [TestCase("field notify: int; method notify(): void { }")]
    [TestCase("method notify(): void { } method notify(): void { }")]
    public void MemberNamesAreUnique(string members)=>Reject("task Worker { "+members+" method run(): void { } }","unique");

    [TestCase("public method notify(): void { }")]
    [TestCase("async method notify(): void { }")]
    [TestCase("static method notify(): void { }")]
    [TestCase("public property Value: int { get { return 1; } }")]
    [TestCase("readonly field count: int;")]
    public void UnsupportedMemberModifiersAreRejected(string member)=>Reject("task Worker { "+member+" method run(): void { } }");

    [Test]
    public void TaskDeclarationModifiersAreRejected()=>Reject("sealed task Worker { method run(): void { } }","modifiers");

    [Test]
    public void DuplicateConstructorsAreRejected()=>Reject("task Worker { construct() { } construct() { } method run(): void { } }","constructor");

    [Test]
    public void DuplicateParameterNamesAreRejected()=>Reject("task Worker { construct(n: int,n: int) { } method run(): void { } }","unique");

    [TestCase("task Worker { method run(): void {")]
    [TestCase("task Worker { construct() {")]
    public void UnterminatedBodiesHaveSourceLocations(string source)=>Reject(source,"line");

    [TestCase("function main(args: string[]): int { task Worker { method run(): void { } } return 0; }")]
    [TestCase("task Worker { method run(): void { task Nested { method run(): void { } } } }")]
    public void NestedTaskDeclarationsAreRejected(string source)=>Reject(source);

    [Test]
    public void BodyRangesKeepNestedBracesAndStringLiterals()
    {
        var tokens=AloeLexer.Parse("task Worker { method run(): void { print(\"} task {\"); if(true) { print(1); } } }");
        var body=TaskDeclarationReader.Read(tokens).Single().Methods.Single().Body;
        Assert.That(tokens[body.Start].Kind,Is.EqualTo(TokenKind.LBrace));
        Assert.That(tokens[body.End-1].Kind,Is.EqualTo(TokenKind.RBrace));
        Assert.That(tokens.Skip(body.Start).Take(body.End-body.Start).Count(token=>token.Kind==TokenKind.LBrace),Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ValidTaskDeclarationsHaveExplicitLoweringBoundary(bool wasm)
    {
        var compiler=new AloeCompiler();
        var error=Assert.Throws<AloeCompileException>(()=>
        {
            if(wasm) compiler.CompileForWasm("task Worker { method run(): void { } } function main(args: string[]): int { return 0; }");
            else compiler.Compile("task Worker { method run(): void { } } function main(args: string[]): int { return 0; }");
        });
        Assert.That(error!.Message,Does.Contain("execution lowering is not implemented"));
    }

    [Test]
    public void InvalidTaskHeaderIsReportedBeforeLoweringBoundary()
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("task Worker { method start(): void { } method run(): void { } }"));
        Assert.That(error!.Message,Does.Contain("built-in"));
    }

    [Test]
    public void ExistingProgramsCanContainTaskTextInStringsAndComments()
    {
        var module=new AloeCompiler().Compile("// task Worker\nfunction main(args: string[]): int { print(\"task\"); return 42; }");
        var settings=new AloeVmSettings();settings.Logging.ConsoleEnabled=false;settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings);vm.OutputWriter=_=>{};vm.RunFromEntryPoint();Assert.That(vm.Pop().AsInt,Is.EqualTo(42));
    }

    [Test]
    public void TaskIsNowReservedAsAnIdentifier()=>Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args: string[]): int { var task=1; return task; }"));

    [Test]
    public void StringLiteralsCannotStandInForMemberKeywords()=>Reject("task Worker { \"method\" run(): void { } }");

    private static IReadOnlyList<TaskDeclaration> Read(string source)=>TaskDeclarationReader.Read(AloeLexer.Parse(source));
    private static void Reject(string source,string? message=null)
    {
        var error=Assert.Throws<AloeCompileException>(()=>Read(source));
        if(message!=null) Assert.That(error!.Message,Does.Contain(message));
    }
}
