using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        // Only expressions of the requested numeric literal kind are evaluated. Unknown syntax remains unknown,
        // even when an earlier operand is known to throw. Ordinary parsing still validates types.
        private bool TryGetDecimalConstantFlow(int start, int end, out ArithmeticTryFlow flow)
            => TryGetNumericLiteralConstantFlow(start, end, TokenKind.DecimalLiteral, out flow);

        private bool TryGetFloatConstantFlow(int start, int end, out ArithmeticTryFlow flow)
            => TryGetNumericLiteralConstantFlow(start, end, TokenKind.FloatLiteral, out flow);

        private bool TryGetNumericLiteralConstantFlow(int start, int end, TokenKind literalKind,
            out ArithmeticTryFlow flow)
        {
            var position = start;
            var known = ParseAdditive(out _, out flow) && position == end;
            if (!known) flow = ArithmeticTryFlow.Unknown;
            return known;

            bool ParseAdditive(out AloeValue value, out ArithmeticTryFlow outcome)
            {
                if (!ParseMultiplicative(out value, out outcome)) return false;
                while (position < end && TokenAt(position).Kind is TokenKind.Plus or TokenKind.Minus)
                {
                    var op = TokenAt(position++).Kind;
                    if (!ParseMultiplicative(out var right, out var rightFlow)) return false;
                    Apply(op, ref value, ref outcome, right, rightFlow);
                }
                return true;
            }

            bool ParseMultiplicative(out AloeValue value, out ArithmeticTryFlow outcome)
            {
                if (!ParsePrimary(out value, out outcome)) return false;
                while (position < end && TokenAt(position).Kind is TokenKind.Star or TokenKind.Slash)
                {
                    var op = TokenAt(position++).Kind;
                    if (!ParsePrimary(out var right, out var rightFlow)) return false;
                    Apply(op, ref value, ref outcome, right, rightFlow);
                }
                return true;
            }

            bool ParsePrimary(out AloeValue value, out ArithmeticTryFlow outcome)
            {
                value = literalKind == TokenKind.DecimalLiteral ? AloeValue.FromDecimal(0) : AloeValue.FromFloat(0);
                outcome = ArithmeticTryFlow.FallsThrough;
                if (position >= end) return false;
                if (TokenAt(position).Kind == TokenKind.LParen)
                {
                    position++;
                    if (!ParseAdditive(out value, out outcome) || position >= end ||
                        TokenAt(position).Kind != TokenKind.RParen) return false;
                    position++;
                    return true;
                }
                var token = TokenAt(position);
                if (token.Kind != literalKind) return false;
                try
                {
                    value = literalKind == TokenKind.DecimalLiteral
                        ? AloeValue.FromDecimal(ParseDecimal(token))
                        : AloeValue.FromFloat(ParseFloat(token));
                }
                catch (AloeCompileException) { return false; }
                position++;
                return true;
            }

            static void Apply(TokenKind op, ref AloeValue left, ref ArithmeticTryFlow leftFlow,
                AloeValue right, ArithmeticTryFlow rightFlow)
            {
                if (leftFlow != ArithmeticTryFlow.FallsThrough) return;
                if (rightFlow != ArithmeticTryFlow.FallsThrough)
                {
                    leftFlow = rightFlow;
                    return;
                }
                try
                {
                    // Reuse the VM value operators, including float32 rounding and float/decimal multiplication/division
                    // underflow, rather than treating rounded zero as nonthrowing.
                    left = op switch
                    {
                        TokenKind.Plus => left + right,
                        TokenKind.Minus => left - right,
                        TokenKind.Star => left * right,
                        TokenKind.Slash => left / right,
                        _ => throw new InvalidOperationException("Unsupported decimal constant operator.")
                    };
                }
                catch (ZeroDivisionException) { leftFlow = ArithmeticTryFlow.ZeroDivision; }
                catch (OverflowException) { leftFlow = ArithmeticTryFlow.Overflow; }
            }
        }
    }
}
