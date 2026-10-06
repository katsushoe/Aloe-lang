using Aloe.CompilerLib.Lexer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Aloe.CompilerLib.Tests
{
    [TestFixture]
    public class AloeLexerTests
    {
        // ----------------------------------------
        // ヘルパ
        // ----------------------------------------

        private static List<AloeToken> Lex(string src)
            => AloeLexer.Parse(src);

        private static string[] Kinds(List<AloeToken> tokens)
            => tokens.Select(t => t.Kind.ToString()).ToArray();

        private static string[] LexemesOfKind(List<AloeToken> tokens, TokenKind kind)
            => tokens.Where(t => t.Kind == kind).Select(t => t.Lexeme).ToArray();

        private static AloeToken[] NonEof(List<AloeToken> tokens)
            => tokens.Where(t => t.Kind != TokenKind.EndOfFile).ToArray();

        // ----------------------------------------
        // 予約語・識別子
        // ----------------------------------------

        [Test]
        public void Keywords_AreRecognized()
        {
            var src = @"
                class struct trait interface namespace field readonly bitfield is delete main
                var let const async sealed extends implements import
            ";

            var tokens = Lex(src);
            var keywords = LexemesOfKind(tokens, TokenKind.Keyword);

            string[] expected =
            {
                "class", "struct", "trait", "interface", "namespace",
                "field", "readonly", "bitfield", "is", "delete",
                "main", "var", "let", "const", "async", "sealed",
                "extends", "implements", "import"
            };

            Assert.That(expected, Is.SubsetOf(keywords));
        }

        [Test]
        public void Identifiers_And_Keywords_AreSeparated()
        {
            var src = @"var fb = new FizzBuzz;";
            var tokens = NonEof(Lex(src));

            Assert.That(tokens.Select(t => t.Kind).ToArray(), Is.EqualTo(new[]
            {
                TokenKind.Keyword,     // var
                TokenKind.Identifier,  // fb
                TokenKind.Assign,      // =
                TokenKind.Keyword,     // new
                TokenKind.Identifier,  // FizzBuzz
                TokenKind.Semicolon    // ;
            }));

            Assert.That(tokens[0].Lexeme, Is.EqualTo("var"));
            Assert.That(tokens[1].Lexeme, Is.EqualTo("fb"));
            Assert.That(tokens[3].Lexeme, Is.EqualTo("new"));
            Assert.That(tokens[4].Lexeme, Is.EqualTo("FizzBuzz"));
        }

        // ----------------------------------------
        // 数値リテラル
        // ----------------------------------------

        [Test]
        public void Integer_Hex_Binary_Literals_AreRecognized()
        {
            var src = @"0 10 -5 0x1A 0XFF 0b1010 0B11;";
            var tokens = NonEof(Lex(src));

            var integerLexemes = tokens
                .Where(t => t.Kind == TokenKind.IntegerLiteral)
                .Select(t => t.Lexeme)
                .ToArray();

            string[] expected =
            {
                "0",
                "10",
                "-5",
                "0x1A",
                "0XFF",
                "0b1010",
                "0B11",
            };

            Assert.That(integerLexemes, Is.EqualTo(expected),
                "整数リテラルの字句解析が想定と異なります");
        }

        [Test]
        public void Negative_Integer_In_Expression_IsSingleToken()
        {
            var src = @"var x = -5;";
            var tokens = NonEof(Lex(src));

            Assert.That(tokens.Select(t => t.Kind).ToArray(), Is.EqualTo(new[]
            {
                TokenKind.Keyword,        // var
                TokenKind.Identifier,     // x
                TokenKind.Assign,         // =
                TokenKind.IntegerLiteral, // -5
                TokenKind.Semicolon       // ;
            }));

            Assert.That(tokens[3].Lexeme, Is.EqualTo("-5"));
        }

        [Test]
        public void Float_And_Decimal_Literals_AreRecognized()
        {
            var src = @"10.5 0.0 -3.14 10.12345678901234567890:d 1.0:D;";
            var tokens = NonEof(Lex(src));

            var floats = tokens
                .Where(t => t.Kind == TokenKind.FloatLiteral)
                .Select(t => t.Lexeme)
                .ToArray();

            var decimals = tokens
                .Where(t => t.Kind == TokenKind.DecimalLiteral)
                .Select(t => t.Lexeme)
                .ToArray();

            // CollectionAssert.AreEquivalent(new[] { "10.5", "0.0", "-3.14" }, floats);
            Assert.That(
                floats,
                Is.EquivalentTo(new[] { "10.5", "0.0", "-3.14" })
            );

            // CollectionAssert.AreEquivalent(
            //     new[] { "10.12345678901234567890:d", "1.0:D" },
            //     decimals);
            Assert.That(
                decimals,
                Is.EquivalentTo(new[]
                {
            "10.12345678901234567890:d",
            "1.0:D"
                })
            );
        }


        // ----------------------------------------
        // 文字列・文字リテラル
        // ----------------------------------------

        [Test]
        public void StringLiteral_WithEscapes_IsRecognized()
        {
            var src = "\"Hello, Aloe!\" \"line\\nnext\";";
            var tokens = NonEof(Lex(src));

            var strings = tokens.Where(t => t.Kind == TokenKind.StringLiteral).ToArray();
            Assert.That(strings.Length, Is.EqualTo(2));

            Assert.That(strings[0].Lexeme, Is.EqualTo("\"Hello, Aloe!\""));
            Assert.That(strings[1].Lexeme, Is.EqualTo("\"line\\nnext\""));
        }

        [Test]
        public void CharLiteral_WithAndWithoutEscape_IsRecognized()
        {
            var src = "'A' '\\n';";
            var tokens = NonEof(Lex(src));

            var chars = tokens.Where(t => t.Kind == TokenKind.CharLiteral).ToArray();
            Assert.That(chars.Length, Is.EqualTo(2));

            Assert.That(chars[0].Lexeme, Is.EqualTo("'A'"));
            Assert.That(chars[1].Lexeme, Is.EqualTo("'\\n'"));
        }

        // ----------------------------------------
        // コメント
        // ----------------------------------------

        [Test]
        public void Comments_AreSkipped()
        {
            var src = @"
// line comment
var x = 10; /* block
comment */
var y = x;";
            var tokens = NonEof(Lex(src));

            var hasCommentLookalike = tokens.Any(t =>
                t.Lexeme.StartsWith("//", StringComparison.Ordinal) ||
                t.Lexeme.StartsWith("/*", StringComparison.Ordinal));

            // コメントトークンが生成されていないこと
            Assert.That(hasCommentLookalike, Is.False);

            // ざっくり個数チェック（var x = 10 ; var y = x ;)
            // var(1) x(2) =(3) 10(4) ;(5) var(6) y(7) =(8) x(9) ;(10)
            Assert.That(tokens.Length, Is.EqualTo(10));
        }

        // ----------------------------------------
        // trait / is / as / delete
        // ----------------------------------------

        [Test]
        public void Trait_Add_With_Alias_UsesKeywordsAndPlus()
        {
            var src = @"var obj2 = obj + Trait_A as a;";
            var tokens = NonEof(Lex(src));

            Assert.That(tokens.Select(t => t.Kind).ToArray(), Is.EqualTo(new[]
            {
                TokenKind.Keyword,     // var
                TokenKind.Identifier,  // obj2
                TokenKind.Assign,      // =
                TokenKind.Identifier,  // obj
                TokenKind.Plus,        // +
                TokenKind.Identifier,  // Trait_A
                TokenKind.Keyword,     // as
                TokenKind.Identifier,  // a
                TokenKind.Semicolon    // ;
            }));

            Assert.That(tokens[0].Lexeme, Is.EqualTo("var"));
            Assert.That(tokens[6].Lexeme, Is.EqualTo("as"));
        }

        [Test]
        public void Is_Keyword_IsRecognized()
        {
            var src = @"if (obj is FizzBuzz) { }";
            var tokens = NonEof(Lex(src));

            Assert.That(
                tokens.Any(t => t.Kind == TokenKind.Keyword && t.Lexeme == "is"),
                "is キーワードが認識されていません。");
        }

        [Test]
        public void Delete_Keyword_IsRecognized()
        {
            var src = @"delete obj;";
            var tokens = NonEof(Lex(src));

            Assert.That(tokens.Select(t => t.Kind).ToArray(), Is.EqualTo(new[]
            {
                TokenKind.Keyword,     // delete
                TokenKind.Identifier,  // obj
                TokenKind.Semicolon    // ;
            }));

            Assert.That(tokens[0].Lexeme, Is.EqualTo("delete"));
        }

        // ----------------------------------------
        // bitfield enum ヘッダ部だけざっくり
        // ----------------------------------------

        [Test]
        public void BitfieldEnum_Header_IsLexedCorrectly()
        {
            var src = @"
bitfield enum LogFlags {
    None : b(0),
    Info : b(3),
    Warn,
}";
            var tokens = NonEof(Lex(src));

            Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.Keyword));
            Assert.That(tokens[0].Lexeme, Is.EqualTo("bitfield"));

            Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.Keyword));
            Assert.That(tokens[1].Lexeme, Is.EqualTo("enum"));

            Assert.That(tokens[2].Kind, Is.EqualTo(TokenKind.Identifier));
            Assert.That(tokens[2].Lexeme, Is.EqualTo("LogFlags"));

            Assert.That(tokens[3].Kind, Is.EqualTo(TokenKind.LBrace)); // {
        }

        // ----------------------------------------
        // FizzBuzz + main サンプルのざっくり検証
        // ----------------------------------------

        private const string FizzBuzzSample = @"
