using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;
using System.Globalization;
using System.Text;


namespace Aloe.CompilerLib
{
    public sealed class AloeCompileException : Exception
    {
        public AloeCompileException(string message) : base(message) { }
    }


    /// <summary>
    /// Minimal Aloe source compiler.
    ///
    /// Supported subset:
    ///   function name(a: int, b: string): int|bool|string|void { ... }
    ///   function main(args: string[]): int { ... }
    ///   var name = expr;
    ///   let name: int|string|bool = expr;
    ///   name = expr;
    ///   if (boolExpr) { ... } else if (...) { ... } else { ... }
    ///   while (boolExpr) { ... }
    ///   break; / continue;
    ///   print(expr);
    ///   GC.require(); / GC.finish(); in main
    ///   readLine(): string / sleep(ms: int) host intrinsics
    ///   class fields, synchronous constructors, new Type(args), getter-only public properties
    ///   synchronous private/protected instance methods (self calls)
    ///   public async instance methods queued until tick()
    ///   synchronous user-function calls and recursion
    ///   return expr; / return; for void functions, including nested early return
    ///   int/string/bool literals and local reads
    ///   + - * / %
    ///   == != < <= > >=
    ///   not / and / or (short-circuit)
    /// </summary>
    public sealed class AloeCompiler
    {
        public Module Compile(string source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return new Parser(AloeLexer.Parse(source)).CompileProgram();
        }


        private enum ExprType
        {
            Int,
            String,
            Bool,
            Object,
            Void,
        }


        private sealed record LocalInfo(int Slot, ExprType Type, string? ClassName = null);


        private sealed record ParameterSignature(AloeToken Name, ExprType Type, string? ClassName = null);


        private sealed record FunctionSignature(
            string Name,
            int Index,
            IReadOnlyList<ParameterSignature> Parameters,
            ExprType ReturnType,
            int BodyStart,
            int BodyEnd,
            bool IsMain,
            string? DeclaringClass = null,
            bool IsConstructor = false,
            string? ReturnClassName = null,
            bool IsPublicAsync = false,
            bool IsPropertyGetter = false);


        private sealed record FieldSignature(string Name, int Index, ExprType Type, string? ClassName = null);


        private sealed record PropertySignature(
            string Name,
            ExprType Type,
            string? ClassName,
            FunctionSignature Getter);


        private sealed record MethodSignature(
            string Name,
            FunctionSignature Function);


        private sealed record FilterSignature(
            string Name,
            string InputPipeType,
            string OutputPipeType,
            FunctionSignature Function);


        private sealed class ClassSignature
        {
            public ClassSignature(string name) => Name = name;
            public string Name { get; }
            public List<FieldSignature> Fields { get; } = new();
            public List<PropertySignature> Properties { get; } = new();
            public List<MethodSignature> Methods { get; } = new();
            public FunctionSignature? Constructor { get; set; }
        }


        private sealed class LoopContext
        {
            public LoopContext(int blockLevel, int loopLevel)
            {
                BlockLevel = blockLevel;
                LoopLevel = loopLevel;
            }

            // Structured-control nesting levels at the BLOCK / LOOP labels.
            public int BlockLevel { get; }
            public int LoopLevel { get; }
        }


        private sealed class Parser
        {
            private readonly IReadOnlyList<AloeToken> _tokens;
            private readonly List<AloeValue> _constants = new();
            private readonly List<Instruction> _code = new();
            private readonly Stack<Dictionary<string, LocalInfo>> _scopes = new();
            private readonly Stack<LoopContext> _loops = new();
            private readonly List<FunctionSignature> _functions = new();
            private readonly Dictionary<string, FunctionSignature> _functionByName =
                new(StringComparer.Ordinal);
            private readonly HashSet<string> _classNames = new(StringComparer.Ordinal);
            private readonly Dictionary<string, ClassSignature> _classes = new(StringComparer.Ordinal);
            private readonly Dictionary<int, HashSet<int>> _asyncCallEdges = new();
            private readonly Dictionary<string, FilterSignature> _filterByName =
                new(StringComparer.Ordinal);


            private int _position;
            private int _nextLocalSlot;
            private int _controlDepth;
            private FunctionSignature? _currentFunction;
            private string? _lastExpressionClassName;


            public Parser(IReadOnlyList<AloeToken> tokens)
            {
                _tokens = tokens;
            }


            public Module CompileProgram()
            {
                CollectFunctionSignatures();


                var main = _functions.SingleOrDefault(f => f.IsMain);
                if (main == null)
                    throw new AloeCompileException(
                        "Program must declare exactly one function main(args: string[]): int.");


                var functionInfos = new FunctionInfo[_functions.Count];


                foreach (var function in _functions)
                {
                    _position = function.BodyStart;
                    _nextLocalSlot = 0;
                    _controlDepth = 0;
                    _currentFunction = function;
                    _scopes.Clear();
                    _loops.Clear();


                    var entryIp = _code.Count;


                    EnterScope();
                    if (function.DeclaringClass != null)
                        DeclareImplicitLocal("this", ExprType.Object, function.DeclaringClass);
                    foreach (var parameter in function.Parameters)
                        DeclareLocal(parameter.Name, parameter.Type, parameter.ClassName);


                    var bodyCanFallThrough = true;
                    while (_position < function.BodyEnd)
                    {
                        if (Check(TokenKind.EndOfFile))
                            throw Error(Current, $"Unexpected end of file in function '{function.Name}'.");


                        var statementCanFallThrough = ParseStatement();
                        if (bodyCanFallThrough)
                            bodyCanFallThrough = statementCanFallThrough;
                    }


                    if (_position != function.BodyEnd || !Check(TokenKind.RBrace))
                        throw Error(Current, $"Function '{function.Name}' body did not end as expected.");


                    ExitScope();


                    if (function.ReturnType == ExprType.Void)
                    {
                        if (bodyCanFallThrough)
                            Emit(EnumOpcode.Return);
                    }
                    else if (bodyCanFallThrough)
                    {
                        throw Error(
                            _tokens[function.BodyEnd],
                            $"Not all code paths in function '{function.Name}' return {FormatType(function.ReturnType)}.");
                    }


                    functionInfos[function.Index] = new FunctionInfo(
                        name: function.Name,
                        entryIp: entryIp,
                        parameterCount: function.Parameters.Count + (function.DeclaringClass != null ? 1 : 0),
                        localCount: _nextLocalSlot);
                }


                _currentFunction = null;
                ValidateAsyncCallGraph();


                return new Module(
                    _constants,
                    _code,
                    functionInfos,
                    entryPointIndex: main.Index);
            }


            private void CollectFunctionSignatures()
            {
                _functions.Clear();
                _functionByName.Clear();
                _classNames.Clear();
                _classes.Clear();
                _asyncCallEdges.Clear();
                _filterByName.Clear();


                var position = 0;
                var mainCount = 0;


                while (TokenAt(position).Kind != TokenKind.EndOfFile)
                {
                    var topLevelToken = TokenAt(position);

                    if (string.Equals(topLevelToken.Lexeme, "filter", StringComparison.Ordinal))
                    {
                        CollectFilterSignature(ref position);
                        continue;
                    }

                    if (string.Equals(topLevelToken.Lexeme, "class", StringComparison.Ordinal))
                    {
                        CollectClassSignature(ref position);
                        continue;
                    }

                    var functionToken = topLevelToken;
                    if (!string.Equals(functionToken.Lexeme, "function", StringComparison.Ordinal))
                        throw Error(functionToken, "Only top-level class, filter, and function declarations are supported by the current compiler subset.");
                    position++;


                    var nameToken = TokenAt(position++);
                    var isMain = string.Equals(nameToken.Lexeme, "main", StringComparison.Ordinal);


                    if (!isMain && nameToken.Kind != TokenKind.Identifier)
                        throw Error(nameToken, $"Expected function name, found '{nameToken.Lexeme}'.");


                    if (_functionByName.ContainsKey(nameToken.Lexeme))
                        throw Error(nameToken, $"Function '{nameToken.Lexeme}' is already declared.");


                    ExpectAt(ref position, TokenKind.LParen);


                    var parameters = new List<ParameterSignature>();


                    if (isMain)
                    {
                        mainCount++;
                        ExpectLexemeAt(ref position, "args");
                        ExpectAt(ref position, TokenKind.Colon);
                        ExpectLexemeAt(ref position, "string");
                        ExpectAt(ref position, TokenKind.LBracket);
                        ExpectAt(ref position, TokenKind.RBracket);
                    }
                    else if (TokenAt(position).Kind != TokenKind.RParen)
                    {
                        while (true)
                        {
                            var parameterName = TokenAt(position++);
                            if (parameterName.Kind != TokenKind.Identifier)
                                throw Error(parameterName, "Expected parameter name.");


                            ExpectAt(ref position, TokenKind.Colon);
                            var parameterType = ScanType(ref position, allowVoid: false, out var parameterClassName);
                            parameters.Add(new ParameterSignature(parameterName, parameterType, parameterClassName));


                            if (TokenAt(position).Kind != TokenKind.Comma)
                                break;
                            position++;
                        }
                    }


                    ExpectAt(ref position, TokenKind.RParen);
                    ExpectAt(ref position, TokenKind.Colon);
                    var returnType = ScanType(ref position, allowVoid: true, out var returnClassName);


                    if (isMain && returnType != ExprType.Int)
                        throw Error(nameToken, "main must return int.");


                    ExpectAt(ref position, TokenKind.LBrace);
                    var bodyStart = position;
                    var depth = 1;


                    while (depth > 0)
                    {
                        var token = TokenAt(position);
                        if (token.Kind == TokenKind.EndOfFile)
                            throw Error(token, $"Unexpected end of file in function '{nameToken.Lexeme}'.");


                        if (token.Kind == TokenKind.LBrace) depth++;
                        else if (token.Kind == TokenKind.RBrace) depth--;
                        position++;
                    }


                    var bodyEnd = position - 1;
                    var signature = new FunctionSignature(
                        Name: nameToken.Lexeme,
                        Index: _functions.Count,
                        Parameters: parameters,
                        ReturnType: returnType,
                        BodyStart: bodyStart,
                        BodyEnd: bodyEnd,
                        IsMain: isMain,
                        ReturnClassName: returnClassName);


                    _functions.Add(signature);
                    _functionByName.Add(signature.Name, signature);
                }


                if (mainCount != 1)
                    throw new AloeCompileException(
                        $"Program must declare exactly one main function; found {mainCount}.");
            }


