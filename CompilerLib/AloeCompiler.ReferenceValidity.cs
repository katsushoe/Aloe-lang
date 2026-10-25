using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private ExprType? _expectedUnboxPrimitive;

        private ExprType ParseExpectedExpression(ExprType expected, string? name = null)
        {
            var previous = _expectedUnboxPrimitive;
            _expectedUnboxPrimitive = IsPrimitiveCastType(expected) ? expected : null;
            try
            {
                var type = ParseExpectedArrayExpression(name);
                return expected == ExprType.Bool
                    ? EmitOperatorUnbox(Previous, type, _lastExpressionClassName, ExprType.Bool)
                    : type;
            }
            finally { _expectedUnboxPrimitive = previous; }
        }

        private sealed record ReferenceTarget(LocalInfo? Local, LocalInfo? Owner, LocalInfo? Index, EnumSyscall Read, EnumSyscall Clear);

        private ReferenceTarget ParseReferenceTarget(AloeToken operation)
        {
            var start = _code.Count;
            var type = ParsePrimary();
            if (type != ExprType.Object || IsPipeTypeName(_lastExpressionClassName) || _code.Count == start)
                throw Error(operation, "Invalidation requires a heap-reference local, field or array element.");
            var last = _code[^1];
            if (last.Opcode == EnumOpcode.LoadLocal && _code.Count == start + 1)
            {
                if (last.Operand0 == 0 && _currentFunction?.DeclaringClass != null && !_currentFunction.IsStaticMethod)
                    throw Error(operation, "The this receiver cannot be invalidated.");
                _code.RemoveAt(_code.Count - 1);
                var local = new LocalInfo(last.Operand0, type, _lastExpressionClassName);
                return new ReferenceTarget(local, null, null, default, default);
            }
            if (last.Opcode != EnumOpcode.Syscall || last.Operand0 is not ((int)EnumSyscall.ArrayGet) and not ((int)EnumSyscall.ReferenceFieldGet))
                throw Error(operation, "Invalidation requires a local, accessible reference field or indexed reference element; properties and temporaries are read-only.");
            if (_currentFunction?.IsPropertyGetter == true)
                throw Error(operation, "Property getters cannot invalidate heap fields or array elements.");
            _code.RemoveAt(_code.Count - 1);
            var index = DeclareImplicitLocal($"$invalidateIndex${_nextLocalSlot}", ExprType.Int);
            var owner = DeclareImplicitLocal($"$invalidateOwner${_nextLocalSlot}", ExprType.Object);
            Emit(EnumOpcode.StoreLocal, index.Slot);
            Emit(EnumOpcode.StoreLocal, owner.Slot);
            var read = (EnumSyscall)last.Operand0;
            return new ReferenceTarget(null, owner, index, read, read == EnumSyscall.ArrayGet ? EnumSyscall.ReferenceArrayInvalidate : EnumSyscall.ReferenceFieldInvalidate);
        }

        private void EmitReferenceTargetOwner(ReferenceTarget target)
        {
            Emit(EnumOpcode.LoadLocal, target.Owner!.Slot);
            Emit(EnumOpcode.LoadLocal, target.Index!.Slot);
        }

        private void EmitReferenceTargetRead(ReferenceTarget target)
        {
            if (target.Local != null) Emit(EnumOpcode.LoadLocal, target.Local.Slot);
            else { EmitReferenceTargetOwner(target); Emit(EnumOpcode.Syscall, (int)target.Read); }
        }

        private void EmitReferenceTargetClear(ReferenceTarget target)
        {
            if (target.Local != null)
            {
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.ReferenceInvalid);
                Emit(EnumOpcode.StoreLocal, target.Local.Slot);
                ForgetInterfaceNarrowing(target.Local.Slot);
            }
            else { EmitReferenceTargetOwner(target); Emit(EnumOpcode.Syscall, (int)target.Clear); }
        }

        private void ParseDeleteLocal()
        {
            var token = Advance();
            if (_forWasm) throw Error(token, "Reference invalidation is unsupported by direct WASM.");
            var target = ParseReferenceTarget(token);
            Expect(TokenKind.Semicolon);
            EmitReferenceTargetClear(target);
        }

        private ExprType ParseConsumingUnbox()
        {
            var token = Advance();
            if (_forWasm) throw Error(token, "Consuming unbox is unsupported by direct WASM.");
            if (_expectedUnboxPrimitive is not ExprType target)
                throw Error(token, "Consuming unbox requires an expected primitive type; use a typed assignment, argument or return.");
            var destination = ParseReferenceTarget(token);
            EmitReferenceTargetRead(destination);
            Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(FormatType(target))));
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.PrimitiveUnbox);
            EmitReferenceTargetClear(destination);
            _lastExpressionClassName = null;
            return target;
        }

        private ExprType ParseInvalidReferenceComparison()
        {
            var token = Advance(); // invalid on the left
            if (_forWasm) throw Error(token, "Reference validity checks are unsupported by direct WASM.");
            var op = Advance();
            if (op.Kind is not (TokenKind.EqualEqual or TokenKind.BangEqual))
                throw Error(token, "invalid is allowed only in reference ==/!= validity checks.");
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ReferenceInvalid);
            var type = ParseShift();
            if (type != ExprType.Object || IsPipeTypeName(_lastExpressionClassName))
                throw Error(token, "invalid checks require a heap-reference expression.");
            Emit(op.Kind == TokenKind.EqualEqual ? EnumOpcode.CmpEq : EnumOpcode.CmpNe);
            _lastExpressionClassName = null;
            return ExprType.Bool;
        }
    }
}
