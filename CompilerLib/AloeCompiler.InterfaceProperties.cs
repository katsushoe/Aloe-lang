using System.Globalization;
using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    // Decision 0063: an interface property contract. HasSetter=false means `{ get; }`.
    private sealed record InterfacePropertySignature(string Name, ExprType Type, string? ClassName, bool HasSetter);

    private sealed partial class Parser
    {
        private static bool HasInterfaceMember(InterfaceSignature target, string name)
            => target.Methods.Any(method => method.Name == name) || target.Properties.Any(property => property.Name == name);

        private InterfacePropertySignature? FindInterfaceProperty(string? typeName, string memberName)
            => typeName != null && _interfaces.TryGetValue(typeName, out var interfaceInfo)
                ? interfaceInfo.Properties.FirstOrDefault(property => property.Name == memberName)
                : null;

        // Parses `property Name: Type { get; }` or `{ get; set; }` after the `property` keyword.
        private void CollectInterfacePropertyContract(InterfaceSignature target, ref int position)
        {
            var name = TokenAt(position++);
            if (name.Kind != TokenKind.Identifier)
                throw Error(name, "Expected interface property name.");
            if (HasInterfaceMember(target, name.Lexeme))
                throw Error(name, $"Interface member '{name.Lexeme}' is already declared or inherited in '{target.Name}'.");
            ExpectAt(ref position, TokenKind.Colon);
            var type = ScanType(ref position, allowVoid: false, out var className);
            ExpectAt(ref position, TokenKind.LBrace);
            ExpectInterfaceAccessor(ref position, "get");
            var hasSetter = TokenAt(position).Lexeme == "set";
            if (hasSetter)
            {
                ExpectInterfaceAccessor(ref position, "set");
                if (!IsScalarSetterType(type))
                    throw Error(name, "Interface get/set property contracts support the seven scalar types only.");
            }
            ExpectAt(ref position, TokenKind.RBrace);
            target.Properties.Add(new InterfacePropertySignature(name.Lexeme, type, className, hasSetter));
        }

        private void ExpectInterfaceAccessor(ref int position, string accessor)
        {
            var token = TokenAt(position++);
            if (token.Lexeme != accessor)
                throw Error(token, $"Interface property contracts declare '{{ get; }}' or '{{ get; set; }}'; expected '{accessor}'.");
            if (TokenAt(position).Kind != TokenKind.Semicolon)
                throw Error(TokenAt(position), "Interface property accessors have no bodies; implement them in classes.");
            position++;
        }

        private void MergeInheritedPropertyContracts(InterfaceSignature target, InterfaceSignature parent, AloeToken parentToken)
        {
            foreach (var required in parent.Properties)
            {
                if (target.Methods.Any(method => method.Name == required.Name))
                    throw Error(parentToken, $"Inherited interface member '{required.Name}' is both a method and a property in '{target.Name}'.");
                var existing = target.Properties.FirstOrDefault(property => property.Name == required.Name);
                if (existing == null)
                    target.Properties.Add(required);
                else if (existing != required)
                    throw Error(parentToken, $"Inherited interface property '{required.Name}' has conflicting types or accessors in '{target.Name}'.");
            }
        }

        private void ValidateInterfacePropertyImplementations(ClassSignature classInfo, AloeToken classToken, string interfaceName)
        {
            foreach (var required in _interfaces[interfaceName].Properties)
            {
                var property = FindProperty(classInfo.Name, required.Name);
                if (property != null && property.Type == required.Type &&
                    string.Equals(property.ClassName, required.ClassName, StringComparison.Ordinal) &&
                    (!required.HasSetter || property.Setter != null))
                    continue;
                var accessors = required.HasSetter ? "{ get; set; }" : "{ get; }";
                throw Error(classToken,
                    $"Class '{classInfo.Name}' must provide public property '{required.Name}: {required.ClassName ?? FormatType(required.Type)}' " +
                    $"with {accessors} matching interface '{interfaceName}'.");
            }
        }

        // The Object receiver is already on the stack and checked live.
        private ExprType ParseInterfacePropertyRead(string interfaceName, AloeToken member)
        {
            var contract = FindInterfaceProperty(interfaceName, member.Lexeme)
                ?? throw Error(member, $"'{interfaceName}.{member.Lexeme}' is not a property contract of interface '{interfaceName}'.");
            EmitInterfacePropertyDispatch(interfaceName, contract, member, write: false);
            _lastExpressionClassName = contract.ClassName;
            return contract.Type;
        }

        // `target.Name = value;` where target is an interface-typed local; the receiver token is consumed.
        private void ParseInterfacePropertyAssignment(LocalInfo target, InterfacePropertySignature contract, AloeToken member)
        {
            if (!contract.HasSetter)
                throw Error(member, "Cannot assign a getter-only property.");
            Expect(TokenKind.Assign);
            Emit(EnumOpcode.LoadLocal, target.Slot);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ReferenceRequireLive);
            var expressionStart = _code.Count;
            var actual = ParseExpectedExpression(contract.Type, contract.ClassName);
            actual = AdaptByteLiteral(member, contract.Type, actual, expressionStart);
            RequireAssignable(member, contract.Type, actual, contract.ClassName, _lastExpressionClassName);
            Expect(TokenKind.Semicolon);
            EmitInterfacePropertyDispatch(target.ClassName!, contract, member, write: true);
        }

        // Builds the known-implementer table (class=function index) used by the runtime type selection.
        private void EmitInterfacePropertyDispatch(string interfaceName, InterfacePropertySignature contract,
            AloeToken token, bool write)
        {
            var entries = new List<string>();
            foreach (var candidate in _classes.Values.Where(candidate => IsClassAssignable(candidate.Name, interfaceName)))
            {
                var property = FindProperty(candidate.Name, contract.Name);
                var accessor = write ? property?.Setter : property?.Getter;
                if (accessor == null)
                    throw Error(token, $"Class '{candidate.Name}' does not implement property contract '{interfaceName}.{contract.Name}'.");
                RecordTaskSynchronousCall(accessor.Index, token);
                entries.Add($"{candidate.Name}={accessor.Index.ToString(CultureInfo.InvariantCulture)}");
            }
            EmitIntConstant(AddConstant(AloeValue.FromString(string.Join(";", entries))));
            EmitIntConstant(write ? 1 : 0);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.InterfacePropertyDispatch);
        }
    }
}
