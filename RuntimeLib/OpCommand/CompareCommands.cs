using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;


namespace Aloe.RuntimeLib.OpCommand
{
    internal static class ComparisonSupport
    {
        public static bool Equal(AloeValue left, AloeValue right)
        {
            if (left.IsNumber && right.IsNumber)
            {
                if (IsInteger(left) && IsInteger(right))
                    return ToInteger(left) == ToInteger(right);

                if (left.IsDecimal || right.IsDecimal)
                {
                    if (left.IsFloat || right.IsFloat)
                        throw new VmException("Decimal and float comparison cannot be mixed.");
                    return left.AsDecimal == right.AsDecimal;
                }


                return left.AsFloat.Equals(right.AsFloat);
            }


            if (left.Kind != right.Kind)
                return false;


            if (left.IsBool)
                return left.AsBool == right.AsBool;

            if (left.IsChar)
                return left.AsChar == right.AsChar;


            if (left.IsString)
                return string.Equals(
                    left.AsString,
                    right.AsString,
                    StringComparison.Ordinal);


            if (left.IsNull)
                return true;


            throw new VmException(
                $"Equality is not implemented for {left.Kind}.");
        }


        public static int CompareNumber(AloeValue left, AloeValue right)
        {
            if (left.IsChar && right.IsChar)
                return left.AsChar.CompareTo(right.AsChar);

            if (!left.IsNumber || !right.IsNumber)
            {
                throw new VmException(
                    $"Ordered comparison requires numeric operands " +
                    $"(left={left.Kind}, right={right.Kind}).");
            }


            if (IsInteger(left) && IsInteger(right))
                return ToInteger(left).CompareTo(ToInteger(right));

            if (left.IsDecimal || right.IsDecimal)
            {
                if (left.IsFloat || right.IsFloat)
                    throw new VmException("Decimal and float comparison cannot be mixed.");
                return left.AsDecimal.CompareTo(right.AsDecimal);
            }


            return left.AsFloat.CompareTo(right.AsFloat);
        }

        private static bool IsInteger(AloeValue value)
            => value.IsInt || value.IsByte;

        private static long ToInteger(AloeValue value)
            => value.IsByte ? value.AsByte : value.AsInt;
    }


    internal sealed class CmpEqCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            var right = vm.Pop();
            var left = vm.Pop();
            vm.Push(AloeValue.FromBool(ComparisonSupport.Equal(left, right)));
        }
    }


    internal sealed class CmpNeCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            var right = vm.Pop();
            var left = vm.Pop();
            vm.Push(AloeValue.FromBool(!ComparisonSupport.Equal(left, right)));
        }
    }


    internal sealed class CmpLeCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            var right = vm.Pop();
            var left = vm.Pop();
            vm.Push(AloeValue.FromBool(
                ComparisonSupport.CompareNumber(left, right) <= 0));
        }
    }


    internal sealed class CmpGtCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            var right = vm.Pop();
            var left = vm.Pop();
            vm.Push(AloeValue.FromBool(
                ComparisonSupport.CompareNumber(left, right) > 0));
        }
    }


    internal sealed class CmpGeCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            var right = vm.Pop();
            var left = vm.Pop();
            vm.Push(AloeValue.FromBool(
                ComparisonSupport.CompareNumber(left, right) >= 0));
        }
    }
}
