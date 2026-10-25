using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private ExprType OperatorPrimitiveType(ExprType type, string? name)
            => type == ExprType.Object && TryGetNamedBoxType(name, out var primitive) ? primitive : type;

        private ExprType EmitOperatorUnbox(AloeToken token, ExprType type, string? name, ExprType expected)
        {
            if (type != ExprType.Object || IsPipeTypeName(name) || !IsPrimitiveCastType(expected)) return type;
            if (_forWasm) throw Error(token, "Operator unboxing is unsupported by direct WASM.");
            Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(FormatType(expected))));
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.PrimitiveCast);
            _lastExpressionClassName = null;
            return expected;
        }

        private void UnboxOperatorOperands(AloeToken token, ref ExprType left, string? leftName,
            ref ExprType right, string? rightName, int rightStart, ExprType? fixedType = null)
        {
            var leftPrimitive = OperatorPrimitiveType(left, leftName);
            var rightPrimitive = OperatorPrimitiveType(right, rightName);
            var contextual = fixedType ?? (_expectedUnboxPrimitive is ExprType expected && IsNumericType(expected) ? expected : null);
            var leftTarget = IsPrimitiveCastType(leftPrimitive) ? leftPrimitive :
                IsPrimitiveCastType(rightPrimitive) ? rightPrimitive : contextual;
            var rightTarget = IsPrimitiveCastType(rightPrimitive) ? rightPrimitive :
                IsPrimitiveCastType(leftPrimitive) ? leftPrimitive : contextual;

            if (left == ExprType.Object && !IsPipeTypeName(leftName) && leftTarget is ExprType primitive)
            {
                // Emit the left check before the already parsed right expression to preserve runtime evaluation order.
                var rightCode = _code.GetRange(rightStart, _code.Count - rightStart);
                _code.RemoveRange(rightStart, rightCode.Count);
                left = EmitOperatorUnbox(token, left, leftName, primitive);
                _code.AddRange(rightCode);
                _lastExpressionClassName = rightName;
            }
            if (rightTarget is ExprType target)
                right = EmitOperatorUnbox(token, right, rightName, target);
        }
    }
}
