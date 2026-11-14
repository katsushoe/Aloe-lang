using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private enum FinallyTransferKind { Return, Break, Continue }
        private sealed record FinallyTransfer(FinallyTransferKind Kind, LoopContext? Loop);
        private sealed record FinallyBodyBoundary(int LoopCount, int CatchDepth);
        private sealed class FinallyGuard(int exitLevel, int catchDepth, int kindSlot, int returnSlot)
        {
            public int ExitLevel { get; } = exitLevel;
            public int CatchDepth { get; } = catchDepth;
            public int KindSlot { get; } = kindSlot;
            public int ReturnSlot { get; } = returnSlot;
            public List<FinallyTransfer> Transfers { get; } = new();
        }
        private readonly Stack<FinallyGuard> _finallyGuards = new();
        private readonly Stack<FinallyBodyBoundary> _finallyBodies = new();

        private bool ContainsFinally(int start, int end)
        {
            for (var cursor = Math.Max(0, start); cursor < end; cursor++)
                if (TokenAt(cursor).Kind == TokenKind.Keyword && TokenAt(cursor).Lexeme == "finally") return true;
            return false;
        }

        private (int Open, int End, bool HasCatch)? FindFinallyClause(int start)
        {
            var cursor = FindInterfaceFlowGroupEnd(start + 1, TokenKind.LBrace, TokenKind.RBrace);
            if (cursor < 0) return null;
            var hasCatch = false;
            while (TokenAt(cursor).Lexeme == "catch")
            {
                hasCatch = true;
                cursor++;
                if (TokenAt(cursor).Kind == TokenKind.LParen)
                    cursor = FindInterfaceFlowGroupEnd(cursor, TokenKind.LParen, TokenKind.RParen);
                if (cursor < 0) return null;
                cursor = FindInterfaceFlowGroupEnd(cursor, TokenKind.LBrace, TokenKind.RBrace);
                if (cursor < 0) return null;
            }
            if (TokenAt(cursor).Lexeme != "finally") return null;
            var open = cursor + 1;
            var end = FindInterfaceFlowGroupEnd(open, TokenKind.LBrace, TokenKind.RBrace);
            if (end < 0) throw Error(TokenAt(open), "finally requires a complete block.");
            return (open, end, hasCatch);
        }

        private bool ParseTryCatchWithFinally()
        {
            var clause = FindFinallyClause(_position);
            if (clause == null) return ParseTryCatchCore();
            var token = Current;
            if (_forWasm || _currentFunction is { IsPublicAsync: true } or { IsPublicStaticAsync: true })
                throw Error(token, "finally requires synchronous non-WASM code.");
            if (_constructingStruct != null || _currentFunction?.DeclaringStruct != null)
                throw Error(token, "finally is unsupported in struct code in this free-function/class subset.");

            var kindSlot = AllocateFinallyTemp(ExprType.Int);
            var returnSlot = _currentFunction!.ReturnType == ExprType.Void
                ? -1 : AllocateFinallyTemp(_currentFunction.ReturnType);
            EmitIntConstant(0);
            Emit(EnumOpcode.StoreLocal, kindSlot);
            // All protected normal exits branch out of this label. The VM expires handlers
            // by instruction range before executing the common finally epilogue.
            Emit(EnumOpcode.Block);
            _controlDepth++;
            var guard = new FinallyGuard(_controlDepth, _catchDepth, kindSlot, returnSlot);
            var handler = EmitPlaceholder(EnumOpcode.TryBegin);
            _finallyGuards.Push(guard);
            bool bodyCanContinue;
            try
            {
                if (clause.Value.HasCatch) bodyCanContinue = ParseTryCatchCore();
                else { Advance(); bodyCanContinue = ParseBlock(); }
            }
            finally { _finallyGuards.Pop(); }
            if (Current.Lexeme != "finally") throw Error(Current, "Expected finally after protected handlers.");
            Emit(EnumOpcode.TryEnd);
            Emit(EnumOpcode.Br, 0);

            // The exceptional path retains the generated catch context until rethrow.
            // It has its own lexical finally copy, never the normal-transfer dispatcher.
            var faultTarget = _code.Count;
            EmitFinallyBody(clause.Value.Open);
            Emit(EnumOpcode.Rethrow);
            Emit(EnumOpcode.CatchEnd);
            var table = _code.Count;
            _code.Add(new Instruction(EnumOpcode.TryCatch, faultTarget, 0));
            _code[handler] = new Instruction(EnumOpcode.TryBegin, table, 0);
            Emit(EnumOpcode.End);
            _controlDepth--;

            var finallyCanContinue = EmitFinallyBody(clause.Value.Open);
            for (var index = 0; index < guard.Transfers.Count; index++)
            {
                Emit(EnumOpcode.LoadLocal, guard.KindSlot);
                EmitIntConstant(index + 1);
                Emit(EnumOpcode.CmpEq);
                Emit(EnumOpcode.If);
                _controlDepth++;
                var transfer = guard.Transfers[index];
                if (transfer.Kind == FinallyTransferKind.Return && guard.ReturnSlot >= 0)
                    Emit(EnumOpcode.LoadLocal, guard.ReturnSlot);
                EmitFinallyTransfer(transfer.Kind, transfer.Loop);
                Emit(EnumOpcode.End);
                _controlDepth--;
            }
            _position = clause.Value.End;
            WidenFinallyInterfaceFacts();
            return bodyCanContinue && finallyCanContinue;
        }

        private int AllocateFinallyTemp(ExprType type)
        {
            var slot = _nextLocalSlot++;
            _localTypes.Add(slot, type);
            return slot;
        }

        private bool EmitFinallyBody(int open)
        {
            _position = open;
            WidenFinallyInterfaceFacts();
            _finallyBodies.Push(new FinallyBodyBoundary(_loops.Count, _catchDepth));
            try { return ParseBlock(); }
            finally { _finallyBodies.Pop(); }
        }

        private void WidenFinallyInterfaceFacts()
        {
            foreach (var local in _scopes.SelectMany(scope => scope.Values).ToArray())
                if (local.Type == ExprType.Object && local != _declaredLocals[local.Slot])
                    ForgetInterfaceNarrowing(local.Slot);
        }

        private FinallyGuard? FindExitedFinally(FinallyTransferKind kind, LoopContext? loop)
            => _finallyGuards.FirstOrDefault(guard => kind == FinallyTransferKind.Return ||
                (kind == FinallyTransferKind.Break ? loop!.BlockLevel : loop!.LoopLevel) < guard.ExitLevel);

        private void EmitFinallyTransfer(FinallyTransferKind kind, LoopContext? loop)
        {
            var guard = FindExitedFinally(kind, loop);
            if (guard != null)
            {
                if (kind == FinallyTransferKind.Return && guard.ReturnSlot >= 0)
                    Emit(EnumOpcode.StoreLocal, guard.ReturnSlot);
                var transfer = new FinallyTransfer(kind, loop);
                var index = guard.Transfers.IndexOf(transfer);
                if (index < 0) { index = guard.Transfers.Count; guard.Transfers.Add(transfer); }
                EmitIntConstant(index + 1);
                Emit(EnumOpcode.StoreLocal, guard.KindSlot);
                for (var depth = _catchDepth; depth > guard.CatchDepth; depth--)
                    Emit(EnumOpcode.CatchEnd, 1);
                Emit(EnumOpcode.Br, _controlDepth - guard.ExitLevel);
                return;
            }
            if (kind == FinallyTransferKind.Return) { Emit(EnumOpcode.Return); return; }
            for (var depth = _catchDepth; depth > loop!.CatchDepth; depth--)
                Emit(EnumOpcode.CatchEnd, 1);
            Emit(EnumOpcode.Br, _controlDepth - (kind == FinallyTransferKind.Break ? loop!.BlockLevel : loop!.LoopLevel));
        }

        private void ValidateFinallyLoopTransfer(AloeToken token)
        {
            if (_finallyBodies.TryPeek(out var body) && _loops.Count <= body.LoopCount)
                throw Error(token, "Outward break/continue from a finally body is unsupported in this subset.");
        }

        private void ValidateFinallyThrow(AloeToken token)
        {
            if (_finallyBodies.TryPeek(out var body) && _catchDepth <= body.CatchDepth)
                throw Error(token, "Bare throw in finally requires a user catch nested inside that finally.");
        }
    }
}
