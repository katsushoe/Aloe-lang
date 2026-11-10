using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private bool TryGetReferenceFlowInitializer(int start, int end, out int initializer)
        {
            initializer = -1;
            if (TokenAt(start).Lexeme != "let" || TokenAt(start + 1).Kind != TokenKind.Identifier ||
                TokenAt(start + 2).Kind != TokenKind.Colon) return false;
            var savedPosition = _position;
            try
            {
                // Reuse the type grammar without emitting code or changing lexical bindings.
                // Collection suffixes and map annotations are references too; pipes are not.
                _position = start + 3;
                if (ParseTypeName(out var name) != ExprType.Object || IsPipeTypeName(name) ||
                    _position >= end || !Check(TokenKind.Assign)) return false;
                initializer = _position + 1;
                return true;
            }
            catch (AloeCompileException)
            {
                // Ordinary parsing owns diagnostics for invalid or unsupported annotations.
                return false;
            }
            finally { _position = savedPosition; }
        }

        private bool IsNonthrowingReferenceReturn(int start, int end,
            IReadOnlyDictionary<string, ExprType> speculativeTypes)
        {
            if (_currentFunction?.ReturnType != ExprType.Object ||
                IsPipeTypeName(_currentFunction.ReturnClassName)) return false;
            while (end - start >= 2 && TokenAt(start).Kind == TokenKind.LParen &&
                   FindInterfaceFlowGroupEnd(start, TokenKind.LParen, TokenKind.RParen) == end)
            {
                start++;
                end--;
            }
            if (end - start != 1 || TokenAt(start).Kind != TokenKind.Identifier) return false;
            var name = TokenAt(start).Lexeme;
            // Unknown shadows cannot borrow the reference kind of an outer parsed binding.
            if (speculativeTypes.TryGetValue(name, out var kind)) return kind == ExprType.Object;
            if (!TryResolveLocal(name, out var local) ||
                local.Type != ExprType.Object || IsPipeTypeName(local.ClassName)) return false;
            // Ordinary parsing validates nominal assignability. A reference-to-reference
            // return loads its handle without allocation, unboxing, dereference or cast.
            return true;
        }
    }
}
