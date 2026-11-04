using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private (AloeToken Member, MethodSignature Method, string ClassName) ParseThisClassMethodTarget()
        {
            var receiver = Advance();
            Expect(TokenKind.Dot);
            var member = Expect(TokenKind.Identifier);
            var function = _currentFunction;
            if (function?.DeclaringClass is not string className || function.IsStaticMethod)
                throw Error(receiver, "this method calls require class instance code.");
            var method = FindMethod(className, member.Lexeme);
            if (method == null || method.Function.IsStaticMethod)
                throw Error(member, $"Unknown instance method '{className}.{member.Lexeme}'.");
            return (member, method, className);
        }

        private ExprType ParseThisClassMethodValue()
        {
            var target = ParseThisClassMethodTarget();
            if (target.Method.Function.IsPublicAsync)
                throw Error(target.Member, "Public async instance methods are commands and cannot be used as values.");
            return ParseResolvedFunctionCall(target.Member, target.Method.Function, requireValue: true, isInstanceSelfCall: true);
        }

        private void ParseThisClassMethodStatement()
        {
            var target = ParseThisClassMethodTarget();
            if (target.Method.Function.IsPublicAsync)
            {
                ParseInstanceAsyncMethodCallStatement(new LocalInfo(0, ExprType.Object, target.ClassName),
                    target.ClassName, target.Method, target.Member);
                return;
            }
            var type = ParseResolvedFunctionCall(target.Member, target.Method.Function,
                requireValue: false, isInstanceSelfCall: true);
            if (type != ExprType.Void)
                throw Error(target.Member, "Ignoring a non-void method result is not supported by the current compiler subset.");
            Expect(TokenKind.Semicolon);
        }
    }
}
