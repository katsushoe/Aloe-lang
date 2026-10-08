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
                if (left.IsInt && right.IsInt)
                    return left.AsInt == right.AsInt;


                return left.AsFloat.Equals(right.AsFloat);
            }


            if (left.Kind != right.Kind)
                return false;


            if (left.IsBool)
                return left.AsBool == right.AsBool;


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
            if (!left.IsNumber || !right.IsNumber)
            {
                throw new VmException(
                    $"Ordered comparison requires numeric operands " +
                    $"(left={left.Kind}, right={right.Kind}).");
            }


            if (left.IsInt && right.IsInt)
                return left.AsInt.CompareTo(right.AsInt);


            return left.AsFloat.CompareTo(right.AsFloat);
        }
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