using System;
using Aloe.CommonLib.Exceptions;

namespace Aloe.CommonLib
{
    public class OperandStack
    {
        private readonly Stack<AloeValue> _stack = new Stack<AloeValue>();

        public int Count => _stack.Count;

        public void Push(AloeValue value)
        {
            _stack.Push(value);
        }

        public AloeValue Pop()
        {
            if (_stack.Count == 0)
            {
                throw new VmException("Operand stack underflow.");
            }
            return _stack.Pop();
        }

        public AloeValue Peek()
        {
            if (_stack.Count == 0)
            {
                throw new VmException("Operand stack underflow.");
            }
            return _stack.Peek();
        }

        public void Clear()
        {
            _stack.Clear();
        }
    }
}
