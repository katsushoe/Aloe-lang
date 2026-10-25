using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private static bool IsMapType(string? name)
            => name?.StartsWith("map<", StringComparison.Ordinal) == true && name.EndsWith(">", StringComparison.Ordinal);

        private static bool TrySplitMapType(string? name, out string keyName, out string valueName)
        {
            keyName = valueName = "";
            if (!IsMapType(name)) return false;
            var body = name![4..^1];
            var slash = body.IndexOf('/');
            keyName = slash < 0 ? "string" : body[..slash];
            valueName = slash < 0 ? body : body[(slash + 1)..];
            return keyName.Length > 0 && valueName.Length > 0;
        }

        private bool TryGetMapTypes(string? name, out ExprType keyType, out string keyName, out ExprType valueType, out string valueName)
        {
            if (TrySplitMapType(name, out keyName, out valueName) &&
                TryGetArrayElementType(keyName + "[]", out keyType) &&
                TryGetArrayElementType(valueName + "[]", out valueType) &&
                IsMapKeyType(keyType, keyName) && IsMapValueType(valueType, valueName)) return true;
            keyType = valueType = ExprType.Void;
            keyName = valueName = "";
            return false;
        }

        private static bool IsMapScalarType(ExprType type, string name)
            => (type is ExprType.Int or ExprType.Bool or ExprType.Byte or ExprType.Char or ExprType.Float or ExprType.Decimal or ExprType.String or ExprType.Enum) &&
               !name.EndsWith("[]", StringComparison.Ordinal) && !name.EndsWith("()", StringComparison.Ordinal) && !name.EndsWith("{}", StringComparison.Ordinal);

        private bool IsMapKeyType(ExprType type, string? name)
            => name != null && (IsMapScalarType(type, name) || type == ExprType.Object && IsClassOrInterface(name));

        private bool IsMapValueType(ExprType type, string name)
            => IsMapScalarType(type, name) || type == ExprType.Object &&
                (name == "object" || IsClassOrInterface(name) || TryGetNamedBoxType(name, out _));

        private string RuntimeMapName(string keyName, string valueName)
            => "map:" + RuntimeArrayName(keyName + "[]") + "|" + RuntimeArrayName(valueName + "[]");

        private ExprType ScanMapType(ref int position, AloeToken token, out string? name)
            => ScanMapType(ref position, token, out name, out _);

        private ExprType ScanMapType(ref int position, AloeToken token, out string? name, out bool outerGreaterConsumed)
        {
            if (_forWasm) throw Error(token, "Maps are unsupported by direct WASM.");
            ExpectAt(ref position, TokenKind.Less);
            var firstType = ScanSingleType(ref position, allowVoid: false, out var firstName);
            var keyType = ExprType.String;
            var keyName = "string";
            var valueType = firstType;
            var valueName = firstName;
            if (TokenAt(position).Kind == TokenKind.Slash)
            {
                keyType = firstType;
                keyName = firstType is ExprType.Enum or ExprType.Object ? firstName! : FormatType(firstType);
                position++;
                valueType = ScanSingleType(ref position, allowVoid: false, out valueName);
            }
            if (TokenAt(position).Kind == TokenKind.ShiftRight)
            {
                position++;
                outerGreaterConsumed = true;
            }
            else
            {
                ExpectAt(ref position, TokenKind.Greater);
                outerGreaterConsumed = false;
            }
            var canonicalValue = valueType is ExprType.Enum or ExprType.Object && valueName != null ? valueName : FormatType(valueType);
            if (!IsMapKeyType(keyType, keyName))
                throw Error(token, "Map keys currently support primitive, enum and statically named class/interface references.");
            if (!IsMapValueType(valueType, canonicalValue))
                throw Error(token, "Map values currently support primitive, string, enum and class/interface/object references.");
            name = "map<" + keyName + "/" + canonicalValue + ">";
            return ExprType.Object;
        }

        private bool IsMapLiteral()
        {
            if (IsMapType(_arrayLiteralContext)) return true;
            if (_arrayLiteralContext != null) return false;
            var depth = 0;
            for (var offset = 0; Peek(offset).Kind != TokenKind.EndOfFile; offset++)
            {
                var kind = Peek(offset).Kind;
                if (kind is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace) depth++;
                else if (kind is TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace)
                {
                    if (--depth == 0) return false;
                }
                else if (depth == 1 && kind == TokenKind.Colon) return true;
            }
            return false;
        }

        private ExprType ParseMapLiteral()
        {
            var token = Expect(TokenKind.LBracket);
            if (_forWasm) throw Error(token, "Maps are unsupported by direct WASM.");
            var expected = _arrayLiteralContext;
            _arrayLiteralContext = null;
            var hasExpected = TryGetMapTypes(expected, out var keyType, out var keyName, out var valueType, out var valueName);
            ExprType? inferredKeyType = hasExpected ? keyType : null;
            string? inferredKeyName = hasExpected ? keyName : null;
            ExprType? inferredValueType = hasExpected ? valueType : null;
            string? inferredValueName = hasExpected ? valueName : null;
            var count = 0;
            while (!Check(TokenKind.RBracket))
            {
                var key = Current;
                var keyStart = _code.Count;
                var actualKey = inferredKeyType is ExprType declaredKey ? ParseExpectedExpression(declaredKey, inferredKeyName) : ParseExpression();
                var actualKeyName = _lastExpressionClassName;
                var actualKeyTypeName = actualKey is ExprType.Enum or ExprType.Object ? actualKeyName ?? "" : FormatType(actualKey);
                if (!IsMapKeyType(actualKey, actualKeyTypeName))
                    throw Error(key, "Map keys currently support primitive, enum and statically named class/interface references.");
                if (inferredKeyType == null)
                {
                    inferredKeyType = actualKey;
                    inferredKeyName = actualKey is ExprType.Enum or ExprType.Object ? actualKeyName : FormatType(actualKey);
                }
                actualKey = AdaptByteLiteral(key, inferredKeyType.Value, actualKey, keyStart);
                RequireAssignable(key, inferredKeyType.Value, actualKey, inferredKeyName, actualKeyName);
                Expect(TokenKind.Colon);
                var value = Current;
                var expressionStart = _code.Count;
                var expectedValueName = inferredValueType == ExprType.Object && inferredValueName == "object" ? null : inferredValueName;
                var actual = inferredValueType is ExprType declared ? ParseExpectedExpression(declared, expectedValueName) : ParseExpression();
                var actualName = _lastExpressionClassName;
                if (!IsMapValueType(actual, actualName ?? FormatType(actual)))
                    throw Error(value, "Map values currently support primitive, string, enum and class/interface/object references.");
                if (inferredValueType == null) { inferredValueType = actual; inferredValueName = actualName; }
                actual = AdaptByteLiteral(value, inferredValueType.Value, actual, expressionStart);
                var assignValueName = inferredValueType == ExprType.Object && inferredValueName == "object" ? null : inferredValueName;
                RequireAssignable(value, inferredValueType.Value, actual, assignValueName, actualName);
                count++;
                if (!Check(TokenKind.Comma)) break;
                Advance();
            }
            Expect(TokenKind.RBracket);
            if (inferredKeyType == null || inferredValueType == null) throw Error(token, "An empty Map requires an expected Map type.");
            var runtimeKeyName = inferredKeyType is ExprType.Enum or ExprType.Object ? inferredKeyName! : FormatType(inferredKeyType.Value);
            var runtimeValueName = inferredValueType is ExprType.Enum or ExprType.Object ? inferredValueName! : FormatType(inferredValueType.Value);
            var name = "map<" + runtimeKeyName + "/" + runtimeValueName + ">";
            Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(RuntimeMapName(runtimeKeyName, runtimeValueName))));
            EmitIntConstant(count);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.MapCreate);
            _lastExpressionClassName = name;
            return ExprType.Object;
        }

        private ExprType ParseMapResultRead()
        {
            var name = _lastExpressionClassName!;
            TryGetMapTypes(name, out var keyType, out var keyName, out var valueType, out var valueName);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ReferenceRequireLive);
            if (Check(TokenKind.LBracket))
            {
                var key = Advance();
                var keyStart = _code.Count;
                var actualKey = ParseExpectedExpression(keyType, keyName);
                actualKey = AdaptByteLiteral(key, keyType, actualKey, keyStart);
                RequireAssignable(key, keyType, actualKey, keyName, _lastExpressionClassName);
                Expect(TokenKind.RBracket);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.MapGet);
                _lastExpressionClassName = valueType is ExprType.Enum or ExprType.Object ? valueName : null;
                return valueType;
            }
            Expect(TokenKind.Dot);
            var member = Expect(TokenKind.Identifier);
            if (member.Lexeme == "Count")
            {
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.MapCount);
                _lastExpressionClassName = null;
                return ExprType.Int;
            }
            if (member.Lexeme != "ContainsKey") throw Error(member, "Maps expose Count and ContainsKey; use a local for Set/Remove.");
            Expect(TokenKind.LParen);
            var containsKey = Current;
            var containsKeyStart = _code.Count;
            var actualContainsKey = ParseExpectedExpression(keyType, keyName);
            actualContainsKey = AdaptByteLiteral(containsKey, keyType, actualContainsKey, containsKeyStart);
            RequireAssignable(containsKey, keyType, actualContainsKey, keyName, _lastExpressionClassName);
            Expect(TokenKind.RParen);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.MapContainsKey);
            _lastExpressionClassName = null;
            return ExprType.Bool;
        }

        private void ParseMapMutationStatement(LocalInfo target)
        {
            if (_currentFunction?.IsPropertyGetter == true)
                throw Error(Current, "Property getters cannot mutate Maps.");
            Advance();
            TryGetMapTypes(target.ClassName, out var keyType, out var keyName, out var valueType, out var valueName);
            Emit(EnumOpcode.LoadLocal, target.Slot);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ReferenceRequireLive);
            Expect(TokenKind.Dot);
            var member = Expect(TokenKind.Identifier);
            if (member.Lexeme is not ("Set" or "Remove")) throw Error(member, "Map mutation methods are Set and Remove.");
            Expect(TokenKind.LParen);
            var key = Current;
            var keyStart = _code.Count;
            var actualKey = ParseExpectedExpression(keyType, keyName);
            actualKey = AdaptByteLiteral(key, keyType, actualKey, keyStart);
            RequireAssignable(key, keyType, actualKey, keyName, _lastExpressionClassName);
            if (member.Lexeme == "Set")
            {
                Expect(TokenKind.Comma);
                var value = Current;
                var expressionStart = _code.Count;
                var expectedValueName = valueType == ExprType.Object && valueName == "object" ? null : valueName;
                var actual = ParseExpectedExpression(valueType, expectedValueName);
                actual = AdaptByteLiteral(value, valueType, actual, expressionStart);
                RequireAssignable(value, valueType, actual, expectedValueName, _lastExpressionClassName);
            }
            Expect(TokenKind.RParen);
            Expect(TokenKind.Semicolon);
            Emit(EnumOpcode.Syscall, (int)(member.Lexeme == "Set" ? EnumSyscall.MapSet : EnumSyscall.MapRemove));
        }

        private void ParseMapForeach(AloeToken itemName, AloeToken? valueItemName, string mapName,
            ExprType keyType, string keyName, ExprType valueType, string valueName)
        {
            Expect(TokenKind.RParen);
            var target = DeclareImplicitLocal($"$mapTarget${_nextLocalSlot}", ExprType.Object, mapName);
            var index = DeclareImplicitLocal($"$mapIndex${_nextLocalSlot}", ExprType.Int);
            Emit(EnumOpcode.StoreLocal, target.Slot);
            EmitIntConstant(0);
            Emit(EnumOpcode.StoreLocal, index.Slot);
            Emit(EnumOpcode.Block);
            var blockLevel = ++_controlDepth;
            Emit(EnumOpcode.Loop);
            var loopLevel = ++_controlDepth;
            Emit(EnumOpcode.LoadLocal, index.Slot);
            Emit(EnumOpcode.LoadLocal, target.Slot);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ReferenceRequireLive);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.MapCount);
            Emit(EnumOpcode.CmpLt);
            EmitBoolConstant(false);
            Emit(EnumOpcode.CmpEq);
            Emit(EnumOpcode.BrIf, _controlDepth - blockLevel);
            Expect(TokenKind.LBrace);
            EnterScope();
            var item = DeclareLocal(itemName, keyType, keyType is ExprType.Enum or ExprType.Object ? keyName : null);
            LocalInfo? valueItem = valueItemName == null
                ? null
                : DeclareLocal(valueItemName, valueType, valueType is ExprType.Enum or ExprType.Object ? valueName : null);
            Emit(EnumOpcode.LoadLocal, target.Slot);
            Emit(EnumOpcode.LoadLocal, index.Slot);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.MapKeyAt);
            Emit(EnumOpcode.StoreLocal, item.Slot);
            if (valueItem != null)
            {
                Emit(EnumOpcode.LoadLocal, target.Slot);
                Emit(EnumOpcode.LoadLocal, item.Slot);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.MapGet);
                Emit(EnumOpcode.StoreLocal, valueItem.Slot);
            }
            Emit(EnumOpcode.LoadLocal, index.Slot);
            EmitIntConstant(1);
            Emit(EnumOpcode.Add);
            Emit(EnumOpcode.StoreLocal, index.Slot);
            _loops.Push(new LoopContext(blockLevel, loopLevel, _catchDepth));
            ParseStatementBlockContents("Map foreach");
            _loops.Pop();
            Expect(TokenKind.RBrace);
            ExitScope();
            Emit(EnumOpcode.Br, _controlDepth - loopLevel);
            Emit(EnumOpcode.End);
            _controlDepth--;
            Emit(EnumOpcode.End);
            _controlDepth--;
        }
    }
}
