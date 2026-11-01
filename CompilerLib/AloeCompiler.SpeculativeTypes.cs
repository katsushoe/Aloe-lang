using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private Dictionary<string, ExprType> GetSpeculativePrimitiveTypes(int start, int end,
            IEnumerable<string> inheritedShadows)
        {
            var scopes = new Stack<Dictionary<string, ExprType>>();
            scopes.Push(GetUnknownShadowTypes(inheritedShadows));
            var bindings = new Dictionary<int, HashSet<string>>();
            for (var cursor = start; cursor < end; cursor++)
            {
                var token = TokenAt(cursor);
                if (token.Kind == TokenKind.Keyword && token.Lexeme == "foreach")
                    AddForeachFlowBindings(cursor, bindings);
                if (token.Kind == TokenKind.LBrace)
                {
                    scopes.Push(new Dictionary<string, ExprType>(scopes.Peek(), StringComparer.Ordinal));
                    if (bindings.TryGetValue(cursor, out var names))
                        foreach (var name in names) scopes.Peek()[name] = ExprType.Object;
                    continue;
                }
                if (token.Kind == TokenKind.RBrace)
                {
                    if (scopes.Count > 1) scopes.Pop();
                    continue;
                }
                if (token.Kind != TokenKind.Keyword || token.Lexeme is not ("let" or "var") ||
                    TokenAt(cursor + 1).Kind != TokenKind.Identifier) continue;

                var statementEnd = FindInterfaceFlowStatementEnd(cursor, end);
                var type = (ExprType?)null;
                var initializer = token.Lexeme == "var" && TokenAt(cursor + 2).Kind == TokenKind.Assign
                    ? cursor + 3
                    : token.Lexeme == "let" && TokenAt(cursor + 2).Kind == TokenKind.Colon &&
                      TokenAt(cursor + 4).Kind == TokenKind.Assign ? cursor + 5 : -1;
                // Read before binding: var alias = alias reads the outer lexical binding.
                if (initializer >= 0 && statementEnd >= initializer)
                {
                    type = GetNonthrowingDirectReadType(initializer, statementEnd, scopes.Peek());
                    if (token.Lexeme == "let" && TokenAt(cursor + 3).Lexeme != type?.ToString().ToLowerInvariant())
                        type = null;
                }
                scopes.Peek()[TokenAt(cursor + 1).Lexeme] =
                    type is ExprType.Int or ExprType.Bool or ExprType.Byte or ExprType.Char or
                        ExprType.Float or ExprType.Decimal or ExprType.String ? type.Value : ExprType.Object;
                if (statementEnd >= cursor) cursor = statementEnd;
            }
            return scopes.Peek();
        }
    }
}