            private void CollectFilterSignature(ref int position)
            {
                position++; // filter
                var nameToken = TokenAt(position++);
                if (nameToken.Kind != TokenKind.Identifier)
                    throw Error(nameToken, "Expected filter name.");
                if (_filterByName.ContainsKey(nameToken.Lexeme) || _functionByName.ContainsKey(nameToken.Lexeme))
                    throw Error(nameToken, $"Filter '{nameToken.Lexeme}' is already declared.");

                ExpectAt(ref position, TokenKind.LBrace);
                ExpectLexemeAt(ref position, "in");
                ExpectAt(ref position, TokenKind.Colon);
                var inputType = ScanType(ref position, allowVoid: false, out var inputPipeType);
                if (inputType != ExprType.Object || !IsPipeTypeName(inputPipeType))
                    throw Error(TokenAt(position - 1), "Filter in type must be pipe<T>.");
                ExpectAt(ref position, TokenKind.Semicolon);

                ExpectLexemeAt(ref position, "out");
                ExpectAt(ref position, TokenKind.Colon);
                var outputType = ScanType(ref position, allowVoid: false, out var outputPipeType);
                if (outputType != ExprType.Object || !IsPipeTypeName(outputPipeType))
                    throw Error(TokenAt(position - 1), "Filter out type must be pipe<T>.");
                ExpectAt(ref position, TokenKind.Semicolon);

                ExpectLexemeAt(ref position, "bound");
                ExpectAt(ref position, TokenKind.LParen);
                var inputName = TokenAt(position++);
                if (inputName.Kind != TokenKind.Identifier)
                    throw Error(inputName, "Expected bound input parameter name.");
                ExpectAt(ref position, TokenKind.Comma);
                var outputName = TokenAt(position++);
                if (outputName.Kind != TokenKind.Identifier)
                    throw Error(outputName, "Expected bound output parameter name.");
                ExpectAt(ref position, TokenKind.RParen);
                ExpectAt(ref position, TokenKind.LBrace);
                var bodyStart = position;
                var depth = 1;
                while (depth > 0)
                {
                    var token = TokenAt(position);
                    if (token.Kind == TokenKind.EndOfFile)
                        throw Error(token, $"Unexpected end of file in filter '{nameToken.Lexeme}'.");
                    if (token.Kind == TokenKind.LBrace) depth++;
                    else if (token.Kind == TokenKind.RBrace) depth--;
                    position++;
                }
                var bodyEnd = position - 1;
                ExpectAt(ref position, TokenKind.RBrace); // filter }

                var parameters = new[]
                {
                    new ParameterSignature(inputName, ExprType.Object, inputPipeType),
                    new ParameterSignature(outputName, ExprType.Object, outputPipeType),
                };
                var function = new FunctionSignature(
                    Name: $"$filter${nameToken.Lexeme}",
                    Index: _functions.Count,
                    Parameters: parameters,
                    ReturnType: ExprType.Void,
                    BodyStart: bodyStart,
                    BodyEnd: bodyEnd,
                    IsMain: false);
                _functions.Add(function);
                _filterByName.Add(nameToken.Lexeme, new FilterSignature(
                    nameToken.Lexeme, inputPipeType!, outputPipeType!, function));
            }

