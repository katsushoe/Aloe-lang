using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private ExprType ParseCast()
        {
            var type = ParseUnary();
            while (Current.Lexeme == "as")
            {
                var token = Advance();
                var sourceName = _lastExpressionClassName;
                var targetType = ParseTypeName(out var targetName);
                if (_forWasm)
                {
                    if (targetType != type || targetType is not (ExprType.Int or ExprType.Float or
                        ExprType.Bool or ExprType.Byte or ExprType.Char))
                        throw Error(token, "Direct WASM supports only identity casts for int, float, bool, byte, and char; numeric conversions and other cast forms are unsupported.");
                    if (Current.Lexeme == "ifnot")
                        throw Error(token, "Direct WASM does not support ifnot cast fallbacks.");
                    _lastExpressionClassName = null;
                    continue;
                }
                if (IsPrimitiveCastType(targetType))
                {
                    if (!IsPrimitiveCastType(type) && (type != ExprType.Object || sourceName?.StartsWith("pipe<", StringComparison.Ordinal) == true))
                        throw Error(token, "Primitive casts require primitive or heap-reference operands; enum/struct/pipe casts are unsupported.");
                    Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(FormatType(targetType))));
                    ParsePrimitiveCastTail(targetType);
                    type = targetType;
                    _lastExpressionClassName = null;
                    continue;
                }
                if (targetType == ExprType.Object && (targetName == null || TryGetNamedBoxType(targetName, out _)) && IsPrimitiveCastType(type))
                {
                    Emit(EnumOpcode.Syscall, (int)EnumSyscall.PrimitiveBox);
                    type = ExprType.Object;
                }
                if (type != ExprType.Object) throw Error(token, "Reference casts require a class/object/array operand or a primitive boxed to object.");
                if (targetType != ExprType.Object) throw Error(token, "This cast subset supports class/object/array targets only.");
                var descriptor = ReferenceCastDescriptor(token, targetName);
                Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(descriptor)));
                if (Current.Lexeme == "ifnot")
                {
                    Advance();
                    if (Current.Lexeme == "throw")
                    {
                        Advance();
                        Emit(EnumOpcode.Syscall, (int)EnumSyscall.ObjectCast);
                    }
                    else
                    {
                        Emit(EnumOpcode.Syscall, (int)EnumSyscall.ObjectTryCast);
                        var result = DeclareImplicitLocal($"$castResult${_nextLocalSlot}", ExprType.Object, targetName);
                        var success = DeclareImplicitLocal($"$castSuccess${_nextLocalSlot}", ExprType.Bool);
                        Emit(EnumOpcode.StoreLocal, success.Slot);
                        Emit(EnumOpcode.StoreLocal, result.Slot);
                        Emit(EnumOpcode.LoadLocal, success.Slot);
                        Emit(EnumOpcode.If);
                        _controlDepth++;
                        Emit(EnumOpcode.Else);
                        var fallbackToken = Current;
                        var fallbackType = ParseExpectedExpression(ExprType.Object, targetName);
                        RequireAssignable(fallbackToken, ExprType.Object, fallbackType, targetName, _lastExpressionClassName);
                        Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(descriptor)));
                        Emit(EnumOpcode.Syscall, (int)EnumSyscall.ObjectCast);
                        Emit(EnumOpcode.StoreLocal, result.Slot);
                        Emit(EnumOpcode.End);
                        _controlDepth--;
                        Emit(EnumOpcode.LoadLocal, result.Slot);
                    }
                }
                else Emit(EnumOpcode.Syscall, (int)EnumSyscall.ObjectCast);
                type = ExprType.Object;
                _lastExpressionClassName = targetName;
                if (TryGetNamedBoxType(targetName, out _) && Check(TokenKind.Dot))
                    type = ParseNamedBoxMember(targetName!);
                else if (IsMapType(targetName) && Current.Kind is TokenKind.Dot or TokenKind.LBracket)
                    type = ParseMapResultRead();
                else if (TryGetArrayElementType(targetName, out var elementType) && Current.Kind is TokenKind.Dot or TokenKind.LBracket)
                    type = ParseArrayResultRead(elementType);
                else if (targetName != null && _classes.ContainsKey(targetName) && Check(TokenKind.Dot))
                    type = ParseReferenceGetters(targetName);
            }
            return type;
        }

        private static bool IsPrimitiveCastType(ExprType type)
            => type is ExprType.Int or ExprType.Byte or ExprType.Char or ExprType.Float or ExprType.Decimal or ExprType.String or ExprType.Bool;

        private void ParsePrimitiveCastTail(ExprType targetType)
        {
            if (Current.Lexeme != "ifnot")
            {
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.PrimitiveCast);
                return;
            }
            Advance();
            if (Current.Lexeme == "throw")
            {
                Advance();
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.PrimitiveCast);
                return;
            }
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.PrimitiveTryCast);
            var result = DeclareImplicitLocal($"$primitiveCast${_nextLocalSlot}", targetType);
            var success = DeclareImplicitLocal($"$primitiveCastSuccess${_nextLocalSlot}", ExprType.Bool);
            Emit(EnumOpcode.StoreLocal, success.Slot);
            Emit(EnumOpcode.StoreLocal, result.Slot);
            Emit(EnumOpcode.LoadLocal, success.Slot);
            Emit(EnumOpcode.If);
            _controlDepth++;
            Emit(EnumOpcode.Else);
            var fallbackToken = Current;
            var expressionStart = _code.Count;
            var fallbackType = ParseExpectedExpression(targetType);
            fallbackType = AdaptByteLiteral(fallbackToken, targetType, fallbackType, expressionStart);
            if (fallbackType == ExprType.Object && !IsPipeTypeName(_lastExpressionClassName))
            {
                RequireAssignable(fallbackToken, targetType, fallbackType, null, _lastExpressionClassName);
                fallbackType = targetType;
            }
            if (fallbackType != targetType)
                throw Error(fallbackToken, "Primitive cast fallback must have the exact target type; numeric conversion is unsupported.");
            Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(FormatType(targetType))));
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.PrimitiveCast);
            Emit(EnumOpcode.StoreLocal, result.Slot);
            Emit(EnumOpcode.End);
            _controlDepth--;
            Emit(EnumOpcode.LoadLocal, result.Slot);
        }

        private string ReferenceCastDescriptor(AloeToken token, string? name)
        {
            if (name == null) return "object";
            if (TryGetMapTypes(name, out _, out var keyName, out _, out var valueName)) return RuntimeMapName(keyName, valueName);
            if (TryGetNamedBoxType(name, out var primitive)) return "box:" + FormatType(primitive);
            if (TryGetArrayElementType(name, out _)) return "array:" + RuntimeArrayName(name);
            if (_classes.ContainsKey(name)) return RuntimeArrayName(name + "[]")[..^2];
            if (_interfaces.ContainsKey(name))
            {
                var implementers = _classes.Values
                    .Where(classInfo => IsClassAssignable(classInfo.Name, name))
                    .Select(classInfo => classInfo.Name)
                    .ToArray();
                return implementers.Length == 0 ? $"class:{name}" : "class:" + string.Join("|", implementers);
            }
            throw Error(token, "Reference casts currently support known class/interface/object/array types.");
        }
    }
}
