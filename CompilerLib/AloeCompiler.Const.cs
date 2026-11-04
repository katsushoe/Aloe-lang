using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        // Slots are unique within a function, including expired lexical scopes.
        private readonly HashSet<int> _immutableLocalSlots = new();

        private void RequireMutableLocal(AloeToken name, LocalInfo local)
        {
            if (_immutableLocalSlots.Contains(local.Slot))
                throw Error(name, $"Cannot write to const local '{name.Lexeme}'.");
        }
    }
}
