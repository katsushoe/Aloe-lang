using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private static bool TryGetNamedBoxType(string? name, out ExprType type)
        {
            type = name switch
            {
                "Integer" => ExprType.Int, "Byte" => ExprType.Byte, "Char" => ExprType.Char,
                "Float" => ExprType.Float, "Decimal" => ExprType.Decimal, "String" => ExprType.String,
                "Boolean" => ExprType.Bool, _ => ExprType.Void
            };
            return type != ExprType.Void;
        }

        private ExprType ParseNamedBoxConstructor(AloeToken name, ExprType primitive)
        {
            if (_forWasm) throw Error(name, "Named boxes are unsupported by the direct WASM backend.");
            Expect(TokenKind.LParen);
            var argument = Current;
            var start = _code.Count;
            var actual = ParseExpectedExpression(primitive);
            actual = AdaptByteLiteral(argument, primitive, actual, start);
            RequireAssignable(argument, primitive, actual, null, _lastExpressionClassName);
            Expect(TokenKind.RParen);
            Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(FormatType(primitive))));
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.PrimitiveCast);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.PrimitiveBox);
            _lastExpressionClassName = name.Lexeme;
            return ExprType.Object;
        }

        private ExprType ParseNamedBoxMember(string name)
        {
            Expect(TokenKind.Dot);
            var member = Expect(TokenKind.Identifier);
            return ParseNamedBoxCall(name, member);
        }

        private ExprType ParseNamedBoxCall(string name, AloeToken member)
        {
            TryGetNamedBoxType(name, out var primitive);
            var method = primitive switch
            {
                ExprType.Int => "ToInt", ExprType.Byte => "ToByte", ExprType.Char => "ToChar",
                ExprType.Float => "ToFloat", ExprType.Decimal => "ToDecimal", ExprType.String => "ToString",
                ExprType.Bool => "ToBool", _ => throw Error(member, "Unknown named box type.")
            };
            if (member.Lexeme != method) throw Error(member, $"{name} exposes only {method}(); its payload is private.");
            Expect(TokenKind.LParen);
            Expect(TokenKind.RParen);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ReferenceRequireLive);
            Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString("box:" + FormatType(primitive))));
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ObjectCast);
            Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(FormatType(primitive))));
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.PrimitiveCast);
            _lastExpressionClassName = null;
            return primitive;
        }
    }
}
