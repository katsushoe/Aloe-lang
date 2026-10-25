using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private string? _arrayLiteralContext;
        private bool _parsingArrayAssignment;
        private (LocalInfo Array, LocalInfo Index, LocalInfo Value)? _arrayStructAssignmentTarget;

        private bool TryGetArrayElementType(string? name, out ExprType type)
        {
            if (name?.EndsWith("{}", StringComparison.Ordinal) == true)
                return TryGetArrayElementType(name[..^2] + "[]", out type);
            if (name?.EndsWith("()", StringComparison.Ordinal) == true)
            {
                var element = name[..^2];
                if (TryGetArrayElementType(element, out _)) { type = ExprType.Object; return true; }
                return TryGetArrayElementType(element + "[]", out type);
            }
            type = name switch
            {
                "int[]" => ExprType.Int, "bool[]" => ExprType.Bool,
                "byte[]" => ExprType.Byte, "char[]" => ExprType.Char,
                "float[]" => ExprType.Float, "decimal[]" => ExprType.Decimal,
                "string[]" => ExprType.String, "object[]" => ExprType.Object,
                _ => ExprType.Void
            };
            if (type == ExprType.Void && name != null && name.EndsWith("[]", StringComparison.Ordinal) &&
                _enumMembers.ContainsKey(name[..^2])) type = ExprType.Enum;
            if (type == ExprType.Void && name != null && name.EndsWith("[]", StringComparison.Ordinal) &&
                _structs.ContainsKey(name[..^2])) type = ExprType.Struct;
            if (type == ExprType.Void && name != null && name.EndsWith("[]", StringComparison.Ordinal) &&
                TryGetArrayElementType(name[..^2], out _)) type = ExprType.Object;
            if (type == ExprType.Void && name != null && name.EndsWith("[]", StringComparison.Ordinal) &&
                (IsClassOrInterface(name[..^2]) || TryGetNamedBoxType(name[..^2], out _))) type = ExprType.Object;
            return type != ExprType.Void;
        }

        private string RuntimeArrayName(string name)
        {
            if (name.EndsWith("{}", StringComparison.Ordinal))
                return "set:" + RuntimeArrayName(name[..^2] + "[]");
            if (name.EndsWith("()", StringComparison.Ordinal))
                return TryGetArrayElementType(name[..^2], out _)
                    ? "nested:list:" + RuntimeArrayName(name[..^2])
                    : "list:" + RuntimeArrayName(name[..^2] + "[]");
            if (name.EndsWith("[]", StringComparison.Ordinal) && name.Contains("()", StringComparison.Ordinal))
                return "nested:array:" + RuntimeArrayName(name[..^2]);
            var baseName = name;
            while (baseName.EndsWith("[]", StringComparison.Ordinal)) baseName = baseName[..^2];
            if (TryGetNamedBoxType(baseName, out var primitive)) return "box:" + FormatType(primitive) + name[baseName.Length..];
            if (_enumMembers.ContainsKey(baseName)) return "enum:" + name;
            if (_structs.ContainsKey(baseName)) return "struct:" + name;
            if (IsClassOrInterface(baseName))
            {
                var names = _classes.Keys.Where(candidate => candidate != baseName && IsClassAssignable(candidate, baseName))
                    .OrderBy(candidate => candidate, StringComparer.Ordinal);
                return "class:" + string.Join("|", new[] { baseName }.Concat(names)) + name[baseName.Length..];
            }
            return name;
        }

        private void ParseListMutationStatement(LocalInfo target)
        {
            var targetToken = Advance();
            var collectionTypeName = target.ClassName!;
            var isSet = collectionTypeName.EndsWith("{}", StringComparison.Ordinal);
            if (!TryGetArrayElementType(collectionTypeName, out var elementType))
                throw Error(targetToken, $"Unsupported collection type '{collectionTypeName}'.");
            if (isSet && (elementType == ExprType.Struct || elementType == ExprType.Object &&
                (collectionTypeName.Length < 3 || !IsClassOrInterface(collectionTypeName[..^2]))))
                throw Error(targetToken, "Set currently supports primitive, string, enum and class/interface-reference values only.");
            Emit(EnumOpcode.LoadLocal, target.Slot);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ReferenceRequireLive);
            Expect(TokenKind.Dot);
            var member = Expect(TokenKind.Identifier);
            Expect(TokenKind.LParen);
            if (isSet && member.Lexeme == "Clear")
            {
                Expect(TokenKind.RParen);
                Expect(TokenKind.Semicolon);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.SetClear);
                return;
            }
            if (isSet && member.Lexeme is "UnionWith" or "IntersectWith" or "ExceptWith")
            {
                var argumentToken = Current;
                var other = ParseExpectedExpression(ExprType.Object, collectionTypeName);
                RequireAssignable(argumentToken, ExprType.Object, other, collectionTypeName, _lastExpressionClassName);
                Expect(TokenKind.RParen);
                Expect(TokenKind.Semicolon);
                Emit(EnumOpcode.Syscall, (int)(member.Lexeme switch
                {
                    "UnionWith" => EnumSyscall.SetUnionWith,
                    "IntersectWith" => EnumSyscall.SetIntersectWith,
                    _ => EnumSyscall.SetExceptWith
                }));
                return;
            }
            if (member.Lexeme == "Add")
            {
                var argumentToken = Current;
                var expressionStart = _code.Count;
                var elementName = collectionTypeName[..^2];
                var actual = ParseExpectedArrayExpression(elementName);
                actual = AdaptByteLiteral(argumentToken, elementType, actual, expressionStart);
                RequireAssignable(argumentToken, elementType, actual, elementName, _lastExpressionClassName);
                Expect(TokenKind.RParen);
                Expect(TokenKind.Semicolon);
                Emit(EnumOpcode.Syscall, (int)(isSet ? EnumSyscall.SetAdd : EnumSyscall.ListAdd));
                return;
            }
            if (member.Lexeme == (isSet ? "Remove" : "RemoveAt"))
            {
                var argumentToken = Current;
                if (isSet)
                {
                    var elementName = collectionTypeName[..^2];
                    var expressionStart = _code.Count;
                    var actual = ParseExpectedArrayExpression(elementName);
                    actual = AdaptByteLiteral(argumentToken, elementType, actual, expressionStart);
                    RequireAssignable(argumentToken, elementType, actual, elementName, _lastExpressionClassName);
                }
                else
                {
                    var actual = ParseExpectedExpression(ExprType.Int);
                    RequireAssignable(argumentToken, ExprType.Int, actual);
                }
                Expect(TokenKind.RParen);
                Expect(TokenKind.Semicolon);
                Emit(EnumOpcode.Syscall, (int)(isSet ? EnumSyscall.SetRemove : EnumSyscall.ListRemoveAt));
                return;
            }
            throw Error(member, $"Collection method '{member.Lexeme}' is not supported.");
        }

        private ExprType ParseExpectedArrayExpression(string? name)
        {
            var previous = _arrayLiteralContext;
            _arrayLiteralContext = (TryGetArrayElementType(name, out _) || IsMapType(name)) ? name : null;
            try { return ParseExpression(); }
            finally { _arrayLiteralContext = previous; }
        }

        private bool IsListLiteral()
        {
            if (Peek(1).Kind == TokenKind.RParen) return true;
            var depth = 0;
            for (var offset = 0; Peek(offset).Kind != TokenKind.EndOfFile; offset++)
            {
                var kind = Peek(offset).Kind;
                if (kind is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace) depth++;
                else if (kind is TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace)
                {
                    if (--depth == 0) return false;
                }
                else if (kind == TokenKind.Comma && depth == 1) return true;
            }
            return false;
        }

        private ExprType ParseArrayLiteral(bool isList = false, bool isSet = false)
        {
            var token = Advance();
            if ((isList || isSet) && _forWasm) throw Error(token, "Lists and Sets are unsupported by direct WASM.");
            var expected = _arrayLiteralContext;
            if (expected != null && expected.EndsWith("()", StringComparison.Ordinal) != isList ||
                expected != null && expected.EndsWith("{}", StringComparison.Ordinal) != isSet) expected = null;
            var close = isList ? TokenKind.RParen : isSet ? TokenKind.RBrace : TokenKind.RBracket;
            _arrayLiteralContext = null;
            ExprType? elementType = TryGetArrayElementType(expected, out var declaredElement) ? declaredElement : null;
            string? elementName = elementType is ExprType.Enum or ExprType.Struct or ExprType.Object ? expected![..^2] : null;
            if (elementName == "object") elementName = null;
            var count = 0;
            while (!Check(close))
            {
                var element = Current;
                var expressionStart = _code.Count;
                var actual = elementType is ExprType known ? ParseExpectedExpression(known, elementName) : ParseExpression();
                var actualName = _lastExpressionClassName;
                if (actual is not (ExprType.Int or ExprType.Bool or ExprType.Byte or ExprType.Char or ExprType.Float or ExprType.Decimal or ExprType.String or ExprType.Enum or ExprType.Struct or ExprType.Object) ||
                    actual == ExprType.Object && actualName != null && !TryGetArrayElementType(actualName, out _) && !IsClassOrInterface(actualName) && !TryGetNamedBoxType(actualName, out _))
                    throw Error(element, "Array elements currently support primitive values, strings, enums, structs, class/object references and supported arrays.");
                if (isSet && (actual == ExprType.Struct || actual == ExprType.Object &&
                    !IsClassOrInterface(actualName)))
                    throw Error(element, "Set currently supports primitive, string, enum and class/interface-reference values only.");
                if (elementType == null) { elementType = actual; elementName = actualName; }
                actual = AdaptByteLiteral(element, elementType.Value, actual, expressionStart);
                RequireAssignable(element, elementType.Value, actual, elementName, actualName);
                count++;
                if (!Check(TokenKind.Comma)) break;
                Advance();
                if (Check(close))
                {
                    if (!isList && !isSet) throw Error(Current, "Trailing commas in array literals are not supported.");
                    break;
                }
            }
            Expect(close);
            if (elementType == null) throw Error(token, "An empty array literal requires a supported array declaration or assignment context.");
            var name = (elementType is ExprType.Enum or ExprType.Struct || elementType == ExprType.Object && elementName != null ? elementName : FormatType(elementType.Value)) + (isList ? "()" : isSet ? "{}" : "[]");
            var runtimeName = RuntimeArrayName(name);
            Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(runtimeName)));
            EmitIntConstant(count);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ArrayCreate);
            _lastExpressionClassName = name;
            return ExprType.Object;
        }

        private void ParseArrayIndex()
        {
            var token = Expect(TokenKind.LBracket);
            RequireAssignable(token, ExprType.Int, ParseExpectedExpression(ExprType.Int));
            Expect(TokenKind.RBracket);
        }

        private ExprType ParseArrayResultRead(ExprType elementType)
        {
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ReferenceRequireLive);
            if (Check(TokenKind.LBracket))
            {
                if (_lastExpressionClassName?.EndsWith("{}", StringComparison.Ordinal) == true)
                    throw Error(Current, "Set values cannot be accessed by index.");
                var elementName = elementType is ExprType.Enum or ExprType.Struct or ExprType.Object ? _lastExpressionClassName![..^2] : null;
                if (elementName == "object") elementName = null;
                var arrayName = _lastExpressionClassName;
                ParseArrayIndex();
                if (_parsingArrayAssignment && elementType == ExprType.Struct && IsStructArrayFieldPath())
                {
                    var array = DeclareImplicitLocal($"$arrayTarget${_nextLocalSlot}", ExprType.Object, arrayName);
                    var index = DeclareImplicitLocal($"$arrayIndex${_nextLocalSlot}", ExprType.Int);
                    var value = DeclareImplicitLocal($"$arrayStruct${_nextLocalSlot}", ExprType.Struct, elementName);
                    Emit(EnumOpcode.StoreLocal, index.Slot);
                    Emit(EnumOpcode.StoreLocal, array.Slot);
                    Emit(EnumOpcode.LoadLocal, array.Slot);
                    Emit(EnumOpcode.LoadLocal, index.Slot);
                    Emit(EnumOpcode.Syscall, (int)EnumSyscall.ArrayGet);
                    _arrayStructAssignmentTarget = (array, index, value);
                    _lastExpressionClassName = elementName;
                    return ExprType.Struct;
                }
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.ArrayGet);
                _lastExpressionClassName = elementName;
                if (elementType == ExprType.Object && Current.Kind is TokenKind.LBracket or TokenKind.Dot &&
                    TryGetArrayElementType(elementName, out var childType)) return ParseArrayResultRead(childType);
                if (elementType == ExprType.Object && elementName != null && (_classes.ContainsKey(elementName) || TryGetNamedBoxType(elementName, out _)))
                    return ParseReferenceGetters(elementName);
                return elementType == ExprType.Struct ? ParseStructResultMembers(elementType, elementName) : elementType;
            }
            Expect(TokenKind.Dot);
            var member = Expect(TokenKind.Identifier);
            var isSet = _lastExpressionClassName?.EndsWith("{}", StringComparison.Ordinal) == true;
            if (isSet && member.Lexeme == "Contains")
            {
                Expect(TokenKind.LParen);
                var elementName = _lastExpressionClassName![..^2];
                var argumentToken = Current;
                var expressionStart = _code.Count;
                var actual = ParseExpectedArrayExpression(elementName);
                actual = AdaptByteLiteral(argumentToken, elementType, actual, expressionStart);
                RequireAssignable(argumentToken, elementType, actual, elementName, _lastExpressionClassName);
                Expect(TokenKind.RParen);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.SetContains);
                _lastExpressionClassName = null;
                return ExprType.Bool;
            }
            if (member.Lexeme != (isSet ? "Count" : "Length"))
                throw Error(member, isSet ? "Sets expose the read-only Count property." : "Arrays and Lists expose the read-only Length property.");
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ReferenceRequireLive);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.ArrayLength);
            _lastExpressionClassName = null;
            return ExprType.Int;
        }

        private ExprType ParseReferenceGetters(string className)
        {
            var type = ExprType.Object;
            while (Check(TokenKind.Dot) && type == ExprType.Object && (_classes.ContainsKey(className) || TryGetNamedBoxType(className, out _)))
            {
                if (TryGetNamedBoxType(className, out _)) return ParseNamedBoxMember(className);
                Advance();
                var member = Expect(TokenKind.Identifier);
                var property = FindProperty(className, member.Lexeme)
                    ?? throw Error(member, "Class references expose public getters; use a local or with target for async calls.");
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.ReferenceRequireLive);
                EmitIntConstant(property.Getter.Index);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.InstancePropertyGet);
                type = property.Type;
                _lastExpressionClassName = property.ClassName;
                if (type == ExprType.Struct) return ParseStructResultMembers(type, property.ClassName);
                if (type == ExprType.Object && IsMapType(property.ClassName) && Current.Kind is TokenKind.Dot or TokenKind.LBracket)
                    return ParseMapResultRead();
                if (type == ExprType.Object && TryGetArrayElementType(property.ClassName, out var elementType) &&
                    Current.Kind is TokenKind.Dot or TokenKind.LBracket) return ParseArrayResultRead(elementType);
                if (property.ClassName == null) break;
                className = property.ClassName;
            }
            return type;
        }

        private bool IsStructArrayFieldPath()
        {
            var offset = 0;
            while (Peek(offset).Kind == TokenKind.Dot && Peek(offset + 1).Kind == TokenKind.Identifier)
                offset += 2;
            return offset > 0 && Peek(offset).Kind == TokenKind.Assign;
        }

        private bool IsArrayAssignmentStatement()
        {
            if (Current.Lexeme is "foreach" or "with" or "while" or "if")
                return false;
            if (Current.Kind is not (TokenKind.Identifier or TokenKind.LParen or TokenKind.LBracket or TokenKind.Dot) && Current.Lexeme != "this")
                return false;
            var depth = 0;
            var previous = TokenKind.EndOfFile;
            for (var offset = 0; ; offset++)
            {
                var kind = Peek(offset).Kind;
                if (kind == TokenKind.EndOfFile || depth == 0 && kind is TokenKind.Semicolon or TokenKind.LBrace or TokenKind.RBrace)
                    return false;
                if (depth == 0 && kind == TokenKind.Assign)
                {
                    if (previous == TokenKind.RBracket) return true;
                    var path = offset - 1;
                    while (path >= 2 && Peek(path).Kind == TokenKind.Identifier && Peek(path - 1).Kind == TokenKind.Dot)
                        path -= 2;
                    return path >= 0 && Peek(path).Kind == TokenKind.RBracket;
                }
                if (kind is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace) depth++;
                if (kind is TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace) depth--;
                previous = kind;
            }
        }

        private void ParseArrayAssignment()
        {
            var token = Current;
            if (_currentFunction?.IsPropertyGetter == true)
                throw Error(token, "Property getters cannot assign array elements.");
            var expressionStart = _code.Count;
            _arrayStructAssignmentTarget = null;
            ExprType elementType;
            _parsingArrayAssignment = true;
            try { elementType = ParseExpression(); }
            finally { _parsingArrayAssignment = false; }
            if (_arrayStructAssignmentTarget is { } target)
            {
                _arrayStructAssignmentTarget = null;
                Emit(EnumOpcode.StoreLocal, target.Value.Slot);
                Expect(TokenKind.Dot);
                ParseStructFieldAssignment(target.Value, Expect(TokenKind.Identifier));
                Emit(EnumOpcode.LoadLocal, target.Array.Slot);
                Emit(EnumOpcode.LoadLocal, target.Index.Slot);
                Emit(EnumOpcode.LoadLocal, target.Value.Slot);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.ArraySet);
                return;
            }
            var elementName = _lastExpressionClassName;
            if (!Check(TokenKind.Assign) || _code.Count == expressionStart ||
                _code[^1].Opcode != EnumOpcode.Syscall || _code[^1].Operand0 != (int)EnumSyscall.ArrayGet)
                throw Error(token, "Array element assignment requires an indexed array expression.");
            // Keep the evaluated receiver and index for ArraySet instead of reading the element.
            _code.RemoveAt(_code.Count - 1);
            Expect(TokenKind.Assign);
            expressionStart = _code.Count;
            var actual = ParseExpectedExpression(elementType, elementName);
            actual = AdaptByteLiteral(token, elementType, actual, expressionStart);
            RequireAssignable(token, elementType, actual, elementName, _lastExpressionClassName);
            Expect(TokenKind.Semicolon);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ArraySet);
        }

        private void ParseArrayForeach(AloeToken itemName, string arrayName, ExprType elementType)
        {
            Expect(TokenKind.RParen);
            var target = DeclareImplicitLocal($"$arrayTarget${_nextLocalSlot}", ExprType.Object, arrayName);
            var index = DeclareImplicitLocal($"$arrayIndex${_nextLocalSlot}", ExprType.Int);
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
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.ArrayLength);
            Emit(EnumOpcode.CmpLt);
            EmitBoolConstant(false);
            Emit(EnumOpcode.CmpEq);
            Emit(EnumOpcode.BrIf, _controlDepth - blockLevel);
            Expect(TokenKind.LBrace);
            EnterScope();
            var item = DeclareLocal(itemName, elementType, elementType is ExprType.Enum or ExprType.Struct or ExprType.Object && arrayName != "object[]" ? arrayName[..^2] : null);
            Emit(EnumOpcode.LoadLocal, target.Slot);
            Emit(EnumOpcode.LoadLocal, index.Slot);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ArrayGet);
            Emit(EnumOpcode.StoreLocal, item.Slot);
            Emit(EnumOpcode.LoadLocal, index.Slot);
            EmitIntConstant(1);
            Emit(EnumOpcode.Add);
            Emit(EnumOpcode.StoreLocal, index.Slot);
            _loops.Push(new LoopContext(blockLevel, loopLevel, _catchDepth));
            ParseStatementBlockContents("array foreach");
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
