using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private void RequireWritableField(AloeToken name, FieldSignature field, bool throughThis)
        {
            if (!field.IsReadOnly) return;
            if (throughThis && _currentFunction is { IsConstructor: true } function &&
                string.Equals(function.DeclaringClass, field.DeclaringClass, StringComparison.Ordinal)) return;
            throw Error(name, $"readonly field '{field.Name}' can be assigned only through this in its declaring class constructor.");
        }
    }
}