            private void CollectClassSignature(ref int position)
            {
                position++; // class
                var classNameToken = TokenAt(position++);
                if (classNameToken.Kind != TokenKind.Identifier)
                    throw Error(classNameToken, "Expected class name.");
                if (!_classNames.Add(classNameToken.Lexeme))
                    throw Error(classNameToken, $"Class '{classNameToken.Lexeme}' is already declared.");

                var classInfo = new ClassSignature(classNameToken.Lexeme);
                _classes.Add(classInfo.Name, classInfo);
                ExpectAt(ref position, TokenKind.LBrace);

                while (TokenAt(position).Kind != TokenKind.RBrace)
                {
                    var token = TokenAt(position);
                    if (token.Kind == TokenKind.EndOfFile)
                        throw Error(token, $"Unexpected end of file in class '{classInfo.Name}'.");

                    var sawPublic = false;
                    var sawAsync = false;
                    while (token.Lexeme is "private" or "protected" or "readonly" or "public" or "async")
                    {
                        if (token.Lexeme == "public")
                            sawPublic = true;
                        if (token.Lexeme == "async")
                            sawAsync = true;
                        position++;
                        token = TokenAt(position);
                    }

                    if (string.Equals(token.Lexeme, "field", StringComparison.Ordinal))
                    {
                        if (sawPublic)
                            throw Error(token, "Class fields cannot be public; expose state through a public property.");
                        position++;
                        var fieldName = TokenAt(position++);
                        if (fieldName.Kind != TokenKind.Identifier)
                            throw Error(fieldName, "Expected field name.");
                        if (classInfo.Fields.Any(x => x.Name == fieldName.Lexeme))
                            throw Error(fieldName, $"Field '{fieldName.Lexeme}' is already declared in class '{classInfo.Name}'.");

                        ExpectAt(ref position, TokenKind.Colon);
                        var fieldType = ScanType(ref position, allowVoid: false, out var fieldClassName);
                        if (TokenAt(position).Kind == TokenKind.Assign)
                            throw Error(TokenAt(position), "Field initializers are not supported yet; initialize the field in construct(...).");
                        ExpectAt(ref position, TokenKind.Semicolon);
                        classInfo.Fields.Add(new FieldSignature(fieldName.Lexeme, classInfo.Fields.Count, fieldType, fieldClassName));
                        continue;
                    }

                    if (string.Equals(token.Lexeme, "construct", StringComparison.Ordinal))
                    {
                        if (classInfo.Constructor != null)
                            throw Error(token, $"Class '{classInfo.Name}' already declares a constructor.");
                        position++;
                        ExpectAt(ref position, TokenKind.LParen);
                        var parameters = new List<ParameterSignature>();
                        if (TokenAt(position).Kind != TokenKind.RParen)
                        {
                            while (true)
                            {
                                var parameterName = TokenAt(position++);
                                if (parameterName.Kind != TokenKind.Identifier)
                                    throw Error(parameterName, "Expected constructor parameter name.");
                                ExpectAt(ref position, TokenKind.Colon);
                                var parameterType = ScanType(ref position, allowVoid: false, out var parameterClassName);
                                parameters.Add(new ParameterSignature(parameterName, parameterType, parameterClassName));
                                if (TokenAt(position).Kind != TokenKind.Comma)
                                    break;
                                position++;
                            }
                        }
                        ExpectAt(ref position, TokenKind.RParen);
                        ExpectAt(ref position, TokenKind.LBrace);
                        var bodyStart = position;
                        var depth = 1;
                        while (depth > 0)
                        {
                            var bodyToken = TokenAt(position);
                            if (bodyToken.Kind == TokenKind.EndOfFile)
                                throw Error(bodyToken, $"Unexpected end of file in constructor for '{classInfo.Name}'.");
                            if (bodyToken.Kind == TokenKind.LBrace) depth++;
                            else if (bodyToken.Kind == TokenKind.RBrace) depth--;
                            position++;
                        }
                        var constructor = new FunctionSignature(
                            Name: $"{classInfo.Name}.construct",
                            Index: _functions.Count,
                            Parameters: parameters,
                            ReturnType: ExprType.Void,
                            BodyStart: bodyStart,
                            BodyEnd: position - 1,
                            IsMain: false,
                            DeclaringClass: classInfo.Name,
                            IsConstructor: true);
                        _functions.Add(constructor);
                        classInfo.Constructor = constructor;
                        continue;
                    }

                    if (string.Equals(token.Lexeme, "property", StringComparison.Ordinal))
                    {
                        if (sawAsync)
                            throw Error(token, "Instance properties cannot be async.");
                        if (!sawPublic)
                            throw Error(token, "Only public instance properties are supported by the current class subset.");
                        position++;
                        var propertyName = TokenAt(position++);
                        if (propertyName.Kind != TokenKind.Identifier)
                            throw Error(propertyName, "Expected property name.");
                        if (classInfo.Properties.Any(x => x.Name == propertyName.Lexeme))
                            throw Error(propertyName, $"Property '{propertyName.Lexeme}' is already declared in class '{classInfo.Name}'.");

                        ExpectAt(ref position, TokenKind.Colon);
                        var propertyType = ScanType(ref position, allowVoid: false, out var propertyClassName);
                        ExpectAt(ref position, TokenKind.LBrace);
                        var getToken = TokenAt(position++);
                        if (!string.Equals(getToken.Lexeme, "get", StringComparison.Ordinal))
                            throw Error(getToken, "Public instance property must declare a getter.");
                        ExpectAt(ref position, TokenKind.LBrace);
                        var bodyStart = position;
                        var depth = 1;
                        while (depth > 0)
                        {
                            var bodyToken = TokenAt(position);
                            if (bodyToken.Kind == TokenKind.EndOfFile)
                                throw Error(bodyToken, $"Unexpected end of file in property '{classInfo.Name}.{propertyName.Lexeme}'.");
                            if (bodyToken.Kind == TokenKind.LBrace) depth++;
                            else if (bodyToken.Kind == TokenKind.RBrace) depth--;
                            position++;
                        }
                        if (TokenAt(position).Kind != TokenKind.RBrace)
                            throw Error(TokenAt(position), "Instance property setters are not supported; close the property after get { ... }.");
                        position++; // property }

                        var getter = new FunctionSignature(
                            Name: $"{classInfo.Name}.get.{propertyName.Lexeme}",
                            Index: _functions.Count,
                            Parameters: Array.Empty<ParameterSignature>(),
                            ReturnType: propertyType,
                            BodyStart: bodyStart,
                            BodyEnd: position - 2,
                            IsMain: false,
                            DeclaringClass: classInfo.Name,
                            ReturnClassName: propertyClassName,
                            IsPropertyGetter: true);
                        _functions.Add(getter);
                        classInfo.Properties.Add(new PropertySignature(
                            propertyName.Lexeme, propertyType, propertyClassName, getter));
                        continue;
                    }

                    if (string.Equals(token.Lexeme, "method", StringComparison.Ordinal))
                    {
                        if (sawPublic && !sawAsync)
                            throw Error(token, "Public instance methods must be declared async.");
                        if (sawAsync && !sawPublic)
                            throw Error(token, "Only public async instance methods are supported; private/protected methods are synchronous.");
                        position++;
                        var methodName = TokenAt(position++);
                        if (methodName.Kind != TokenKind.Identifier)
                            throw Error(methodName, "Expected method name.");
                        if (classInfo.Methods.Any(x => x.Name == methodName.Lexeme))
                            throw Error(methodName, $"Method '{methodName.Lexeme}' is already declared in class '{classInfo.Name}'.");

                        ExpectAt(ref position, TokenKind.LParen);
                        var parameters = new List<ParameterSignature>();
                        if (TokenAt(position).Kind != TokenKind.RParen)
                        {
                            while (true)
                            {
                                var parameterName = TokenAt(position++);
                                if (parameterName.Kind != TokenKind.Identifier)
                                    throw Error(parameterName, "Expected method parameter name.");
                                ExpectAt(ref position, TokenKind.Colon);
                                var parameterType = ScanType(ref position, allowVoid: false, out var parameterClassName);
                                parameters.Add(new ParameterSignature(parameterName, parameterType, parameterClassName));
                                if (TokenAt(position).Kind != TokenKind.Comma)
                                    break;
                                position++;
                            }
                        }
                        ExpectAt(ref position, TokenKind.RParen);
                        ExpectAt(ref position, TokenKind.Colon);
                        var returnType = ScanType(ref position, allowVoid: true, out var returnClassName);
                        if (sawPublic && sawAsync && returnType != ExprType.Void)
                            throw Error(methodName, "public async instance methods must return void.");
                        ExpectAt(ref position, TokenKind.LBrace);
                        var bodyStart = position;
                        var depth = 1;
                        while (depth > 0)
                        {
                            var bodyToken = TokenAt(position);
                            if (bodyToken.Kind == TokenKind.EndOfFile)
                                throw Error(bodyToken, $"Unexpected end of file in method '{classInfo.Name}.{methodName.Lexeme}'.");
                            if (bodyToken.Kind == TokenKind.LBrace) depth++;
                            else if (bodyToken.Kind == TokenKind.RBrace) depth--;
                            position++;
                        }

                        var methodFunction = new FunctionSignature(
                            Name: $"{classInfo.Name}.{methodName.Lexeme}",
                            Index: _functions.Count,
                            Parameters: parameters,
                            ReturnType: returnType,
                            BodyStart: bodyStart,
                            BodyEnd: position - 1,
                            IsMain: false,
                            DeclaringClass: classInfo.Name,
                            ReturnClassName: returnClassName,
                            IsPublicAsync: sawPublic && sawAsync);
                        _functions.Add(methodFunction);
                        classInfo.Methods.Add(new MethodSignature(methodName.Lexeme, methodFunction));
                        continue;
                    }

                    // Keep accepting declaration forms not compiled by this milestone.
                    // Skip one class member without treating its nested braces as class end.
                    while (TokenAt(position).Kind != TokenKind.EndOfFile &&
                           TokenAt(position).Kind != TokenKind.Semicolon &&
                           TokenAt(position).Kind != TokenKind.LBrace &&
                           TokenAt(position).Kind != TokenKind.RBrace)
                        position++;
                    if (TokenAt(position).Kind == TokenKind.Semicolon)
                    {
                        position++;
                        continue;
                    }
                    if (TokenAt(position).Kind == TokenKind.LBrace)
                    {
                        var depth = 1;
                        position++;
                        while (depth > 0)
                        {
                            var memberToken = TokenAt(position);
                            if (memberToken.Kind == TokenKind.EndOfFile)
                                throw Error(memberToken, $"Unexpected end of file in class '{classInfo.Name}'.");
                            if (memberToken.Kind == TokenKind.LBrace) depth++;
                            else if (memberToken.Kind == TokenKind.RBrace) depth--;
                            position++;
                        }
                        continue;
                    }
                    throw Error(TokenAt(position), $"Unsupported class member in '{classInfo.Name}'.");
                }

                position++; // class }
            }


            private ExprType ScanType(ref int position, bool allowVoid)
                => ScanType(ref position, allowVoid, out _);


            private ExprType ScanType(ref int position, bool allowVoid, out string? className)
            {
                var token = TokenAt(position++);
                className = null;
                switch (token.Lexeme)
                {
                    case "int": return ExprType.Int;
                    case "string": return ExprType.String;
                    case "bool":
                    case "boolean": return ExprType.Bool;
                    case "object": return ExprType.Object;
                    case "pipe":
                        ExpectAt(ref position, TokenKind.Less);
                        var elementToken = TokenAt(position++);
                        var elementType = elementToken.Lexeme switch
                        {
                            "int" => ExprType.Int,
                            "string" => ExprType.String,
                            "bool" or "boolean" => ExprType.Bool,
                            _ => throw Error(elementToken, "pipe<T> currently supports int, string, and bool elements.")
                        };
                        ExpectAt(ref position, TokenKind.Greater);
                        className = $"pipe<{FormatType(elementType)}>";
                        return ExprType.Object;
                    case "void" when allowVoid: return ExprType.Void;
                    default:
                        if (_classNames.Contains(token.Lexeme))
                        {
                            className = token.Lexeme;
                            return ExprType.Object;
                        }
                        throw Error(token, $"Type '{token.Lexeme}' is not supported by the current compiler subset.");
                }
            }


            private AloeToken TokenAt(int position)
                => _tokens[Math.Min(position, _tokens.Count - 1)];


            private AloeToken ExpectAt(ref int position, TokenKind kind)
            {
                var token = TokenAt(position);
                if (token.Kind != kind)
                    throw Error(
                        token,
                        $"Expected {kind}, found '{token.Lexeme}' ({token.Kind}).");
                position++;
                return token;
            }


            private AloeToken ExpectLexemeAt(ref int position, string lexeme)
            {
                var token = TokenAt(position);
                if (!string.Equals(token.Lexeme, lexeme, StringComparison.Ordinal))
                    throw Error(token, $"Expected '{lexeme}', found '{token.Lexeme}'.");
                position++;
                return token;
            }


            private bool ParseStatement()
            {
                if (Current.Lexeme == "this" && Peek(1).Kind == TokenKind.Dot)
                {
                    ParseThisFieldAssignment();
                    return true;
                }

                if (Current.Lexeme == "var")
                {
                    ParseVarDeclaration();
                    return true;
                }


                if (Current.Lexeme == "let")
                {
                    ParseLetDeclaration();
                    return true;
                }


                if (Current.Lexeme == "if")
                    return ParseIf();


                if (Current.Lexeme == "foreach")
                {
                    ParseForeach();
                    return true;
                }


                if (Current.Lexeme == "while")
                {
                    ParseWhile();
                    return true;
                }


                if (Current.Lexeme == "break")
                {
                    ParseBreak();
                    return false;
                }


                if (Current.Lexeme == "continue")
                {
                    ParseContinue();
                    return false;
                }


                if (Current.Lexeme == "print")
                {
                    ParsePrint();
                    return true;
                }


                if (Current.Lexeme == "return")
                {
                    ParseReturn();
                    return false;
                }


                if (Current.Lexeme == "GC" && Peek(1).Kind == TokenKind.Dot)
                {
                    ParseGcStaticMemberStatement();
                    return true;
                }


                if (Peek(1).Kind == TokenKind.LParen &&
                    HostIntrinsicCatalog.TryGet(Current.Lexeme, out var hostStatement) &&
                    hostStatement.ReturnType == AloeHostValueType.Void)
                {
                    ParseHostIntrinsic(hostStatement, requireValue: false);
                    Expect(TokenKind.Semicolon);
                    return true;
                }


                if (Current.Lexeme == "tick" && Peek(1).Kind == TokenKind.LParen)
                {
                    ParseTickIntrinsic();
                    return true;
                }


                if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Pipe)
                {
                    ParsePipelineStatement();
                    return true;
                }


