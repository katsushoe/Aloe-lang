using System;

namespace Aloe.CommonLib.Exceptions
{
    public class VmException : Exception
    {
        public VmException()
        {
        }

        public VmException(string message)
            : base(message)
        {
        }

        public VmException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
