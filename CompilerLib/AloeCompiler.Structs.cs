using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed record StructSignature(string Name, List<FieldSignature> Fields)
    {
        public FunctionSignature? Constructor { get; set; }
        public List<MethodSignature> Methods { get; } = new();
    }

    private sealed partial class Parser
    {
        private readonly Dictionary<string, StructSignature> _structs = new(StringComparer.Ordinal);
        private readonly Dictionary<int, int> _structEndPositions = new();
        private StructSignature? _constructingStruct;
        private readonly Dictionary<string, LocalInfo> _constructorFields = new(StringComparer.Ordinal);
        private readonly HashSet<string> _initializedConstructorFields = new(StringComparer.Ordinal);

        private void CollectStructSignatures()
        {
            _structs.Clear();
            _structEndPositions.Clear();
            var starts = new List<(int Start, int Body, string Name)>();
            var position = 0;
            var depth = 0;
            while (TokenAt(position).Kind != TokenKind.EndOfFile)
            {
                var start = position;
                if (depth == 0 && TokenAt(position).Lexeme == "sealed" && TokenAt(position + 1).Lexeme == "struct")
                    position++;
                if (depth == 0 && TokenAt(position).Lexeme == "struct")
                {
                    position++;
                    var name = TokenAt(position++);
                    if (name.Kind != TokenKind.Identifier || _enumMembers.ContainsKey(name.Lexeme) ||
                        !_structs.TryAdd(name.Lexeme, new StructSignature(name.Lexeme, new())))
                        throw Error(name, "Struct name must be a unique type identifier.");
                    ExpectAt(ref position, TokenKind.LBrace);
                    starts.Add((start, position, name.Lexeme));
                    depth++;
                    continue;
                }
                if (TokenAt(position).Kind == TokenKind.LBrace) depth++;
                if (TokenAt(position).Kind == TokenKind.RBrace) depth--;
                position++;
            }
            foreach (var declaration in starts)
            {
                position = declaration.Body;
                var signature = _structs[declaration.Name];
                while (TokenAt(position).Kind != TokenKind.RBrace)
                {
                    if (TokenAt(position).Lexeme == "construct")
                    {
                        CollectStructConstructor(signature, ref position);
                        continue;
                    }
                    if (TokenAt(position).Lexeme == "method")
                    {
                        CollectStructMethod(signature, ref position);
                        continue;
                    }
                    ExpectLexemeAt(ref position, "field");
                    var name = TokenAt(position++);
                    if (name.Kind != TokenKind.Identifier || signature.Fields.Any(f => f.Name == name.Lexeme))
                        throw Error(name, "Struct field name must be a unique identifier.");
                    ExpectAt(ref position, TokenKind.Colon);
                    var type = ScanType(ref position, allowVoid: false, out var typeName);
                    if (type is not (ExprType.Int or ExprType.Byte or ExprType.Char or ExprType.Float or ExprType.Decimal or ExprType.Bool or ExprType.Struct))
                        throw Error(name, "Struct fields support primitive value types and other structs; references are forbidden.");
                    ExpectAt(ref position, TokenKind.Semicolon);
                    signature.Fields.Add(new FieldSignature(name.Lexeme, signature.Fields.Count, type, typeName));
                }
                _structEndPositions.Add(declaration.Start, position + 1);
            }
            foreach (var signature in _structs.Values)
                CheckStructCycle(signature, new HashSet<string>(StringComparer.Ordinal));
        }

        private void CollectStructMethod(StructSignature signature, ref int position)
        {
            position++; // method
            var name = TokenAt(position++);
            if (name.Kind != TokenKind.Identifier || signature.Methods.Any(method => method.Name == name.Lexeme))
                throw Error(name, "Struct method names must be unique identifiers.");
            ExpectAt(ref position, TokenKind.LParen);
            var parameters = new List<ParameterSignature>();
            if (TokenAt(position).Kind != TokenKind.RParen)
            {
                while (true)
                {
                    var parameterName = TokenAt(position++);
                    if (parameterName.Kind != TokenKind.Identifier || parameterName.Lexeme == "this" ||
                        parameters.Any(parameter => parameter.Name.Lexeme == parameterName.Lexeme))
                        throw Error(parameterName, "Struct method parameter names must be unique identifiers.");
                    ExpectAt(ref position, TokenKind.Colon);
                    var parameterType = ScanType(ref position, allowVoid: false, out var parameterClassName);
                    if (parameterType is not (ExprType.Int or ExprType.Byte or ExprType.Char or ExprType.Float or ExprType.Decimal or ExprType.Bool or ExprType.Struct))
                        throw Error(parameterName, "Struct methods support primitive value types and structs as parameters.");
                    parameters.Add(new ParameterSignature(parameterName, parameterType, parameterClassName));
                    if (TokenAt(position).Kind != TokenKind.Comma) break;
                    position++;
                }
            }
            ExpectAt(ref position, TokenKind.RParen);
            ExpectAt(ref position, TokenKind.Colon);
            var returnType = ScanType(ref position, allowVoid: true, out var returnClassName);
            if (returnType is not (ExprType.Void or ExprType.Int or ExprType.Byte or ExprType.Char or ExprType.Float or ExprType.Decimal or ExprType.Bool or ExprType.Struct))
                throw Error(name, "Struct methods support primitive value types and structs as return values.");
            ExpectAt(ref position, TokenKind.LBrace);
            var bodyStart = position;
            var depth = 1;
            while (depth > 0)
            {
                var bodyToken = TokenAt(position++);
                if (bodyToken.Kind == TokenKind.EndOfFile)
                    throw Error(bodyToken, $"Unexpected end of file in struct method '{signature.Name}.{name.Lexeme}'.");
                if (bodyToken.Kind == TokenKind.LBrace) depth++;
                else if (bodyToken.Kind == TokenKind.RBrace) depth--;
            }
            var function = new FunctionSignature($"{signature.Name}.{name.Lexeme}", _functions.Count, parameters,
                returnType, bodyStart, position - 1, false, ReturnClassName: returnClassName, DeclaringStruct: signature.Name);
            signature.Methods.Add(new MethodSignature(name.Lexeme, function));
            _functions.Add(function);
        }

        private void CollectStructConstructor(StructSignature signature, ref int position)
        {
            var token = TokenAt(position++);
            if (signature.Constructor != null) throw Error(token, "Only one struct constructor is supported.");
            ExpectAt(ref position, TokenKind.LParen);
            var parameters = new List<ParameterSignature>();
            while (TokenAt(position).Kind != TokenKind.RParen)
            {
                var name = TokenAt(position++);
                if (name.Kind != TokenKind.Identifier || parameters.Any(p => p.Name.Lexeme == name.Lexeme))
                    throw Error(name, "Constructor parameter names must be unique identifiers.");
                ExpectAt(ref position, TokenKind.Colon);
                var type = ScanType(ref position, false, out var typeName);
                if (type is not (ExprType.Int or ExprType.Byte or ExprType.Char or ExprType.Float or ExprType.Decimal or ExprType.Bool or ExprType.Struct))
                    throw Error(name, "Struct constructor parameters currently support primitive value types and structs.");
                parameters.Add(new ParameterSignature(name, type, typeName));
                if (TokenAt(position).Kind != TokenKind.Comma) break;
                position++;
                if (TokenAt(position).Kind == TokenKind.RParen) throw Error(TokenAt(position), "Expected constructor parameter after comma.");
            }
            ExpectAt(ref position, TokenKind.RParen);
            ExpectAt(ref position, TokenKind.LBrace);
            var start = position;
            var depth = 1;
            while (depth > 0)
            {
                var current = TokenAt(position++);
                if (current.Kind == TokenKind.EndOfFile) throw Error(current, "Unterminated struct constructor.");
                if (current.Kind == TokenKind.LBrace) depth++;
                if (current.Kind == TokenKind.RBrace) depth--;
            }
            var function = new FunctionSignature($"{signature.Name}.construct", _functions.Count, parameters,
                ExprType.Struct, start, position - 1, false, IsConstructor: true, ReturnClassName: signature.Name);
            signature.Constructor = function;
            _functions.Add(function);
        }

        private bool CompileStructConstructor(FunctionSignature function)
        {
            if (!function.IsConstructor || function.DeclaringClass != null) return false;
            var signature = _structs[function.ReturnClassName!];
            _constructingStruct = signature;
            _constructorFields.Clear();
            _initializedConstructorFields.Clear();
            foreach (var field in signature.Fields)
                _constructorFields.Add(field.Name, DeclareImplicitLocal($"$field${field.Index}", field.Type, field.ClassName));
            var canFallThrough = true;
            while (_position < function.BodyEnd)
            {
                if (Current.Lexeme == "this" && Peek(1).Kind == TokenKind.Dot)
                {
                    ParseStructConstructorThisAssignment(signature);
                }
                else
                {
                    if (Current.Lexeme is "with" || Check(TokenKind.LBrace))
                        throw Error(Current, "Struct constructors currently reject with blocks and standalone blocks.");
                    var statementCanFallThrough = ParseStatement();
                    if (canFallThrough) canFallThrough = statementCanFallThrough;
                }
            }
            if (canFallThrough)
            {
                RequireAllConstructorFieldsInitialized(signature, Current);
                EmitStructConstructorValue(signature);
                Emit(EnumOpcode.Return);
            }
            _constructingStruct = null;
            return true;
        }

        private void ParseStructConstructorThisAssignment(StructSignature signature)
        {
            Advance(); // this
            Expect(TokenKind.Dot);
            var rootMember = Expect(TokenKind.Identifier);
            var rootField = ResolveStructField(signature.Name, rootMember);
            var root = _constructorFields[rootField.Name];
            var path = new List<AloeToken>();
            while (Check(TokenKind.Dot))
            {
                Advance();
                path.Add(Expect(TokenKind.Identifier));
            }

            if (path.Count == 0)
            {
                Expect(TokenKind.Assign);
                var start = _code.Count;
                var type = ParseExpression();
                var typeName = _lastExpressionClassName;
                type = AdaptByteLiteral(rootMember, rootField.Type, type, start);
                RequireAssignable(rootMember, rootField.Type, type, rootField.ClassName, typeName);
                Emit(EnumOpcode.StoreLocal, root.Slot);
                _initializedConstructorFields.Add(rootField.Name);
                Expect(TokenKind.Semicolon);
                return;
            }

            if (!_initializedConstructorFields.Contains(rootField.Name))
                throw Error(rootMember, "Nested constructor assignment requires the containing field to be initialized first.");

            var parent = root;
            var parents = new List<(LocalInfo Parent, FieldSignature Field, LocalInfo Child)>();
            var currentStructName = rootField.ClassName;
            FieldSignature? field = null;
            for (var i = 0; i < path.Count; i++)
            {
                var pathMember = path[i];
                field = ResolveStructField(currentStructName, pathMember);
                if (i == path.Count - 1) break;
                if (field.Type != ExprType.Struct)
                    throw Error(pathMember, "Nested field assignment requires a struct field.");
                var child = DeclareImplicitLocal($"$struct${_nextLocalSlot}", field.Type, field.ClassName);
                Emit(EnumOpcode.LoadLocal, parent.Slot);
                Emit(EnumOpcode.StructGet, field.Index);
                Emit(EnumOpcode.StoreLocal, child.Slot);
                parents.Add((parent, field, child));
                parent = child;
                currentStructName = field.ClassName;
            }

            Expect(TokenKind.Assign);
            Emit(EnumOpcode.LoadLocal, parent.Slot);
            var expressionStart = _code.Count;
            var actual = ParseExpression();
            var actualName = _lastExpressionClassName;
            var target = field!;
            var member = path[^1];
            actual = AdaptByteLiteral(member, target.Type, actual, expressionStart);
            RequireAssignable(member, target.Type, actual, target.ClassName, actualName);
            Emit(EnumOpcode.StructSet, target.Index);
            for (var i = parents.Count - 1; i >= 0; i--)
            {
                var ancestor = parents[i];
                Emit(EnumOpcode.StoreLocal, ancestor.Child.Slot);
                Emit(EnumOpcode.LoadLocal, ancestor.Parent.Slot);
                Emit(EnumOpcode.LoadLocal, ancestor.Child.Slot);
                Emit(EnumOpcode.StructSet, ancestor.Field.Index);
            }
            Emit(EnumOpcode.StoreLocal, root.Slot);
            Expect(TokenKind.Semicolon);
        }

        private void ParseStructConstructorReturn(StructSignature signature)
        {
            var token = Advance(); // return
            if (!Check(TokenKind.Semicolon))
                throw Error(token, "Struct constructor return does not accept a value; use 'return;' after initializing every field.");
            RequireAllConstructorFieldsInitialized(signature, token);
            Expect(TokenKind.Semicolon);
            EmitStructConstructorValue(signature);
            Emit(EnumOpcode.Return);
        }

        private void RequireAllConstructorFieldsInitialized(StructSignature signature, AloeToken token)
        {
            if (_initializedConstructorFields.Count != signature.Fields.Count)
                throw Error(token, "Struct constructors must initialize every field before returning.");
        }

        private void EmitStructConstructorValue(StructSignature signature)
        {
            Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(signature.Name)));
            foreach (var field in signature.Fields) Emit(EnumOpcode.LoadLocal, _constructorFields[field.Name].Slot);
            Emit(EnumOpcode.StructCreate, signature.Fields.Count);
        }

        private HashSet<string> CaptureConstructorInitialization()
            => new(_initializedConstructorFields, StringComparer.Ordinal);

        private void RestoreConstructorInitialization(IEnumerable<string> initialized)
        {
            _initializedConstructorFields.Clear();
            _initializedConstructorFields.UnionWith(initialized);
        }

        private void MergeConstructorInitialization(
            IEnumerable<string> first,
            IEnumerable<string> second,
            bool firstFallsThrough,
            bool secondFallsThrough)
        {
            if (!firstFallsThrough && !secondFallsThrough)
            {
                _initializedConstructorFields.Clear();
                return;
            }
            if (!firstFallsThrough)
            {
                RestoreConstructorInitialization(second);
                return;
            }
            if (!secondFallsThrough)
            {
                RestoreConstructorInitialization(first);
                return;
            }
            var definitelyInitialized = new HashSet<string>(first, StringComparer.Ordinal);
            definitelyInitialized.IntersectWith(second);
            RestoreConstructorInitialization(definitelyInitialized);
        }

        private void MergeConstructorBranch(HashSet<string> before, HashSet<string> thenState, bool thenFallsThrough, bool hasElse)
        {
            if (_constructingStruct == null) return;
            if (!hasElse)
            {
                MergeConstructorInitialization(thenState, before, thenFallsThrough, secondFallsThrough: true);
                return;
            }
            var elseState = CaptureConstructorInitialization();
            MergeConstructorInitialization(thenState, elseState, thenFallsThrough, secondFallsThrough: true);
        }

        private void MergeConstructorBranch(HashSet<string> thenState, bool thenFallsThrough,
            HashSet<string> elseState, bool elseFallsThrough)
        {
            if (_constructingStruct != null)
                MergeConstructorInitialization(thenState, elseState, thenFallsThrough, elseFallsThrough);
        }

        private void RestoreConstructorLoopEntry(HashSet<string>? entry)
        {
            if (_constructingStruct != null && entry != null) RestoreConstructorInitialization(entry);
        }

        private ExprType ParseStructConstructorFieldRead()
        {
            Advance();
            Expect(TokenKind.Dot);
            var member = Expect(TokenKind.Identifier);
            var field = ResolveStructField(_constructingStruct!.Name, member);
            if (!_initializedConstructorFields.Contains(field.Name))
                throw Error(member, "Struct constructor cannot read an uninitialized field.");
            Emit(EnumOpcode.LoadLocal, _constructorFields[field.Name].Slot);
            while (Check(TokenKind.Dot))
            {
                Advance();
                var nested = Expect(TokenKind.Identifier);
                if (field.Type != ExprType.Struct) throw Error(nested, "Nested field reads require a struct.");
                field = ResolveStructField(field.ClassName, nested);
                Emit(EnumOpcode.StructGet, field.Index);
            }
            _lastExpressionClassName = field.ClassName;
            return field.Type;
        }

        private ExprType ParseNewStruct()
        {
            Advance();
            var name = Expect(TokenKind.Identifier);
            var signature = _structs[name.Lexeme];
            var constructor = signature.Constructor;
            Expect(TokenKind.LParen);
            var count = 0;
            while (!Check(TokenKind.RParen))
            {
                if (constructor == null || count >= constructor.Parameters.Count)
                    throw Error(Current, "Struct constructor received too many arguments.");
                var token = Current;
                var start = _code.Count;
                var actual = ParseExpression();
                var actualName = _lastExpressionClassName;
                var parameter = constructor.Parameters[count++];
                actual = AdaptByteLiteral(token, parameter.Type, actual, start);
                RequireAssignable(token, parameter.Type, actual, parameter.ClassName, actualName);
                if (!Check(TokenKind.Comma)) break;
                Advance();
                if (Check(TokenKind.RParen)) throw Error(Current, "Expected constructor argument after comma.");
            }
            Expect(TokenKind.RParen);
            if (constructor == null)
            {
                if (signature.Fields.Count != 0) throw Error(name, "Struct with fields requires a constructor for new; use a struct literal instead.");
                Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(signature.Name)));
                Emit(EnumOpcode.StructCreate, 0);
            }
            else
            {
                if (count != constructor.Parameters.Count) throw Error(name, $"Constructor expects {constructor.Parameters.Count} arguments, found {count}.");
                Emit(EnumOpcode.Call, constructor.Index);
            }
            _lastExpressionClassName = signature.Name;
            return ExprType.Struct;
        }

        private void CheckStructCycle(StructSignature signature, HashSet<string> path)
        {
            if (!path.Add(signature.Name))
                throw new AloeCompileException($"Struct '{signature.Name}' contains a recursive value layout.");
            foreach (var field in signature.Fields.Where(f => f.Type == ExprType.Struct))
                CheckStructCycle(_structs[field.ClassName!], path);
            path.Remove(signature.Name);
        }

        private FieldSignature ResolveStructField(string? name, AloeToken member)
        {
            if (name == null || !_structs.TryGetValue(name, out var signature))
                throw Error(member, "Field access requires a statically known struct value.");
            return signature.Fields.FirstOrDefault(f => f.Name == member.Lexeme)
                ?? throw Error(member, $"Struct '{name}' has no field '{member.Lexeme}'.");
        }

        private ExprType ParseStructLiteral()
        {
            var token = Advance();
            var signature = _structs[token.Lexeme];
            Expect(TokenKind.LBrace);
            var slots = new Dictionary<string, int>(StringComparer.Ordinal);
            while (!Check(TokenKind.RBrace))
            {
                var member = Expect(TokenKind.Identifier);
                var field = ResolveStructField(signature.Name, member);
                if (slots.ContainsKey(field.Name)) throw Error(member, "Duplicate struct field initializer.");
                Expect(TokenKind.Colon);
                var expressionStart = _code.Count;
                var actual = ParseExpression();
                var actualName = _lastExpressionClassName;
                actual = AdaptByteLiteral(member, field.Type, actual, expressionStart);
                RequireAssignable(member, field.Type, actual, field.ClassName, actualName);
                var temp = DeclareImplicitLocal($"$struct${_nextLocalSlot}", field.Type, field.ClassName);
                slots.Add(field.Name, temp.Slot);
                Emit(EnumOpcode.StoreLocal, temp.Slot);
                if (!Check(TokenKind.Comma)) break;
                Advance();
            }
            Expect(TokenKind.RBrace);
            if (slots.Count != signature.Fields.Count)
                throw Error(token, "Struct literals must initialize every field exactly once.");
            Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(signature.Name)));
            foreach (var field in signature.Fields) Emit(EnumOpcode.LoadLocal, slots[field.Name]);
            Emit(EnumOpcode.StructCreate, signature.Fields.Count);
            _lastExpressionClassName = signature.Name;
            return ExprType.Struct;
        }

        private ExprType ParseStructFieldRead()
        {
            var token = Advance();
            var local = ResolveLocal(token);
            Emit(EnumOpcode.LoadLocal, local.Slot);
            var type = local.Type;
            var name = local.ClassName;
            while (Check(TokenKind.Dot))
            {
                Advance();
                var member = Expect(TokenKind.Identifier);
                if (type != ExprType.Struct) throw Error(member, "Only structs have struct fields.");
                var field = ResolveStructField(name, member);
                Emit(EnumOpcode.StructGet, field.Index);
                type = field.Type;
                name = field.ClassName;
            }
            _lastExpressionClassName = name;
            return type;
        }

        private bool IsStructFieldAssignment()
        {
            if (!Check(TokenKind.Identifier) || Peek(1).Kind != TokenKind.Dot ||
                !TryResolveLocal(Current.Lexeme, out var local) || local.Type != ExprType.Struct)
                return false;
            var offset = 1;
            while (Peek(offset).Kind == TokenKind.Dot && Peek(offset + 1).Kind == TokenKind.Identifier)
                offset += 2;
            return Peek(offset).Kind == TokenKind.Assign;
        }

        private void ParseStructFieldAssignment()
        {
            var root = ResolveLocal(Advance());
            Expect(TokenKind.Dot);
            ParseStructFieldAssignment(root, Expect(TokenKind.Identifier));
        }

        private void ParseStructFieldAssignment(LocalInfo root, AloeToken firstMember)
        {
            var parent = root;
            var parents = new List<(LocalInfo Parent, FieldSignature Field, LocalInfo Child)>();
            FieldSignature field;
            var member = firstMember;
            while (true)
            {
                field = ResolveStructField(parent.ClassName, member);
                if (!Check(TokenKind.Dot)) break;
                Advance();
                if (field.Type != ExprType.Struct) throw Error(member, "Nested field assignment requires a struct field.");
                var child = DeclareImplicitLocal($"$struct${_nextLocalSlot}", field.Type, field.ClassName);
                Emit(EnumOpcode.LoadLocal, parent.Slot);
                Emit(EnumOpcode.StructGet, field.Index);
                Emit(EnumOpcode.StoreLocal, child.Slot);
                parents.Add((parent, field, child));
                parent = child;
                member = Expect(TokenKind.Identifier);
            }
            Expect(TokenKind.Assign);
            Emit(EnumOpcode.LoadLocal, parent.Slot);
            var expressionStart = _code.Count;
            var actual = ParseExpression();
            var actualName = _lastExpressionClassName;
            actual = AdaptByteLiteral(member, field.Type, actual, expressionStart);
            RequireAssignable(member, field.Type, actual, field.ClassName, actualName);
            Emit(EnumOpcode.StructSet, field.Index);
            for (var i = parents.Count - 1; i >= 0; i--)
            {
                var ancestor = parents[i];
                Emit(EnumOpcode.StoreLocal, ancestor.Child.Slot);
                Emit(EnumOpcode.LoadLocal, ancestor.Parent.Slot);
                Emit(EnumOpcode.LoadLocal, ancestor.Child.Slot);
                Emit(EnumOpcode.StructSet, ancestor.Field.Index);
            }
            Emit(EnumOpcode.StoreLocal, root.Slot);
            Expect(TokenKind.Semicolon);
        }
    }
}
