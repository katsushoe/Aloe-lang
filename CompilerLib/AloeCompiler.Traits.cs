using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    // Decision 0062: a trait method body is compiled once per applying class.
    private sealed record TraitMethodSignature(
        string TraitName,
        AloeToken NameToken,
        IReadOnlyList<ParameterSignature> Parameters,
        ExprType ReturnType,
        string? ReturnClassName,
        bool IsPublicAsync,
        int BodyStart,
        int BodyEnd);

    private sealed record TraitRequirement(string TraitName, AloeToken NameToken, FunctionSignature Contract);

    private sealed class TraitSignature
    {
        public TraitSignature(string name) => Name = name;
        public string Name { get; }
        public List<string> Includes { get; } = new();
        public List<TraitMethodSignature> Methods { get; } = new();
        public List<TraitRequirement> Requirements { get; } = new();
    }

    private sealed partial class Parser
    {
        private readonly Dictionary<string, TraitSignature> _traits = new(StringComparer.Ordinal);

        // trait Name [with A, B] { require method m(...): T; [public async|private|protected] method m(...): T { ... } }
        // A trait is also registered as an interface so trait types reuse interface assignability, casts and dispatch.
        private void CollectTraitSignature(ref int position)
        {
            position++; // trait
            var nameToken = TokenAt(position++);
            if (nameToken.Kind != TokenKind.Identifier)
                throw Error(nameToken, "Expected trait name.");
            if (_interfaceNames.Contains(nameToken.Lexeme) || _classNames.Contains(nameToken.Lexeme) ||
                _structs.ContainsKey(nameToken.Lexeme) || _enumMembers.ContainsKey(nameToken.Lexeme) ||
                TryGetNamedBoxType(nameToken.Lexeme, out _))
                throw Error(nameToken, $"Type '{nameToken.Lexeme}' is already declared.");

            var trait = new TraitSignature(nameToken.Lexeme);
            var view = new InterfaceSignature(nameToken.Lexeme);
            if (TokenAt(position).Lexeme == "with")
            {
                position++;
                foreach (var included in ScanTraitList(ref position, trait.Name))
                {
                    trait.Includes.Add(included.Name);
                    view.Parents.Add(included.Name);
                    foreach (var method in _interfaces[included.Name].Methods)
                        if (view.Methods.All(existing => existing.Name != method.Name))
                            view.Methods.Add(method);
                }
            }
            _traits.Add(trait.Name, trait);
            _interfaces.Add(view.Name, view);
            _interfaceNames.Add(view.Name);

            ExpectAt(ref position, TokenKind.LBrace);
            while (TokenAt(position).Kind != TokenKind.RBrace)
            {
                if (TokenAt(position).Kind == TokenKind.EndOfFile)
                    throw Error(TokenAt(position), $"Unexpected end of file in trait '{trait.Name}'.");
                if (TokenAt(position).Lexeme == "require")
                {
                    position++;
                    CollectTraitRequirement(trait, ref position);
                    continue;
                }
                CollectTraitMethod(trait, view, ref position);
            }
            position++; // trait }
        }

        private List<TraitSignature> ScanTraitList(ref int position, string? owner)
        {
            var traits = new List<TraitSignature>();
            while (true)
            {
                var token = TokenAt(position++);
                if (token.Kind != TokenKind.Identifier || token.Lexeme == owner || !_traits.TryGetValue(token.Lexeme, out var trait))
                    throw Error(token, _interfaces.ContainsKey(token.Lexeme)
                        ? $"Interface '{token.Lexeme}' is implemented with 'implements', not applied with 'with'."
                        : "Applied traits must be previously declared traits other than the trait itself.");
                if (traits.Contains(trait))
                    throw Error(token, $"Trait '{trait.Name}' is listed more than once.");
                traits.Add(trait);
                if (TokenAt(position).Kind != TokenKind.Comma)
                    return traits;
                position++;
            }
        }

        private void CollectTraitRequirement(TraitSignature trait, ref int position)
        {
            var methodToken = TokenAt(position++);
            if (methodToken.Lexeme != "method")
                throw Error(methodToken, "Expected 'method' after 'require'.");
            var name = ScanTraitMemberName(trait, ref position);
            var parameters = ScanTraitParameters(ref position);
            ExpectAt(ref position, TokenKind.Colon);
            var returnType = ScanType(ref position, allowVoid: true, out var returnClassName);
            if (TokenAt(position).Kind != TokenKind.Semicolon)
                throw Error(TokenAt(position), "A required trait method has no body; end it with ';'.");
            position++;
            var contract = new FunctionSignature($"{trait.Name}.{name.Lexeme}", -1, parameters, returnType,
                -1, -1, false, ReturnClassName: returnClassName);
            trait.Requirements.Add(new TraitRequirement(trait.Name, name, contract));
        }

        private void CollectTraitMethod(TraitSignature trait, InterfaceSignature view, ref int position)
        {
            var sawPublic = false;
            var sawAsync = false;
            var start = TokenAt(position);
            while (TokenAt(position).Lexeme is "public" or "private" or "protected" or "async" or "static" or
                   "virtual" or "override" or "readonly")
            {
                var modifier = TokenAt(position++);
                if (modifier.Lexeme is "static" or "virtual" or "override" or "readonly")
                    throw Error(modifier, $"Trait methods cannot be {modifier.Lexeme}.");
                sawPublic |= modifier.Lexeme == "public";
                sawAsync |= modifier.Lexeme == "async";
            }
            var methodToken = TokenAt(position++);
            if (methodToken.Lexeme != "method")
                throw Error(methodToken.Kind == TokenKind.EndOfFile ? start : methodToken,
                    "Traits may declare only methods and 'require method' contracts; fields, constructors, properties and static members belong to classes.");
            if (sawPublic != sawAsync)
                throw Error(methodToken, sawPublic
                    ? "Public trait methods must be declared async."
                    : "Only public async trait methods may be async; private/protected trait methods are synchronous.");

            var name = ScanTraitMemberName(trait, ref position);
            var parameters = ScanTraitParameters(ref position);
            ExpectAt(ref position, TokenKind.Colon);
            var returnType = ScanType(ref position, allowVoid: true, out var returnClassName);
            if (sawPublic && returnType != ExprType.Void)
                throw Error(name, "public async methods must return void.");
            if (TokenAt(position).Kind != TokenKind.LBrace)
                throw Error(TokenAt(position), "Trait methods need a body; use 'require method' for a contract the class must provide.");
            position++;
            var bodyStart = position;
            var depth = 1;
            while (depth > 0)
            {
                var token = TokenAt(position);
                if (token.Kind == TokenKind.EndOfFile)
                    throw Error(token, $"Unexpected end of file in trait method '{trait.Name}.{name.Lexeme}'.");
                if (token.Kind == TokenKind.LBrace) depth++;
                else if (token.Kind == TokenKind.RBrace) depth--;
                position++;
            }
            var method = new TraitMethodSignature(trait.Name, name, parameters, returnType, returnClassName,
                sawPublic, bodyStart, position - 1);
            trait.Methods.Add(method);
            if (sawPublic)
            {
                var contract = new FunctionSignature($"{trait.Name}.{name.Lexeme}", -1, parameters, returnType,
                    -1, -1, false, DeclaringClass: trait.Name, ReturnClassName: returnClassName,
                    IsPublicAsync: true, IsVirtual: true);
                view.Methods.RemoveAll(existing => existing.Name == name.Lexeme);
                view.Methods.Add(new MethodSignature(name.Lexeme, contract));
            }
        }

        private AloeToken ScanTraitMemberName(TraitSignature trait, ref int position)
        {
            var name = TokenAt(position++);
            if (name.Kind != TokenKind.Identifier)
                throw Error(name, "Expected trait method name.");
            if (trait.Methods.Any(method => method.NameToken.Lexeme == name.Lexeme) ||
                trait.Requirements.Any(requirement => requirement.NameToken.Lexeme == name.Lexeme))
                throw Error(name, $"Trait member '{name.Lexeme}' is already declared in '{trait.Name}'.");
            return name;
        }

        private List<ParameterSignature> ScanTraitParameters(ref int position)
        {
            ExpectAt(ref position, TokenKind.LParen);
            var parameters = new List<ParameterSignature>();
            if (TokenAt(position).Kind != TokenKind.RParen)
            {
                while (true)
                {
                    var parameterName = TokenAt(position++);
                    if (parameterName.Kind != TokenKind.Identifier)
                        throw Error(parameterName, "Expected trait method parameter name.");
                    if (parameters.Any(parameter => parameter.Name.Lexeme == parameterName.Lexeme))
                        throw Error(parameterName, $"Parameter '{parameterName.Lexeme}' is already declared.");
                    ExpectAt(ref position, TokenKind.Colon);
                    var type = ScanType(ref position, allowVoid: false, out var className);
                    parameters.Add(new ParameterSignature(parameterName, type, className));
                    if (TokenAt(position).Kind != TokenKind.Comma)
                        break;
                    position++;
                }
            }
            ExpectAt(ref position, TokenKind.RParen);
            return parameters;
        }

        private void CollectClassTraits(ClassSignature classInfo, ref int position)
        {
            if (TokenAt(position).Lexeme != "with")
                return;
            position++;
            classInfo.Traits.AddRange(ScanTraitList(ref position, owner: null).Select(trait => trait.Name));
        }

        // Included traits first, each trait once, in declaration order.
        private List<TraitSignature> TraitClosure(IEnumerable<string> roots)
        {
            var ordered = new List<TraitSignature>();
            void Visit(string name)
            {
                var trait = _traits[name];
                if (ordered.Contains(trait)) return;
                foreach (var included in trait.Includes)
                    Visit(included);
                ordered.Add(trait);
            }
            foreach (var root in roots)
                Visit(root);
            return ordered;
        }

        private bool IsTraitMemberVisible(string traitName, string memberName)
            => TraitClosure(new[] { traitName }).Any(trait =>
                trait.Methods.Any(method => method.NameToken.Lexeme == memberName) ||
                trait.Requirements.Any(requirement => requirement.NameToken.Lexeme == memberName));

        // Copies trait methods into the class (class-defined methods win), then checks requirements.
        private void ApplyTraits(ClassSignature classInfo, AloeToken classToken)
        {
            if (classInfo.Traits.Count == 0)
                return;
            var closure = TraitClosure(classInfo.Traits);
            var ownNames = classInfo.Methods.Concat(classInfo.StaticAsyncMethods).Concat(classInfo.SynchronousStaticMethods)
                .Select(method => method.Name).ToHashSet(StringComparer.Ordinal);
            var chosen = new Dictionary<string, TraitMethodSignature>(StringComparer.Ordinal);
            foreach (var method in closure.SelectMany(trait => trait.Methods))
            {
                var name = method.NameToken.Lexeme;
                if (ownNames.Contains(name))
                    continue;
                if (chosen.TryGetValue(name, out var previous))
                    throw Error(classToken, $"Trait methods '{previous.TraitName}.{name}' and '{method.TraitName}.{name}' conflict in class " +
                        $"'{classInfo.Name}'; define '{name}' in the class to resolve the conflict.");
                if (FindMethod(classInfo.BaseClassName, name) != null)
                    throw Error(classToken, $"Trait method '{method.TraitName}.{name}' conflicts with a method inherited by class " +
                        $"'{classInfo.Name}'; define '{name}' in the class to resolve the conflict.");
                chosen.Add(name, method);
            }

            foreach (var (name, method) in chosen)
            {
                var function = new FunctionSignature($"{classInfo.Name}.{name}", _functions.Count, method.Parameters,
                    method.ReturnType, method.BodyStart, method.BodyEnd, false, DeclaringClass: classInfo.Name,
                    ReturnClassName: method.ReturnClassName, IsPublicAsync: method.IsPublicAsync, DeclaringTrait: method.TraitName);
                _functions.Add(function);
                classInfo.Methods.Add(new MethodSignature(name, function));
            }

            foreach (var trait in closure)
            {
                if (!classInfo.Interfaces.Contains(trait.Name, StringComparer.Ordinal))
                    classInfo.Interfaces.Add(trait.Name);
                foreach (var requirement in trait.Requirements)
                {
                    var implementation = FindMethod(classInfo.Name, requirement.NameToken.Lexeme);
                    if (implementation == null || implementation.Function.IsStaticMethod ||
                        !HasMatchingMethodSignature(requirement.Contract, implementation.Function.Parameters,
                            implementation.Function.ReturnType, implementation.Function.ReturnClassName))
                        throw Error(classToken, $"Class '{classInfo.Name}' must provide method '{requirement.NameToken.Lexeme}' " +
                            $"matching the requirement of trait '{trait.Name}'.");
                }
            }
        }
    }
}
