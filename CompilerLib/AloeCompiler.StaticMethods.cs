using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private static bool IsStaticScalarType(ExprType type)
            => type is ExprType.Int or ExprType.Bool or ExprType.Byte or ExprType.Char or
                ExprType.Float or ExprType.Decimal or ExprType.String;

        private ExprType ParseSynchronousStaticMethodValue()
        {
            var type = Expect(TokenKind.Identifier);
            var classInfo = _classes[type.Lexeme];
            Expect(TokenKind.Dot);
            var member = Expect(TokenKind.Identifier);
            var method = classInfo.SynchronousStaticMethods.FirstOrDefault(x => x.Name == member.Lexeme)
                ?? throw Error(member, $"'{classInfo.Name}.{member.Lexeme}' is not a public synchronous static method.");
            return ParseResolvedFunctionCall(member, method.Function, requireValue: true, isInstanceSelfCall: false);
        }

        private void ParseClassStaticMethodCallStatement()
        {
            var type = Expect(TokenKind.Identifier);
            var classInfo = _classes[type.Lexeme];
            Expect(TokenKind.Dot);
            var member = Expect(TokenKind.Identifier);
            var method = classInfo.SynchronousStaticMethods.FirstOrDefault(x => x.Name == member.Lexeme);
            if (method == null)
            {
                ParseStaticAsyncMethodCallStatement(classInfo, member);
                return;
            }
            var result = ParseResolvedFunctionCall(member, method.Function, requireValue: false, isInstanceSelfCall: false);
            if (result != ExprType.Void)
                throw Error(member, "Ignoring a non-void static method result is not supported.");
            Expect(TokenKind.Semicolon);
        }
    }
}
