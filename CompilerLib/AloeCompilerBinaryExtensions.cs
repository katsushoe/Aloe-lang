using Aloe.CommonLib;
using System;

namespace Aloe.CompilerLib
{
    /// <summary>
    /// Convenience API for compiling Aloe source directly to AloeBC bytes.
    /// </summary>
    public static class AloeCompilerBinaryExtensions
    {
        public static byte[] CompileToAloeBc(this AloeCompiler compiler, string source)
        {
            if (compiler == null) throw new ArgumentNullException(nameof(compiler));
            return AloeBcCodec.Write(compiler.Compile(source));
        }
    }
}
