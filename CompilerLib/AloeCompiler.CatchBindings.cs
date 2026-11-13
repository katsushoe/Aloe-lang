using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private void AddCatchFlowBindings(int position, Dictionary<int, HashSet<string>> bindings)
        {
            if (TokenAt(position).Lexeme != "catch" || TokenAt(position + 1).Kind != TokenKind.LParen ||
                TokenAt(position + 2).Kind != TokenKind.Identifier || TokenAt(position + 3).Kind != TokenKind.Colon ||
                TokenAt(position + 4).Kind != TokenKind.Identifier || TokenAt(position + 5).Kind != TokenKind.RParen ||
                TokenAt(position + 6).Kind != TokenKind.LBrace) return;
            bindings[position + 6] = new HashSet<string>(StringComparer.Ordinal) { TokenAt(position + 2).Lexeme };
        }
    }
}
