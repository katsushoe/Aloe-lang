using Aloe.CompilerLib.Lexer;
using Aloe.CommonLib.Constants;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private FunctionSignature? ScanScalarPropertySetter(ref int position, ClassSignature owner,
            AloeToken property, ExprType type, string? typeName, bool isStatic)
        {
            if (TokenAt(position).Lexeme != "set") return null;
            var token = TokenAt(position++);
            if (isStatic || _forWasm)
                throw Error(token, "Scalar property setters require class instance VM execution.");
            if (type is not (ExprType.Int or ExprType.Bool or ExprType.Byte or ExprType.Char or
                ExprType.Float or ExprType.Decimal or ExprType.String))
                throw Error(token, "Property setters currently support the seven scalar types only.");
            ExpectAt(ref position, TokenKind.LBrace);
            var start = position;
            var depth = 1;
            while (depth > 0)
            {
                var current = TokenAt(position++);
                if (current.Kind == TokenKind.EndOfFile) throw Error(current, "Unterminated property setter.");
                if (current.Kind == TokenKind.LBrace) depth++;
                else if (current.Kind == TokenKind.RBrace) depth--;
            }
            var value = new AloeToken(TokenKind.Identifier, "value", token.Position, token.Line, token.Column);
            var setter = new FunctionSignature($"{owner.Name}.set.{property.Lexeme}", _functions.Count,
                new[] { new ParameterSignature(value, type, typeName) }, ExprType.Void,
                start, position - 1, false, DeclaringClass: owner.Name);
            _functions.Add(setter);
            return setter;
        }

        private bool IsScalarPropertyAssignment()
        {
            if (Peek(1).Kind != TokenKind.Dot || Peek(2).Kind != TokenKind.Identifier ||
                Peek(3).Kind != TokenKind.Assign) return false;
            if (Current.Lexeme == "this")
                return _currentFunction?.DeclaringClass is string owner &&
                    FindField(owner, Peek(2).Lexeme) == null && FindProperty(owner, Peek(2).Lexeme) != null;
            return Current.Kind == TokenKind.Identifier && TryResolveLocal(Current.Lexeme, out var local) &&
                local.Type == ExprType.Object && FindProperty(local.ClassName, Peek(2).Lexeme) != null;
        }

        private void ParseScalarPropertyAssignment()
        {
            var receiver = Advance();
            LocalInfo local;
            if (receiver.Lexeme == "this")
            {
                if (_currentFunction?.DeclaringClass is not string owner || _currentFunction.IsStaticMethod)
                    throw Error(receiver, "this property assignment requires class instance code.");
                local = new LocalInfo(0, ExprType.Object, owner);
            }
            else local = ResolveLocal(receiver);
            Expect(TokenKind.Dot);
            var member = Expect(TokenKind.Identifier);
            var property = FindProperty(local.ClassName, member.Lexeme)!;
            var setter = property.Setter ?? throw Error(member, "Cannot assign a getter-only property.");
            Expect(TokenKind.Assign);
            Emit(EnumOpcode.LoadLocal, local.Slot);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ReferenceRequireLive);
            var expressionStart = _code.Count;
            var actual = ParseExpectedExpression(property.Type, property.ClassName);
            actual = AdaptByteLiteral(member, property.Type, actual, expressionStart);
            RequireAssignable(member, property.Type, actual, property.ClassName, _lastExpressionClassName);
            Expect(TokenKind.Semicolon);
            Emit(EnumOpcode.Call, setter.Index);
        }
    }
}
