using System;
using System.Runtime.Serialization;

namespace Aloe.CommonLib.Exceptions
{
    /// <summary>
    /// 0 除算を表す例外。
    /// Aloe 言語仕様の ZeroDivisionException に対応する .NET 側の例外型。
    /// </summary>
    public class ZeroDivisionException : SystemException
    {
        public ZeroDivisionException()
        {
        }

        public ZeroDivisionException(string message)
            : base(message)
        {
        }

        public ZeroDivisionException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
