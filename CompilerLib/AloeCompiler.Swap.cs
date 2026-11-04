using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private void ParseSwap()
        {
            var operation = Advance();
            Expect(TokenKind.LParen);
            var first = Expect(TokenKind.Identifier);
            Expect(TokenKind.Comma);
            var second = Expect(TokenKind.Identifier);
            Expect(TokenKind.RParen);
            Expect(TokenKind.Semicolon);
            // Validate declared slots, not a temporary interface/type-test narrowing.
            var left = _declaredLocals[ResolveLocal(first).Slot];
            var right = _declaredLocals[ResolveLocal(second).Slot];
            if (left.Type is not (ExprType.Int or ExprType.Bool or ExprType.Byte or ExprType.Char or
                ExprType.Float or ExprType.Decimal or ExprType.String) || left.Type != right.Type)
                throw Error(operation, "swap requires two local scalar variables with exactly the same declared type.");
            RequireMutableLocal(first, left);
            RequireMutableLocal(second, right);
            if (left.Slot == right.Slot) return;
            // Both reads precede both writes; the existing stack transfers preserve the exact values.
            Emit(EnumOpcode.LoadLocal, left.Slot);
            Emit(EnumOpcode.LoadLocal, right.Slot);
            Emit(EnumOpcode.StoreLocal, left.Slot);
            Emit(EnumOpcode.StoreLocal, right.Slot);
            ForgetInterfaceNarrowing(left.Slot);
            ForgetInterfaceNarrowing(right.Slot);
        }
    }
}