class FizzBuzz {
    construct() {
    }

    method run(): void {
        var i = 1;

        while (i <= 100) {
            var output = """";

            if (i % 3 == 0) {
                output = output + ""Fizz"";
            }

            if (i % 5 == 0) {
                output = output + ""Buzz"";
            }

            if (output == """") {
                // どちらにも当てはまらないときは数値を出力
                print(i);
            } else {
                // ""Fizz"", ""Buzz"", ""FizzBuzz"" を出力
                print(output);
            }

            i = i + 1;
        }
    }
}

main(args: string[]) {
    var fb = new FizzBuzz();
    fb.run();
}
";

        [Test]
        public void FizzBuzzSample_StartsAndEndsWithExpectedTokens()
        {
            var tokens = AloeLexer.Parse(FizzBuzzSample);
            var nonEof = NonEof(tokens);

            // 先頭数トークン: class FizzBuzz {
            Assert.That(nonEof[0].Kind, Is.EqualTo(TokenKind.Keyword));
            Assert.That(nonEof[0].Lexeme, Is.EqualTo("class"));

            Assert.That(nonEof[1].Kind, Is.EqualTo(TokenKind.Identifier));
            Assert.That(nonEof[1].Lexeme, Is.EqualTo("FizzBuzz"));

            Assert.That(nonEof[2].Kind, Is.EqualTo(TokenKind.LBrace));

            // main ブロック開始付近が含まれているか
            var mainIndex = Array.FindIndex(nonEof, t =>
                t.Kind == TokenKind.Keyword && t.Lexeme == "main");
            Assert.That(mainIndex, Is.GreaterThan(0), "main キーワードが見つかりません。");

            // main(args: string[]) の形をざっくり確認
            Assert.That(nonEof[mainIndex + 1].Kind, Is.EqualTo(TokenKind.LParen));
            Assert.That(nonEof[mainIndex + 2].Kind, Is.EqualTo(TokenKind.Identifier)); // args
            Assert.That(nonEof[mainIndex + 3].Kind, Is.EqualTo(TokenKind.Colon));
            Assert.That(nonEof[mainIndex + 4].Kind, Is.EqualTo(TokenKind.Identifier)); // string
            Assert.That(nonEof[mainIndex + 5].Kind, Is.EqualTo(TokenKind.LBracket));
            Assert.That(nonEof[mainIndex + 6].Kind, Is.EqualTo(TokenKind.RBracket));
            Assert.That(nonEof[mainIndex + 7].Kind, Is.EqualTo(TokenKind.RParen));
            Assert.That(nonEof[mainIndex + 8].Kind, Is.EqualTo(TokenKind.LBrace));

            // 最後のトークンは必ず EndOfFile
            Assert.That(tokens.Last().Kind, Is.EqualTo(TokenKind.EndOfFile));
        }
    }
}