                if (Current.Kind == TokenKind.Identifier &&
                    Peek(1).Kind == TokenKind.Dot &&
                    Peek(2).Kind == TokenKind.Identifier &&
                    Peek(3).Kind == TokenKind.LParen &&
                    TryResolveLocal(Current.Lexeme, out var memberTarget) &&
                    IsPipeTypeName(memberTarget.ClassName))
                {
                    ParsePipeMemberStatement();
                    return true;
                }


                if (Current.Kind == TokenKind.Identifier &&
                    Peek(1).Kind == TokenKind.Dot &&
                    Peek(2).Kind == TokenKind.Identifier &&
                    Peek(3).Kind == TokenKind.LParen)
                {
                    ParseInstanceAsyncMethodCallStatement();
                    return true;
                }


                if (Current.Kind == TokenKind.Identifier &&
                    Peek(1).Kind == TokenKind.LParen)
                {
                    ParseFunctionCallStatement();
                    return true;
                }


                if (Current.Kind == TokenKind.Identifier &&
                    Peek(1).Kind == TokenKind.Assign)
                {
                    ParseAssignment();
                    return true;
                }


                throw Error(
                    Current,
                    $"Unsupported statement '{Current.Lexeme}' in the current compiler subset.");
            }


            private void ParseVarDeclaration()
            {
                Advance(); // var
                var name = Expect(TokenKind.Identifier);
                Expect(TokenKind.Assign);
                var type = ParseExpression();
                var className = _lastExpressionClassName;
                Expect(TokenKind.Semicolon);


                var local = DeclareLocal(name, type, className);
                Emit(EnumOpcode.StoreLocal, local.Slot);
            }


            private void ParseLetDeclaration()
            {
                Advance(); // let
                var name = Expect(TokenKind.Identifier);
                Expect(TokenKind.Colon);
                var declaredType = ParseTypeName(out var declaredClassName);
                Expect(TokenKind.Assign);
                var actualType = ParseExpression();
                var actualClassName = _lastExpressionClassName;
                RequireAssignable(name, declaredType, actualType, declaredClassName, actualClassName);
                Expect(TokenKind.Semicolon);


                var local = DeclareLocal(name, declaredType, declaredClassName);
                Emit(EnumOpcode.StoreLocal, local.Slot);
            }


            private void ParseAssignment()
            {
                var name = Expect(TokenKind.Identifier);
                var local = ResolveLocal(name);
                Expect(TokenKind.Assign);
                var actualType = ParseExpression();
                var actualClassName = _lastExpressionClassName;
                RequireAssignable(name, local.Type, actualType, local.ClassName, actualClassName);
                Expect(TokenKind.Semicolon);
                Emit(EnumOpcode.StoreLocal, local.Slot);
            }


            private void ParseThisFieldAssignment()
            {
                var thisToken = Advance(); // this
                var function = _currentFunction;
                if (function?.DeclaringClass == null)
                    throw Error(thisToken, "'this' is only available in class instance code.");

                Expect(TokenKind.Dot);
                var fieldToken = Expect(TokenKind.Identifier);
                var field = ResolveField(function.DeclaringClass, fieldToken);
                Expect(TokenKind.Assign);

                Emit(EnumOpcode.LoadLocal, 0);
                EmitIntConstant(field.Index);
                var actualType = ParseExpression();
                var actualClassName = _lastExpressionClassName;
                RequireAssignable(fieldToken, field.Type, actualType, field.ClassName, actualClassName);
                Expect(TokenKind.Semicolon);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.ObjectFieldSet);
            }


            private FieldSignature ResolveField(string className, AloeToken fieldToken)
            {
                if (!_classes.TryGetValue(className, out var classInfo))
                    throw Error(fieldToken, $"Unknown class '{className}'.");
                var field = classInfo.Fields.FirstOrDefault(x => x.Name == fieldToken.Lexeme);
                return field ?? throw Error(fieldToken, $"Unknown field '{fieldToken.Lexeme}' in class '{className}'.");
            }


            private bool ParseIf()
            {
                Advance(); // if
                Expect(TokenKind.LParen);

                var conditionType = ParseExpression();
                if (conditionType != ExprType.Bool)
                    throw Error(Previous, "if condition must be bool.");

                Expect(TokenKind.RParen);

                Emit(EnumOpcode.If);
                _controlDepth++;
                var thenCanFallThrough = ParseBlock();

                if (Current.Lexeme != "else")
                {
                    Emit(EnumOpcode.End);
                    _controlDepth--;
                    return true;
                }

                Emit(EnumOpcode.Else);
                Advance(); // else

                var elseCanFallThrough = Current.Lexeme == "if"
                    ? ParseIf()
                    : ParseBlock();

                Emit(EnumOpcode.End);
                _controlDepth--;
                return thenCanFallThrough || elseCanFallThrough;
            }


            private void ParseForeach()
            {
                Advance(); // foreach
                Expect(TokenKind.LParen);
                var itemName = Expect(TokenKind.Identifier);
                ExpectLexeme("in");
                var sourceName = Expect(TokenKind.Identifier);
                var source = ResolveLocal(sourceName);
                if (!TryGetPipeElementType(source.ClassName, out var elementType))
                    throw Error(sourceName, $"foreach source '{sourceName.Lexeme}' must be pipe<T>.");
                Expect(TokenKind.RParen);

                Emit(EnumOpcode.Block);
                _controlDepth++;
                var blockLevel = _controlDepth;

                Emit(EnumOpcode.Loop);
                _controlDepth++;
                var loopLevel = _controlDepth;

                Expect(TokenKind.LBrace);
                EnterScope();
                var itemLocal = DeclareLocal(itemName, elementType);
                var hasValueSlot = _nextLocalSlot++;

                Emit(EnumOpcode.LoadLocal, source.Slot);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.PipeTryRead);
                Emit(EnumOpcode.StoreLocal, hasValueSlot);
                Emit(EnumOpcode.StoreLocal, itemLocal.Slot);
                Emit(EnumOpcode.LoadLocal, hasValueSlot);
                EmitBoolConstant(false);
                Emit(EnumOpcode.CmpEq);
                Emit(EnumOpcode.BrIf, _controlDepth - blockLevel);

                var loop = new LoopContext(blockLevel, loopLevel);
                _loops.Push(loop);
                while (!Check(TokenKind.RBrace))
                {
                    if (Check(TokenKind.EndOfFile))
                        throw Error(Current, "Unexpected end of file in foreach block.");
                    ParseStatement();
                }
                _loops.Pop();
                Expect(TokenKind.RBrace);
                ExitScope();

