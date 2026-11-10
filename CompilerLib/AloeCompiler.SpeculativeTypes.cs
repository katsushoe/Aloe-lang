using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private Dictionary<string, ExprType> GetLoopPrimitiveBindingTypes(int bodyStart,
            IEnumerable<string> inheritedShadows)
        {
            var types = GetUnknownShadowTypes(inheritedShadows);
            // Resolve only a declared direct source, before either body binding shadows its name.
            // Nominal/unknown kinds and complex headers retain their conservative shadow masks.
            if (TokenAt(_position).Lexeme != "foreach" || TokenAt(_position + 1).Kind != TokenKind.LParen ||
                TokenAt(_position + 2).Kind != TokenKind.Identifier) return types;
            var firstName = TokenAt(_position + 2).Lexeme;
            string? secondName = null;
            var cursor = _position + 3;
            if (TokenAt(cursor).Kind == TokenKind.Comma)
            {
                if (TokenAt(cursor + 1).Kind != TokenKind.Identifier) return types;
                secondName = TokenAt(cursor + 1).Lexeme;
                cursor += 2;
            }
            if (TokenAt(cursor).Lexeme != "in" || TokenAt(bodyStart).Kind != TokenKind.LBrace ||
                bodyStart != FindInterfaceFlowGroupEnd(_position + 1, TokenKind.LParen, TokenKind.RParen))
                return types;
            var sourceStart = cursor + 1;
            var sourceEnd = bodyStart - 1;
            // Parentheses change grouping, not evaluation: unwrap only the complete source expression.
            while (sourceEnd - sourceStart >= 2 && TokenAt(sourceStart).Kind == TokenKind.LParen &&
                   FindInterfaceFlowGroupEnd(sourceStart, TokenKind.LParen, TokenKind.RParen) == sourceEnd)
            {
                sourceStart++;
                sourceEnd--;
            }
            if (sourceEnd - sourceStart != 1 || TokenAt(sourceStart).Kind != TokenKind.Identifier ||
                !TryResolveLocal(TokenAt(sourceStart).Lexeme, out var source) || source.Type != ExprType.Object)
                return types;
            if (TryGetMapTypes(source.ClassName, out var keyType, out _, out var valueType, out _))
            {
                SetPrimitive(firstName, keyType);
                if (secondName != null) SetPrimitive(secondName, valueType);
                return types;
            }
            if (secondName != null) return types;
            if (TryGetArrayElementType(source.ClassName, out var elementType) ||
                sourceStart == cursor + 1 && TryGetPipeElementType(source.ClassName, out elementType))
                SetPrimitive(firstName, elementType);
            return types;

            void SetPrimitive(string name, ExprType type)
            {
                if (type is ExprType.Int or ExprType.Bool or ExprType.Byte or ExprType.Char or
                    ExprType.Float or ExprType.Decimal or ExprType.String)
                    types[name] = type;
            }
        }

        private Dictionary<string, ExprType> GetSpeculativePrimitiveTypes(int start, int end,
            IReadOnlyDictionary<string, ExprType> inheritedTypes)
        {
            var scopes = new Stack<Dictionary<string, ExprType>>();
            scopes.Push(new Dictionary<string, ExprType>(inheritedTypes, StringComparer.Ordinal));
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
                        foreach (var name in names) scopes.Peek()[name] = ExprType.Unknown;
                    continue;
                }
                if (token.Kind == TokenKind.RBrace)
                {
                    if (scopes.Count > 1) scopes.Pop();
                    continue;
                }
                if (token.Kind != TokenKind.Keyword || token.Lexeme is not ("let" or "var" or "const") ||
                    TokenAt(cursor + 1).Kind != TokenKind.Identifier) continue;

                var statementEnd = FindInterfaceFlowStatementEnd(cursor, end);
                var type = (ExprType?)null;
                var referenceDeclaration = TryGetReferenceFlowInitializer(cursor, end, out var referenceInitializer);
                var initializer = token.Lexeme == "var" && TokenAt(cursor + 2).Kind == TokenKind.Assign
                    ? cursor + 3
                    : referenceDeclaration ? referenceInitializer
                    : (token.Lexeme is "let" or "const") && TokenAt(cursor + 2).Kind == TokenKind.Colon &&
                      TokenAt(cursor + 4).Kind == TokenKind.Assign ? cursor + 5 : -1;
                // Read before binding: var alias = alias reads the outer lexical binding.
                if (initializer >= 0 && statementEnd >= initializer)
                {
                    type = GetNonthrowingDirectReadType(initializer, statementEnd, scopes.Peek());
                    if (type == null && TryGetDecimalConstantFlow(initializer, statementEnd, out var decimalFlow) &&
                        decimalFlow == ArithmeticTryFlow.FallsThrough)
                        type = ExprType.Decimal;
                    if (type == null && TryGetFloatConstantFlow(initializer, statementEnd, out var floatFlow) &&
                        floatFlow == ArithmeticTryFlow.FallsThrough)
                        type = ExprType.Float;
                    if ((token.Lexeme is "let" or "const") &&
                        !(type == ExprType.Object && referenceDeclaration) &&
                        !(token.Lexeme == "let" && type == ExprType.Enum &&
                          _enumMembers.ContainsKey(TokenAt(cursor + 3).Lexeme)) &&
                        TokenAt(cursor + 3).Lexeme != type?.ToString().ToLowerInvariant())
                        type = null;
                }
                scopes.Peek()[TokenAt(cursor + 1).Lexeme] =
                    type is ExprType.Int or ExprType.Bool or ExprType.Byte or ExprType.Char or
                        ExprType.Float or ExprType.Decimal or ExprType.String or ExprType.Object or ExprType.Enum ? type.Value : ExprType.Unknown;
                if (statementEnd >= cursor) cursor = statementEnd;
            }
            return scopes.Peek();
        }
    }
}
