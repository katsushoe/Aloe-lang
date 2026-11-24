using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private sealed class TryFlowTypes(IReadOnlyDictionary<string, ExprType>? inherited)
            : Dictionary<string, ExprType>(inherited ?? new Dictionary<string, ExprType>(), StringComparer.Ordinal)
        {
            private readonly Dictionary<string, string> _structShapes = inherited is TryFlowTypes parent
                ? new(parent._structShapes, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);

            public string? StructShape(string name) => _structShapes.GetValueOrDefault(name);

            public void Bind(string name, ExprType type, string? shape = null)
            {
                this[name] = type;
                if (shape == null) _structShapes.Remove(name);
                else _structShapes[name] = shape;
            }
        }

        private ExprType? GetNonthrowingStructFieldReadType(int start, int end,
            IReadOnlyDictionary<string, ExprType>? speculativeTypes)
            => GetNonthrowingStructPath(start, end, speculativeTypes, allowLocal: false)?.Type;

        private (ExprType Type, string? Shape)? GetNonthrowingStructPath(int start, int end,
            IReadOnlyDictionary<string, ExprType>? speculativeTypes, bool allowLocal)
        {
            while (TokenAt(start).Kind == TokenKind.LParen &&
                   FindInterfaceFlowGroupEnd(start, TokenKind.LParen, TokenKind.RParen) == end)
            {
                start++;
                end--;
            }
            if (start >= end || (!allowLocal && end - start < 3) ||
                TokenAt(start).Kind != TokenKind.Identifier) return null;
            var name = TokenAt(start).Lexeme;
            string? shape;
            if (speculativeTypes?.ContainsKey(name) == true)
                shape = (speculativeTypes as TryFlowTypes)?.StructShape(name);
            else
                shape = TryResolveLocal(name, out var receiver) && receiver.Type == ExprType.Struct
                    ? receiver.ClassName : null;
            if (shape == null) return null;
            var cursor = start + 1;
            if (cursor == end) return (ExprType.Struct, shape);
            while (cursor + 1 < end && TokenAt(cursor).Kind == TokenKind.Dot &&
                   TokenAt(cursor + 1).Kind == TokenKind.Identifier)
            {
                if (!_structs.TryGetValue(shape, out var signature)) return null;
                var field = signature.Fields.FirstOrDefault(x => x.Name == TokenAt(cursor + 1).Lexeme);
                if (field == null) return null; // A property or method can execute arbitrary code.
                cursor += 2;
                if (cursor == end)
                    return field.Type is ExprType.Int or ExprType.Bool or ExprType.Byte or ExprType.Char or
                        ExprType.Float or ExprType.Decimal or ExprType.String or ExprType.Enum or ExprType.Struct
                        ? (field.Type, field.ClassName) : null;
                if (field.Type != ExprType.Struct || field.ClassName is not string nested) return null;
                shape = nested;
            }
            return null;
        }
    }
}
