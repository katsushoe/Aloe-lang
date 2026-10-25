using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private readonly record struct BooleanExceptionFlow(ConditionEvaluation Values, ArithmeticTryFlow Failures);

        private bool IsNonthrowingPrimitiveComparison(int start, int end)
        {
            var comparison = -1;
            var depth = 0;
            for (var cursor = start; cursor < end; cursor++)
            {
                var token = TokenAt(cursor);
                if (token.Kind == TokenKind.LParen) depth++;
                else if (token.Kind == TokenKind.RParen) depth--;
                else if (depth == 0 && token.Kind is TokenKind.EqualEqual or TokenKind.BangEqual or
                         TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual)
                {
                    if (comparison >= 0) return false;
                    comparison = cursor;
                }
            }
            if (comparison < 0) return false;
            var left = SafeOperandType(start, comparison);
            var right = SafeOperandType(comparison + 1, end);
            return left != null && left == right;

            ExprType? SafeOperandType(int from, int to)
            {
                while (to - from >= 2 && TokenAt(from).Kind == TokenKind.LParen &&
                       FindInterfaceFlowGroupEnd(from, TokenKind.LParen, TokenKind.RParen) == to)
                {
                    from++;
                    to--;
                }
                // Typed primitive reads and proven constant arithmetic have no language
                // exception edge. Variable arithmetic, calls, indexing and members remain unknown.
                if (to - from == 1 && TokenAt(from).Kind == TokenKind.Identifier &&
                    TryResolveLocal(TokenAt(from).Lexeme, out var local) &&
                    local.Type is ExprType.Int or ExprType.Byte or ExprType.Char or ExprType.Float)
                    return local.Type;
                return GetGuaranteedInt32ArithmeticFailure(from, to) == GuaranteedInt32Failure.None
                    ? ExprType.Int : null;
            }
        }

        private ArithmeticTryFlow GetPrimitiveExpressionFlow(int start, int end)
        {
            var arithmetic = GetGuaranteedInt32ArithmeticFailure(start, end);
            if (arithmetic != GuaranteedInt32Failure.Unknown)
                return arithmetic switch
                {
                    GuaranteedInt32Failure.None => ArithmeticTryFlow.FallsThrough,
                    GuaranteedInt32Failure.ZeroDivision => ArithmeticTryFlow.ZeroDivision,
                    _ => ArithmeticTryFlow.Overflow
                };
            var boolean = EvaluateBooleanExceptionFlow(start, end);
            return boolean == null ? ArithmeticTryFlow.Unknown : boolean.Value.Failures |
                (boolean.Value.Values != ConditionEvaluation.None ? ArithmeticTryFlow.FallsThrough : 0);
        }

        private BooleanExceptionFlow? EvaluateBooleanExceptionFlow(int start, int end)
        {
            while (end - start >= 2 && TokenAt(start).Kind == TokenKind.LParen &&
                FindInterfaceFlowGroupEnd(start, TokenKind.LParen, TokenKind.RParen) == end)
            {
                start++;
                end--;
            }
            if (start >= end) return null;
            var firstAnd = -1;
            var firstOr = -1;
            var equality = -1;
            var equalities = 0;
            var depth = 0;
            for (var cursor = start; cursor < end; cursor++)
            {
                var token = TokenAt(cursor);
                if (token.Kind == TokenKind.LParen) depth++;
                else if (token.Kind == TokenKind.RParen) depth--;
                else if (depth == 0)
                {
                    if (token.Lexeme == "and" && firstAnd < 0) firstAnd = cursor;
                    if (token.Lexeme == "or" && firstOr < 0) firstOr = cursor;
                    if (token.Kind is TokenKind.EqualEqual or TokenKind.BangEqual)
                    {
                        equality = cursor;
                        equalities++;
                    }
                }
            }

            var logical = firstOr >= 0 ? firstOr : firstAnd;
            if (logical >= 0)
            {
                var left = EvaluateBooleanExceptionFlow(start, logical);
                if (left == null) return null;
                var isAnd = TokenAt(logical).Lexeme == "and";
                var skipped = isAnd ? ConditionEvaluation.False : ConditionEvaluation.True;
                var evaluatesRight = isAnd ? ConditionEvaluation.True : ConditionEvaluation.False;
                var values = left.Value.Values & skipped;
                if ((left.Value.Values & evaluatesRight) == 0)
                    return new BooleanExceptionFlow(values, left.Value.Failures);
                var right = EvaluateBooleanExceptionFlow(logical + 1, end);
                if (right == null) return null;
                return new BooleanExceptionFlow(values | right.Value.Values,
                    left.Value.Failures | right.Value.Failures);
            }

            var failure = GetGuaranteedInt32ConditionFailure(start, end);
            if (failure != ArithmeticTryFlow.Unknown)
                return new BooleanExceptionFlow(ConditionEvaluation.None, failure);
            var safe = EvaluateNonthrowingBooleanCondition(start, end);
            if (safe != null) return new BooleanExceptionFlow(safe.Value, 0);

            if (equalities == 1)
            {
                var left = EvaluateBooleanExceptionFlow(start, equality);
                if (left == null) return null;
                // Equality evaluates both sides, but a failing left operand prevents the right.
                if (left.Value.Values == ConditionEvaluation.None) return left;
                var right = EvaluateBooleanExceptionFlow(equality + 1, end);
                if (right == null) return null;
                var equal = (left.Value.Values & right.Value.Values) != 0;
                var different = (left.Value.Values & ConditionEvaluation.False) != 0 &&
                                    (right.Value.Values & ConditionEvaluation.True) != 0 ||
                                (left.Value.Values & ConditionEvaluation.True) != 0 &&
                                    (right.Value.Values & ConditionEvaluation.False) != 0;
                var isEquality = TokenAt(equality).Kind == TokenKind.EqualEqual;
                var values = (equal ? isEquality ? ConditionEvaluation.True : ConditionEvaluation.False : 0) |
                    (different ? isEquality ? ConditionEvaluation.False : ConditionEvaluation.True : 0);
                return new BooleanExceptionFlow(values, left.Value.Failures | right.Value.Failures);
            }
            if (equalities > 1) return null;
            if (TokenAt(start).Lexeme == "not")
            {
                var operand = EvaluateBooleanExceptionFlow(start + 1, end);
                if (operand == null) return null;
                var values = ConditionEvaluation.None;
                if ((operand.Value.Values & ConditionEvaluation.False) != 0) values |= ConditionEvaluation.True;
                if ((operand.Value.Values & ConditionEvaluation.True) != 0) values |= ConditionEvaluation.False;
                return new BooleanExceptionFlow(values, operand.Value.Failures);
            }
            return null;
        }
    }
}
