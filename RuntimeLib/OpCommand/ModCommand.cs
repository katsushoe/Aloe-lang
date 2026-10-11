using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;


namespace Aloe.RuntimeLib.OpCommand
{
    internal sealed class ModCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            var right = vm.Pop();
            var left = vm.Pop();


            if ((!left.IsInt && !left.IsByte && !left.IsFloat && !left.IsDecimal) ||
                (!right.IsInt && !right.IsByte && !right.IsFloat && !right.IsDecimal))
            {
                throw new VmException(
                    $"Mod is defined only for int, float, or decimal operands " +
                    $"(left={left.Kind}, right={right.Kind}).");
            }


            if (left.IsDecimal || right.IsDecimal)
            {
                if (left.IsFloat || right.IsFloat)
                    throw new VmException("Decimal and float modulo cannot be mixed.");

                var decimalDivisor = right.AsDecimal;
                if (decimalDivisor == 0)
                    throw new ZeroDivisionException("Modulo by zero.");

                vm.Push(AloeValue.FromDecimal(left.AsDecimal % decimalDivisor));
                return;
            }


            if ((left.IsInt || left.IsByte) && (right.IsInt || right.IsByte))
            {
                var leftValue = AloeValue.ToInt32(left);
                var rightValue = AloeValue.ToInt32(right);
                if (rightValue == 0)
                    throw new ZeroDivisionException("Modulo by zero.");

                // The mathematical remainder is zero, even though CLR's rem
                // instruction can overflow for MinValue / -1.
                vm.Push(AloeValue.FromInt(rightValue == -1 ? 0 : leftValue % rightValue));
                return;
            }


            var divisor = (float)right.AsFloat;
            if (divisor == 0)
                throw new Aloe.CommonLib.Exceptions.ZeroDivisionException(
                    "Modulo by zero.");


            var remainder = (float)((float)left.AsFloat % divisor);
            if (!float.IsFinite(remainder))
                throw new OverflowException("Floating-point modulo produced a non-finite result.");

            vm.Push(AloeValue.FromFloat(remainder));
        }
    }
}
