using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private ExprType? GetNonthrowingStructFieldReadType(int start, int end,
            IReadOnlyDictionary<string, ExprType>? speculativeTypes)
        {
            if (end - start < 3 || TokenAt(start).Kind != TokenKind.Identifier) return null;
            var name = TokenAt(start).Lexeme;
            // Speculative kinds carry no nominal struct shape. Never borrow an outer
            // parsed receiver through a declaration or foreach binding that shadows it.
            if (speculativeTypes?.ContainsKey(name) == true ||
                !TryResolveLocal(name, out var receiver) || receiver.Type != ExprType.Struct ||
                receiver.ClassName is not string shape) return null;
            var cursor = start + 1;
            while (cursor + 1 < end && TokenAt(cursor).Kind == TokenKind.Dot &&
                   TokenAt(cursor + 1).Kind == TokenKind.Identifier)
            {
                if (!_structs.TryGetValue(shape, out var signature)) return null;
                var field = signature.Fields.FirstOrDefault(x => x.Name == TokenAt(cursor + 1).Lexeme);
                if (field == null) return null; // A property or method can execute arbitrary code.
                cursor += 2;
                if (cursor == end)
                    return field.Type is ExprType.Int or ExprType.Bool or ExprType.Byte or ExprType.Char or
                        ExprType.Float or ExprType.Decimal or ExprType.String or ExprType.Enum ? field.Type : null;
                if (field.Type != ExprType.Struct || field.ClassName is not string nested) return null;
                shape = nested;
            }
            return null;
        }
    }
}
