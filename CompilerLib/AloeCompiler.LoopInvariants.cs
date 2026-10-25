using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private Dictionary<int, string> GetLoopBackEdgeInvariantTypes(int bodyStart, int bodyEnd)
        {
            if (bodyStart < 0 || bodyEnd <= bodyStart) return [];
            var entries = CaptureInterfaceFlowState().Where(pair => pair.Value.Type == ExprType.Object &&
                pair.Value.ClassName != null && _interfaces.ContainsKey(pair.Value.ClassName))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var firstEntry = entries.ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ClassName);
            var bindings = new Dictionary<int, HashSet<string>>();
            if (TokenAt(_position).Lexeme == "foreach") AddForeachFlowBindings(_position, bindings);
            var initialShadows = bindings.GetValueOrDefault(bodyStart) ?? new HashSet<string>(StringComparer.Ordinal);
            var head = firstEntry;
            // Each slot ranges over the finite interface names plus unknown. Joining the first entry
            // with every back edge makes transfer lose candidates; a failed bound widens safely.
            var converged = false;
            for (var iteration = 0; iteration <= _interfaces.Count + 1; iteration++)
            {
                var backEdges = new List<Dictionary<int, string?>>();
                var result = AnalyzeRange(bodyStart + 1, bodyEnd - 1,
                    new Dictionary<int, string?>(head), initialShadows, backEdges, null);
                if (result.FallsThrough) backEdges.Add(result.State);
                var incoming = new List<Dictionary<int, string?>> { firstEntry };
                incoming.AddRange(backEdges);
                var next = JoinStates(incoming);
                if (StatesEqual(head, next))
                {
                    head = next;
                    converged = true;
                    break;
                }
                head = next;
            }
            if (!converged) head = EmptyState();
            return head.Where(pair => pair.Value != null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!);

            Dictionary<int, string?> EmptyState()
                => entries.Keys.ToDictionary(slot => slot, _ => (string?)null);

            (bool FallsThrough, Dictionary<int, string?> State) AnalyzeRange(int start, int end,
                Dictionary<int, string?> state, HashSet<string> inheritedShadows,
                List<Dictionary<int, string?>> loopBackEdges, List<Dictionary<int, string?>>? loopBreakEdges)
            {
                var shadows = new Stack<HashSet<string>>();
                shadows.Push(new HashSet<string>(inheritedShadows, StringComparer.Ordinal));
                for (var position = start; position < end;)
                {
                    if (TryFindWithBlockEnd(position, out var withStart, out var withEnd))
                    {
                        if (withEnd > end) return (true, EmptyState());
                        // Targets evaluate once, left to right, before entering the lexical scope.
                        // A short circuit in one target cannot hide a later target's consumer.
                        var targetStart = position + 2;
                        for (var cursor = targetStart; cursor < withStart; cursor++)
                        {
                            var kind = TokenAt(cursor).Kind;
                            var close = kind switch
                            {
                                TokenKind.LParen => TokenKind.RParen,
                                TokenKind.LBracket => TokenKind.RBracket,
                                TokenKind.LBrace => TokenKind.RBrace,
                                _ => TokenKind.EndOfFile
                            };
                            if (close != TokenKind.EndOfFile)
                            {
                                var groupEnd = FindInterfaceFlowGroupEnd(cursor, kind, close);
                                if (groupEnd > cursor) cursor = groupEnd - 1;
                            }
                            else if (kind == TokenKind.Comma || cursor == withStart - 1)
                            {
                                foreach (var slot in RecordConsumers(targetStart, cursor, shadows.Peek())) state[slot] = null;
                                targetStart = cursor + 1;
                            }
                        }
                        var withFlow = AnalyzeRange(withStart + 1, withEnd - 1, state, shadows.Peek(), loopBackEdges, loopBreakEdges);
                        state = withFlow.State;
                        if (!withFlow.FallsThrough) return withFlow;
                        position = withEnd;
                        continue;
                    }
                    if (TokenAt(position).Lexeme == "if")
                    {
                        var conditional = AnalyzeConditional(position, end, state, shadows.Peek(), loopBackEdges, loopBreakEdges);
                        if (conditional.End <= position) return (true, EmptyState());
                        state = conditional.State;
                        if (!conditional.FallsThrough) return (false, state);
                        position = conditional.End;
                        continue;
                    }
                    if (TokenAt(position).Lexeme == "try" && TokenAt(position + 1).Kind == TokenKind.LBrace)
                    {
                        var tryEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LBrace, TokenKind.RBrace);
                        var catchBodies = new List<(int Open, int Close)>();
                        var lastCatchClose = -1;
                        var hasCatches = tryEnd > 0 && TryFindCatchBodies(tryEnd, out catchBodies, out lastCatchClose);
                        if (tryEnd <= position || !hasCatches || lastCatchClose <= tryEnd || lastCatchClose > end)
                            return (true, EmptyState());

                        // An initializer can throw before its assignment completes. Keep the
                        // pre-try state separate from the normally executed try transfer.
                        var tryFlow = AnalyzeRange(position + 2, tryEnd - 1,
                            new Dictionary<int, string?>(state), shadows.Peek(), loopBackEdges, loopBreakEdges);
                        if (CanProveTryRangeHasNoExceptionalEdges(position + 2, tryEnd - 1))
                        {
                            if (!tryFlow.FallsThrough) return tryFlow;
                            state = tryFlow.State;
                            position = lastCatchClose;
                            continue;
                        }
                        var continuingStates = new List<Dictionary<int, string?>>();
                        if (tryFlow.FallsThrough) continuingStates.Add(tryFlow.State);
                        var catchEntry = new Dictionary<int, string?>(state);
                        foreach (var (slot, assignments) in GetTrackedLocalWriteTypes(
                            position + 2, tryEnd - 1, shadows.Peek(), state))
                        {
                            var incomingType = catchEntry.GetValueOrDefault(slot);
                            catchEntry[slot] = JoinTypeCandidates(assignments.Prepend(incomingType));
                        }
                        foreach (var (catchOpen, catchClose) in catchBodies)
                        {
                            var catchFlow = AnalyzeRange(catchOpen + 1, catchClose - 1,
                                new Dictionary<int, string?>(catchEntry), shadows.Peek(), loopBackEdges, loopBreakEdges);
                            if (catchFlow.FallsThrough) continuingStates.Add(catchFlow.State);
                        }
                        if (continuingStates.Count == 0) return (false, state);
                        state = JoinStates(continuingStates);
                        position = lastCatchClose;
                        continue;
                    }
                    if (TokenAt(position).Kind == TokenKind.LBrace)
                    {
                        var blockEnd = FindInterfaceFlowGroupEnd(position, TokenKind.LBrace, TokenKind.RBrace);
                        if (blockEnd <= position || blockEnd > end) return (true, EmptyState());
                        var block = AnalyzeRange(position + 1, blockEnd - 1, state, shadows.Peek(), loopBackEdges, loopBreakEdges);
                        state = block.State;
                        if (!block.FallsThrough) return block;
                        position = blockEnd;
                        continue;
                    }
                    if (TokenAt(position).Lexeme is "while" or "foreach" &&
                        TokenAt(position + 1).Kind == TokenKind.LParen)
                    {
                        var loopHeaderEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
                        var loopBodyEnd = loopHeaderEnd > 0 && TokenAt(loopHeaderEnd).Kind == TokenKind.LBrace
                            ? FindInterfaceFlowGroupEnd(loopHeaderEnd, TokenKind.LBrace, TokenKind.RBrace)
                            : -1;
                        if (loopBodyEnd <= position || loopBodyEnd > end)
                            return (true, EmptyState());
                        var isWhile = TokenAt(position).Lexeme == "while";
                        if (isWhile && IsNonfallthroughWhile(position, loopHeaderEnd, loopBodyEnd))
                            return (false, state);

                        var zeroIterationState = new Dictionary<int, string?>(state);
                        var conditionWrites = RecordConsumers(position + 2, loopHeaderEnd - 1, shadows.Peek());
                        foreach (var slot in conditionWrites)
                            zeroIterationState[slot] = null;
                        var conditionOutcomes = isWhile
                            ? EvaluateGuaranteedZeroDivisionConditionExpression(position + 2, loopHeaderEnd - 1)
                            : ConditionEvaluation.Any;
                        if (isWhile && (conditionOutcomes & ConditionEvaluation.True) == 0)
                        {
                            if (conditionOutcomes == ConditionEvaluation.Throws) return (false, state);
                            state = ApplyConditionProofs(position + 2, loopHeaderEnd - 1,
                                zeroIterationState, whenTrue: false, shadows.Peek(), conditionWrites);
                            position = loopBodyEnd;
                            continue;
                        }
                        var conditionValue = false;
                        var hasConstantCondition = isWhile && TryEvaluateBooleanConstant(position + 2,
                            loopHeaderEnd - 1, out conditionValue);
                        var firstLoopEntry = new Dictionary<int, string?>(zeroIterationState);
                        if (isWhile)
                        {
                            zeroIterationState = ApplyConditionProofs(position + 2, loopHeaderEnd - 1,
                                zeroIterationState, whenTrue: false, shadows.Peek(), conditionWrites);
                            firstLoopEntry = ApplyConditionProofs(position + 2, loopHeaderEnd - 1,
                                firstLoopEntry, whenTrue: true, shadows.Peek(), conditionWrites);
                        }
                        var loopShadows = new HashSet<string>(shadows.Peek(), StringComparer.Ordinal);
                        if (!isWhile)
                        {
                            var nestedBindings = new Dictionary<int, HashSet<string>>();
                            AddForeachFlowBindings(position, nestedBindings);
                            if (nestedBindings.TryGetValue(loopHeaderEnd, out var names)) loopShadows.UnionWith(names);
                        }
                        var nestedBackEdges = new List<Dictionary<int, string?>>();
                        var nestedBreakEdges = new List<Dictionary<int, string?>>();
                        var nestedBodyEntry = firstLoopEntry;
                        var fixedPoint = false;
                        for (var iteration = 0; iteration <= _interfaces.Count + 1 &&
                            !(isWhile && hasConstantCondition && !conditionValue); iteration++)
                        {
                            nestedBackEdges = new List<Dictionary<int, string?>>();
                            nestedBreakEdges = new List<Dictionary<int, string?>>();
                            var nestedFlow = AnalyzeRange(loopHeaderEnd + 1, loopBodyEnd - 1,
                                new Dictionary<int, string?>(nestedBodyEntry),
                                loopShadows, nestedBackEdges, nestedBreakEdges);
                            if (nestedFlow.FallsThrough) nestedBackEdges.Add(nestedFlow.State);
                            var nextEntries = new List<Dictionary<int, string?>> { firstLoopEntry };
                            foreach (var edge in nestedBackEdges)
                            {
                                var reevaluated = new Dictionary<int, string?>(edge);
                                foreach (var slot in conditionWrites) reevaluated[slot] = null;
                                nextEntries.Add(isWhile
                                    ? ApplyConditionProofs(position + 2, loopHeaderEnd - 1, reevaluated,
                                        whenTrue: true, shadows.Peek(), conditionWrites)
                                    : reevaluated);
                            }
                            var nextEntry = JoinStates(nextEntries);
                            if (StatesEqual(nestedBodyEntry, nextEntry))
                            {
                                nestedBodyEntry = nextEntry;
                                fixedPoint = true;
                                break;
                            }
                            nestedBodyEntry = nextEntry;
                        }
                        if (!(isWhile && hasConstantCondition && !conditionValue) && !fixedPoint)
                        {
                            nestedBodyEntry = EmptyState();
                            nestedBackEdges = new List<Dictionary<int, string?>>();
                            nestedBreakEdges = new List<Dictionary<int, string?>>();
                            var nestedFlow = AnalyzeRange(loopHeaderEnd + 1, loopBodyEnd - 1,
                                new Dictionary<int, string?>(nestedBodyEntry),
                                loopShadows, nestedBackEdges, nestedBreakEdges);
                            if (nestedFlow.FallsThrough) nestedBackEdges.Add(nestedFlow.State);
                        }

                        var hasZeroIterationExit = !isWhile || !hasConstantCondition || !conditionValue;
                        var exits = new List<Dictionary<int, string?>>();
                        if (hasZeroIterationExit) exits.Add(zeroIterationState);
                        if (isWhile && !(hasConstantCondition && conditionValue))
                            foreach (var edge in nestedBackEdges)
                            {
                                var falseExit = new Dictionary<int, string?>(edge);
                                foreach (var slot in conditionWrites) falseExit[slot] = null;
                                exits.Add(ApplyConditionProofs(position + 2, loopHeaderEnd - 1, falseExit,
                                    whenTrue: false, shadows.Peek(), conditionWrites));
                            }
                        else if (!isWhile)
                            exits.AddRange(nestedBackEdges);
                        exits.AddRange(nestedBreakEdges);
                        if (exits.Count == 0) return (false, state);

                        state = JoinStates(exits);
                        position = loopBodyEnd;
                        continue;
                    }
                    var statementEnd = FindInterfaceFlowStatementEnd(position, end);
                    if (statementEnd < 0) return (true, EmptyState());
                    if (TokenAt(position).Lexeme == "continue")
                    {
                        loopBackEdges.Add(new Dictionary<int, string?>(state));
                        return (false, state);
                    }
                    if (TokenAt(position).Lexeme == "break")
                    {
                        loopBreakEdges?.Add(new Dictionary<int, string?>(state));
                        return (false, state);
                    }
                    if (TokenAt(position).Lexeme is "return" or "throw") return (false, state);
                    if (IsGuaranteedZeroDivisionStatement(position, end, out _) ||
                        IsGuaranteedZeroDivisionConditionStatement(position, end, out _) ||
                        IsGuaranteedInvalidReferenceCastStatement(position, end, out _)) return (false, state);

                    foreach (var slot in RecordConsumers(position, statementEnd, shadows.Peek())) state[slot] = null;
                    if (TokenAt(position).Kind == TokenKind.Identifier && TokenAt(position + 1).Kind == TokenKind.Assign &&
                        !shadows.Peek().Contains(TokenAt(position).Lexeme) &&
                        TryResolveLocal(TokenAt(position).Lexeme, out var target) && entries.ContainsKey(target.Slot))
                        state[target.Slot] = GetNominalLoopAssignmentType(TokenAt(position).Lexeme,
                            position + 2, statementEnd, shadows, state[target.Slot]);

                    if (TokenAt(position).Kind == TokenKind.Keyword && TokenAt(position).Lexeme is "let" or "var")
                        shadows.Peek().Add(TokenAt(position + 1).Lexeme);
                    position = statementEnd + 1;
                }
                return (true, state);
            }

            // Joins expose declared nominal types only; incomparable parents have no public flow type.
            Dictionary<int, string?> JoinStates(IReadOnlyList<Dictionary<int, string?>> states)
            {
                var joined = EmptyState();
                foreach (var slot in entries.Keys)
                    joined[slot] = JoinTypeCandidates(states.Select(path => path.GetValueOrDefault(slot)));
                return joined;
            }

            string? JoinTypeCandidates(IEnumerable<string?> candidates)
            {
                var types = candidates.ToArray();
                if (types.Length == 0 || types.Any(type => type == null)) return null;
                if (types.All(type => type == types[0])) return types[0];
                var common = _interfaces.Keys.Where(candidate =>
                    types.All(type => IsClassAssignable(type, candidate))).ToHashSet(StringComparer.Ordinal);
                return GetUniqueInterfaceFlowAncestor(common);
            }

            bool StatesEqual(Dictionary<int, string?> left, Dictionary<int, string?> right)
                => entries.Keys.All(slot => left.GetValueOrDefault(slot) == right.GetValueOrDefault(slot));

            Dictionary<int, string?> ApplyConditionProofs(int start, int end, Dictionary<int, string?> branch,
                bool whenTrue, HashSet<string> shadows, HashSet<int> conditionWrites)
            {
                var facts = new Dictionary<string, string>(StringComparer.Ordinal);
                var ambiguous = new HashSet<string>(StringComparer.Ordinal);
                CollectInterfaceConditionFacts(start, end, whenTrue, facts, ambiguous);
                foreach (var (name, fact) in facts)
                    if (!ambiguous.Contains(name) && !shadows.Contains(name) && TryResolveLocal(name, out var local) &&
                        !conditionWrites.Contains(local.Slot) && entries.ContainsKey(local.Slot) &&
                        _declaredLocals.TryGetValue(local.Slot, out var declared) &&
                        (declared.ClassName == null || IsClassAssignable(fact, declared.ClassName)))
                    {
                        var current = branch[local.Slot];
                        if (current == null || IsClassAssignable(fact, current) ||
                            !IsClassAssignable(current, fact)) branch[local.Slot] = fact;
                    }
                return branch;
            }

            (int End, bool FallsThrough, Dictionary<int, string?> State) AnalyzeConditional(int position, int limit,
                Dictionary<int, string?> state, HashSet<string> shadows,
                List<Dictionary<int, string?>> loopBackEdges, List<Dictionary<int, string?>>? loopBreakEdges)
            {
                var headerEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
                var thenEnd = headerEnd > 0 ? FindInterfaceFlowGroupEnd(headerEnd, TokenKind.LBrace, TokenKind.RBrace) : -1;
                TryFindConditionalChainTerminator(position, out var chainEnd, out _);
                if (thenEnd <= 0 || chainEnd <= position || chainEnd > limit) return (-1, true, state);
                var conditionWrites = RecordConsumers(position + 2, headerEnd - 1, shadows);
                foreach (var slot in conditionWrites) state[slot] = null;
                var conditionOutcomes = EvaluateGuaranteedZeroDivisionConditionExpression(position + 2, headerEnd - 1);
                var conditionCanBeTrue = (conditionOutcomes & ConditionEvaluation.True) != 0;
                var conditionCanBeFalse = (conditionOutcomes & ConditionEvaluation.False) != 0;
                var thenState = ApplyConditionProofs(position + 2, headerEnd - 1,
                    new Dictionary<int, string?>(state), whenTrue: true, shadows, conditionWrites);
                var elseState = ApplyConditionProofs(position + 2, headerEnd - 1,
                    new Dictionary<int, string?>(state), whenTrue: false, shadows, conditionWrites);
                var thenFlow = !conditionCanBeTrue
                    ? (FallsThrough: false, State: state)
                    : AnalyzeRange(headerEnd + 1, thenEnd - 1, thenState, shadows, loopBackEdges, loopBreakEdges);
                var elseFlow = (FallsThrough: conditionCanBeFalse, State: elseState);
                if (conditionCanBeFalse && TokenAt(thenEnd).Lexeme == "else")
                {
                    if (TokenAt(thenEnd + 1).Lexeme == "if")
                    {
                        var nested = AnalyzeConditional(thenEnd + 1, limit, elseState, shadows, loopBackEdges, loopBreakEdges);
                        if (nested.End < 0) return (-1, true, state);
                        elseFlow = (nested.FallsThrough, nested.State);
                    }
                    else
                        elseFlow = AnalyzeRange(thenEnd + 2, chainEnd - 1, elseState, shadows, loopBackEdges, loopBreakEdges);
                }
                if (!thenFlow.FallsThrough) return (chainEnd, elseFlow.FallsThrough, elseFlow.State);
                if (!elseFlow.FallsThrough) return (chainEnd, true, thenFlow.State);
                return (chainEnd, true, JoinStates(new[] { thenFlow.State, elseFlow.State }));

            }

            HashSet<int> RecordConsumers(int start, int end, HashSet<string> shadows)
            {
                var consumed = new HashSet<int>();
                var expressionStart = start;
                for (var cursor = start; cursor < end; cursor++)
                    if (TokenAt(cursor).Kind == TokenKind.Assign)
                    {
                        expressionStart = cursor + 1;
                        break;
                    }
                var unreachable = new List<(int Start, int End)>();
                TryEvaluateBooleanConstant(expressionStart, end, out _, unreachable);
                for (var cursor = start; cursor < end; cursor++)
                {
                    if (TokenAt(cursor).Kind is not (TokenKind.Keyword or TokenKind.Identifier) ||
                        TokenAt(cursor).Lexeme is not ("delete" or "unbox") ||
                        unreachable.Any(range => cursor >= range.Start && cursor < range.End)) continue;
                    var sourceName = GetInterfaceConsumedLocalName(cursor);
                    if (sourceName != null && !shadows.Contains(sourceName) &&
                        TryResolveLocal(sourceName, out var source) && entries.ContainsKey(source.Slot))
                        consumed.Add(source.Slot);
                }
                return consumed;
            }

            Dictionary<int, List<string?>> GetTrackedLocalWriteTypes(int start, int end,
                HashSet<string> shadows, IReadOnlyDictionary<int, string?> entryState)
            {
                var writes = new Dictionary<int, List<string?>>();
                var assignmentShadows = new Stack<HashSet<string>>();
                assignmentShadows.Push(new HashSet<string>(shadows, StringComparer.Ordinal));
                var foreachBindings = new Dictionary<int, HashSet<string>>();
                HashSet<string>? declarationScope = null;
                string? declarationName = null;
                var declarationPosition = -1;
                var nestedHandlers = GetNestedHandlerRangesInInterfaceFlow(start, end);
                void RecordWrite(int slot, string? assignedType)
                {
                    if (!writes.TryGetValue(slot, out var values)) writes[slot] = values = [];
                    values.Add(assignedType);
                }
                var blockDepth = 0;
                var guaranteedBlockStarts = new HashSet<int>();
                var guaranteedBlockDepths = new Stack<int>();
                var conditionalBlockStarts = new HashSet<int>();
                var conditionalBlockDepths = new Stack<int>();
                var optionalRanges = new List<(int Start, int End)>();
                var unreachableOperands = new List<(int Start, int End)>();
                var unreachableBranchEnds = GetUnreachableCatchEndsInInterfaceFlow(start, end);
                for (var cursor = start; cursor < end; cursor++)
                {
                    if (unreachableBranchEnds.TryGetValue(cursor, out var afterElse))
                    {
                        cursor = afterElse - 1;
                        continue;
                    }
                    if (unreachableOperands.Any(range => cursor >= range.Start && cursor < range.End)) continue;
                    var token = TokenAt(cursor);
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
                    // The exception shortcut is valid only on a mandatory path; unknown conditional
                    // bodies must keep their writes because execution can skip the division.
                    // A nested handler can consume a guaranteed exception and resume normal flow.
                    // Keep scanning inside nested try/catch ranges conservatively, while
                    // exceptions outside those ranges still terminate the enclosing try.
                    if (!nestedHandlers.Any(range => cursor >= range.Start && cursor < range.End) &&
                        !optionalRanges.Any(range => cursor >= range.Start && cursor < range.End) &&
                        (blockDepth == 0 || guaranteedBlockDepths.TryPeek(out var guaranteedDepth) &&
                            blockDepth == guaranteedDepth && conditionalBlockDepths.Count == 0) &&
                        (IsGuaranteedZeroDivisionStatement(cursor, end, out _) ||
                         IsGuaranteedZeroDivisionConditionStatement(cursor, end, out _) ||
                         IsGuaranteedInvalidReferenceCastStatement(cursor, end, out _)))
                        break;
                    if (TokenAt(cursor).Kind == TokenKind.LBrace)
                    {
                        var scope = new HashSet<string>(StringComparer.Ordinal);
                        if (foreachBindings.TryGetValue(cursor, out var names)) scope.UnionWith(names);
                        assignmentShadows.Push(scope);
                        blockDepth++;
                        if (guaranteedBlockStarts.Remove(cursor)) guaranteedBlockDepths.Push(blockDepth);
                        if (conditionalBlockStarts.Remove(cursor)) conditionalBlockDepths.Push(blockDepth);
                        continue;
                    }
                    if (TokenAt(cursor).Kind == TokenKind.RBrace)
                    {
                        if (assignmentShadows.Count > 1) assignmentShadows.Pop();
                        if (guaranteedBlockDepths.TryPeek(out var closingDepth) && closingDepth == blockDepth)
                            guaranteedBlockDepths.Pop();
                        if (conditionalBlockDepths.TryPeek(out var conditionalDepth) && conditionalDepth == blockDepth)
                            conditionalBlockDepths.Pop();
                        blockDepth--;
                        continue;
                    }

                    if (token.Kind == TokenKind.Keyword && token.Lexeme is "let" or "var")
                    {
                        declarationScope = assignmentShadows.Peek();
                        declarationName = TokenAt(cursor + 1).Lexeme;
                        declarationPosition = cursor + 1;
                    }
                    if (token.Kind == TokenKind.Semicolon && declarationScope != null)
                    {
                        declarationScope.Add(declarationName!);
                        declarationScope = null;
                    }

                    if (token.Lexeme is "unbox" or "delete")
                    {
                        var consumedName = GetInterfaceConsumedLocalName(cursor);
                        if (consumedName != null && !assignmentShadows.Any(scope => scope.Contains(consumedName)) &&
                            TryResolveLocal(consumedName, out var consumed) && entries.ContainsKey(consumed.Slot))
                            RecordWrite(consumed.Slot, null);
                    }
                    if (TokenAt(cursor).Kind == TokenKind.Identifier && TokenAt(cursor + 1).Kind == TokenKind.Assign &&
                        cursor != declarationPosition && TokenAt(cursor - 1).Kind != TokenKind.Dot &&
                        !assignmentShadows.Any(scope => scope.Contains(TokenAt(cursor).Lexeme)) &&
                        TryResolveLocal(TokenAt(cursor).Lexeme, out var assigned) && entries.ContainsKey(assigned.Slot))
                    {
                        var statementEnd = FindInterfaceFlowStatementEnd(cursor + 1, end);
                        var assignedType = statementEnd > cursor + 1
                            ? GetNominalLoopAssignmentType(TokenAt(cursor).Lexeme, cursor + 2,
                                statementEnd, assignmentShadows, entryState.GetValueOrDefault(assigned.Slot))
                            : null;
                        RecordWrite(assigned.Slot, assignedType);
                    }
                }
                return writes;
            }
        }

        private bool IsGuaranteedZeroDivisionStatement(int position, int end, out int statementEnd)
        {
            statementEnd = FindInterfaceFlowStatementEnd(position, end);
            if (statementEnd <= position) return false;

            int assignment;
            if (TokenAt(position).Lexeme is "let" or "var")
            {
                assignment = position + 1;
                while (assignment < statementEnd && TokenAt(assignment).Kind != TokenKind.Assign)
                    assignment++;
                if (assignment >= statementEnd) return false;
            }
            else if (TokenAt(position).Kind == TokenKind.Identifier &&
                     TokenAt(position + 1).Kind == TokenKind.Assign)
            {
                assignment = position + 1;
            }
            else
            {
                return false;
            }

            return HasGuaranteedZeroDivision(assignment + 1, statementEnd);
        }

        private bool IsGuaranteedZeroDivisionConditionStatement(int position, int end, out int statementEnd)
        {
            statementEnd = FindInterfaceFlowStatementEnd(position, end);
            if (statementEnd <= position || TokenAt(position).Lexeme is not ("if" or "while") ||
                TokenAt(position + 1).Kind != TokenKind.LParen)
                return false;

            var conditionEnd = FindInterfaceFlowGroupEnd(position + 1, TokenKind.LParen, TokenKind.RParen);
            if (conditionEnd <= position || TokenAt(conditionEnd).Kind != TokenKind.LBrace)
                return false;

            var guaranteed = HasGuaranteedZeroDivisionConditionExpression(position + 2, conditionEnd - 1);
            return guaranteed;
        }

        private bool HasGuaranteedZeroDivisionConditionExpression(int start, int finish)
        {
            return EvaluateGuaranteedZeroDivisionConditionExpression(start, finish) == ConditionEvaluation.Throws;
        }

        private bool HasPossibleTrueConditionOutcome(int start, int finish)
        {
            return (EvaluateGuaranteedZeroDivisionConditionExpression(start, finish) & ConditionEvaluation.True) != 0;
        }

        [Flags]
        private enum ConditionEvaluation
        {
            None = 0,
            False = 1,
            True = 2,
            Throws = 4,
            Unknown = False | True,
            Any = Unknown | Throws
        }

        private ConditionEvaluation EvaluateGuaranteedZeroDivisionConditionExpression(int start, int finish)
        {
            while (finish - start >= 2 && TokenAt(start).Kind == TokenKind.LParen &&
                   TokenAt(finish - 1).Kind == TokenKind.RParen &&
                   FindInterfaceFlowGroupEnd(start, TokenKind.LParen, TokenKind.RParen) == finish)
            {
                start++;
                finish--;
            }

            var comparison = -1;
            var firstAnd = -1;
            var firstOr = -1;
            var hasMultipleComparisons = false;
            var depth = 0;
            for (var cursor = start; cursor < finish; cursor++)
            {
                var kind = TokenAt(cursor).Kind;
                if (kind == TokenKind.LParen) depth++;
                else if (kind == TokenKind.RParen) depth--;
                else if (depth == 0 && TokenAt(cursor).Lexeme == "and")
                {
                    if (firstAnd < 0) firstAnd = cursor;
                }
                else if (depth == 0 && TokenAt(cursor).Lexeme == "or")
                {
                    if (firstOr < 0) firstOr = cursor;
                }
                else if (depth == 0 && kind is TokenKind.EqualEqual or TokenKind.BangEqual or
                         TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual)
                {
                    if (comparison >= 0) hasMultipleComparisons = true;
                    comparison = cursor;
                }
            }

            // `or` has lower precedence than `and`; splitting at the first `or` preserves
            // the parser's grouping and lets recursive evaluation retain short-circuit order.
            var logical = firstOr >= 0 ? firstOr : firstAnd;
            if (logical >= 0)
            {
                var left = EvaluateGuaranteedZeroDivisionConditionExpression(start, logical);
                var isAnd = TokenAt(logical).Lexeme == "and";
                var right = EvaluateGuaranteedZeroDivisionConditionExpression(logical + 1, finish);
                var outcomes = left & ConditionEvaluation.Throws;
                if (isAnd)
                {
                    if ((left & ConditionEvaluation.False) != 0) outcomes |= ConditionEvaluation.False;
                    if ((left & ConditionEvaluation.True) != 0) outcomes |= right;
                }
                else
                {
                    if ((left & ConditionEvaluation.True) != 0) outcomes |= ConditionEvaluation.True;
                    if ((left & ConditionEvaluation.False) != 0) outcomes |= right;
                }
                return outcomes;
            }

            // `not` is parsed as a unary operator and binds more tightly than `and`/`or`.
            // Apply it only after top-level logical operators have been split.
            if (TokenAt(start).Lexeme == "not")
            {
                var operand = EvaluateGuaranteedZeroDivisionConditionExpression(start + 1, finish);
                var negated = operand & ConditionEvaluation.Throws;
                if ((operand & ConditionEvaluation.False) != 0) negated |= ConditionEvaluation.True;
                if ((operand & ConditionEvaluation.True) != 0) negated |= ConditionEvaluation.False;
                return negated;
            }

            if (!hasMultipleComparisons && comparison > start &&
                (HasGuaranteedZeroDivision(start, comparison) ||
                 HasGuaranteedZeroDivision(comparison + 1, finish)))
                return ConditionEvaluation.Throws;

            if (TryEvaluateBooleanConstant(start, finish, out var constant))
                return constant ? ConditionEvaluation.True : ConditionEvaluation.False;

            // Unknown calls, unsafe arithmetic, and unsupported comparisons may return either
            // boolean or throw. Preserve all outcomes here; enclosing short-circuit operators
            // can still prove that every normal-return path reaches a guaranteed zero division.
            return ConditionEvaluation.Any;
        }

        private bool IsGuaranteedInvalidReferenceCastStatement(int position, int end, out int statementEnd)
        {
            statementEnd = FindInterfaceFlowStatementEnd(position, end);
            if (statementEnd <= position) return false;

            int assignment;
            if (TokenAt(position).Lexeme is "let" or "var")
            {
                assignment = position + 1;
                while (assignment < statementEnd && TokenAt(assignment).Kind != TokenKind.Assign)
                    assignment++;
                if (assignment >= statementEnd) return false;
            }
            else if (TokenAt(position).Kind == TokenKind.Identifier &&
                     TokenAt(position + 1).Kind == TokenKind.Assign)
            {
                assignment = position + 1;
            }
            else
            {
                return false;
            }

            var start = assignment + 1;
            var expressionEnd = statementEnd;
            while (TokenAt(start).Kind == TokenKind.LParen &&
                   FindInterfaceFlowGroupEnd(start, TokenKind.LParen, TokenKind.RParen) == expressionEnd)
            {
                start++;
                expressionEnd--;
            }

            if (expressionEnd - start < 8 || TokenAt(expressionEnd - 1).Lexeme != "throw" ||
                TokenAt(expressionEnd - 2).Lexeme != "ifnot")
                return false;

            var targetPosition = expressionEnd - 3;
            var castPosition = expressionEnd - 4;
            if (TokenAt(castPosition).Lexeme != "as")
                return false;

            var sourcePosition = start;
            if (TokenAt(sourcePosition).Lexeme != "new" || TokenAt(sourcePosition + 1).Kind != TokenKind.Identifier ||
                TokenAt(sourcePosition + 2).Kind != TokenKind.LParen ||
                FindInterfaceFlowGroupEnd(sourcePosition + 2, TokenKind.LParen, TokenKind.RParen) != castPosition)
                return false;

            var sourceType = TokenAt(sourcePosition + 1).Lexeme;
            var targetType = TokenAt(targetPosition).Lexeme;
            var isPrimitiveTarget = targetType is "int" or "float" or "bool" or "byte" or "char" or "decimal" or "string";
            var isNominalTarget = TokenAt(targetPosition).Kind == TokenKind.Identifier &&
                (_classes.ContainsKey(targetType) || _interfaces.ContainsKey(targetType));
            if (!_classes.ContainsKey(sourceType) || (!isPrimitiveTarget && !isNominalTarget))
                return false;

            return isPrimitiveTarget || !IsClassAssignable(sourceType, targetType);
        }
        private bool HasGuaranteedZeroDivision(int start, int end)
        => GetGuaranteedInt32ArithmeticFailure(start, end) == GuaranteedInt32Failure.ZeroDivision;

    private enum GuaranteedInt32Failure : byte
    {
        None = 0,
        ZeroDivision = 1,
        Overflow = 2,
        Unknown = 3
    }

    private GuaranteedInt32Failure GetGuaranteedInt32ArithmeticFailure(int start, int end)
        {
            const byte NoFailure = 0;
            const byte ZeroDivision = 1;
            const byte OtherFailure = 2;

            var position = start;
            if (!ParseAdditive(out _, out var failure) || position != end)
                return GuaranteedInt32Failure.Unknown;
            return (GuaranteedInt32Failure)failure;

            bool ParseAdditive(out long value, out byte parseFailure)
            {
                if (!ParseMultiplicative(out value, out parseFailure))
                    return false;
                while (position < end && TokenAt(position).Kind is TokenKind.Plus or TokenKind.Minus)
                {
                    var op = TokenAt(position++).Kind;
                    if (!ParseMultiplicative(out var right, out var rightFailure))
                        return false;
                    ApplyIntegerConstantOperator(op, value, parseFailure, right, rightFailure,
                        out value, out parseFailure);
                }
                return true;
            }

            bool ParseMultiplicative(out long value, out byte parseFailure)
            {
                if (!ParseUnary(out value, out parseFailure))
                    return false;
                while (position < end && TokenAt(position).Kind is
                       TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
                {
                    var op = TokenAt(position++).Kind;
                    if (!ParseUnary(out var right, out var rightFailure))
                        return false;
                    ApplyIntegerConstantOperator(op, value, parseFailure, right, rightFailure,
                        out value, out parseFailure);
                }
                return true;
            }

            bool ParseUnary(out long value, out byte parseFailure)
            {
                if (position < end && TokenAt(position).Kind is TokenKind.Plus or TokenKind.Minus)
                {
                    var op = TokenAt(position++).Kind;
                    if (op == TokenKind.Minus && position < end &&
                        TokenAt(position).Kind == TokenKind.IntegerLiteral &&
                        TryParseIntegerLiteral(TokenAt(position).Lexeme, out var magnitude))
                    {
                        position++;
                        value = -magnitude;
                        parseFailure = value < int.MinValue || value > int.MaxValue
                            ? OtherFailure
                            : NoFailure;
                        return true;
                    }

                    if (!ParseUnary(out value, out parseFailure))
                        return false;
                    if (parseFailure != NoFailure)
                        return true;
                    if (op == TokenKind.Minus)
                        value = -value;
                    parseFailure = value < int.MinValue || value > int.MaxValue
                        ? OtherFailure
                        : NoFailure;
                    return true;
                }

                if (!ParsePrimary(out value, out parseFailure))
                    return false;
                if (parseFailure == NoFailure && (value < int.MinValue || value > int.MaxValue))
                    parseFailure = OtherFailure;
                return true;
            }

            bool ParsePrimary(out long value, out byte parseFailure)
            {
                value = 0;
                parseFailure = NoFailure;
                if (position >= end)
                    return false;

                if (TokenAt(position).Kind == TokenKind.LParen)
                {
                    position++;
                    if (!ParseAdditive(out value, out parseFailure) || position >= end ||
                        TokenAt(position).Kind != TokenKind.RParen)
                        return false;
                    position++;
                    return true;
                }

                var token = TokenAt(position);
                if (token.Kind != TokenKind.IntegerLiteral ||
                    !TryParseIntegerLiteral(token.Lexeme, out value))
                    return false;
                position++;
                return true;
            }

            void ApplyIntegerConstantOperator(TokenKind op, long left, byte leftFailure,
                long right, byte rightFailure, out long result, out byte resultFailure)
            {
                result = left;
                if (leftFailure != NoFailure)
                {
                    resultFailure = leftFailure;
                    return;
                }
                if (rightFailure != NoFailure)
                {
                    resultFailure = rightFailure;
                    return;
                }
                if (op is TokenKind.Slash or TokenKind.Percent && right == 0)
                {
                    resultFailure = ZeroDivision;
                    return;
                }

                try
                {
                    result = op switch
                    {
                        TokenKind.Plus => checked(left + right),
                        TokenKind.Minus => checked(left - right),
                        TokenKind.Star => checked(left * right),
                        TokenKind.Slash => left / right,
                        TokenKind.Percent => left % right,
                        _ => throw new InvalidOperationException("Unsupported constant integer operator.")
                    };
                }
                catch (OverflowException)
                {
                    resultFailure = OtherFailure;
                    return;
                }

                resultFailure = result < int.MinValue || result > int.MaxValue
                    ? OtherFailure
                    : NoFailure;
            }

            static bool TryParseIntegerLiteral(string text, out long value) =>
                long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }
}