                Emit(EnumOpcode.Br, _controlDepth - loopLevel);
                Emit(EnumOpcode.End); // LOOP
                _controlDepth--;
                Emit(EnumOpcode.End); // BLOCK
                _controlDepth--;
            }


            private void ParsePipelineStatement()
            {
                var sourceToken = Expect(TokenKind.Identifier);
                var source = ResolveLocal(sourceToken);
                if (!IsPipeTypeName(source.ClassName))
                    throw Error(sourceToken, $"Pipeline source '{sourceToken.Lexeme}' must be pipe<T>.");

                var filters = new List<FilterSignature>();
                while (Check(TokenKind.Pipe))
                {
                    Advance(); // |
                    if (string.Equals(Current.Lexeme, "filter", StringComparison.Ordinal))
                    {
                        Advance();
                        Expect(TokenKind.LParen);
                        var filterName = Expect(TokenKind.Identifier);
                        Expect(TokenKind.RParen);
                        if (!_filterByName.TryGetValue(filterName.Lexeme, out var filter))
                            throw Error(filterName, $"Unknown filter '{filterName.Lexeme}'.");
                        filters.Add(filter);
                        continue;
                    }

                    var destinationToken = Expect(TokenKind.Identifier);
                    var destination = ResolveLocal(destinationToken);
                    if (!IsPipeTypeName(destination.ClassName))
                        throw Error(destinationToken, $"Pipeline destination '{destinationToken.Lexeme}' must be pipe<T>.");
                    Expect(TokenKind.Semicolon);
                    if (filters.Count == 0)
                        throw Error(destinationToken, "Pipeline currently requires at least one filter stage.");

                    var currentType = source.ClassName!;
                    var currentSlot = source.Slot;
                    for (var i = 0; i < filters.Count; i++)
                    {
                        var filter = filters[i];
                        if (!string.Equals(currentType, filter.InputPipeType, StringComparison.Ordinal))
                            throw Error(sourceToken, $"Filter '{filter.Name}' expects {filter.InputPipeType}, found {currentType}.");

                        int outputSlot;
                        if (i == filters.Count - 1)
                        {
                            if (!string.Equals(destination.ClassName, filter.OutputPipeType, StringComparison.Ordinal))
                                throw Error(destinationToken, $"Pipeline destination expects {filter.OutputPipeType}, found {destination.ClassName}.");
                            outputSlot = destination.Slot;
                        }
                        else
                        {
                            outputSlot = _nextLocalSlot++;
                            Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(filter.OutputPipeType)));
                            EmitIntConstant(-1); // unbounded intermediate pipe
                            Emit(EnumOpcode.Syscall, (int)EnumSyscall.PipeCreate);
                            Emit(EnumOpcode.StoreLocal, outputSlot);
                        }

                        Emit(EnumOpcode.LoadLocal, currentSlot);
                        Emit(EnumOpcode.LoadLocal, outputSlot);
                        EmitIntConstant(filter.Function.Index);
                        Emit(EnumOpcode.Syscall, (int)EnumSyscall.PipeBindFilter);
                        currentSlot = outputSlot;
                        currentType = filter.OutputPipeType;
                    }
                    return;
                }

                throw Error(Current, "Pipeline must end with '| destination;'.");
            }

            private void ParsePipeMemberStatement()
            {
                var targetToken = Expect(TokenKind.Identifier);
                var target = ResolveLocal(targetToken);
                if (!TryGetPipeElementType(target.ClassName, out var elementType))
                    throw Error(targetToken, $"'{targetToken.Lexeme}' is not pipe<T>.");

                Expect(TokenKind.Dot);
                var member = Expect(TokenKind.Identifier);
                Expect(TokenKind.LParen);
                Emit(EnumOpcode.LoadLocal, target.Slot);

                if (string.Equals(member.Lexeme, "write", StringComparison.Ordinal))
                {
                    var actualType = ParseExpression();
                    if (actualType != elementType)
                        throw Error(member, $"pipe<{FormatType(elementType)}>.write expects {FormatType(elementType)}, found {FormatType(actualType)}.");
                    Expect(TokenKind.RParen);
                    Expect(TokenKind.Semicolon);
                    Emit(EnumOpcode.Syscall, (int)EnumSyscall.PipeWrite);
                    return;
                }

                if (string.Equals(member.Lexeme, "close", StringComparison.Ordinal))
                {
                    if (!Check(TokenKind.RParen))
                        throw Error(Current, "pipe.close() does not accept arguments.");
                    Expect(TokenKind.RParen);
                    Expect(TokenKind.Semicolon);
                    Emit(EnumOpcode.Syscall, (int)EnumSyscall.PipeClose);
                    return;
                }

                throw Error(member, $"Unknown pipe member '{member.Lexeme}'. Expected write or close.");
            }


            private void ParseWhile()
            {
                Advance(); // while
                Expect(TokenKind.LParen);

                // while (cond) body
                //   => BLOCK; LOOP; cond; false == cond; BR_IF break; body; BR continue; END; END
                Emit(EnumOpcode.Block);
                _controlDepth++;
                var blockLevel = _controlDepth;

                Emit(EnumOpcode.Loop);
                _controlDepth++;
                var loopLevel = _controlDepth;

                var conditionType = ParseExpression();
                if (conditionType != ExprType.Bool)
                    throw Error(Previous, "while condition must be bool.");

                Expect(TokenKind.RParen);

                // BR_IF branches on true. Invert the while condition using bool equality.
                EmitBoolConstant(false);
                Emit(EnumOpcode.CmpEq);
                Emit(EnumOpcode.BrIf, _controlDepth - blockLevel);

                var loop = new LoopContext(blockLevel, loopLevel);
                _loops.Push(loop);
                ParseBlock();
                _loops.Pop();

                Emit(EnumOpcode.Br, _controlDepth - loopLevel);

                Emit(EnumOpcode.End); // LOOP
                _controlDepth--;
                Emit(EnumOpcode.End); // BLOCK
                _controlDepth--;
            }


            private void ParseBreak()
            {
                var token = Advance(); // break
                if (_loops.Count == 0)
                    throw Error(token, "break can be used only inside while.");

                Expect(TokenKind.Semicolon);
                var loop = _loops.Peek();
                Emit(EnumOpcode.Br, _controlDepth - loop.BlockLevel);
            }


            private void ParseContinue()
            {
                var token = Advance(); // continue
                if (_loops.Count == 0)
                    throw Error(token, "continue can be used only inside while.");

                Expect(TokenKind.Semicolon);
                var loop = _loops.Peek();
                Emit(EnumOpcode.Br, _controlDepth - loop.LoopLevel);
            }


            private bool ParseBlock()
            {
                Expect(TokenKind.LBrace);
                EnterScope();


                var canFallThrough = true;
                while (!Check(TokenKind.RBrace))
                {
                    if (Check(TokenKind.EndOfFile))
                        throw Error(Current, "Unexpected end of file in block.");


                    var statementCanFallThrough = ParseStatement();
                    if (canFallThrough)
                        canFallThrough = statementCanFallThrough;
                }


                ExitScope();
                Expect(TokenKind.RBrace);
                return canFallThrough;
            }


            private void ParsePrint()
            {
                Advance(); // print
                Expect(TokenKind.LParen);
                ParseExpression();
                Expect(TokenKind.RParen);
                Expect(TokenKind.Semicolon);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.Print);
            }


            private ExprType ParseHostIntrinsic(AloeHostIntrinsic intrinsic, bool requireValue)
            {
                var name = Advance();
                if (!string.Equals(name.Lexeme, intrinsic.Name, StringComparison.Ordinal))
                    throw Error(name, $"Expected host intrinsic '{intrinsic.Name}'.");

                if (requireValue && intrinsic.ReturnType == AloeHostValueType.Void)
                    throw Error(name, $"Void host intrinsic '{intrinsic.Name}' cannot be used as a value.");

                Expect(TokenKind.LParen);
                for (var i = 0; i < intrinsic.ParameterTypes.Count; i++)
                {
                    if (i > 0)
                        Expect(TokenKind.Comma);
                    if (Check(TokenKind.RParen))
                        throw Error(Current, $"Host intrinsic '{intrinsic.Name}' expects {intrinsic.ParameterTypes.Count} argument(s).");

                    var actualType = ParseExpression();
                    var expectedType = MapHostType(intrinsic.ParameterTypes[i]);
                    if (actualType != expectedType)
                    {
                        throw Error(
                            Previous,
                            $"Host intrinsic '{intrinsic.Name}' argument {i + 1} expects {FormatType(expectedType)}, found {FormatType(actualType)}.");
                    }
                }

                if (!Check(TokenKind.RParen))
                    throw Error(Current, $"Host intrinsic '{intrinsic.Name}' received too many arguments.");
                Expect(TokenKind.RParen);
                Emit(EnumOpcode.Syscall, (int)intrinsic.Syscall);
                _lastExpressionClassName = null;
                return MapHostType(intrinsic.ReturnType);
            }

            private static ExprType MapHostType(AloeHostValueType type)
                => type switch
                {
                    AloeHostValueType.Int => ExprType.Int,
                    AloeHostValueType.String => ExprType.String,
                    AloeHostValueType.Bool => ExprType.Bool,
                    AloeHostValueType.Void => ExprType.Void,
                    _ => throw new AloeCompileException($"Unsupported host intrinsic value type '{type}'.")
                };


            private void ParseTickIntrinsic()
            {
                var tickToken = Advance(); // tick
                if (_currentFunction?.IsMain != true)
                    throw Error(tickToken, "tick() is only callable from main.");

                Expect(TokenKind.LParen);
                if (!Check(TokenKind.RParen))
                    throw Error(Current, "tick() does not accept arguments.");
                Expect(TokenKind.RParen);
                Expect(TokenKind.Semicolon);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.Tick);
            }


            private void ParseGcStaticMemberStatement()
            {
                var gcToken = Advance(); // GC
                if (_currentFunction?.IsMain != true)
                    throw Error(gcToken, "GC runtime controls are only available from main.");

                Expect(TokenKind.Dot);
                var member = Current;
                if (member.Kind != TokenKind.Identifier && member.Kind != TokenKind.Keyword)
                    throw Error(member, "Expected GC member name.");
                Advance();

                if (string.Equals(member.Lexeme, "debug", StringComparison.Ordinal))
                {
                    Expect(TokenKind.Assign);
                    var valueType = ParseExpression();
                    if (valueType != ExprType.Bool)
                        throw Error(member, "GC.debug must be assigned a bool value.");
                    Expect(TokenKind.Semicolon);
                    Emit(EnumOpcode.Syscall, (int)EnumSyscall.GCSetDebug);
                    return;
                }

                Expect(TokenKind.LParen);
                Expect(TokenKind.RParen);
                Expect(TokenKind.Semicolon);

                switch (member.Lexeme)
                {
                    case "require":
                        Emit(EnumOpcode.Syscall, (int)EnumSyscall.GCRequire);
                        break;
                    case "finish":
                        Emit(EnumOpcode.Syscall, (int)EnumSyscall.GCFinish);
                        break;
                    default:
                        throw Error(member, $"Unknown GC member '{member.Lexeme}'. Expected debug, require, or finish.");
                }
            }


            private ExprType ParseGcStaticPropertyRead()
            {
                var gcToken = Advance(); // GC
                if (_currentFunction?.IsMain != true)
                    throw Error(gcToken, "GC runtime controls are only available from main.");

                Expect(TokenKind.Dot);
                var member = Current;
                if (member.Kind != TokenKind.Identifier && member.Kind != TokenKind.Keyword)
                    throw Error(member, "Expected GC static property name.");
                Advance();

                if (!string.Equals(member.Lexeme, "debug", StringComparison.Ordinal))
                    throw Error(member, $"Unknown readable GC static property '{member.Lexeme}'.");

                Emit(EnumOpcode.Syscall, (int)EnumSyscall.GCGetDebug);
                _lastExpressionClassName = null;
                return ExprType.Bool;
            }


            private void ParseReturn()
            {
                var function = _currentFunction
                    ?? throw new AloeCompileException("return outside function.");


                var returnToken = Advance(); // return


                if (function.ReturnType == ExprType.Void)
                {
                    if (!Check(TokenKind.Semicolon))
                        throw Error(Current, $"Void function '{function.Name}' must use 'return;'.");
                }
                else
                {
                    var type = ParseExpression();
                    var className = _lastExpressionClassName;
                    RequireAssignable(returnToken, function.ReturnType, type, function.ReturnClassName, className);
                }


                Expect(TokenKind.Semicolon);
                Emit(EnumOpcode.Return);
            }


            private ExprType ParseExpression()
            {
                _lastExpressionClassName = null;
                return ParseLogicalOr();
            }


            private ExprType ParseLogicalOr()
            {
                var type = ParseLogicalAnd();

                while (Current.Lexeme == "or")
                {
                    var op = Advance();
                    RequireBool(op, type);

                    // Result-less IF blocks preserve short-circuiting through a hidden local.
                    var tempSlot = _nextLocalSlot++;
                    Emit(EnumOpcode.If);
                    _controlDepth++;

                    EmitBoolConstant(true);
                    Emit(EnumOpcode.StoreLocal, tempSlot);

                    Emit(EnumOpcode.Else);
                    var right = ParseLogicalAnd();
                    RequireBool(op, right);
                    Emit(EnumOpcode.StoreLocal, tempSlot);

                    Emit(EnumOpcode.End);
                    _controlDepth--;
                    Emit(EnumOpcode.LoadLocal, tempSlot);
                    type = ExprType.Bool;
                    _lastExpressionClassName = null;
                }

                return type;
            }


            private ExprType ParseLogicalAnd()
            {
                var type = ParseComparison();

                while (Current.Lexeme == "and")
                {
                    var op = Advance();
                    RequireBool(op, type);

                    var tempSlot = _nextLocalSlot++;
                    Emit(EnumOpcode.If);
                    _controlDepth++;

                    var right = ParseComparison();
                    RequireBool(op, right);
                    Emit(EnumOpcode.StoreLocal, tempSlot);

                    Emit(EnumOpcode.Else);
                    EmitBoolConstant(false);
                    Emit(EnumOpcode.StoreLocal, tempSlot);

                    Emit(EnumOpcode.End);
                    _controlDepth--;
                    Emit(EnumOpcode.LoadLocal, tempSlot);
                    type = ExprType.Bool;
                    _lastExpressionClassName = null;
                }

                return type;
            }


            private ExprType ParseComparison()
            {
                var leftType = ParseAdditive();


                if (!IsComparison(Current.Kind))
                    return leftType;


                var op = Advance();
                var rightType = ParseAdditive();


                switch (op.Kind)
                {
                    case TokenKind.EqualEqual:
                    case TokenKind.BangEqual:
                        if (leftType != rightType)
                            throw Error(
                                op,
                                $"Cannot compare {leftType} and {rightType} with '{op.Lexeme}'.");
                        Emit(
                            op.Kind == TokenKind.EqualEqual
                                ? EnumOpcode.CmpEq
                                : EnumOpcode.CmpNe);
                        break;


                    case TokenKind.Less:
                    case TokenKind.LessEqual:
                    case TokenKind.Greater:
                    case TokenKind.GreaterEqual:
                        RequireIntBinary(op, leftType, rightType);
                        Emit(op.Kind switch
                        {
                            TokenKind.Less => EnumOpcode.CmpLt,
                            TokenKind.LessEqual => EnumOpcode.CmpLe,
                            TokenKind.Greater => EnumOpcode.CmpGt,
                            TokenKind.GreaterEqual => EnumOpcode.CmpGe,
                            _ => throw Error(op, "Unsupported comparison operator.")
                        });
                        break;


                    default:
                        throw Error(op, "Unsupported comparison operator.");
                }


                _lastExpressionClassName = null;
                return ExprType.Bool;
            }


            private ExprType ParseAdditive()
            {
                var type = ParseMultiplicative();


                while (Check(TokenKind.Plus) || Check(TokenKind.Minus))
                {
                    var op = Advance();
                    var right = ParseMultiplicative();
                    RequireIntBinary(op, type, right);


                    Emit(
                        op.Kind == TokenKind.Plus
                            ? EnumOpcode.Add
                            : EnumOpcode.Sub);


                    type = ExprType.Int;
                    _lastExpressionClassName = null;
                }


                return type;
            }


            private ExprType ParseMultiplicative()
            {
                var type = ParseUnary();


                while (
                    Check(TokenKind.Star) ||
                    Check(TokenKind.Slash) ||
                    Check(TokenKind.Percent))
                {
                    var op = Advance();
                    var right = ParseUnary();
                    RequireIntBinary(op, type, right);


                    Emit(op.Kind switch
                    {
                        TokenKind.Star => EnumOpcode.Mul,
                        TokenKind.Slash => EnumOpcode.Div,
                        TokenKind.Percent => EnumOpcode.Mod,
                        _ => throw Error(op, "Unsupported multiplicative operator.")
                    });


                    type = ExprType.Int;
                    _lastExpressionClassName = null;
                }


                return type;
            }


            private ExprType ParseUnary()
            {
                if (Current.Lexeme == "not")
                {
                    var op = Advance();
                    var type = ParseUnary();
                    RequireBool(op, type);


                    EmitBoolConstant(false);
                    Emit(EnumOpcode.CmpEq);
                    _lastExpressionClassName = null;
                    return ExprType.Bool;
                }


                return ParsePrimary();
            }


            private void ParseInstanceAsyncMethodCallStatement()
            {
                var targetToken = Expect(TokenKind.Identifier);
                var target = ResolveLocal(targetToken);
                if (target.Type != ExprType.Object || target.ClassName == null)
                    throw Error(targetToken, $"Async member call requires a statically known class instance; '{targetToken.Lexeme}' is {FormatType(target.Type)}.");

                if (!_classes.TryGetValue(target.ClassName, out var classInfo))
                    throw Error(targetToken, $"Unknown class '{target.ClassName}'.");

                Expect(TokenKind.Dot);
                var member = Expect(TokenKind.Identifier);
                var method = classInfo.Methods.FirstOrDefault(x => x.Name == member.Lexeme && x.Function.IsPublicAsync);
                if (method == null)
                    throw Error(member, $"'{classInfo.Name}.{member.Lexeme}' is not a public async instance method.");

                var function = method.Function;
                Emit(EnumOpcode.LoadLocal, target.Slot);
                Expect(TokenKind.LParen);

                var argumentIndex = 0;
                if (!Check(TokenKind.RParen))
                {
                    while (true)
                    {
                        if (argumentIndex >= function.Parameters.Count)
                            throw Error(Current, $"Method '{function.Name}' received too many arguments.");

                        var argumentToken = Current;
                        var actualType = ParseExpression();
                        var actualClassName = _lastExpressionClassName;
                        var parameter = function.Parameters[argumentIndex];
                        RequireAssignable(argumentToken, parameter.Type, actualType, parameter.ClassName, actualClassName);
                        argumentIndex++;

                        if (!Check(TokenKind.Comma))
                            break;
                        Advance();
                    }
                }

                Expect(TokenKind.RParen);
                if (argumentIndex != function.Parameters.Count)
                    throw Error(member, $"Method '{function.Name}' expects {function.Parameters.Count} arguments, found {argumentIndex}.");
                Expect(TokenKind.Semicolon);

                if (_currentFunction?.IsPublicAsync == true)
                {
                    if (!_asyncCallEdges.TryGetValue(_currentFunction.Index, out var targets))
                    {
                        targets = new HashSet<int>();
                        _asyncCallEdges.Add(_currentFunction.Index, targets);
                    }
                    targets.Add(function.Index);
                }

                EmitIntConstant(function.Index);
                EmitIntConstant(argumentIndex);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.InstanceAsyncEnqueue);
            }


            private void ValidateAsyncCallGraph()
            {
                var state = new Dictionary<int, int>(); // 0=unseen, 1=visiting, 2=done
                var path = new Stack<int>();

                foreach (var function in _functions.Where(x => x.IsPublicAsync))
                {
                    if (!state.TryGetValue(function.Index, out var existing) || existing == 0)
                        Visit(function.Index);
                }

                void Visit(int functionIndex)
                {
                    state[functionIndex] = 1;
                    path.Push(functionIndex);

                    if (_asyncCallEdges.TryGetValue(functionIndex, out var targets))
                    {
                        foreach (var target in targets)
                        {
                            state.TryGetValue(target, out var targetState);
                            if (targetState == 1)
                            {
                                var cycle = path.Reverse()
                                    .SkipWhile(x => x != target)
                                    .Concat(new[] { target })
                                    .Select(x => _functions[x].Name);
                                throw new AloeCompileException(
                                    $"Async CallGraph cycle is not allowed: {string.Join(" -> ", cycle)}.");
                            }

                            if (targetState == 0)
                                Visit(target);
                        }
                    }

                    path.Pop();
                    state[functionIndex] = 2;
                }
            }


            private void ParseFunctionCallStatement()
            {
                var type = ParseFunctionCall(requireValue: false);
                if (type != ExprType.Void)
                    throw Error(
                        Previous,
                        "Ignoring a non-void function result is not supported by the current compiler subset.");
                Expect(TokenKind.Semicolon);
            }


            private ExprType ParseFunctionCall(bool requireValue)
            {
                var name = Expect(TokenKind.Identifier);
                var isInstanceSelfCall = false;
                FunctionSignature? function = null;

                if (!_functionByName.TryGetValue(name.Lexeme, out function))
                {
                    var declaringClass = _currentFunction?.DeclaringClass;
                    if (declaringClass != null && _classes.TryGetValue(declaringClass, out var classInfo))
                    {
                        var method = classInfo.Methods.FirstOrDefault(x => x.Name == name.Lexeme);
                        if (method != null)
                        {
                            function = method.Function;
                            isInstanceSelfCall = true;
                        }
                    }
                }

                if (function == null)
                    throw Error(name, $"Unknown function or private instance method '{name.Lexeme}'.");

                if (requireValue && function.ReturnType == ExprType.Void)
                    throw Error(name, $"Void function '{name.Lexeme}' cannot be used as a value.");

                Expect(TokenKind.LParen);
                if (isInstanceSelfCall)
                    Emit(EnumOpcode.LoadLocal, 0); // implicit this

                var argumentIndex = 0;
                if (!Check(TokenKind.RParen))
                {
                    while (true)
                    {
                        if (argumentIndex >= function.Parameters.Count)
                            throw Error(Current, $"Function '{function.Name}' received too many arguments.");

                        var argumentToken = Current;
                        var actualType = ParseExpression();
                        var actualClassName = _lastExpressionClassName;
                        var parameter = function.Parameters[argumentIndex];
                        RequireAssignable(argumentToken, parameter.Type, actualType, parameter.ClassName, actualClassName);
                        argumentIndex++;

                        if (!Check(TokenKind.Comma))
                            break;
                        Advance();
                    }
                }

                Expect(TokenKind.RParen);
                if (argumentIndex != function.Parameters.Count)
                    throw Error(name, $"Function '{function.Name}' expects {function.Parameters.Count} arguments, found {argumentIndex}.");

                Emit(EnumOpcode.Call, function.Index);
                _lastExpressionClassName = function.ReturnClassName;
                return function.ReturnType;
            }


            private ExprType ParsePrimary()
            {
                if (Current.Lexeme == "this" && Peek(1).Kind == TokenKind.Dot)
                    return ParseThisFieldRead();

                if (Current.Lexeme == "pipe" && Peek(1).Kind == TokenKind.Less)
                    return ParsePipeCreate();

                // GC.<property> is a built-in static property, so it must be resolved
                // before the generic "identifier ." instance property read.
                if (Current.Lexeme == "GC" && Peek(1).Kind == TokenKind.Dot)
                    return ParseGcStaticPropertyRead();

                if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Dot)
                    return ParseInstancePropertyRead();

                if (Check(TokenKind.IntegerLiteral))
                {
                    var token = Advance();
                    Emit(
                        EnumOpcode.PushConst,
                        AddConstant(AloeValue.FromInt(ParseInteger(token))));
                    _lastExpressionClassName = null;
                    return ExprType.Int;
                }


                if (Check(TokenKind.StringLiteral))
                {
                    var token = Advance();
                    Emit(
                        EnumOpcode.PushConst,
                        AddConstant(AloeValue.FromString(ParseString(token))));
                    _lastExpressionClassName = null;
                    return ExprType.String;
                }


                if (Check(TokenKind.BoolLiteral))
                {
                    var token = Advance();
                    Emit(
                        EnumOpcode.PushConst,
                        AddConstant(AloeValue.FromBool(token.Lexeme == "true")));
                    _lastExpressionClassName = null;
                    return ExprType.Bool;
                }


                if (Peek(1).Kind == TokenKind.LParen &&
                    HostIntrinsicCatalog.TryGet(Current.Lexeme, out var hostExpression) &&
                    hostExpression.ReturnType != AloeHostValueType.Void)
                    return ParseHostIntrinsic(hostExpression, requireValue: true);


                if (Current.Lexeme == "new")
                    return ParseNewObject();


                if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.LParen)
                    return ParseFunctionCall(requireValue: true);


                if (Check(TokenKind.Identifier))
                {
                    var name = Advance();
                    var local = ResolveLocal(name);
                    Emit(EnumOpcode.LoadLocal, local.Slot);
                    _lastExpressionClassName = local.ClassName;
                    return local.Type;
                }


                if (Check(TokenKind.LParen))
                {
                    Advance();
                    var type = ParseExpression();
                    Expect(TokenKind.RParen);
                    return type;
                }


                throw Error(
                    Current,
                    "Expected a literal, local variable, or parenthesized expression.");
            }


            private ExprType ParsePipeCreate()
            {
                Advance(); // pipe
                Expect(TokenKind.Less);
                var elementToken = Advance();
                var elementType = elementToken.Lexeme switch
                {
                    "int" => ExprType.Int,
                    "string" => ExprType.String,
                    "bool" or "boolean" => ExprType.Bool,
                    _ => throw Error(elementToken, "pipe<T> currently supports int, string, and bool elements.")
                };
                Expect(TokenKind.Greater);
                Expect(TokenKind.Dot);
                var create = Expect(TokenKind.Identifier);
                if (!string.Equals(create.Lexeme, "create", StringComparison.Ordinal))
                    throw Error(create, "Expected pipe<T>.create().");
                Expect(TokenKind.LParen);
                var hasCapacity = !Check(TokenKind.RParen);
                int? capacitySlot = null;
                if (hasCapacity)
                {
                    var capacityToken = Current;
                    var capacityType = ParseExpression();
                    if (capacityType != ExprType.Int)
                        throw Error(capacityToken, "pipe<T>.create(capacity) expects an int capacity.");
                    if (Check(TokenKind.Comma))
                        throw Error(Current, "pipe<T>.create accepts at most one capacity argument.");
                    capacitySlot = _nextLocalSlot++;
                    Emit(EnumOpcode.StoreLocal, capacitySlot.Value);
                }
                Expect(TokenKind.RParen);

                var typeName = $"pipe<{FormatType(elementType)}>";
                Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(typeName)));
                if (!hasCapacity)
                    EmitIntConstant(-1); // -1 = unbounded
                else
                    Emit(EnumOpcode.LoadLocal, capacitySlot!.Value);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.PipeCreate);
                _lastExpressionClassName = typeName;
                return ExprType.Object;
            }


            private ExprType ParseThisFieldRead()
            {
                var thisToken = Advance(); // this
                var function = _currentFunction;
                if (function?.DeclaringClass == null)
                    throw Error(thisToken, "'this' is only available in class instance code.");

                Expect(TokenKind.Dot);
                var fieldToken = Expect(TokenKind.Identifier);
                var field = ResolveField(function.DeclaringClass, fieldToken);
                Emit(EnumOpcode.LoadLocal, 0);
                EmitIntConstant(field.Index);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.ObjectFieldGet);
                _lastExpressionClassName = field.ClassName;
                return field.Type;
            }


            private ExprType ParseInstancePropertyRead()
            {
                var targetToken = Expect(TokenKind.Identifier);
                var target = ResolveLocal(targetToken);
                if (target.Type != ExprType.Object || target.ClassName == null)
                    throw Error(targetToken, $"Member access requires a statically known class instance; '{targetToken.Lexeme}' is {FormatType(target.Type)}.");

                if (!_classes.TryGetValue(target.ClassName, out var classInfo))
                    throw Error(targetToken, $"Unknown class '{target.ClassName}'.");

                Emit(EnumOpcode.LoadLocal, target.Slot);
                Expect(TokenKind.Dot);
                var member = Expect(TokenKind.Identifier);
                var property = classInfo.Properties.FirstOrDefault(x => x.Name == member.Lexeme);
                if (property == null)
                    throw Error(member, $"'{classInfo.Name}.{member.Lexeme}' is not a public instance property in the current compiler subset.");

                EmitIntConstant(property.Getter.Index);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.InstancePropertyGet);
                _lastExpressionClassName = property.ClassName;
                return property.Type;
            }


            private ExprType ParseNewObject()
            {
                Advance(); // new
                var className = Expect(TokenKind.Identifier);
                if (!_classes.TryGetValue(className.Lexeme, out var classInfo))
                    throw Error(className, $"Unknown class '{className.Lexeme}'.");

                Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(classInfo.Name)));
                EmitIntConstant(classInfo.Fields.Count);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.ObjectAllocate);
                var objectSlot = _nextLocalSlot++;
                Emit(EnumOpcode.StoreLocal, objectSlot);

                Expect(TokenKind.LParen);
                var constructor = classInfo.Constructor;
                if (constructor == null)
                {
                    if (!Check(TokenKind.RParen))
                        throw Error(Current, $"Class '{classInfo.Name}' has no constructor and cannot accept arguments.");
                    if (classInfo.Fields.Count != 0)
                        throw Error(className, $"Class '{classInfo.Name}' has fields but no constructor to initialize them.");
                    Expect(TokenKind.RParen);
                    Emit(EnumOpcode.LoadLocal, objectSlot);
                    _lastExpressionClassName = classInfo.Name;
                    return ExprType.Object;
                }

                Emit(EnumOpcode.LoadLocal, objectSlot); // implicit this argument
                var argumentIndex = 0;
                if (!Check(TokenKind.RParen))
                {
                    while (true)
                    {
                        if (argumentIndex >= constructor.Parameters.Count)
                            throw Error(Current, $"Constructor '{classInfo.Name}' received too many arguments.");
                        var argumentToken = Current;
                        var actualType = ParseExpression();
                        var actualClassName = _lastExpressionClassName;
                        var parameter = constructor.Parameters[argumentIndex];
                        RequireAssignable(argumentToken, parameter.Type, actualType, parameter.ClassName, actualClassName);
                        argumentIndex++;
                        if (!Check(TokenKind.Comma))
                            break;
                        Advance();
                    }
                }
                Expect(TokenKind.RParen);
                if (argumentIndex != constructor.Parameters.Count)
                    throw Error(className, $"Constructor '{classInfo.Name}' expects {constructor.Parameters.Count} arguments, found {argumentIndex}.");

                Emit(EnumOpcode.Call, constructor.Index);
                Emit(EnumOpcode.LoadLocal, objectSlot);
                _lastExpressionClassName = classInfo.Name;
                return ExprType.Object;
            }


            private ExprType ParseTypeName()
                => ParseTypeName(out _);


            private ExprType ParseTypeName(out string? className)
            {
                var token = Advance();
                className = null;
                switch (token.Lexeme)
                {
                    case "int": return ExprType.Int;
                    case "string": return ExprType.String;
                    case "bool":
                    case "boolean": return ExprType.Bool;
                    case "object": return ExprType.Object;
                    case "pipe":
                        Expect(TokenKind.Less);
                        var elementToken = Advance();
                        var elementType = elementToken.Lexeme switch
                        {
                            "int" => ExprType.Int,
                            "string" => ExprType.String,
                            "bool" or "boolean" => ExprType.Bool,
                            _ => throw Error(elementToken, "pipe<T> currently supports int, string, and bool elements.")
                        };
                        Expect(TokenKind.Greater);
                        className = $"pipe<{FormatType(elementType)}>";
                        return ExprType.Object;
                    default:
                        if (_classNames.Contains(token.Lexeme))
                        {
                            className = token.Lexeme;
                            return ExprType.Object;
                        }
                        throw Error(token, $"Type '{token.Lexeme}' is not supported by the current compiler subset.");
                }
            }


            private static string FormatType(ExprType type)
                => type switch
                {
                    ExprType.Int => "int",
                    ExprType.String => "string",
                    ExprType.Bool => "bool",
                    ExprType.Object => "object",
                    ExprType.Void => "void",
                    _ => type.ToString()
                };


            private LocalInfo DeclareImplicitLocal(string name, ExprType type, string? className = null)
            {
                var scope = _scopes.Peek();
                if (scope.ContainsKey(name))
                    throw new AloeCompileException($"Local '{name}' is already declared in this scope.");
                var local = new LocalInfo(_nextLocalSlot++, type, className);
                scope.Add(name, local);
                return local;
            }


            private LocalInfo DeclareLocal(AloeToken name, ExprType type, string? className = null)
            {
                var scope = _scopes.Peek();


                if (scope.ContainsKey(name.Lexeme))
                    throw Error(
                        name,
                        $"Local '{name.Lexeme}' is already declared in this scope.");


                var local = new LocalInfo(_nextLocalSlot++, type, className);
                scope.Add(name.Lexeme, local);
                return local;
            }


            private bool TryResolveLocal(string name, out LocalInfo local)
            {
                foreach (var scope in _scopes)
                {
                    if (scope.TryGetValue(name, out local!))
                        return true;
                }
                local = null!;
                return false;
            }


            private static bool IsPipeTypeName(string? className)
                => className != null && className.StartsWith("pipe<", StringComparison.Ordinal) && className.EndsWith(">", StringComparison.Ordinal);


            private static bool TryGetPipeElementType(string? className, out ExprType elementType)
            {
                elementType = ExprType.Void;
                if (!IsPipeTypeName(className))
                    return false;

                var elementName = className![5..^1];
                elementType = elementName switch
                {
                    "int" => ExprType.Int,
                    "string" => ExprType.String,
                    "bool" => ExprType.Bool,
                    _ => ExprType.Void
                };
                return elementType != ExprType.Void;
            }


            private LocalInfo ResolveLocal(AloeToken name)
            {
                foreach (var scope in _scopes)
                {
                    if (scope.TryGetValue(name.Lexeme, out var local))
                        return local;
                }


                throw Error(name, $"Unknown local variable '{name.Lexeme}'.");
            }


            private void EnterScope()
                => _scopes.Push(
                    new Dictionary<string, LocalInfo>(StringComparer.Ordinal));


            private void ExitScope()
                => _scopes.Pop();


            private static bool IsComparison(TokenKind kind)
                => kind is
                    TokenKind.EqualEqual or
                    TokenKind.BangEqual or
                    TokenKind.Less or
                    TokenKind.LessEqual or
                    TokenKind.Greater or
                    TokenKind.GreaterEqual;


            private static void RequireAssignable(
                AloeToken token,
                ExprType expected,
                ExprType actual)
                => RequireAssignable(token, expected, actual, null, null);


            private static void RequireAssignable(
                AloeToken token,
                ExprType expected,
                ExprType actual,
                string? expectedClassName,
                string? actualClassName)
            {
                if (expected != actual)
                    throw Error(token, $"Type mismatch: expected {expected}, found {actual}.");

                if (expected == ExprType.Object && expectedClassName != null)
                {
                    if (actualClassName == null || !string.Equals(expectedClassName, actualClassName, StringComparison.Ordinal))
                        throw Error(token, $"Type mismatch: expected {expectedClassName}, found {actualClassName ?? "object"}.");
                }
            }


            private static void RequireBool(AloeToken op, ExprType type)
            {
                if (type != ExprType.Bool)
                    throw Error(
                        op,
                        $"Operator '{op.Lexeme}' requires bool operands.");
            }


            private void EmitIntConstant(long value)
            {
                Emit(
                    EnumOpcode.PushConst,
                    AddConstant(AloeValue.FromInt(value)));
            }


            private void EmitBoolConstant(bool value)
            {
                Emit(
                    EnumOpcode.PushConst,
                    AddConstant(AloeValue.FromBool(value)));
            }


            private static void RequireIntBinary(
                AloeToken op,
                ExprType left,
                ExprType right)
            {
                if (left != ExprType.Int || right != ExprType.Int)
                    throw Error(
                        op,
                        $"Operator '{op.Lexeme}' is supported only for int in the current compiler subset.");
            }


            private static long ParseInteger(AloeToken token)
            {
                var text = token.Lexeme;
                var negative = text.StartsWith("-", StringComparison.Ordinal);
                var unsigned = negative ? text[1..] : text;
                long value;


                if (unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    value = Convert.ToInt64(unsigned[2..], 16);
                else if (unsigned.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                    value = Convert.ToInt64(unsigned[2..], 2);
                else
                    value = long.Parse(
                        unsigned,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture);


                return negative ? -value : value;
            }


            private static string ParseString(AloeToken token)
            {
                var raw = token.Lexeme;


                if (raw.Length < 2 || raw[0] != '"' || raw[^1] != '"')
                    throw Error(token, "Unterminated string literal.");


                var sb = new StringBuilder();


                for (var i = 1; i < raw.Length - 1; i++)
                {
                    var c = raw[i];


                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }


                    if (++i >= raw.Length - 1)
                        throw Error(token, "Invalid string escape.");


                    sb.Append(raw[i] switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        _ => throw Error(
                            token,
                            $"Unsupported string escape '\\{raw[i]}'.")
                    });
                }


                return sb.ToString();
            }


            private int AddConstant(AloeValue value)
            {
                var index = _constants.Count;
                _constants.Add(value);
                return index;
            }


            private void Emit(EnumOpcode opcode)
                => _code.Add(new Instruction(opcode));


            private void Emit(EnumOpcode opcode, int operand0)
                => _code.Add(new Instruction(opcode, operand0));


            private int EmitPlaceholder(EnumOpcode opcode)
            {
                var index = _code.Count;
                _code.Add(new Instruction(opcode, -1));
                return index;
            }


            private void PatchOperand(int instructionIndex, int operand0)
            {
                var old = _code[instructionIndex];
                _code[instructionIndex] =
                    new Instruction(old.Opcode, operand0, old.Operand1);
            }


            private AloeToken Current
                => _tokens[Math.Min(_position, _tokens.Count - 1)];


            private AloeToken Previous
                => _tokens[Math.Max(0, _position - 1)];


            private AloeToken Peek(int offset)
                => _tokens[Math.Min(_position + offset, _tokens.Count - 1)];

            private bool Check(TokenKind kind)
                => Current.Kind == kind;


            private AloeToken Advance()
            {
                var token = Current;
                if (_position < _tokens.Count - 1)
                    _position++;
                return token;
            }


            private AloeToken Expect(TokenKind kind)
            {
                if (!Check(kind))
                    throw Error(
                        Current,
                        $"Expected {kind}, found '{Current.Lexeme}' ({Current.Kind}).");

                return Advance();
            }


            private AloeToken ExpectLexeme(string lexeme)
            {
                if (!string.Equals(
                    Current.Lexeme,
                    lexeme,
                    StringComparison.Ordinal))
                {
                    throw Error(
                        Current,
                        $"Expected '{lexeme}', found '{Current.Lexeme}'.");
                }

                return Advance();
            }


            private static AloeCompileException Error(
                AloeToken token,
                string message)
                => new($"{message} (line {token.Line}, col {token.Column})");
        }
    }
}
