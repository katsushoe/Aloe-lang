using Aloe.CommonLib;
using Aloe.CompilerLib.Lexer;
using Aloe.CommonLib.Exceptions;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private static bool IsCatchTypeShadowed(Type exceptionType, IEnumerable<Type> handledTypes)
            => handledTypes.Any(handled => handled.IsAssignableFrom(exceptionType));

        private bool TryFindCatchBodies(int catchToken, out List<(int Open, int Close)> bodies, out int lastClose,
            Dictionary<int, int>? unreachableHandlers = null)
        {
            bodies = new List<(int Open, int Close)>();
            lastClose = -1;
            var cursor = catchToken;
            var handledTypes = new List<Type>();
            while (TokenAt(cursor).Lexeme == "catch")
            {
                var candidate = cursor + 1;
                var exceptionType = typeof(Exception);
                if (TokenAt(candidate).Kind == TokenKind.LParen)
                {
                    var parameterEnd = FindInterfaceFlowGroupEnd(candidate, TokenKind.LParen, TokenKind.RParen);
                    if (parameterEnd != candidate + 3 ||
                        !AloeExceptionTypes.TryResolve(TokenAt(candidate + 1).Lexeme, out exceptionType)) return false;
                    candidate = parameterEnd;
                }
                if (TokenAt(candidate).Kind != TokenKind.LBrace) return false;
                var close = FindInterfaceFlowGroupEnd(candidate, TokenKind.LBrace, TokenKind.RBrace);
                if (close <= candidate) return false;
                if (IsCatchTypeShadowed(exceptionType, handledTypes))
                    unreachableHandlers?.TryAdd(cursor, close);
                else
                    bodies.Add((candidate, close));
                handledTypes.Add(exceptionType);
                lastClose = close;
                cursor = close;
            }
            return bodies.Count > 0;
        }

        private readonly Stack<(int LoopDepth, List<Dictionary<int, LocalInfo>> BreakStates)> _foreachInterfaceFlows = new();

        private bool TryEvaluateBooleanConstant(int start, int end, out bool value,
            List<(int Start, int End)>? unreachableOperands = null)
        {
            const byte CanBeFalse = 1;
            const byte CanBeTrue = 2;
            const byte Unknown = CanBeFalse | CanBeTrue;

            var outcomes = EvaluateOr(start, end);
            value = outcomes == CanBeTrue;
            return outcomes is CanBeFalse or CanBeTrue;

            byte EvaluateOr(int from, int to)
            {
                var parts = SplitAtTopLevel(from, to, "or");
                var result = EvaluateAnd(parts[0].Start, parts[0].End);
                for (var i = 1; i < parts.Count; i++)
                {
                    if (result == CanBeTrue) unreachableOperands?.Add(parts[i]);
                    var right = EvaluateAnd(parts[i].Start, parts[i].End);
                    var combined = (byte)0;
                    if ((result & CanBeTrue) != 0) combined |= CanBeTrue;
                    if ((result & CanBeFalse) != 0) combined |= right;
                    result = combined;
                }
                return result;
            }

            byte EvaluateAnd(int from, int to)
            {
                var parts = SplitAtTopLevel(from, to, "and");
                var result = EvaluateUnary(parts[0].Start, parts[0].End);
                for (var i = 1; i < parts.Count; i++)
                {
                    if (result == CanBeFalse) unreachableOperands?.Add(parts[i]);
                    var right = EvaluateUnary(parts[i].Start, parts[i].End);
                    var combined = (byte)0;
                    if ((result & CanBeFalse) != 0) combined |= CanBeFalse;
                    if ((result & CanBeTrue) != 0) combined |= right;
                    result = combined;
                }
                return result;
            }

            byte EvaluateUnary(int from, int to)
            {
                var negate = false;
                while (from < to && TokenAt(from).Lexeme == "not")
                {
                    negate = !negate;
                    from++;
                }

                byte result;
                if (from < to && TokenAt(from).Kind == TokenKind.LParen &&
                    TokenAt(to - 1).Kind == TokenKind.RParen && WrapsWholeExpression(from, to))
                    result = EvaluateOr(from + 1, to - 1);
                else if (to - from == 1 && TokenAt(from).Lexeme is "true" or "false")
                    result = TokenAt(from).Lexeme == "true" ? CanBeTrue : CanBeFalse;
                else if (TryEvaluateLiteralComparison(from, to, out var comparison))
                    result = comparison ? CanBeTrue : CanBeFalse;
                else
                {
                    result = Unknown;
                    if (unreachableOperands != null)
                        for (var position = from; position < to; position++)
                        {
                            var open = TokenAt(position).Kind;
                            var close = open switch
                            {
                                TokenKind.LParen => TokenKind.RParen,
                                TokenKind.LBracket => TokenKind.RBracket,
                                TokenKind.LBrace => TokenKind.RBrace,
                                _ => TokenKind.EndOfFile
                            };
                            if (close == TokenKind.EndOfFile) continue;
                            var groupEnd = FindInterfaceFlowGroupEnd(position, open, close);
                            if (groupEnd <= position || groupEnd > to) continue;
                            // Each argument/element evaluates independently. A short circuit
                            // in one argument must not hide consumers in a following argument.
                            foreach (var part in SplitAtTopLevel(position + 1, groupEnd - 1, ","))
                                EvaluateOr(part.Start, part.End);
                            position = groupEnd - 1;
                        }
                }

                if (negate)
                    result = (byte)(((result & CanBeFalse) != 0 ? CanBeTrue : 0) |
                        ((result & CanBeTrue) != 0 ? CanBeFalse : 0));
                return result;
            }

            bool TryEvaluateLiteralComparison(int from, int to, out bool result)
            {
                var comparisonPosition = -1;
                var depth = 0;
                for (var i = from; i < to; i++)
                {
                    var kind = TokenAt(i).Kind;
                    if (kind == TokenKind.LParen) depth++;
                    else if (kind == TokenKind.RParen) depth--;
                    else if (depth == 0 && kind is TokenKind.EqualEqual or TokenKind.BangEqual or
                        TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual)
                    {
                        if (comparisonPosition >= 0) { result = false; return false; }
                        comparisonPosition = i;
                    }
                }

                if (comparisonPosition < 0) { result = false; return false; }
                var op = TokenAt(comparisonPosition).Lexeme;
                if (TryReadSignedInteger(from, comparisonPosition, out var left) &&
                    TryReadSignedInteger(comparisonPosition + 1, to, out var right))
                {
                    result = op switch
                    {
                        "==" => left == right,
                        "!=" => left != right,
                        "<" => left < right,
                        "<=" => left <= right,
                        ">" => left > right,
                        ">=" => left >= right,
                        _ => false
                    };
                    return true;
                }

                if (TryReadBooleanLiteral(from, comparisonPosition, out var leftBool) &&
                    TryReadBooleanLiteral(comparisonPosition + 1, to, out var rightBool) && op is "==" or "!=")
                {
                    result = op == "==" ? leftBool == rightBool : leftBool != rightBool;
                    return true;
                }

                if (TryReadStringLiteral(from, comparisonPosition, out var leftString) &&
                    TryReadStringLiteral(comparisonPosition + 1, to, out var rightString) && op is "==" or "!=")
                {
                    var equal = string.Equals(leftString, rightString, StringComparison.Ordinal);
                    result = op == "==" ? equal : !equal;
                    return true;
                }

                if (TryReadCharLiteral(from, comparisonPosition, out var leftChar) &&
                    TryReadCharLiteral(comparisonPosition + 1, to, out var rightChar))
                {
                    var comparison = leftChar.CompareTo(rightChar);
                    result = op switch
                    {
                        "==" => comparison == 0,
                        "!=" => comparison != 0,
                        "<" => comparison < 0,
                        "<=" => comparison <= 0,
                        ">" => comparison > 0,
                        ">=" => comparison >= 0,
                        _ => false
                    };
                    return true;
                }

                if (TryReadFloatLiteral(from, comparisonPosition, out var leftFloat) &&
                    TryReadFloatLiteral(comparisonPosition + 1, to, out var rightFloat))
                {
                    var comparison = leftFloat.CompareTo(rightFloat);
                    result = op switch
                    {
                        "==" => leftFloat.Equals(rightFloat),
                        "!=" => !leftFloat.Equals(rightFloat),
                        "<" => comparison < 0,
                        "<=" => comparison <= 0,
                        ">" => comparison > 0,
                        ">=" => comparison >= 0,
                        _ => false
                    };
                    return true;
                }

                if (TryReadLiteralNumber(from, comparisonPosition, out var leftNumber, out var leftUsesFloat) &&
                    TryReadLiteralNumber(comparisonPosition + 1, to, out var rightNumber, out var rightUsesFloat) &&
                    (leftUsesFloat || rightUsesFloat))
                {
                    // Aloe stores float operands as float32 but preserves int32 precision
                    // when both values are widened through AloeValue.AsFloat for comparison.
                    var comparison = leftNumber.CompareTo(rightNumber);
                    result = op switch
                    {
                        "==" => leftNumber.Equals(rightNumber),
                        "!=" => !leftNumber.Equals(rightNumber),
                        "<" => comparison < 0,
                        "<=" => comparison <= 0,
                        ">" => comparison > 0,
                        ">=" => comparison >= 0,
                        _ => false
                    };
                    return true;
                }

                result = false;
                return false;
            }

            bool TryReadSignedInteger(int from, int to, out long value)
            {
                var originalStart = from;
                var originalEnd = to;
                while (to - from >= 2 && TokenAt(from).Kind == TokenKind.LParen &&
                    TokenAt(to - 1).Kind == TokenKind.RParen && WrapsWholeExpression(from, to))
                {
                    from++;
                    to--;
                }
                var sign = 1;
                if (to - from == 2 && TokenAt(from).Kind is TokenKind.Plus or TokenKind.Minus)
                {
                    sign = TokenAt(from).Kind == TokenKind.Minus ? -1 : 1;
                    from++;
                }
                if (to - from != 1 || TokenAt(from).Kind != TokenKind.IntegerLiteral)
                {
                    return TryReadInt32Arithmetic(originalStart, originalEnd, out value);
                }
                try
                {
                    value = checked(ParseInteger(TokenAt(from)) * sign);
                    return value is >= int.MinValue and <= int.MaxValue;
                }
                catch (OverflowException)
                {
                    value = 0;
                    return false;
                }
            }

            bool TryReadInt32Arithmetic(int from, int to, out long value)
            {
                while (to - from >= 2 && TokenAt(from).Kind == TokenKind.LParen &&
                    TokenAt(to - 1).Kind == TokenKind.RParen && WrapsWholeExpression(from, to))
                {
                    from++;
                    to--;
                }

                if (to - from >= 2 && TokenAt(from).Kind is TokenKind.Plus or TokenKind.Minus)
                {
                    var sign = TokenAt(from).Kind == TokenKind.Minus ? -1 : 1;
                    var operandStart = from + 1;
                    if (to - operandStart == 1 && TokenAt(operandStart).Kind == TokenKind.IntegerLiteral)
                    {
                        try
                        {
                            var literal = ParseInteger(TokenAt(operandStart));
                            var signed = sign == -1 ? -literal : literal;
                            if (signed is >= int.MinValue and <= int.MaxValue)
                            {
                                value = signed;
                                return true;
                            }
                        }
                        catch (OverflowException)
                        {
                            // An unrepresentable signed literal is not a safe compile-time fact.
                        }
                        value = 0;
                        return false;
                    }

                    if (TryReadInt32Arithmetic(operandStart, to, out var operand))
                    {
                        try
                        {
                            value = sign == -1 ? checked(-(int)operand) : checked((int)operand);
                            return true;
                        }
                        catch (OverflowException)
                        {
                            // A unary operation that overflows int32 is not a safe compile-time fact.
                        }
                    }
                    value = 0;
                    return false;
                }

                var operation = FindRightmostOperator(from, to, additive: true);
                if (operation < 0)
                    operation = FindRightmostOperator(from, to, additive: false);
                if (operation >= 0 && TryReadInt32Arithmetic(from, operation, out var left) &&
                    TryReadInt32Arithmetic(operation + 1, to, out var right))
                {
                    try
                    {
                        value = TokenAt(operation).Kind switch
                        {
                            TokenKind.Plus => checked((int)left + (int)right),
                            TokenKind.Minus => checked((int)left - (int)right),
                            TokenKind.Star => checked((int)left * (int)right),
                            TokenKind.Slash when right != 0 => checked((int)left / (int)right),
                            TokenKind.Percent when right != 0 => left == int.MinValue && right == -1
                                ? 0
                                : (int)left % (int)right,
                            _ => throw new InvalidOperationException()
                        };
                        return true;
                    }
                    catch (Exception exception) when (exception is OverflowException or InvalidOperationException)
                    {
                        value = 0;
                        return false;
                    }
                }

                if (to - from == 1 && TokenAt(from).Kind == TokenKind.IntegerLiteral)
                {
                    try
                    {
                        var literal = ParseInteger(TokenAt(from));
                        if (literal is >= int.MinValue and <= int.MaxValue)
                        {
                            value = literal;
                            return true;
                        }
                    }
                    catch (OverflowException)
                    {
                        // An unrepresentable literal is not a safe compile-time fact.
                    }
                }

                value = 0;
                return false;
            }

            int FindRightmostOperator(int from, int to, bool additive)
            {
                var depth = 0;
                var result = -1;
                for (var position = from; position < to; position++)
                {
                    var kind = TokenAt(position).Kind;
                    if (kind is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace)
                    {
                        depth++;
                        continue;
                    }
                    if (kind is TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace)
                    {
                        depth--;
                        continue;
                    }
                    if (depth != 0) continue;

                    var isOperator = additive
                        ? kind is TokenKind.Plus or TokenKind.Minus
                        : kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent;
                    if (!isOperator || position == from) continue;

                    var previous = TokenAt(position - 1).Kind;
                    if (previous is TokenKind.Plus or TokenKind.Minus or TokenKind.Star or
                        TokenKind.Slash or TokenKind.Percent or TokenKind.LParen) continue;
                    result = position;
                }
                return result;
            }

            bool TryReadBooleanLiteral(int from, int to, out bool value)
            {
                while (to - from >= 2 && TokenAt(from).Kind == TokenKind.LParen &&
                    TokenAt(to - 1).Kind == TokenKind.RParen && WrapsWholeExpression(from, to))
                {
                    from++;
                    to--;
                }
                if (to - from == 1 && TokenAt(from).Lexeme is "true" or "false")
                {
                    value = TokenAt(from).Lexeme == "true";
                    return true;
                }
                value = false;
                return false;
            }

            bool TryReadStringLiteral(int from, int to, out string value)
            {
                while (to - from >= 2 && TokenAt(from).Kind == TokenKind.LParen &&
                    TokenAt(to - 1).Kind == TokenKind.RParen && WrapsWholeExpression(from, to))
                {
                    from++;
                    to--;
                }
                if (to - from == 1 && TokenAt(from).Kind == TokenKind.StringLiteral)
                {
                    value = ParseString(TokenAt(from));
                    return true;
                }
                value = string.Empty;
                return false;
            }

            bool TryReadCharLiteral(int from, int to, out char value)
            {
                while (to - from >= 2 && TokenAt(from).Kind == TokenKind.LParen &&
                    TokenAt(to - 1).Kind == TokenKind.RParen && WrapsWholeExpression(from, to))
                {
                    from++;
                    to--;
                }
                if (to - from == 1 && TokenAt(from).Kind == TokenKind.CharLiteral)
                {
                    value = ParseChar(TokenAt(from));
                    return true;
                }
                value = default;
                return false;
            }

            bool TryReadFloatLiteral(int from, int to, out float value)
            {
                while (to - from >= 2 && TokenAt(from).Kind == TokenKind.LParen &&
                    TokenAt(to - 1).Kind == TokenKind.RParen && WrapsWholeExpression(from, to))
                {
                    from++;
                    to--;
                }
                if (to - from == 1 && TokenAt(from).Kind == TokenKind.FloatLiteral)
                {
                    value = (float)ParseFloat(TokenAt(from));
                    return true;
                }
                value = default;
                return false;
            }

            bool TryReadLiteralNumber(int from, int to, out double value, out bool usesFloat)
            {
                if (TryReadFloatLiteral(from, to, out var floating))
                {
                    value = floating;
                    usesFloat = true;
                    return true;
                }
                if (TryReadSignedInteger(from, to, out var integer))
                {
                    value = integer;
                    usesFloat = false;
                    return true;
                }
                value = 0;
                usesFloat = false;
                return false;
            }

            List<(int Start, int End)> SplitAtTopLevel(int from, int to, string op)
            {
                var parts = new List<(int Start, int End)>();
                var partStart = from;
                var depth = 0;
                for (var i = from; i < to; i++)
                {
                    var kind = TokenAt(i).Kind;
                    if (kind is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace) depth++;
                    else if (kind is TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace) depth--;
                    else if (depth == 0 && TokenAt(i).Lexeme == op)
                    {
                        parts.Add((partStart, i));
                        partStart = i + 1;
                    }
                }
                parts.Add((partStart, to));
                return parts;
            }

            bool WrapsWholeExpression(int from, int to)
            {
                var depth = 0;
                for (var i = from; i < to; i++)
                {
                    if (TokenAt(i).Kind == TokenKind.LParen) depth++;
                    else if (TokenAt(i).Kind == TokenKind.RParen) depth--;
                    if (depth == 0 && i < to - 1) return false;
                }
                return depth == 0;
            }
        }

        private sealed record InterfaceNarrowing(Dictionary<string, LocalInfo> Scope, string Name,
            LocalInfo Original, LocalInfo Narrowed);

        private readonly Stack<HashSet<int>> _interfaceWriteScopes = new();

        private List<InterfaceNarrowing> GetConditionInterfaceNarrowings(int start, int end,
            HashSet<int> conditionWrites, bool whenTrue = true)
        {
            var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
            CollectInterfaceConditionFacts(start, end, whenTrue, candidates, new HashSet<string>(StringComparer.Ordinal));
            var result = new List<InterfaceNarrowing>();
            if (candidates.Count == 0) return result;

            foreach (var candidate in candidates)
            {
                foreach (var scope in _scopes)
                {
                    if (!scope.TryGetValue(candidate.Key, out var local)) continue;
                    if (!conditionWrites.Contains(local.Slot) && local.Type == ExprType.Object && !IsPipeTypeName(local.ClassName) &&
                        !IsClassAssignable(local.ClassName, candidate.Value))
                        result.Add(new InterfaceNarrowing(scope, candidate.Key, local,
                            local with { ClassName = candidate.Value }));
                    break;
                }
            }
            return result;
        }

        private Dictionary<int, LocalInfo> CaptureInterfaceFlowState() =>
            _scopes.SelectMany(scope => scope.Values).ToDictionary(local => local.Slot);

        private void RestoreInterfaceFlowState(Dictionary<int, LocalInfo> state)
        {
            foreach (var scope in _scopes)
                foreach (var name in scope.Keys.ToArray())
                    if (state.TryGetValue(scope[name].Slot, out var local)) scope[name] = local;
        }

        private void RestoreConditionInterfaceNarrowings(List<InterfaceNarrowing> narrowings)
        {
            foreach (var narrowing in narrowings)
                if (narrowing.Scope[narrowing.Name] == narrowing.Narrowed)
                    narrowing.Scope[narrowing.Name] = narrowing.Original;
        }

        private void MergeInterfaceFlowState(Dictionary<int, LocalInfo> entry,
            Dictionary<int, LocalInfo> thenState, bool thenCanFallThrough,
            Dictionary<int, LocalInfo> elseState, bool elseCanFallThrough)
        {
            RestoreInterfaceFlowState(entry);
            foreach (var scope in _scopes)
                foreach (var name in scope.Keys.ToArray())
                {
                    var local = scope[name];
                    if (!entry.ContainsKey(local.Slot)) continue;
                    if (thenCanFallThrough && !PreservesInterfaceFlowEntry(thenState[local.Slot], local) ||
                        elseCanFallThrough && !PreservesInterfaceFlowEntry(elseState[local.Slot], local))
                        scope[name] = _declaredLocals[local.Slot];
                }
        }

        private void JoinInterfaceFlowStates(IReadOnlyList<Dictionary<int, LocalInfo>> states)
        {
            foreach (var scope in _scopes)
                foreach (var name in scope.Keys.ToArray())
                {
                    var local = scope[name];
                    if (local.Type != ExprType.Object || IsPipeTypeName(local.ClassName)) continue;
                    if (states.Any(state => !state.ContainsKey(local.Slot))) continue;
                    if (states.Any(state => !PreservesInterfaceFlowEntry(state[local.Slot], local)))
                    {
                        local = _declaredLocals[local.Slot];
                        scope[name] = local;
                    }
                    var common = _interfaces.Keys.Where(parent =>
                        states.All(state => IsClassAssignable(state[local.Slot].ClassName, parent)))
                        .ToHashSet(StringComparer.Ordinal);
                    var joined = GetUniqueInterfaceFlowAncestor(common);
                    if (joined != null && !IsClassAssignable(local.ClassName, joined))
                        scope[name] = local with { ClassName = joined };
                }
        }

        private bool PreservesInterfaceFlowEntry(LocalInfo actual, LocalInfo entry) =>
            actual == entry || actual.Type == ExprType.Object && entry.Type == ExprType.Object &&
            (entry.ClassName == null || IsClassAssignable(actual.ClassName, entry.ClassName));

        private bool CanProveTryRangeHasNoExceptionalEdges(int start, int end,
            IReadOnlyDictionary<string, ExprType>? inheritedTypes = null)
        {
            if (start < 0 || end < start) return false;
            // Copy at each lexical boundary: declarations must not escape a branch/loop/block.
            var localTypes = inheritedTypes == null
                ? new Dictionary<string, ExprType>(StringComparer.Ordinal)
                : new Dictionary<string, ExprType>(inheritedTypes, StringComparer.Ordinal);
            for (var cursor = start; cursor < end; cursor++)
            {
                var token = TokenAt(cursor);
                if (token.Kind == TokenKind.Semicolon) continue;
                if (token.Kind == TokenKind.LBrace)
                {
                    var blockEnd = FindInterfaceFlowGroupEnd(cursor, TokenKind.LBrace, TokenKind.RBrace);
                    if (blockEnd <= 0 || blockEnd > end ||
                        !CanProveTryRangeHasNoExceptionalEdges(cursor + 1, blockEnd - 1, localTypes)) return false;
                    cursor = blockEnd - 1;
                    continue;
                }
                // A nested handler cannot run when its own try body is independently nonthrowing.
                if (token.Lexeme == "try" && TokenAt(cursor + 1).Kind == TokenKind.LBrace)
                {
                    var tryEnd = FindInterfaceFlowGroupEnd(cursor + 1, TokenKind.LBrace, TokenKind.RBrace);
                    if (tryEnd <= cursor + 1 || !TryFindCatchBodies(tryEnd, out var handlers, out var lastCatchEnd) ||
                        lastCatchEnd > end ||
                        !(CanProveTryRangeHasNoExceptionalEdges(cursor + 2, tryEnd - 1, localTypes) ||
                          CanProveHandledConstantNestedFailure(cursor + 2, tryEnd - 1, handlers, localTypes)))
                        return false;
                    cursor = lastCatchEnd - 1;
                    continue;
                }
                // Initialization must independently be safe for the declared target type;
                // an empty arithmetic failure set alone is not a general nonthrowing proof.
                if (token.Lexeme == "let" && TokenAt(cursor + 1).Kind == TokenKind.Identifier &&
                    TokenAt(cursor + 2).Kind == TokenKind.Colon && TokenAt(cursor + 3).Lexeme is "int" or "bool" or "string" or "byte" or "char" or "float" or "decimal" &&
                    TokenAt(cursor + 4).Kind == TokenKind.Assign)
                {
                    var statementEnd = FindInterfaceFlowStatementEnd(cursor, end);
                    if (statementEnd < 0) return false;
                    var type = TokenAt(cursor + 3).Lexeme switch
                    {
                        "int" => ExprType.Int,
                        "bool" => ExprType.Bool,
                        "byte" => ExprType.Byte,
                        "char" => ExprType.Char,
                        "float" => ExprType.Float,
                        "decimal" => ExprType.Decimal,
                        _ => ExprType.String
                    };
                    var initializerIsSafe = type switch
                    {
                        ExprType.Int => GetNonthrowingDirectReadType(cursor + 5, statementEnd, localTypes) == ExprType.Int ||
                            GetGuaranteedInt32ArithmeticFailure(cursor + 5, statementEnd) == GuaranteedInt32Failure.None,
                        ExprType.Bool => EvaluateNonthrowingBooleanCondition(cursor + 5, statementEnd, localTypes) != null,
                        _ => GetNonthrowingDirectReadType(cursor + 5, statementEnd, localTypes) == type
                    };
                    if (!initializerIsSafe) return false;
                    // Bind after proving the initializer, so a shadow initializer reads its outer binding.
                    localTypes[TokenAt(cursor + 1).Lexeme] = type;
                    cursor = statementEnd;
                    continue;
                }
                // Same-type primitive reads and independently proven boolean or int constants are safe assignments.
                // Unproven calls, unboxing, conversions and member/index writes stay unknown.
                if (token.Kind == TokenKind.Identifier && TokenAt(cursor + 1).Kind == TokenKind.Assign)
                {
                    var targetType = GetNonthrowingDirectReadType(cursor, cursor + 1, localTypes);
                    var statementEnd = FindInterfaceFlowStatementEnd(cursor, end);
                    if (targetType is not (ExprType.Int or ExprType.Bool or ExprType.String or ExprType.Byte or
                        ExprType.Char or ExprType.Float or ExprType.Decimal) || statementEnd < 0)
                        return false;
                    var provenBoolExpression = targetType == ExprType.Bool &&
                        EvaluateNonthrowingBooleanCondition(cursor + 2, statementEnd, localTypes) != null;
                    var provenIntConstant = targetType == ExprType.Int &&
                        GetGuaranteedInt32ArithmeticFailure(cursor + 2, statementEnd) == GuaranteedInt32Failure.None;
                    if (!provenBoolExpression && !provenIntConstant &&
                        GetNonthrowingDirectReadType(cursor + 2, statementEnd, localTypes) != targetType)
                        return false;
                    cursor = statementEnd;
                    continue;
                }
                if (token.Lexeme == "if" && TokenAt(cursor + 1).Kind == TokenKind.LParen)
                {
                    if (!CanProveConditionalHasNoExceptionalEdges(cursor, end, out var afterIf, localTypes)) return false;
                    cursor = afterIf - 1;
                    continue;
                }
                if (token.Lexeme == "while" && TokenAt(cursor + 1).Kind == TokenKind.LParen)
                {
                    var conditionEnd = FindInterfaceFlowGroupEnd(cursor + 1, TokenKind.LParen, TokenKind.RParen);
                    var bodyEnd = conditionEnd > 0 && TokenAt(conditionEnd).Kind == TokenKind.LBrace
                        ? FindInterfaceFlowGroupEnd(conditionEnd, TokenKind.LBrace, TokenKind.RBrace) : -1;
                    if (bodyEnd <= 0 || bodyEnd > end) return false;
                    var condition = EvaluateNonthrowingBooleanCondition(cursor + 2, conditionEnd - 1, localTypes);
                    if (condition == null || (condition != ConditionEvaluation.False &&
                        !CanProveTryRangeHasNoExceptionalEdges(conditionEnd + 1, bodyEnd - 1, localTypes)))
                        return false;
                    cursor = bodyEnd - 1;
                    continue;
                }
                // Each nested loop consumes its own bare transfers before scanning resumes.
                if (token.Kind == TokenKind.Keyword && token.Lexeme is "return" or "break" or "continue" &&
                    TokenAt(cursor + 1).Kind == TokenKind.Semicolon)
                    return true;
                return false;
            }
            return true;
        }

        private bool CanProveHandledConstantNestedFailure(int start, int end,
            IReadOnlyList<(int Open, int Close)> handlers, IReadOnlyDictionary<string, ExprType> localTypes)
        {
            // Restrict this proof to one explicitly typed local initialized by constant int arithmetic.
            if (TokenAt(start).Lexeme != "let" || TokenAt(start + 1).Kind != TokenKind.Identifier ||
                TokenAt(start + 2).Kind != TokenKind.Colon || TokenAt(start + 3).Lexeme != "int" ||
                TokenAt(start + 4).Kind != TokenKind.Assign) return false;
            var statementEnd = FindInterfaceFlowStatementEnd(start, end);
            if (statementEnd < 0 || statementEnd + 1 != end) return false;
            var exceptionType = GetGuaranteedInt32ArithmeticFailure(start + 5, statementEnd) switch
            {
                GuaranteedInt32Failure.ZeroDivision => typeof(ZeroDivisionException),
                GuaranteedInt32Failure.Overflow => typeof(OverflowException),
                _ => null
            };
            if (exceptionType == null) return false;
            foreach (var (open, close) in handlers)
            {
                var handlerType = typeof(Exception);
                if (TokenAt(open - 1).Kind == TokenKind.RParen &&
                    !AloeExceptionTypes.TryResolve(TokenAt(open - 2).Lexeme, out handlerType)) return false;
                if (handlerType.IsAssignableFrom(exceptionType))
                    return CanProveTryRangeHasNoExceptionalEdges(open + 1, close - 1, localTypes);
            }
            return false;
        }

        private bool CanProveConditionalHasNoExceptionalEdges(int start, int end, out int afterIf,
            IReadOnlyDictionary<string, ExprType> localTypes)
        {
            afterIf = -1;
            var canReachCondition = true;
            for (var cursor = start; ;)
            {
                var conditionEnd = FindInterfaceFlowGroupEnd(cursor + 1, TokenKind.LParen, TokenKind.RParen);
                var bodyEnd = conditionEnd > 0 && TokenAt(conditionEnd).Kind == TokenKind.LBrace
                    ? FindInterfaceFlowGroupEnd(conditionEnd, TokenKind.LBrace, TokenKind.RBrace) : -1;
                if (bodyEnd <= 0 || bodyEnd > end) return false;
                if (canReachCondition)
                {
                    var condition = EvaluateNonthrowingBooleanCondition(cursor + 2, conditionEnd - 1, localTypes);
                    if (condition == null || ((condition.Value & ConditionEvaluation.True) != 0 &&
                        !CanProveTryRangeHasNoExceptionalEdges(conditionEnd + 1, bodyEnd - 1, localTypes)))
                        return false;
                    canReachCondition = (condition.Value & ConditionEvaluation.False) != 0;
                }
                afterIf = bodyEnd;
                if (TokenAt(bodyEnd).Lexeme != "else") return true;
                var elseStart = bodyEnd + 1;
                if (TokenAt(elseStart).Lexeme == "if" && TokenAt(elseStart + 1).Kind == TokenKind.LParen)
                {
                    cursor = elseStart;
                    continue;
                }
                if (TokenAt(elseStart).Kind != TokenKind.LBrace) return false;
                afterIf = FindInterfaceFlowGroupEnd(elseStart, TokenKind.LBrace, TokenKind.RBrace);
                return afterIf > 0 && afterIf <= end && (!canReachCondition ||
                    CanProveTryRangeHasNoExceptionalEdges(elseStart + 1, afterIf - 1, localTypes));
            }
        }

        private readonly Stack<ArithmeticTryFlow> _arithmeticRethrowContexts = new();

        private static ArithmeticTryFlow GetArithmeticRethrowContext(Type handlerType,
            IEnumerable<Type>? possibleExceptions, IEnumerable<Type> handledTypes)
        {
            if (handlerType == typeof(ZeroDivisionException)) return ArithmeticTryFlow.ZeroDivision;
            if (handlerType == typeof(OverflowException)) return ArithmeticTryFlow.Overflow;
            if (possibleExceptions == null) return ArithmeticTryFlow.Unknown;
            var result = ArithmeticTryFlow.Unknown;
            foreach (var possible in possibleExceptions)
            {
                if (!handlerType.IsAssignableFrom(possible) || IsCatchTypeShadowed(possible, handledTypes)) continue;
                if (possible == typeof(ZeroDivisionException)) result |= ArithmeticTryFlow.ZeroDivision;
                else if (possible == typeof(OverflowException)) result |= ArithmeticTryFlow.Overflow;
                else return ArithmeticTryFlow.Unknown;
            }
            return result;
        }

        private Type[]? GetPossibleArithmeticExceptions(int start, int end)
        {
            var flow = EvaluateArithmeticTryFlow(start, end,
                _arithmeticRethrowContexts.TryPeek(out var context) ? context : ArithmeticTryFlow.Unknown);
            if (flow == ArithmeticTryFlow.Unknown) return null;
            var exceptions = new List<Type>();
            if ((flow & ArithmeticTryFlow.ZeroDivision) != 0) exceptions.Add(typeof(ZeroDivisionException));
            if ((flow & ArithmeticTryFlow.Overflow) != 0) exceptions.Add(typeof(OverflowException));
            return exceptions.Count == 0 ? null : exceptions.ToArray();
        }

        [Flags]
        private enum ArithmeticTryFlow
        {
            Unknown = 0,
            FallsThrough = 1,
            DoesNotReturn = 2,
            ZeroDivision = 4,
            Overflow = 8,
            Break = 16,
            Continue = 32
        }

        private ArithmeticTryFlow EvaluateArithmeticTryFlow(int start, int end, ArithmeticTryFlow rethrowExceptions)
        {
            // Keep every supported exceptional outcome while following normally continuing paths.
            // Any unknown operation invalidates the finite exception set.
            var priorOutcomes = (ArithmeticTryFlow)0;
            for (var cursor = start; cursor < end;)
            {
                if (TokenAt(cursor).Lexeme == "try" && TokenAt(cursor + 1).Kind == TokenKind.LBrace)
                {
                    var nestedFlow = EvaluateArithmeticNestedTryFlow(cursor, end, out var afterHandlers, rethrowExceptions);
                    if (nestedFlow == ArithmeticTryFlow.Unknown) return ArithmeticTryFlow.Unknown;
                    priorOutcomes |= nestedFlow & ~ArithmeticTryFlow.FallsThrough;
                    if ((nestedFlow & ArithmeticTryFlow.FallsThrough) == 0) return priorOutcomes;
                    cursor = afterHandlers;
                    continue;
                }

                if (TokenAt(cursor).Lexeme == "while" && TokenAt(cursor + 1).Kind == TokenKind.LParen)
                {
                    var conditionEnd = FindInterfaceFlowGroupEnd(cursor + 1, TokenKind.LParen, TokenKind.RParen);
                    var bodyEnd = conditionEnd > 0 && TokenAt(conditionEnd).Kind == TokenKind.LBrace
                        ? FindInterfaceFlowGroupEnd(conditionEnd, TokenKind.LBrace, TokenKind.RBrace)
                        : -1;
                    if (bodyEnd <= 0 || bodyEnd > end) return ArithmeticTryFlow.Unknown;
                    var condition = EvaluateBooleanExceptionFlow(cursor + 2, conditionEnd - 1);
                    if (condition == null) return ArithmeticTryFlow.Unknown;
                    priorOutcomes |= condition.Value.Failures;
                    var outcomes = condition.Value.Values;
                    if (outcomes == ConditionEvaluation.None) return priorOutcomes;
                    if (outcomes == ConditionEvaluation.False)
                    {
                        cursor = bodyEnd;
                        continue;
                    }
                    if (outcomes != ConditionEvaluation.True &&
                        outcomes != (ConditionEvaluation.False | ConditionEvaluation.True))
                        return ArithmeticTryFlow.Unknown;
                    var bodyFlow = EvaluateArithmeticTryFlow(conditionEnd + 1, bodyEnd - 1, rethrowExceptions);
                    if (bodyFlow == ArithmeticTryFlow.Unknown) return ArithmeticTryFlow.Unknown;
                    // Consume only this loop's transfers. Inner loops consume their own
                    // break/continue flags before returning their flow to this body.
                    var loopFlow = bodyFlow & ~(ArithmeticTryFlow.FallsThrough |
                        ArithmeticTryFlow.Break | ArithmeticTryFlow.Continue);
                    if ((bodyFlow & (ArithmeticTryFlow.FallsThrough | ArithmeticTryFlow.Continue)) != 0)
                        loopFlow |= ArithmeticTryFlow.DoesNotReturn;
                    if ((bodyFlow & ArithmeticTryFlow.Break) != 0)
                        loopFlow |= ArithmeticTryFlow.FallsThrough;
                    priorOutcomes |= loopFlow & ~ArithmeticTryFlow.FallsThrough;
                    if (outcomes == ConditionEvaluation.True &&
                        (loopFlow & ArithmeticTryFlow.FallsThrough) == 0) return priorOutcomes;
                    cursor = bodyEnd;
                    continue;
                }

                if (TokenAt(cursor).Lexeme == "if" && TokenAt(cursor + 1).Kind == TokenKind.LParen)
                {
                    var selectedFlow = EvaluateArithmeticIfFlow(cursor, end, out var afterIf, rethrowExceptions);
                    if (selectedFlow == ArithmeticTryFlow.Unknown) return ArithmeticTryFlow.Unknown;
                    priorOutcomes |= selectedFlow & ~ArithmeticTryFlow.FallsThrough;
                    if ((selectedFlow & ArithmeticTryFlow.FallsThrough) == 0) return priorOutcomes;
                    cursor = afterIf;
                    continue;
                }

                var statementEnd = FindInterfaceFlowStatementEnd(cursor, end);
                if (statementEnd < 0) return ArithmeticTryFlow.Unknown;

                if (statementEnd == cursor + 1 && TokenAt(cursor).Lexeme == "throw")
                    return rethrowExceptions == ArithmeticTryFlow.Unknown
                        ? ArithmeticTryFlow.Unknown : priorOutcomes | rethrowExceptions;

                if (statementEnd == cursor + 1 && TokenAt(cursor).Lexeme is "break" or "continue")
                    return priorOutcomes | (TokenAt(cursor).Lexeme == "break"
                        ? ArithmeticTryFlow.Break : ArithmeticTryFlow.Continue);

                if (TokenAt(cursor).Lexeme == "return")
                {
                    if (statementEnd == cursor + 1)
                        return priorOutcomes | ArithmeticTryFlow.DoesNotReturn;
                    // Evaluate the return operand before leaving the try. Unknown calls and
                    // other expressions must preserve their possible exceptional edges.
                    var returnFlow = GetPrimitiveExpressionFlow(cursor + 1, statementEnd);
                    return returnFlow == ArithmeticTryFlow.Unknown ? ArithmeticTryFlow.Unknown :
                        priorOutcomes | (returnFlow & ~ArithmeticTryFlow.FallsThrough) |
                        ((returnFlow & ArithmeticTryFlow.FallsThrough) != 0 ? ArithmeticTryFlow.DoesNotReturn : 0);
                }

                if (TokenAt(cursor).Lexeme is not ("let" or "var") &&
                    !(TokenAt(cursor).Kind == TokenKind.Identifier && TokenAt(cursor + 1).Kind == TokenKind.Assign))
                    return ArithmeticTryFlow.Unknown;

                var assignment = cursor + 1;
                while (assignment < statementEnd && TokenAt(assignment).Kind != TokenKind.Assign)
                    assignment++;
                if (assignment >= statementEnd)
                    return ArithmeticTryFlow.Unknown;

                var expressionFlow = GetPrimitiveExpressionFlow(assignment + 1, statementEnd);
                if (expressionFlow == ArithmeticTryFlow.Unknown) return ArithmeticTryFlow.Unknown;
                priorOutcomes |= expressionFlow & ~ArithmeticTryFlow.FallsThrough;
                if ((expressionFlow & ArithmeticTryFlow.FallsThrough) == 0) return priorOutcomes;

                cursor = statementEnd + 1;
            }
            return priorOutcomes | ArithmeticTryFlow.FallsThrough;
        }

        private ArithmeticTryFlow EvaluateArithmeticNestedTryFlow(int start, int end, out int afterHandlers,
            ArithmeticTryFlow rethrowExceptions)
        {
            afterHandlers = -1;
            var tryEnd = FindInterfaceFlowGroupEnd(start + 1, TokenKind.LBrace, TokenKind.RBrace);
            if (tryEnd <= start || tryEnd > end ||
                !TryFindCatchBodies(tryEnd, out var handlers, out afterHandlers) || afterHandlers > end)
                return ArithmeticTryFlow.Unknown;

            var tryFlow = EvaluateArithmeticTryFlow(start + 2, tryEnd - 1, rethrowExceptions);
            if (tryFlow == ArithmeticTryFlow.Unknown) return ArithmeticTryFlow.Unknown;
            const ArithmeticTryFlow failures = ArithmeticTryFlow.ZeroDivision | ArithmeticTryFlow.Overflow;
            var remaining = tryFlow & failures;
            var result = tryFlow & ~failures;
            foreach (var (open, close) in handlers)
            {
                var handlerType = typeof(Exception);
                if (TokenAt(open - 1).Kind == TokenKind.RParen &&
                    !AloeExceptionTypes.TryResolve(TokenAt(open - 2).Lexeme, out handlerType))
                    return ArithmeticTryFlow.Unknown;

                var matched = (ArithmeticTryFlow)0;
                if ((remaining & ArithmeticTryFlow.ZeroDivision) != 0 &&
                    handlerType.IsAssignableFrom(typeof(ZeroDivisionException)))
                    matched |= ArithmeticTryFlow.ZeroDivision;
                if ((remaining & ArithmeticTryFlow.Overflow) != 0 &&
                    handlerType.IsAssignableFrom(typeof(OverflowException)))
                    matched |= ArithmeticTryFlow.Overflow;
                if (matched == 0) continue;

                var handlerFlow = EvaluateArithmeticTryFlow(open + 1, close - 1, matched);
                if (handlerFlow == ArithmeticTryFlow.Unknown) return ArithmeticTryFlow.Unknown;
                // A failure raised by a handler escapes this try; later sibling catches
                // may handle only failures from the original try body.
                result |= handlerFlow;
                remaining &= ~matched;
            }
            return result | remaining;
        }

        private ArithmeticTryFlow EvaluateArithmeticIfFlow(int start, int end, out int afterIf,
            ArithmeticTryFlow rethrowExceptions)
        {
            afterIf = -1;
            var conditionEnd = FindInterfaceFlowGroupEnd(start + 1, TokenKind.LParen, TokenKind.RParen);
            var bodyEnd = conditionEnd > 0 && TokenAt(conditionEnd).Kind == TokenKind.LBrace
                ? FindInterfaceFlowGroupEnd(conditionEnd, TokenKind.LBrace, TokenKind.RBrace)
                : -1;
            if (bodyEnd <= 0 || bodyEnd > end) return ArithmeticTryFlow.Unknown;
            afterIf = bodyEnd;
            var elseFlow = ArithmeticTryFlow.FallsThrough;
            if (TokenAt(bodyEnd).Lexeme == "else")
            {
                var elseStart = bodyEnd + 1;
                if (TokenAt(elseStart).Lexeme == "if" && TokenAt(elseStart + 1).Kind == TokenKind.LParen)
                {
                    elseFlow = EvaluateArithmeticIfFlow(elseStart, end, out afterIf, rethrowExceptions);
                    if (afterIf <= 0) return ArithmeticTryFlow.Unknown;
                }
                else
                {
                    if (TokenAt(elseStart).Kind != TokenKind.LBrace) return ArithmeticTryFlow.Unknown;
                    afterIf = FindInterfaceFlowGroupEnd(elseStart, TokenKind.LBrace, TokenKind.RBrace);
                    if (afterIf <= 0 || afterIf > end) return ArithmeticTryFlow.Unknown;
                    elseFlow = EvaluateArithmeticTryFlow(elseStart + 1, afterIf - 1, rethrowExceptions);
                }
            }

            var condition = EvaluateBooleanExceptionFlow(start + 2, conditionEnd - 1);
            if (condition == null) return ArithmeticTryFlow.Unknown;
            var outcomes = condition.Value.Values;
            if (outcomes == (ConditionEvaluation.False | ConditionEvaluation.True))
            {
                var thenFlow = EvaluateArithmeticTryFlow(conditionEnd + 1, bodyEnd - 1, rethrowExceptions);
                return thenFlow == ArithmeticTryFlow.Unknown || elseFlow == ArithmeticTryFlow.Unknown
                    ? ArithmeticTryFlow.Unknown
                    : condition.Value.Failures | thenFlow | elseFlow;
            }
            return outcomes switch
            {
                ConditionEvaluation.True => MergeConditionFailures(EvaluateArithmeticTryFlow(conditionEnd + 1, bodyEnd - 1, rethrowExceptions)),
                ConditionEvaluation.False => MergeConditionFailures(elseFlow),
                ConditionEvaluation.None => condition.Value.Failures,
                _ => ArithmeticTryFlow.Unknown
            };

            ArithmeticTryFlow MergeConditionFailures(ArithmeticTryFlow bodyFlow) =>
                bodyFlow == ArithmeticTryFlow.Unknown ? ArithmeticTryFlow.Unknown : condition.Value.Failures | bodyFlow;
        }

        private ConditionEvaluation? EvaluateNonthrowingBooleanCondition(int start, int end,
            IReadOnlyDictionary<string, ExprType>? localTypes = null)
        {
            while (end - start >= 2 && TokenAt(start).Kind == TokenKind.LParen &&
                   FindInterfaceFlowGroupEnd(start, TokenKind.LParen, TokenKind.RParen) == end)
            {
                start++;
                end--;
            }

            var known = EvaluateGuaranteedZeroDivisionConditionExpression(start, end);
            if (known is ConditionEvaluation.False or ConditionEvaluation.True) return known;

            // Primitive local reads cannot throw or consume a reference. Calls and other
            // expressions must retain their possible exceptional edges even when both bodies agree.
            if (GetNonthrowingDirectReadType(start, end, localTypes) == ExprType.Bool)
                return ConditionEvaluation.False | ConditionEvaluation.True;

            if (IsNonthrowingPrimitiveComparison(start, end, localTypes))
                return ConditionEvaluation.False | ConditionEvaluation.True;

            var firstAnd = -1;
            var firstOr = -1;
            var depth = 0;
            var equality = -1;
            var equalities = 0;
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
                var left = EvaluateNonthrowingBooleanCondition(start, logical, localTypes);
                if (left == null) return null;
                var isAnd = TokenAt(logical).Lexeme == "and";
                var shortCircuit = isAnd ? ConditionEvaluation.False : ConditionEvaluation.True;
                if (left == shortCircuit) return shortCircuit;
                var right = EvaluateNonthrowingBooleanCondition(logical + 1, end, localTypes);
                if (right == null) return null;
                var result = left.Value & shortCircuit;
                var evaluatesRight = isAnd ? ConditionEvaluation.True : ConditionEvaluation.False;
                if ((left.Value & evaluatesRight) != 0) result |= right.Value;
                return result;
            }

            if (equalities == 1)
            {
                var left = EvaluateNonthrowingBooleanCondition(start, equality, localTypes);
                var right = EvaluateNonthrowingBooleanCondition(equality + 1, end, localTypes);
                if (left == null || right == null) return null;
                var canEqual = (left.Value & right.Value) != 0;
                var canDiffer = (left.Value & ConditionEvaluation.False) != 0 &&
                                    (right.Value & ConditionEvaluation.True) != 0 ||
                                (left.Value & ConditionEvaluation.True) != 0 &&
                                    (right.Value & ConditionEvaluation.False) != 0;
                var isEquality = TokenAt(equality).Kind == TokenKind.EqualEqual;
                return (canEqual ? isEquality ? ConditionEvaluation.True : ConditionEvaluation.False : 0) |
                       (canDiffer ? isEquality ? ConditionEvaluation.False : ConditionEvaluation.True : 0);
            }
            if (equalities > 1) return null;

            if (TokenAt(start).Lexeme == "not")
            {
                var operand = EvaluateNonthrowingBooleanCondition(start + 1, end, localTypes);
                if (operand == null) return null;
                var result = (ConditionEvaluation)0;
                if ((operand.Value & ConditionEvaluation.False) != 0) result |= ConditionEvaluation.True;
                if ((operand.Value & ConditionEvaluation.True) != 0) result |= ConditionEvaluation.False;
                return result;
            }
            return null;
        }

        private ArithmeticTryFlow GetGuaranteedInt32ConditionFailure(int start, int end)
        {
            while (end - start >= 2 && TokenAt(start).Kind == TokenKind.LParen &&
                   FindInterfaceFlowGroupEnd(start, TokenKind.LParen, TokenKind.RParen) == end)
            {
                start++;
                end--;
            }

            var firstAnd = -1;
            var firstOr = -1;
            var comparison = -1;
            var comparisons = 0;
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
                    if (token.Kind is TokenKind.EqualEqual or TokenKind.BangEqual or TokenKind.Less or
                        TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual)
                    {
                        comparison = cursor;
                        comparisons++;
                    }
                }
            }

            var logical = firstOr >= 0 ? firstOr : firstAnd;
            if (logical >= 0)
            {
                var leftFailure = GetGuaranteedInt32ConditionFailure(start, logical);
                if (leftFailure != ArithmeticTryFlow.Unknown) return leftFailure;
                var left = EvaluateGuaranteedZeroDivisionConditionExpression(start, logical);
                var evaluatesRight = TokenAt(logical).Lexeme == "and"
                    ? left == ConditionEvaluation.True
                    : left == ConditionEvaluation.False;
                return evaluatesRight
                    ? GetGuaranteedInt32ConditionFailure(logical + 1, end)
                    : ArithmeticTryFlow.Unknown;
            }
            if (TokenAt(start).Lexeme == "not")
                return GetGuaranteedInt32ConditionFailure(start + 1, end);
            if (comparisons != 1) return ArithmeticTryFlow.Unknown;

            // A right-side failure is exact only after the left operand is proven safe.
            // An unknown left operand can fail first with a different exception type.
            var failure = GetGuaranteedInt32ArithmeticFailure(start, comparison);
            if (failure == GuaranteedInt32Failure.None)
                failure = GetGuaranteedInt32ArithmeticFailure(comparison + 1, end);
            return failure switch
            {
                GuaranteedInt32Failure.ZeroDivision => ArithmeticTryFlow.ZeroDivision,
                GuaranteedInt32Failure.Overflow => ArithmeticTryFlow.Overflow,
                _ => ArithmeticTryFlow.Unknown
            };
        }

        private Dictionary<int, int> GetUnreachableCatchEndsInInterfaceFlow(int start, int end)
        {
            var ends = new Dictionary<int, int>();
            for (var cursor = start; cursor + 1 < end; cursor++)
                if (TokenAt(cursor).Lexeme == "try" && TokenAt(cursor + 1).Kind == TokenKind.LBrace)
                {
                    var tryEnd = FindInterfaceFlowGroupEnd(cursor + 1, TokenKind.LBrace, TokenKind.RBrace);
                    if (tryEnd > cursor && TryFindCatchBodies(tryEnd, out _, out var handlerEnd, ends) &&
                        CanProveTryRangeHasNoExceptionalEdges(cursor + 2, tryEnd - 1))
                        ends[tryEnd] = handlerEnd;
                }
            return ends;
        }

        private List<(int Start, int End)> GetNestedHandlerRangesInInterfaceFlow(int start, int end)
        {
            var ranges = new List<(int Start, int End)>();
            for (var cursor = start; cursor + 1 < end; cursor++)
                if (TokenAt(cursor).Lexeme == "try" && TokenAt(cursor + 1).Kind == TokenKind.LBrace)
                {
                    var tryEnd = FindInterfaceFlowGroupEnd(cursor + 1, TokenKind.LBrace, TokenKind.RBrace);
                    if (tryEnd <= cursor || !TryFindCatchBodies(tryEnd, out _, out var handlerEnd))
                    {
                        ranges.Add((cursor, end));
                        break;
                    }
                    ranges.Add((cursor, handlerEnd));
                    cursor = handlerEnd - 1;
                }
            return ranges;
        }

        private bool AreTryBlockWritesAssignableToInterface(int slot, string interfaceType, int start, int end)
        {
            var name = _scopes.SelectMany(scope => scope)
                .FirstOrDefault(binding => binding.Value.Slot == slot).Key;
            if (name == null) return false;
            var shadows = new Stack<HashSet<string>>();
            shadows.Push(new HashSet<string>(StringComparer.Ordinal));
            var foreachBindings = new Dictionary<int, HashSet<string>>();
            HashSet<string>? declarationScope = null;
            string? declarationName = null;
            var declarationPosition = -1;
            var blockDepth = 0;
            var guaranteedBlockStarts = new HashSet<int>();
            var guaranteedBlockDepths = new Stack<int>();
            var conditionalBlockStarts = new HashSet<int>();
            var conditionalBlockDepths = new Stack<int>();
            var optionalRanges = new List<(int Start, int End)>();
            var unreachableOperands = new List<(int Start, int End)>();
            var unreachableBranchEnds = GetUnreachableCatchEndsInInterfaceFlow(start, end);
            var nestedHandlers = GetNestedHandlerRangesInInterfaceFlow(start, end);
            for (var cursor = start; cursor < end; cursor++)
            {
                if (unreachableBranchEnds.TryGetValue(cursor, out var afterElse))
                {
                    cursor = afterElse - 1;
                    continue;
                }
                if (unreachableOperands.Any(range => cursor >= range.Start && cursor < range.End)) continue;
                var token = TokenAt(cursor);
                // Stop only when every enclosing branch is mandatory; a division in an unknown branch
                // cannot make writes after that branch unreachable on its other path.
                if (!nestedHandlers.Any(range => cursor >= range.Start && cursor < range.End) &&
                    !optionalRanges.Any(range => cursor >= range.Start && cursor < range.End) &&
                    (blockDepth == 0 || guaranteedBlockDepths.TryPeek(out var guaranteedDepth) &&
                        blockDepth == guaranteedDepth && conditionalBlockDepths.Count == 0) &&
                    (IsGuaranteedZeroDivisionStatement(cursor, end, out _) ||
                     IsGuaranteedZeroDivisionConditionStatement(cursor, end, out _) ||
                     IsGuaranteedInvalidReferenceCastStatement(cursor, end, out _)))
                    break;
                if (token.Lexeme == "if" && TokenAt(cursor + 1).Kind == TokenKind.LParen)
                {
                    var conditionEnd = FindInterfaceFlowGroupEnd(cursor + 1, TokenKind.LParen, TokenKind.RParen);
                    var thenEnd = conditionEnd > 0 && TokenAt(conditionEnd).Kind == TokenKind.LBrace
                        ? FindInterfaceFlowGroupEnd(conditionEnd, TokenKind.LBrace, TokenKind.RBrace)
                        : -1;
                    if (thenEnd > 0)
                    {
                        var conditionValue = false;
                        var hasConstantCondition = TryEvaluateBooleanConstant(cursor + 2,
                            conditionEnd - 1, out conditionValue, unreachableOperands);
                        var conditionOutcomes = EvaluateGuaranteedZeroDivisionConditionExpression(
                            cursor + 2, conditionEnd - 1);
                        if ((conditionOutcomes & ConditionEvaluation.True) == 0)
                        {
                            if ((conditionOutcomes & ConditionEvaluation.False) == 0) break;
                            unreachableBranchEnds[conditionEnd] = thenEnd;
                            continue;
                        }
                        if (hasConstantCondition && conditionValue)
                        {
                            if (conditionalBlockDepths.Count == 0)
                                guaranteedBlockStarts.Add(conditionEnd);
                            if (TokenAt(thenEnd).Lexeme == "else")
                            {
                                if (TokenAt(thenEnd + 1).Lexeme == "if")
                                {
                                    TryFindConditionalChainTerminator(cursor, out var chainEnd, out _);
                                    if (chainEnd > thenEnd) unreachableBranchEnds[thenEnd] = chainEnd;
                                }
                                var elseEnd = TokenAt(thenEnd + 1).Kind == TokenKind.LBrace
                                    ? FindInterfaceFlowGroupEnd(thenEnd + 1, TokenKind.LBrace, TokenKind.RBrace)
                                    : -1;
                                if (elseEnd > 0) unreachableBranchEnds[thenEnd] = elseEnd;
                            }
                        }
                        else
                        {
                            conditionalBlockStarts.Add(conditionEnd);
                            TryFindConditionalChainTerminator(cursor, out var chainEnd, out _);
                            optionalRanges.Add((conditionEnd, chainEnd > conditionEnd ? chainEnd : end));
                        }
                    }
                }
                if (token.Lexeme is "while" or "foreach" && TokenAt(cursor + 1).Kind == TokenKind.LParen)
                {
                    if (token.Lexeme == "foreach") AddForeachFlowBindings(cursor, foreachBindings);
                    var conditionEnd = FindInterfaceFlowGroupEnd(cursor + 1, TokenKind.LParen, TokenKind.RParen);
                    var bodyEnd = conditionEnd > 0 && TokenAt(conditionEnd).Kind == TokenKind.LBrace
                        ? FindInterfaceFlowGroupEnd(conditionEnd, TokenKind.LBrace, TokenKind.RBrace)
                        : -1;
                    var conditionValue = false;
                    var hasConstantCondition = token.Lexeme == "while" &&
                        TryEvaluateBooleanConstant(cursor + 2, conditionEnd - 1,
                            out conditionValue, unreachableOperands);
                    var conditionCannotBeTrue = token.Lexeme == "while" &&
                        !HasPossibleTrueConditionOutcome(cursor + 2, conditionEnd - 1);
                    if (bodyEnd > 0 && token.Lexeme == "while" &&
                        (conditionCannotBeTrue || hasConstantCondition && !conditionValue))
                    {
                        if (conditionCannotBeTrue &&
                            EvaluateGuaranteedZeroDivisionConditionExpression(cursor + 2, conditionEnd - 1) ==
                            ConditionEvaluation.Throws)
                            break;
                        unreachableBranchEnds[conditionEnd] = bodyEnd;
                        continue;
                    }
                    if (bodyEnd > 0 && conditionalBlockDepths.Count == 0)
                        conditionalBlockStarts.Add(conditionEnd);
                    if (bodyEnd > 0) optionalRanges.Add((conditionEnd, bodyEnd));
                }
                if (token.Kind == TokenKind.LBrace)
                {
                    var scope = new HashSet<string>(StringComparer.Ordinal);
                    if (foreachBindings.TryGetValue(cursor, out var names)) scope.UnionWith(names);
                    shadows.Push(scope);
                    blockDepth++;
                    if (guaranteedBlockStarts.Remove(cursor)) guaranteedBlockDepths.Push(blockDepth);
                    if (conditionalBlockStarts.Remove(cursor)) conditionalBlockDepths.Push(blockDepth);
                }
                else if (token.Kind == TokenKind.RBrace && shadows.Count > 1)
                {
                    if (guaranteedBlockDepths.TryPeek(out var closingDepth) &&
                        closingDepth == blockDepth)
                        guaranteedBlockDepths.Pop();
                    if (conditionalBlockDepths.TryPeek(out var conditionalDepth) &&
                        conditionalDepth == blockDepth)
                        conditionalBlockDepths.Pop();
                    shadows.Pop();
                    blockDepth--;
                }

                if (token.Kind == TokenKind.Keyword && token.Lexeme is "let" or "var")
                {
                    declarationScope = shadows.Peek();
                    declarationName = TokenAt(cursor + 1).Lexeme;
                    declarationPosition = cursor + 1;
                }
                if (token.Kind == TokenKind.Semicolon && declarationScope != null)
                {
                    declarationScope.Add(declarationName!);
                    declarationScope = null;
                }

                if (TokenAt(cursor).Lexeme is "delete" or "unbox")
                {
                    var consumedName = GetInterfaceConsumedLocalName(cursor);
                    if (consumedName == name && !shadows.Any(scope => scope.Contains(consumedName)) &&
                        TryResolveLocal(consumedName, out var consumed) && consumed.Slot == slot)
                        return false;
                }
                if (token.Kind != TokenKind.Identifier || token.Lexeme != name || cursor == declarationPosition ||
                    shadows.Any(scope => scope.Contains(name)) || TokenAt(cursor - 1).Kind == TokenKind.Dot ||
                    TokenAt(cursor + 1).Kind != TokenKind.Assign ||
                    !TryResolveLocal(name, out var assigned) || assigned.Slot != slot)
                    continue;
                var statementEnd = FindInterfaceFlowStatementEnd(cursor + 1, end);
                if (statementEnd <= cursor + 2) return false;
                var assignedType = GetNominalLoopAssignmentType(name, cursor + 2, statementEnd,
                    shadows, interfaceType);
                if (assignedType == null || !IsClassAssignable(assignedType, interfaceType)) return false;
            }
            return true;
        }

        private void ForgetInterfaceNarrowing(int slot)
        {
            if (_interfaceWriteReachable)
                foreach (var writes in _interfaceWriteScopes) writes.Add(slot);
            foreach (var scope in _scopes)
            {
                var binding = scope.FirstOrDefault(entry => entry.Value.Slot == slot);
                if (binding.Key == null) continue;
                scope[binding.Key] = _declaredLocals[slot];
                return;
            }
        }

        private Dictionary<int, LocalInfo> CaptureLoopInterfaceFlowState()
        {
            var headerEnd = FindInterfaceFlowGroupEnd(_position + 1, TokenKind.LParen, TokenKind.RParen);
            var bodyEnd = headerEnd < 0 ? -1 : FindInterfaceFlowGroupEnd(headerEnd, TokenKind.LBrace, TokenKind.RBrace);
            var isWhileLoop = TokenAt(_position).Lexeme == "while";
            if (isWhileLoop && !HasPossibleTrueConditionOutcome(_position + 2, headerEnd - 1))
                return CaptureInterfaceFlowState();
            var backEdgeInvariantTypes = GetLoopBackEdgeInvariantTypes(headerEnd, bodyEnd);
            var unreachableLogicalOperands = new List<(int Start, int End)>();
            foreach (var (slot, typeName) in backEdgeInvariantTypes)
                foreach (var scope in _scopes)
                {
                    var binding = scope.FirstOrDefault(entry => entry.Value.Slot == slot);
                    if (binding.Key == null) continue;
                    scope[binding.Key] = scope[binding.Key] with { ClassName = typeName };
                    break;
                }
            // Drop potentially loop-carried facts before compiling the header or any iteration.
            // Declarations shadow only after their initializer; foreach bindings start at the body.
            var shadows = new Stack<HashSet<string>>();
            shadows.Push(new HashSet<string>(StringComparer.Ordinal));
            var foreachBindings = new Dictionary<int, HashSet<string>>();
            var enclosingBlockEnds = new Stack<int>();
            var bodyBlockDepth = 0;
            HashSet<string>? declarationScope = null;
            string? declarationName = null;
            var declarationPosition = -1;
            // Outer terminal joins are discovered before their nested branches are scanned, so keep all pending boundaries.
            var stopAtTerminalBoundaries = new List<(int Position, int ParentBlockEnd)>();
            var unreachableElseChains = new List<(int ElsePosition, int ChainEnd)>();
            var nonLoopExitBlocks = new List<(int StartPosition, int EndPosition)>();
            var exceptionRejoinRanges = new List<(int Start, int End)>();
            var nestedLoopRanges = new List<(int Start, int End)>();

            bool CanSkipBranchWrites(int start, int end, int statementPosition)
                => IsNonLoopExitBlock(start, end) ||
                    !nestedLoopRanges.Any(range => statementPosition > range.Start && statementPosition < range.End) &&
                    CanProveBlockCannotFallThrough(start, end) && !HasReachableContinueInRange(start + 1, end - 1);

            void InvalidateReachableConditionConsumers(int start, int end)
            {
                for (var consumerPosition = start; consumerPosition < end; consumerPosition++)
                {
                    if (TokenAt(consumerPosition).Lexeme is not ("unbox" or "delete") ||
                        unreachableLogicalOperands.Any(range => consumerPosition >= range.Start && consumerPosition < range.End))
                        continue;
                    var consumerName = GetInterfaceConsumedLocalName(consumerPosition);
                    if (consumerName != null && !shadows.Any(scope => scope.Contains(consumerName)) &&
                        TryResolveLocal(consumerName, out var consumedLocal))
                        ForgetInterfaceNarrowing(consumedLocal.Slot);
                }
            }

            for (var position = _position; position < bodyEnd; position++)
            {
                var token = TokenAt(position);
                if (bodyBlockDepth > 0 && (token.Lexeme is "while" or "foreach") &&
                    TokenAt(position + 1).Kind == TokenKind.LParen)
                {
                    var nestedLoopHeaderEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
                    var nestedBodyEnd = nestedLoopHeaderEnd > 0 && TokenAt(nestedLoopHeaderEnd).Kind == TokenKind.LBrace
                        ? FindInterfaceFlowGroupEnd(nestedLoopHeaderEnd, TokenKind.LBrace, TokenKind.RBrace)
                        : -1;
                    if (nestedBodyEnd > 0)
                        nestedLoopRanges.Add((nestedLoopHeaderEnd, nestedBodyEnd));
                    var nestedCondition = false;
                    var nestedIsConstant = token.Lexeme == "while" &&
                        TryEvaluateBooleanConstant(position + 2, nestedLoopHeaderEnd - 1,
                            out nestedCondition, unreachableLogicalOperands);
                    var nestedCannotBeTrue = token.Lexeme == "while" &&
                        !HasPossibleTrueConditionOutcome(position + 2, nestedLoopHeaderEnd - 1);
                    if (nestedBodyEnd > 0 && token.Lexeme == "while" &&
                        (nestedCannotBeTrue || nestedIsConstant && !nestedCondition))
                    {
                        // A constant-false nested loop cannot carry writes into this loop's entry state.
                        // Its condition still runs before the body is skipped.
                        InvalidateReachableConditionConsumers(position + 2, nestedLoopHeaderEnd - 1);
                        position = nestedBodyEnd - 1;
                        continue;
                    }
                }
                if (bodyBlockDepth > 0 && token.Lexeme == "try" && TokenAt(position + 1).Kind == TokenKind.LBrace)
                {
                    var tryEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LBrace, TokenKind.RBrace);
                    if (tryEnd > 0 && TryFindCatchBodies(tryEnd, out var catchBodies, out var lastCatchClose))
                    {
                        var hasEnclosingRejoin = exceptionRejoinRanges.Any(range => position > range.Start && position < range.End);
                        if (hasEnclosingRejoin ||
                            catchBodies.Any(body => !CanProveBlockCannotFallThrough(body.Open, body.Close) ||
                                HasReachableLoopTransferInRange(body.Open + 1, body.Close - 1, "break") ||
                                HasReachableContinueInRange(body.Open + 1, body.Close - 1)))
                            exceptionRejoinRanges.Add((position + 1, tryEnd));
                        if (TryFindTopLevelLoopTerminator(position + 1, tryEnd, out _) &&
                            catchBodies.All(body => TryFindTopLevelLoopTerminator(body.Open, body.Close, out _)))
                        {
                            // Every normal and exceptional arm terminates, so the following text is unreachable.
                            stopAtTerminalBoundaries.Add((lastCatchClose - 1, enclosingBlockEnds.Peek()));
                            if (!hasEnclosingRejoin && IsNonLoopExitBlock(position + 1, tryEnd) &&
                                catchBodies.All(body => IsNonLoopExitBlock(body.Open, body.Close)))
                            {
                                foreach (var body in catchBodies)
                                    nonLoopExitBlocks.Add((body.Open, body.Close));
                                nonLoopExitBlocks.Add((position + 1, tryEnd));
                            }
                        }
                    }
                }
                var nonLoopExitBlockIndex = nonLoopExitBlocks.FindLastIndex(block => block.StartPosition == position);
                if (nonLoopExitBlockIndex >= 0)
                {
                    var endPosition = nonLoopExitBlocks[nonLoopExitBlockIndex].EndPosition;
                    nonLoopExitBlocks.RemoveAt(nonLoopExitBlockIndex);
                    var terminalMarkerIndex = stopAtTerminalBoundaries.FindLastIndex(marker => marker.Position == endPosition - 1);
                    if (terminalMarkerIndex >= 0)
                    {
                        var parentBlockEnd = stopAtTerminalBoundaries[terminalMarkerIndex].ParentBlockEnd;
                        stopAtTerminalBoundaries.RemoveAt(terminalMarkerIndex);
                        if (bodyBlockDepth == 1)
                            break;
                        while (enclosingBlockEnds.Count > 0 && enclosingBlockEnds.Peek() < parentBlockEnd)
                        {
                            enclosingBlockEnds.Pop();
                            bodyBlockDepth--;
                            shadows.Pop();
                        }
                        position = parentBlockEnd - 1;
                    }
                    else
                    {
                        position = endPosition - 1;
                    }
                    continue;
                }
                if (bodyBlockDepth > 0 && token.Lexeme == "if" && TokenAt(position + 1).Kind == TokenKind.LParen)
                {
                    var isElseIfArm = TokenAt(position - 1).Lexeme == "else";
                    var conditionEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
                    if (conditionEnd > 0 && TokenAt(conditionEnd).Kind == TokenKind.LBrace)
                    {
                        var thenEnd = FindInterfaceFlowGroupEnd(conditionEnd, TokenKind.LBrace, TokenKind.RBrace);
                        var hasConstantCondition = TryEvaluateBooleanConstant(position + 2, conditionEnd - 1,
                            out var ifCondition, unreachableLogicalOperands);
                        if (thenEnd > 0 && hasConstantCondition && ifCondition && TokenAt(thenEnd).Lexeme == "else")
                        {
                            TryFindConditionalChainTerminator(position, out var unreachableChainEnd, out _);
                            if (unreachableChainEnd > thenEnd)
                                unreachableElseChains.Add((thenEnd, unreachableChainEnd));
                        }
                        if (thenEnd > 0 && !hasConstantCondition &&
                            !exceptionRejoinRanges.Any(range => position > range.Start && position < range.End) &&
                            CanSkipBranchWrites(conditionEnd, thenEnd, position))
                        {
                            var elsePosition = thenEnd;
                            if (TokenAt(elsePosition).Lexeme == "else" &&
                                TokenAt(elsePosition + 1).Kind == TokenKind.LBrace)
                            {
                                var elseEnd = FindInterfaceFlowGroupEnd(elsePosition + 1, TokenKind.LBrace, TokenKind.RBrace);
                                if (elseEnd > 0 && TryFindTopLevelLoopTerminator(elsePosition + 1, elseEnd, out _))
                                {
                                    stopAtTerminalBoundaries.Add((elseEnd - 1, enclosingBlockEnds.Peek()));
                                }
                            }
                            else if (!isElseIfArm && TokenAt(elsePosition).Lexeme == "else" &&
                                TokenAt(elsePosition + 1).Lexeme == "if" &&
                                TryFindConditionalChainTerminator(position, out var returnChainEnd, out _))
                            {
                                stopAtTerminalBoundaries.Add((returnChainEnd - 1, enclosingBlockEnds.Peek()));
                            }
                            // These writes cannot reach the next iteration. The parser still
                            // captures break-exit states independently for loop exit joins.
                            // The condition itself runs even when its selected body exits.
                            InvalidateReachableConditionConsumers(position + 2, conditionEnd - 1);
                            position = thenEnd - 1;
                            continue;
                        }
                        if (thenEnd > 0 && hasConstantCondition && !ifCondition)
                        {
                            InvalidateReachableConditionConsumers(position + 2, conditionEnd - 1);
                            var elsePosition = thenEnd;
                            if (TokenAt(elsePosition).Lexeme == "else" &&
                                TokenAt(elsePosition + 1).Kind == TokenKind.LBrace)
                            {
                                var elseEnd = FindInterfaceFlowGroupEnd(elsePosition + 1, TokenKind.LBrace, TokenKind.RBrace);
                                if (elseEnd > 0 && TryFindTopLevelLoopTerminator(elsePosition + 1, elseEnd,
                                    out var elseTerminatorPosition))
                                {
                                    stopAtTerminalBoundaries.Add((elseTerminatorPosition, enclosingBlockEnds.Peek()));
                                }
                            }
                            else if (!isElseIfArm && TokenAt(elsePosition).Lexeme == "else" &&
                                TokenAt(elsePosition + 1).Lexeme == "if" &&
                                TryFindConditionalChainTerminator(position, out var falseChainEnd, out _))
                            {
                                stopAtTerminalBoundaries.Add((falseChainEnd - 1, enclosingBlockEnds.Peek()));
                            }
                            position = thenEnd - 1;
                            continue;
                        }
                        if (thenEnd > 0 && !isElseIfArm && hasConstantCondition && ifCondition &&
                            TryFindTopLevelLoopTerminator(conditionEnd, thenEnd, out var terminatorPosition))
                        {
                            stopAtTerminalBoundaries.Add((terminatorPosition, enclosingBlockEnds.Peek()));
                        }
                        if (thenEnd > 0 && !isElseIfArm && !hasConstantCondition &&
                            TokenAt(thenEnd).Lexeme == "else" &&
                            TryFindConditionalChainTerminator(position, out var allArmsChainEnd, out _))
                        {
                            // Every branch in the complete else-if chain terminates; skip its parent tail.
                            stopAtTerminalBoundaries.Add((allArmsChainEnd - 1, enclosingBlockEnds.Peek()));
                        }
                    }
                }
                if (bodyBlockDepth > 0 && token.Lexeme == "else")
                {
                    var unreachableElseIndex = unreachableElseChains.FindLastIndex(chain => chain.ElsePosition == position);
                    if (unreachableElseIndex >= 0)
                    {
                        var chainEnd = unreachableElseChains[unreachableElseIndex].ChainEnd;
                        unreachableElseChains.RemoveAt(unreachableElseIndex);
                        var terminalMarkerIndex = stopAtTerminalBoundaries.FindLastIndex(marker => marker.Position == chainEnd - 1);
                        if (terminalMarkerIndex >= 0)
                        {
                            var parentBlockEnd = stopAtTerminalBoundaries[terminalMarkerIndex].ParentBlockEnd;
                            stopAtTerminalBoundaries.RemoveAt(terminalMarkerIndex);
                            if (bodyBlockDepth == 1)
                                break;
                            while (enclosingBlockEnds.Count > 0 && enclosingBlockEnds.Peek() < parentBlockEnd)
                            {
                                enclosingBlockEnds.Pop();
                                bodyBlockDepth--;
                                shadows.Pop();
                            }
                            position = parentBlockEnd - 1;
                        }
                        else
                        {
                            position = chainEnd - 1;
                        }
                        continue;
                    }
                }
                if (bodyBlockDepth > 0 && token.Lexeme == "else" && TokenAt(position + 1).Kind == TokenKind.LBrace &&
                    !exceptionRejoinRanges.Any(range => position > range.Start && position < range.End))
                {
                    var elseEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LBrace, TokenKind.RBrace);
                    if (elseEnd > 0 && CanSkipBranchWrites(position + 1, elseEnd, position))
                    {
                        // As above, this arm cannot rejoin a surviving loop path.
                        var markerIndex = stopAtTerminalBoundaries.FindLastIndex(marker => marker.Position == elseEnd - 1);
                        if (markerIndex >= 0)
                        {
                            var parentBlockEnd = stopAtTerminalBoundaries[markerIndex].ParentBlockEnd;
                            stopAtTerminalBoundaries.RemoveAt(markerIndex);
                            if (bodyBlockDepth == 1)
                                break;
                            while (enclosingBlockEnds.Count > 0 && enclosingBlockEnds.Peek() < parentBlockEnd)
                            {
                                enclosingBlockEnds.Pop();
                                bodyBlockDepth--;
                                shadows.Pop();
                            }
                            position = parentBlockEnd - 1;
                            continue;
                        }
                        position = elseEnd - 1;
                        continue;
                    }
                }
                if (token.Kind is TokenKind.Keyword or TokenKind.Identifier && token.Lexeme == "foreach")
                    AddForeachFlowBindings(position, foreachBindings);
                if (token.Kind == TokenKind.LBrace)
                {
                    bodyBlockDepth++;
                    enclosingBlockEnds.Push(FindInterfaceFlowGroupEnd(position, TokenKind.LBrace, TokenKind.RBrace) - 1);
                    shadows.Push(foreachBindings.GetValueOrDefault(position) ?? new HashSet<string>(StringComparer.Ordinal));
                }
                if (token.Kind == TokenKind.RBrace)
                {
                    bodyBlockDepth--;
                    enclosingBlockEnds.Pop();
                    shadows.Pop();
                }
                // A terminal statement makes the rest of its lexical block unreachable. At the
                // loop body's top level it also makes the rest of the loop body unreachable.
                var stopMarkerIndex = stopAtTerminalBoundaries.FindLastIndex(marker => marker.Position == position);
                if (stopMarkerIndex >= 0)
                {
                    var parentBlockEnd = stopAtTerminalBoundaries[stopMarkerIndex].ParentBlockEnd;
                    stopAtTerminalBoundaries.RemoveAt(stopMarkerIndex);
                    if (bodyBlockDepth == 1)
                        break;
                    while (enclosingBlockEnds.Count > 0 && enclosingBlockEnds.Peek() < parentBlockEnd)
                    {
                        enclosingBlockEnds.Pop();
                        bodyBlockDepth--;
                        shadows.Pop();
                    }
                    position = parentBlockEnd - 1;
                    continue;
                }
                if (bodyBlockDepth > 0 && token.Lexeme is ("break" or "continue" or "return" or "throw") &&
                    FindInterfaceFlowStatementEnd(position, bodyEnd) > position)
                {
                    if (bodyBlockDepth == 1)
                        break;
                    var enclosingBlockEnd = FindEnclosingBlockEnd(position + 1, bodyEnd);
                    if (enclosingBlockEnd > position)
                    {
                        position = enclosingBlockEnd - 1;
                        continue;
                    }
                }
                if (token.Kind == TokenKind.Keyword && token.Lexeme is "let" or "var")
                {
                    declarationScope = shadows.Peek();
                    declarationName = TokenAt(position + 1).Lexeme;
                    declarationPosition = position + 1;
                }
                if (token.Kind == TokenKind.Semicolon && declarationScope != null)
                {
                    declarationScope.Add(declarationName!);
                    declarationScope = null;
                }
                if ((token.Kind is TokenKind.Identifier or TokenKind.Keyword) &&
                    TokenAt(position + 1).Kind == TokenKind.LParen &&
                    token.Lexeme is not ("if" or "while" or "foreach" or "with"))
                {
                    var callEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
                    if (callEnd > position)
                        TryEvaluateBooleanConstant(position, callEnd, out _, unreachableLogicalOperands);
                }
                if (token.Kind == TokenKind.Assign)
                {
                    var expressionEnd = FindInterfaceFlowStatementEnd(position, bodyEnd);
                    if (expressionEnd > position)
                        TryEvaluateBooleanConstant(position + 1, expressionEnd, out _, unreachableLogicalOperands);
                }
                string? name = position != declarationPosition && token.Kind == TokenKind.Identifier &&
                    TokenAt(position + 1).Kind == TokenKind.Assign && TokenAt(position - 1).Kind != TokenKind.Dot
                    ? token.Lexeme : null;
                if (token.Kind is TokenKind.Keyword or TokenKind.Identifier && token.Lexeme is "delete" or "unbox" &&
                    !unreachableLogicalOperands.Any(range => position >= range.Start && position < range.End))
                {
                    name = GetInterfaceConsumedLocalName(position);
                }
                if (name != null && !shadows.Any(scope => scope.Contains(name)) && TryResolveLocal(name, out var local))
                {
                    var assignmentPreservesEntry = token.Kind == TokenKind.Identifier &&
                        TokenAt(position + 1).Kind == TokenKind.Assign &&
                        PreservesNominalLoopAssignment(local, name, position + 2,
                            FindInterfaceFlowStatementEnd(position + 1, bodyEnd), shadows);
                    if (!assignmentPreservesEntry &&
                        !(position > headerEnd && backEdgeInvariantTypes.ContainsKey(local.Slot)))
                        ForgetInterfaceNarrowing(local.Slot);
                }
            }
            return CaptureInterfaceFlowState();
        }

        private bool PreservesNominalLoopAssignment(LocalInfo entry, string name, int start, int end,
            Stack<HashSet<string>> shadows, bool selfPreserves = true)
        {
            if (entry.Type != ExprType.Object || entry.ClassName == null || !_interfaces.ContainsKey(entry.ClassName))
                return false;
            var resultClassName = GetNominalLoopAssignmentType(name, start, end, shadows,
                selfPreserves ? entry.ClassName : null);
            return resultClassName != null && IsClassAssignable(resultClassName, entry.ClassName);
        }

        private string? GetNominalLoopAssignmentType(string name, int start, int end,
            Stack<HashSet<string>> shadows, string? selfType)
        {
            while (start < end && TokenAt(start).Kind == TokenKind.LParen &&
                FindInterfaceFlowGroupEnd(start, TokenKind.LParen, TokenKind.RParen) == end)
            {
                start++;
                end--;
            }
            // Use declared nominal contracts, never another local's temporary narrowing.
            // Consumers inside arguments are still visited by the enclosing entry scan.
            string? resultClassName = null;
            if (end - start >= 3 && TokenAt(end - 2).Lexeme == "as" &&
                TokenAt(end - 1).Kind == TokenKind.Identifier &&
                (_interfaces.ContainsKey(TokenAt(end - 1).Lexeme) || _classes.ContainsKey(TokenAt(end - 1).Lexeme)))
                resultClassName = TokenAt(end - 1).Lexeme;
            else if (end == start + 1 && TokenAt(start).Kind == TokenKind.Identifier)
            {
                var sourceName = TokenAt(start).Lexeme;
                if (sourceName == name) return selfType;
                if (!shadows.Any(scope => scope.Contains(sourceName)) && TryResolveLocal(sourceName, out var source) &&
                    _declaredLocals.TryGetValue(source.Slot, out var declared) && declared.Type == ExprType.Object)
                    resultClassName = declared.ClassName;
            }
            else if (TokenAt(start).Lexeme == "new" && TokenAt(start + 1).Kind == TokenKind.Identifier &&
                TokenAt(start + 2).Kind == TokenKind.LParen &&
                FindInterfaceFlowGroupEnd(start + 2, TokenKind.LParen, TokenKind.RParen) == end &&
                _classes.ContainsKey(TokenAt(start + 1).Lexeme))
                resultClassName = TokenAt(start + 1).Lexeme;
            else if (TokenAt(start).Kind == TokenKind.Identifier && TokenAt(start + 1).Kind == TokenKind.LParen &&
                FindInterfaceFlowGroupEnd(start + 1, TokenKind.LParen, TokenKind.RParen) == end &&
                !HostIntrinsicCatalog.TryGet(TokenAt(start).Lexeme, out _) &&
                _functionByName.TryGetValue(TokenAt(start).Lexeme, out var function) && function.ReturnType == ExprType.Object)
                resultClassName = function.ReturnClassName;
            return resultClassName;
        }

        // Recognize straight-line bodies and supported terminal branches, retaining first-
        // iteration facts only when no path can reach this loop's condition a second time.
        private bool CanProveLoopBodyHasNoBackEdge(int bodyStart, int bodyEnd)
        {
            for (var position = bodyStart + 1; position < bodyEnd - 1; position++)
            {
                var token = TokenAt(position);
                if (token.Lexeme == "if" && TokenAt(position + 1).Kind == TokenKind.LParen)
                {
                    var allBranchesTerminate = TryFindConditionalChainTerminator(position,
                        out var chainEnd, out _);
                    if (chainEnd <= position || HasReachableContinueInRange(position, chainEnd))
                        return false;
                    if (allBranchesTerminate)
                        return true;
                    position = chainEnd - 1;
                    continue;
                }
                if (token.Lexeme == "try" && TokenAt(position + 1).Kind == TokenKind.LBrace)
                {
                    var tryEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LBrace, TokenKind.RBrace);
                    if (tryEnd <= 0 || !TryFindCatchBodies(tryEnd, out var catchBodies, out var lastCatchClose))
                        return false;
                    if (lastCatchClose <= 0 || HasReachableContinueInRange(position + 2, tryEnd - 1) ||
                        catchBodies.Any(body => HasReachableContinueInRange(body.Open + 1, body.Close - 1)))
                        return false;
                    var tryTerminates = TryFindTopLevelLoopTerminator(position + 1, tryEnd, out _);
                    var catchesTerminate = catchBodies.All(body => TryFindTopLevelLoopTerminator(body.Open, body.Close, out _));
                    if (tryTerminates && catchesTerminate)
                        return true;
                    position = lastCatchClose - 1;
                    continue;
                }
                if (TryFindWithBlockEnd(position, out var withBlockStart, out var withBlockEnd))
                {
                    if (HasReachableContinueInRange(withBlockStart + 1, withBlockEnd - 1))
                        return false;
                    if (CanProveBlockCannotFallThrough(withBlockStart, withBlockEnd))
                        return true;
                    position = withBlockEnd - 1;
                    continue;
                }
                if ((token.Lexeme is "while" or "foreach") && TokenAt(position + 1).Kind == TokenKind.LParen)
                {
                    var headerEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
                    var nestedEnd = headerEnd > 0 && TokenAt(headerEnd).Kind == TokenKind.LBrace
                        ? FindInterfaceFlowGroupEnd(headerEnd, TokenKind.LBrace, TokenKind.RBrace)
                        : -1;
                    if (nestedEnd <= 0)
                        return false;
                    if (IsNonfallthroughWhile(position, headerEnd, nestedEnd))
                        return true;
                    // Inner loop transfers cannot revisit this loop's condition.
                    // If the inner loop finishes, keep checking the outer continuation.
                    position = nestedEnd - 1;
                    continue;
                }
                if (token.Kind == TokenKind.LBrace || token.Lexeme == "continue")
                    return false;
                if (token.Lexeme is "break" or "return" or "throw")
                    return true;
            }
            return false;
        }

        private bool HasReachableContinueInRange(int start, int end)
            => HasReachableLoopTransferInRange(start, end, "continue");

        private bool HasReachableLoopTransferInRange(int start, int end, string transfer)
        {
            for (var position = start; position < end; position++)
            {
                var token = TokenAt(position);
                if (token.Kind == TokenKind.Keyword && token.Lexeme == transfer)
                    return true;
                if (token.Kind == TokenKind.Keyword && token.Lexeme is "break" or "continue" or "return" or "throw")
                    return false;
                if (token.Lexeme == "if" && TokenAt(position + 1).Kind == TokenKind.LParen)
                {
                    var conditionEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
                    var thenEnd = conditionEnd > 0 && TokenAt(conditionEnd).Kind == TokenKind.LBrace
                        ? FindInterfaceFlowGroupEnd(conditionEnd, TokenKind.LBrace, TokenKind.RBrace)
                        : -1;
                    if (thenEnd > 0)
                    {
                        var chainTerminates = TryFindConditionalChainTerminator(position,
                            out var parsedChainEnd, out _);
                        if (HasReachableLoopTransferInConditional(position, conditionEnd, thenEnd, transfer))
                            return true;
                        var chainEnd = parsedChainEnd > position ? parsedChainEnd : thenEnd;
                        if (chainTerminates)
                            return false;
                        position = chainEnd - 1;
                        continue;
                    }
                }
                if (token.Lexeme == "try" && TokenAt(position + 1).Kind == TokenKind.LBrace)
                {
                    var tryEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LBrace, TokenKind.RBrace);
                    if (tryEnd > 0 && TryFindCatchBodies(tryEnd, out var catchBodies, out var lastCatchClose))
                    {
                        if (HasReachableLoopTransferInRange(position + 2, tryEnd - 1, transfer) ||
                            catchBodies.Any(body => HasReachableLoopTransferInRange(body.Open + 1, body.Close - 1, transfer)))
                        {
                            return true;
                        }
                        var tryCannotFallThrough = CanProveBlockCannotFallThrough(position + 1, tryEnd);
                        var allCatchesCannotFallThrough = catchBodies.All(body => CanProveBlockCannotFallThrough(body.Open, body.Close));
                        if (tryCannotFallThrough && allCatchesCannotFallThrough)
                            return false;
                        position = lastCatchClose - 1;
                        continue;
                    }
                }
                if (TryFindWithBlockEnd(position, out var withBlockStart, out var withBlockEnd))
                {
                    if (HasReachableLoopTransferInRange(withBlockStart + 1, withBlockEnd - 1, transfer))
                        return true;
                    if (CanProveBlockCannotFallThrough(withBlockStart, withBlockEnd))
                        return false;
                    position = withBlockEnd - 1;
                    continue;
                }
                if ((token.Lexeme is "while" or "foreach") && TokenAt(position + 1).Kind == TokenKind.LParen)
                {
                    var headerEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
                    var bodyEnd = headerEnd > 0 && TokenAt(headerEnd).Kind == TokenKind.LBrace
                        ? FindInterfaceFlowGroupEnd(headerEnd, TokenKind.LBrace, TokenKind.RBrace)
                        : -1;
                    if (bodyEnd > 0)
                    {
                        if (IsNonfallthroughWhile(position, headerEnd, bodyEnd))
                            return false;
                        // Transfers inside a nested loop target that loop, not this loop.
                        position = bodyEnd - 1;
                        continue;
                    }
                }
            }
            return false;
        }

        private bool CanProveBlockCannotFallThrough(int blockStart, int blockEnd)
        {
            for (var position = blockStart + 1; position < blockEnd - 1; position++)
            {
                if (TokenAt(position).Kind == TokenKind.Keyword &&
                    TokenAt(position).Lexeme is "break" or "continue" or "return" or "throw")
                    return true;
                if (TokenAt(position).Lexeme == "if" && TokenAt(position + 1).Kind == TokenKind.LParen)
                {
                    var terminates = TryFindConditionalChainTerminator(position, out var chainEnd, out _);
                    if (terminates)
                        return true;
                    if (chainEnd > position)
                    {
                        position = chainEnd - 1;
                        continue;
                    }
                }
                if (TokenAt(position).Lexeme == "try" && TokenAt(position + 1).Kind == TokenKind.LBrace)
                {
                    var tryEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LBrace, TokenKind.RBrace);
                    if (tryEnd > 0 && TryFindCatchBodies(tryEnd, out var catchBodies, out var lastCatchClose))
                    {
                        if (CanProveBlockCannotFallThrough(position + 1, tryEnd) &&
                            catchBodies.All(body => CanProveBlockCannotFallThrough(body.Open, body.Close)))
                            return true;
                        position = lastCatchClose - 1;
                        continue;
                    }
                }
                if (TryFindWithBlockEnd(position, out var withBlockStart, out var withBlockEnd))
                {
                    if (CanProveBlockCannotFallThrough(withBlockStart, withBlockEnd))
                        return true;
                    position = withBlockEnd - 1;
                    continue;
                }
                if ((TokenAt(position).Lexeme is "while" or "foreach") &&
                    TokenAt(position + 1).Kind == TokenKind.LParen)
                {
                    var headerEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
                    var nestedEnd = headerEnd > 0 && TokenAt(headerEnd).Kind == TokenKind.LBrace
                        ? FindInterfaceFlowGroupEnd(headerEnd, TokenKind.LBrace, TokenKind.RBrace)
                        : -1;
                    if (nestedEnd > 0)
                    {
                        if (IsNonfallthroughWhile(position, headerEnd, nestedEnd))
                            return true;
                        position = nestedEnd - 1;
                        continue;
                    }
                }
            }
            return false;
        }

        private bool TryFindWithBlockEnd(int position, out int blockStart, out int blockEnd)
        {
            blockStart = -1;
            blockEnd = -1;
            if (TokenAt(position).Lexeme != "with" || TokenAt(position + 1).Kind != TokenKind.LParen)
                return false;
            var headerEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
            if (headerEnd <= 0 || TokenAt(headerEnd).Kind != TokenKind.LBrace)
                return false;
            blockStart = headerEnd;
            blockEnd = FindInterfaceFlowGroupEnd(blockStart, TokenKind.LBrace, TokenKind.RBrace);
            return blockEnd > blockStart;
        }

        private bool HasReachableLoopTransferInConditional(int ifPosition, int conditionEnd, int thenEnd, string transfer)
        {
            var hasConstantCondition = TryEvaluateBooleanConstant(ifPosition + 2, conditionEnd - 1,
                out var conditionValue);
            var thenHasTransfer = HasReachableLoopTransferInRange(conditionEnd + 1, thenEnd - 1, transfer);
            var elsePosition = thenEnd;
            var hasElse = TokenAt(elsePosition).Lexeme == "else";
            var elseHasTransfer = false;
            if (hasElse && TokenAt(elsePosition + 1).Lexeme == "if")
            {
                var elseIf = elsePosition + 1;
                var elseConditionEnd = FindInterfaceFlowGroupEnd(elseIf + 1, TokenKind.LParen, TokenKind.RParen);
                var elseThenEnd = elseConditionEnd > 0 && TokenAt(elseConditionEnd).Kind == TokenKind.LBrace
                    ? FindInterfaceFlowGroupEnd(elseConditionEnd, TokenKind.LBrace, TokenKind.RBrace)
                    : -1;
                elseHasTransfer = elseThenEnd > 0 &&
                    HasReachableLoopTransferInConditional(elseIf, elseConditionEnd, elseThenEnd, transfer);
            }
            else if (hasElse && TokenAt(elsePosition + 1).Kind == TokenKind.LBrace)
            {
                var elseEnd = FindInterfaceFlowGroupEnd(elsePosition + 1, TokenKind.LBrace, TokenKind.RBrace);
                elseHasTransfer = elseEnd > 0 &&
                    HasReachableLoopTransferInRange(elsePosition + 2, elseEnd - 1, transfer);
            }

            if (!hasConstantCondition)
                return thenHasTransfer || elseHasTransfer;
            return conditionValue ? thenHasTransfer : elseHasTransfer;
        }

        private bool IsNonfallthroughWhile(int position, int headerEnd, int bodyEnd)
        {
            if (TokenAt(position).Lexeme != "while") return false;
            var outcomes = EvaluateGuaranteedZeroDivisionConditionExpression(position + 2, headerEnd - 1);
            return (outcomes & ConditionEvaluation.False) == 0 &&
                !HasReachableLoopTransferInRange(headerEnd + 1, bodyEnd - 1, "break");
        }

        private Dictionary<int, LocalInfo> CaptureLoopConditionEvaluationState(
            Dictionary<int, LocalInfo> preLoopState, Dictionary<int, LocalInfo> parsedState,
            HashSet<int> conditionWrites)
        {
            // The scanner also widens locals written by the body. Replay only writes that
            // the parser observed while evaluating the condition on the zero-iteration edge.
            RestoreInterfaceFlowState(preLoopState);
            foreach (var slot in conditionWrites)
                ForgetInterfaceNarrowing(slot);
            var conditionState = CaptureInterfaceFlowState();
            RestoreInterfaceFlowState(parsedState);
            return conditionState;
        }

        private bool TryFindTopLevelLoopTerminator(int blockStart, int blockEnd, out int terminatorPosition)
        {
            for (var position = blockStart + 1; position < blockEnd - 1; position++)
            {
                if (TokenAt(position).Lexeme is ("break" or "continue" or "return" or "throw") &&
                    FindInterfaceFlowStatementEnd(position, blockEnd) > position)
                {
                    terminatorPosition = position;
                    return true;
                }
                if (TokenAt(position).Lexeme == "if" && TokenAt(position + 1).Kind == TokenKind.LParen)
                {
                    var conditionalTerminates = TryFindConditionalChainTerminator(position,
                        out var conditionalChainEnd, out var conditionalTerminator);
                    if (conditionalTerminates)
                    {
                        terminatorPosition = conditionalTerminator;
                        return true;
                    }
                    if (conditionalChainEnd > position)
                    {
                        position = conditionalChainEnd - 1;
                        continue;
                    }
                }
                if (TokenAt(position).Lexeme == "try" && TokenAt(position + 1).Kind == TokenKind.LBrace)
                {
                    var tryEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LBrace, TokenKind.RBrace);
                    if (tryEnd > 0 && TryFindCatchBodies(tryEnd, out var catchBodies, out var lastCatchClose))
                    {
                        if (TryFindTopLevelLoopTerminator(position + 1, tryEnd, out _) &&
                            catchBodies.All(body => TryFindTopLevelLoopTerminator(body.Open, body.Close, out _)))
                        {
                            terminatorPosition = lastCatchClose - 1;
                            return true;
                        }
                        position = lastCatchClose - 1;
                        continue;
                    }
                }
                if (TokenAt(position).Lexeme == "while" && TokenAt(position + 1).Kind == TokenKind.LParen)
                {
                    var conditionEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
                    var loopBodyEnd = conditionEnd > 0 && TokenAt(conditionEnd).Kind == TokenKind.LBrace
                        ? FindInterfaceFlowGroupEnd(conditionEnd, TokenKind.LBrace, TokenKind.RBrace)
                        : -1;
                    if (loopBodyEnd > 0)
                    {
                        if (IsNonfallthroughWhile(position, conditionEnd, loopBodyEnd))
                        {
                            terminatorPosition = loopBodyEnd - 1;
                            return true;
                        }
                        // A nested loop is a statement that may finish; its internal break/continue targets another loop.
                        position = loopBodyEnd - 1;
                        continue;
                    }
                }
                if (TokenAt(position).Kind == TokenKind.LBrace)
                {
                    var nestedBlockEnd = FindInterfaceFlowGroupEnd(position, TokenKind.LBrace, TokenKind.RBrace);
                    if (nestedBlockEnd > 0)
                    {
                        if (TryFindTopLevelLoopTerminator(position, nestedBlockEnd, out terminatorPosition))
                            return true;
                        position = nestedBlockEnd - 1;
                        continue;
                    }
                }
                if (TokenAt(position).Lexeme == "foreach" && TokenAt(position + 1).Kind == TokenKind.LParen)
                {
                    var headerEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
                    var foreachBodyEnd = headerEnd > 0 && TokenAt(headerEnd).Kind == TokenKind.LBrace
                        ? FindInterfaceFlowGroupEnd(headerEnd, TokenKind.LBrace, TokenKind.RBrace)
                        : -1;
                    if (foreachBodyEnd > 0)
                    {
                        // A nested foreach is also an independent break/continue target.
                        position = foreachBodyEnd - 1;
                        continue;
                    }
                }
            }
            terminatorPosition = -1;
            return false;
        }

        private bool IsNonLoopExitBlock(int blockStart, int blockEnd)
        {
            if (!TryFindTopLevelLoopTerminator(blockStart, blockEnd, out var terminatorPosition)) return false;
            var nestedBlockDepth = 0;
            for (var position = blockStart + 1; position < blockEnd - 1; position++)
            {
                var token = TokenAt(position);
                if (position == terminatorPosition && nestedBlockDepth == 0 &&
                    token.Kind == TokenKind.Keyword && token.Lexeme is ("return" or "throw"))
                    return true;
                if (nestedBlockDepth == 0 && token.Lexeme == "if" && TokenAt(position + 1).Kind == TokenKind.LParen &&
                    TryFindConditionalChainTerminator(position, out var chainEnd, out _) &&
                    !HasLoopTransferInRange(position, chainEnd))
                    return true;
                if (token.Kind == TokenKind.Keyword && token.Lexeme is ("break" or "continue") &&
                    FindInterfaceFlowStatementEnd(position, blockEnd) > position)
                    return false;
                if (token.Lexeme is ("while" or "foreach") && TokenAt(position + 1).Kind == TokenKind.LParen)
                {
                    var headerEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
                    var bodyEnd = headerEnd > 0 && TokenAt(headerEnd).Kind == TokenKind.LBrace
                        ? FindInterfaceFlowGroupEnd(headerEnd, TokenKind.LBrace, TokenKind.RBrace)
                        : -1;
                    if (bodyEnd > 0)
                    {
                        position = bodyEnd - 1;
                        continue;
                    }
                }
                if (token.Kind == TokenKind.LBrace) nestedBlockDepth++;
                else if (token.Kind == TokenKind.RBrace) nestedBlockDepth--;
            }
            return true;
        }

        private bool HasLoopTransferInRange(int start, int end)
        {
            var openBlocks = new Stack<int>();
            for (var position = start; position < end; position++)
            {
                var token = TokenAt(position);
                if (token.Kind == TokenKind.Keyword && token.Lexeme is ("break" or "continue") &&
                    FindInterfaceFlowStatementEnd(position, end) > position)
                    return true;
                if (token.Kind == TokenKind.Keyword && token.Lexeme is ("return" or "throw") &&
                    openBlocks.Count > 0)
                {
                    // Ignore loop transfers that follow an unconditional exit within the same arm.
                    var statementEnd = FindInterfaceFlowStatementEnd(position, end);
                    var blockEnd = FindInterfaceFlowGroupEnd(openBlocks.Pop(), TokenKind.LBrace, TokenKind.RBrace);
                    if (statementEnd > position && blockEnd > statementEnd)
                        position = blockEnd - 1;
                    continue;
                }
                if (token.Lexeme == "if" && TokenAt(position + 1).Kind == TokenKind.LParen)
                {
                    var conditionEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
                    var thenEnd = conditionEnd > 0 && TokenAt(conditionEnd).Kind == TokenKind.LBrace
                        ? FindInterfaceFlowGroupEnd(conditionEnd, TokenKind.LBrace, TokenKind.RBrace)
                        : -1;
                    if (thenEnd > 0 && TryEvaluateBooleanConstant(position + 2, conditionEnd - 1,
                        out var conditionValue))
                    {
                        if (!conditionValue)
                        {
                            // The then arm is unreachable; leave the else arm for the next scan steps.
                            position = thenEnd - 1;
                            continue;
                        }
                        if (HasLoopTransferInRange(conditionEnd, thenEnd))
                            return true;
                        if (TryFindTopLevelLoopTerminator(conditionEnd, thenEnd, out var terminalPosition) &&
                            TokenAt(terminalPosition).Lexeme is ("return" or "throw") && openBlocks.Count > 0)
                        {
                            // A terminal, selected arm makes the rest of its containing block unreachable.
                            var parentEnd = FindInterfaceFlowGroupEnd(openBlocks.Peek(), TokenKind.LBrace, TokenKind.RBrace);
                            if (parentEnd > position)
                            {
                                position = parentEnd - 1;
                                continue;
                            }
                        }
                        TryFindConditionalChainTerminator(position, out var chainEnd, out _);
                        if (chainEnd > position)
                        {
                            position = chainEnd - 1;
                            continue;
                        }
                    }
                }
                if (token.Lexeme is ("while" or "foreach") && TokenAt(position + 1).Kind == TokenKind.LParen)
                {
                    var headerEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
                    var bodyEnd = headerEnd > 0 && TokenAt(headerEnd).Kind == TokenKind.LBrace
                        ? FindInterfaceFlowGroupEnd(headerEnd, TokenKind.LBrace, TokenKind.RBrace)
                        : -1;
                    if (bodyEnd > 0)
                    {
                        position = bodyEnd - 1;
                        continue;
                    }
                }
                if (token.Kind == TokenKind.LBrace)
                    openBlocks.Push(position);
                else if (token.Kind == TokenKind.RBrace && openBlocks.Count > 0)
                    openBlocks.Pop();
            }
            return false;
        }

        private int FindInterfaceFlowStatementEnd(int start, int end)
        {
            for (var position = start + 1; position < end; position++)
            {
                var token = TokenAt(position);
                if (token.Kind == TokenKind.Semicolon)
                    return position;
                if (token.Kind == TokenKind.LParen)
                {
                    var groupEnd = FindInterfaceFlowGroupEnd(position, TokenKind.LParen, TokenKind.RParen);
                    if (groupEnd > position)
                        position = groupEnd - 1;
                }
                else if (token.Kind == TokenKind.LBracket)
                {
                    var groupEnd = FindInterfaceFlowGroupEnd(position, TokenKind.LBracket, TokenKind.RBracket);
                    if (groupEnd > position)
                        position = groupEnd - 1;
                }
            }
            return -1;
        }

        private bool TryFindConditionalChainTerminator(int ifPosition, out int chainEnd, out int terminatorPosition)
        {
            chainEnd = ifPosition;
            terminatorPosition = -1;
            if (TokenAt(ifPosition + 1).Kind != TokenKind.LParen) return false;
            var conditionEnd = FindInterfaceFlowGroupEnd(ifPosition + 1, TokenKind.LParen, TokenKind.RParen);
            if (conditionEnd <= 0 || TokenAt(conditionEnd).Kind != TokenKind.LBrace) return false;
            var thenEnd = FindInterfaceFlowGroupEnd(conditionEnd, TokenKind.LBrace, TokenKind.RBrace);
            if (thenEnd <= 0) return false;

            var hasConstantCondition = TryEvaluateBooleanConstant(ifPosition + 2, conditionEnd - 1,
                out var conditionValue);
            var thenTerminates = TryFindTopLevelLoopTerminator(conditionEnd, thenEnd, out var thenTerminator);
            var elsePosition = thenEnd;
            var hasElse = TokenAt(elsePosition).Lexeme == "else";
            var elseTerminates = false;
            var elseTerminator = -1;
            chainEnd = thenEnd;
            if (hasElse && TokenAt(elsePosition + 1).Lexeme == "if")
            {
                elseTerminates = TryFindConditionalChainTerminator(elsePosition + 1, out chainEnd,
                    out elseTerminator);
            }
            else if (hasElse && TokenAt(elsePosition + 1).Kind == TokenKind.LBrace)
            {
                chainEnd = FindInterfaceFlowGroupEnd(elsePosition + 1, TokenKind.LBrace, TokenKind.RBrace);
                elseTerminates = chainEnd > 0 &&
                    TryFindTopLevelLoopTerminator(elsePosition + 1, chainEnd, out elseTerminator);
            }

            if (hasConstantCondition)
            {
                if (conditionValue && thenTerminates)
                {
                    terminatorPosition = thenTerminator;
                    return true;
                }
                if (!conditionValue && hasElse && elseTerminates)
                {
                    terminatorPosition = elseTerminator;
                    return true;
                }
                return false;
            }
            if (thenTerminates && hasElse && elseTerminates)
            {
                // Every arm terminates, so the next statement after the complete chain is unreachable.
                terminatorPosition = chainEnd - 1;
                return true;
            }
            return false;
        }

        private int FindEnclosingBlockEnd(int start, int end)
        {
            var nestedDepth = 0;
            for (var position = start; position < end; position++)
            {
                if (TokenAt(position).Kind == TokenKind.LBrace)
                    nestedDepth++;
                else if (TokenAt(position).Kind == TokenKind.RBrace)
                {
                    if (nestedDepth == 0)
                        return position;
                    nestedDepth--;
                }
            }
            return -1;
        }

        private string? GetInterfaceConsumedLocalName(int position)
        {
            var operand = position + 1;
            var wrapping = 0;
            while (TokenAt(operand).Kind == TokenKind.LParen) { operand++; wrapping++; }
            if (TokenAt(operand).Kind != TokenKind.Identifier) return null;
            var name = TokenAt(operand++).Lexeme;
            while (wrapping > 0 && TokenAt(operand).Kind == TokenKind.RParen) { operand++; wrapping--; }
            if (wrapping != 0 || TokenAt(operand).Kind is TokenKind.Dot or TokenKind.LBracket or TokenKind.LParen)
                return null;
            return name;
        }

        private void AddForeachFlowBindings(int position, Dictionary<int, HashSet<string>> bindings)
        {
            var headerEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
            if (headerEnd < 0 || TokenAt(position + 2).Kind != TokenKind.Identifier) return;
            var names = new HashSet<string>(StringComparer.Ordinal) { TokenAt(position + 2).Lexeme };
            if (TokenAt(position + 3).Kind == TokenKind.Comma && TokenAt(position + 4).Kind == TokenKind.Identifier)
                names.Add(TokenAt(position + 4).Lexeme);
            bindings[headerEnd] = names;
        }

        private int FindInterfaceFlowGroupEnd(int position, TokenKind open, TokenKind close)
        {
            if (TokenAt(position).Kind != open) return -1;
            var depth = 0;
            for (; TokenAt(position).Kind != TokenKind.EndOfFile; position++)
            {
                if (TokenAt(position).Kind == open) depth++;
                if (TokenAt(position).Kind == close && --depth == 0) return position + 1;
            }
            return -1;
        }

        private void CollectInterfaceConditionFacts(int start, int end, bool whenTrue, Dictionary<string, string> candidates,
            HashSet<string> ambiguous, Dictionary<string, HashSet<string>>? ancestorCandidates = null)
        {
            while (end - start >= 2 && TokenAt(start).Kind == TokenKind.LParen)
            {
                var depth = 0;
                var close = start;
                for (; close < end; close++)
                {
                    if (TokenAt(close).Kind == TokenKind.LParen) depth++;
                    if (TokenAt(close).Kind == TokenKind.RParen && --depth == 0) break;
                }
                if (close != end - 1) break;
                start++; end--;
            }

            var conjunctions = new List<int>();
            var disjunctions = new List<int>();
            var nesting = 0;
            for (var position = start; position < end; position++)
            {
                var token = TokenAt(position);
                if (token.Kind is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace) nesting++;
                else if (token.Kind is TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace) nesting--;
                else if (nesting == 0 && token.Kind == TokenKind.Keyword)
                {
                    if (token.Lexeme == "or") disjunctions.Add(position);
                    if (token.Lexeme == "and") conjunctions.Add(position);
                }
            }
            var splits = disjunctions.Count != 0 ? disjunctions : conjunctions;
            var alternatives = disjunctions.Count != 0 ? whenTrue : !whenTrue;
            if (splits.Count != 0 && alternatives)
            {
                Dictionary<string, HashSet<string>>? common = null;
                foreach (var boundary in splits.Append(end))
                {
                    // A constant operand that cannot produce the selected truth value
                    // contributes no alternative path to the interface-fact intersection.
                    if (TryEvaluateBooleanConstant(start, boundary, out var constantValue) &&
                        constantValue != whenTrue)
                    {
                        start = boundary + 1;
                        continue;
                    }
                    var branch = new Dictionary<string, string>(StringComparer.Ordinal);
                    var branchAncestors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                    CollectInterfaceConditionFacts(start, boundary, whenTrue, branch,
                        new HashSet<string>(StringComparer.Ordinal), branchAncestors);
                    start = boundary + 1;
                    if (common == null)
                    {
                        common = branchAncestors;
                        continue;
                    }
                    foreach (var commonName in common.Keys.ToArray())
                    {
                        if (!branchAncestors.TryGetValue(commonName, out var branchTargets))
                            common.Remove(commonName);
                        else
                            common[commonName].IntersectWith(branchTargets);
                    }
                }
                if (common != null)
                    foreach (var candidate in common)
                    {
                        if (ancestorCandidates != null)
                            ancestorCandidates[candidate.Key] = candidate.Value.ToHashSet(StringComparer.Ordinal);
                        var joinedTarget = GetUniqueInterfaceFlowAncestor(candidate.Value);
                        if (joinedTarget != null) AddPositiveInterfaceTest(candidate.Key, joinedTarget, candidates, ambiguous);
                    }
                return;
            }
            if (splits.Count != 0)
            {
                foreach (var boundary in splits.Append(end))
                {
                    var operandAncestors = ancestorCandidates == null ? null
                        : new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                    CollectInterfaceConditionFacts(start, boundary, whenTrue, candidates, ambiguous, operandAncestors);
                    if (operandAncestors != null && ancestorCandidates != null)
                        foreach (var operand in operandAncestors)
                        {
                            if (ancestorCandidates.TryGetValue(operand.Key, out var existing)) existing.UnionWith(operand.Value);
                            else ancestorCandidates[operand.Key] = operand.Value;
                        }
                    start = boundary + 1;
                }
                return;
            }
            if (TokenAt(start).Kind == TokenKind.Keyword && TokenAt(start).Lexeme == "not")
            {
                CollectInterfaceConditionFacts(start + 1, end, !whenTrue, candidates, ambiguous, ancestorCandidates);
                return;
            }
            if (!whenTrue || end - start != 3 || TokenAt(start).Kind != TokenKind.Identifier ||
                TokenAt(start + 1).Lexeme != "is" || !_interfaces.ContainsKey(TokenAt(start + 2).Lexeme)) return;
            var name = TokenAt(start).Lexeme;
            var target = TokenAt(start + 2).Lexeme;
            AddPositiveInterfaceTest(name, target, candidates, ambiguous);
            if (ancestorCandidates != null)
                ancestorCandidates[name] = _interfaces.Keys.Where(parent => IsInterfaceAssignable(target, parent))
                    .ToHashSet(StringComparer.Ordinal);
        }

        private string? GetUniqueInterfaceFlowAncestor(HashSet<string> common)
        {
            var mostSpecific = common.Where(candidate => !common.Any(other => other != candidate &&
                IsInterfaceAssignable(other, candidate))).Take(2).ToArray();
            return mostSpecific.Length == 1 ? mostSpecific[0] : null;
        }

        private void AddPositiveInterfaceTest(string name, string target, Dictionary<string, string> candidates,
            HashSet<string> ambiguous)
        {
            if (ambiguous.Contains(name)) return;
            if (!candidates.TryGetValue(name, out var existing) || IsInterfaceAssignable(target, existing))
                candidates[name] = target;
            else if (!IsInterfaceAssignable(existing, target))
            {
                candidates.Remove(name);
                ambiguous.Add(name);
            }
        }
    }
}
