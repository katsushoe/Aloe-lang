using System;
using System.Collections.Generic;

namespace Aloe.CommonLib.Exceptions
{
    /// <summary>
    /// Decision 0059: an exception value thrown by <c>throw value;</c>. The value is an exception struct
    /// whose fields start with Message and TypeName; <see cref="TypeChain"/> lists the thrown type first
    /// and its exception bases after it, ending with the built-in root.
    /// </summary>
    public sealed class AloeThrownException : Exception
    {
        public AloeThrownException(AloeValue value, IReadOnlyList<string> typeChain, Type builtinBase)
            : base(FormatMessage(value, typeChain))
        {
            Value = value;
            TypeChain = typeChain;
            BuiltinBase = builtinBase;
        }

        /// <summary>The thrown exception struct value.</summary>
        public AloeValue Value { get; }

        /// <summary>Thrown type, then each base type up to the built-in root.</summary>
        public IReadOnlyList<string> TypeChain { get; }

        /// <summary>The built-in exception class the thrown type ultimately derives from.</summary>
        public Type BuiltinBase { get; }

        /// <summary>The thrown type name.</summary>
        public string TypeName => TypeChain[0];

        /// <summary>The Message field of the thrown value.</summary>
        public string UserMessage => Value.AsStruct.GetField(0).AsString;

        private static string FormatMessage(AloeValue value, IReadOnlyList<string> typeChain)
            => typeChain[0] + ": " + value.AsStruct.GetField(0).AsString;
    }
}
