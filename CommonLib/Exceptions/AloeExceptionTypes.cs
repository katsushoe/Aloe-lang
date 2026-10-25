using System;
using System.Collections.Generic;

namespace Aloe.CommonLib.Exceptions
{
    /// <summary>Exception classes currently exposed to Aloe typed catch clauses.</summary>
    public static class AloeExceptionTypes
    {
        private static readonly IReadOnlyDictionary<string, Type> Types =
            new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                ["Exception"] = typeof(Exception),
                ["SystemException"] = typeof(SystemException),
                ["ZeroDivisionException"] = typeof(ZeroDivisionException),
                ["InvalidReferenceException"] = typeof(InvalidReferenceException),
                ["InvalidCastException"] = typeof(InvalidCastException),
                ["OverflowException"] = typeof(OverflowException),
                ["ArgumentException"] = typeof(ArgumentException),
                ["IndexOutOfRangeException"] = typeof(IndexOutOfRangeException),
                ["VmException"] = typeof(VmException),
            };

        public static bool TryResolve(string name, out Type exceptionType) => Types.TryGetValue(name, out exceptionType!);
    }
}
