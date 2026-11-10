using Aloe.CommonLib;
using Aloe.Aloe2Wasm;
using Aloe.CompilerLib.Lexer;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class SubtractionLexingTests
{
    [TestCase("value-1")]
    [TestCase("value -1")]
    [TestCase("value- 1")]
    [TestCase("value - 1")]
    [TestCase("41-1")]
    [TestCase("-41-1")]
    [TestCase("call()-1")]
    [TestCase("values[0]-1")]
    [TestCase("box.Value-1")]
    [TestCase("(42)-1")]
    [TestCase("1.5-0.5")]
    [TestCase("2:d-1:d")]
    [TestCase("value/*comment*/-1")]
    [TestCase("value//comment\n-1")]
    [TestCase("true-1")]
    [TestCase("'A'-1")]
    [TestCase("\"s\"-1")]
    [TestCase("invalid-1")]
    [TestCase("null-1")]
    [TestCase("this-1")]
    [TestCase("value as int-1")]
    public void MinusAfterOperandIsBinaryRegardlessOfSpacing(string expression)
    {
        var tokens=AloeLexer.Parse(expression);
        Assert.That(tokens.Count(t=>t.Kind==TokenKind.Minus),Is.EqualTo(1));
        var last=tokens.Last(t=>t.Kind!=TokenKind.EndOfFile);
        Assert.That(last.Kind,Is.AnyOf(TokenKind.IntegerLiteral,TokenKind.FloatLiteral,TokenKind.DecimalLiteral));
        Assert.That(last.Lexeme.StartsWith('-'),Is.False);
    }

    [TestCase("")]
    [TestCase("return ")]
    [TestCase("var value=")]
    [TestCase("(")]
    [TestCase("[")]
    [TestCase("{")]
    [TestCase("call(")]
    [TestCase("call(1,")]
    [TestCase("value+")]
    [TestCase("value-")]
    [TestCase("value*")]
    [TestCase("value/")]
    [TestCase("value==")]
    [TestCase("value<")]
    [TestCase("not ")]
    [TestCase("if(")]
    public void PrefixPositionRetainsSignedMinimumLiteral(string prefix)
    {
        var token=AloeLexer.Parse(prefix+"-2147483648").Last(t=>t.Kind!=TokenKind.EndOfFile);
        Assert.That(token.Kind,Is.EqualTo(TokenKind.IntegerLiteral));
        Assert.That(token.Lexeme,Is.EqualTo("-2147483648"));
    }

    [Test]
    public void BinaryMinusPreservesSourceCoordinatesAcrossComments()
    {
        var tokens=AloeLexer.Parse("a//x\n-1");var minus=tokens.Single(t=>t.Kind==TokenKind.Minus);
        Assert.That((minus.Position,minus.Line,minus.Column),Is.EqualTo((5,2,1)));
        var number=tokens.Single(t=>t.Kind==TokenKind.IntegerLiteral);
        Assert.That((number.Position,number.Line,number.Column),Is.EqualTo((6,2,2)));
    }

    [TestCase("var n=43; return n-1;",42)]
    [TestCase("var n=43; return n -1;",42)]
    [TestCase("var n=43; return n- 1;",42)]
    [TestCase("var n=43; return n - 1;",42)]
    [TestCase("return 43-1;",42)]
    [TestCase("return -41-1;",-42)]
    [TestCase("return (43)-1;",42)]
    [TestCase("var values=[43]; return values[0]-1;",42)]
    [TestCase("return 0x2b-0x1;",42)]
    [TestCase("return 0b101011-0b1;",42)]
    [TestCase("return 41--1;",42)]
    [TestCase("return 41 - -1;",42)]
    [TestCase("return 44-1-1;",42)]
    [TestCase("return 44-1*2;",42)]
    [TestCase("return 2*-21;",-42)]
    [TestCase("return -2147483648;",int.MinValue)]
    [TestCase("return -2147483647-1;",int.MinValue)]
    [TestCase("var n=43; return n/*comment*/-1;",42)]
    [TestCase("var n=43; return n//comment\n-1;",42)]
    [TestCase("if(1.5-0.5 == 1.0) { return 42; } return 0;",42)]
    [TestCase("if(2.25:d-1.25:d == 1.0:d) { return 42; } return 0;",42)]
    public void VmSubtractionUsesExistingArithmetic(string body,int expected)
        => Assert.That(Run("function main(args: string[]): int { "+body+" }"),Is.EqualTo(expected));

    [Test]
    public void RecursiveStaticArgumentNeedsNoWhitespace()
        => Assert.That(Run("""
class Utility { public static method count(n: int): int { if(n == 0) { return 42; } return Utility.count(n-1); } }
function main(args: string[]): int { return Utility.count(3); }
"""),Is.EqualTo(42));

    [Test]
    public void GetterAndFunctionResultsMayBeLeftOperands()
        => Assert.That(Run("""
class Box { public property Value: int { get { return 44; } } }
function one(): int { return 1; }
function main(args: string[]): int { var box=new Box(); return box.Value-one()-1; }
"""),Is.EqualTo(42));

    [Test]
    public void DirectWasmUsesSubtractionOpcode()
    {
        var module=new AloeCompiler().CompileForWasm("function main(args: string[]): int { var n=43; return n-1; }");
        var bytes=new AloeWasmCompiler().Compile(module);
        Assert.That(bytes.Length,Is.GreaterThan(8));
        Assert.That(bytes.Take(4),Is.EqualTo(new byte[]{0,97,115,109}));
    }

    [TestCase("return true-1;")]
    public void InvalidOperandsRemainRejected(string body)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args: string[]): int { "+body+" }"));

    [Test]
    public void WideIntegerLiteralRetainsRuntimeOperandRangeCheck()
        => Assert.Throws<OverflowException>(()=>Run("function main(args: string[]): int { return 0-2147483648; }"));

    private static long Run(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings();settings.Logging.ConsoleEnabled=false;settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings);vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count,Is.EqualTo(1));return vm.Pop().AsInt;
    }
}
