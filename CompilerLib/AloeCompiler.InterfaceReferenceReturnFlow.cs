using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private bool IsReferenceFlowAnnotation(string name)
            => name == "object" || _classes.ContainsKey(name) || _interfaces.ContainsKey(name) ||
               TryGetNamedBoxType(name, out _);

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
