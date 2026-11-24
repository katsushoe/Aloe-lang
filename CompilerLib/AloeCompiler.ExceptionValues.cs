using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    // Decision 0059: exception value types. Chain lists the type, then its bases, ending at a built-in type.
    private sealed record ExceptionTypeInfo(string Name, IReadOnlyList<string> Chain, bool IsBuiltin)
    {
        public string BuiltinRoot => Chain[^1];
    }

    private sealed partial class Parser
    {
        private readonly Dictionary<string, ExceptionTypeInfo> _exceptionTypes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _exceptionDeclarationsPendingBase = new(StringComparer.Ordinal);

        private static List<FieldSignature> ExceptionHeaderFields(string owner) => new()
        {
            new FieldSignature("Message", 0, ExprType.String, DeclaringClass: owner),
            new FieldSignature("TypeName", 1, ExprType.String, DeclaringClass: owner)
        };

        private void RegisterBuiltinExceptionTypes()
        {
            _exceptionTypes.Clear();
            _exceptionDeclarationsPendingBase.Clear();
            foreach (var name in AloeExceptionTypes.Names)
            {
                _structs.Add(name, new StructSignature(name, ExceptionHeaderFields(name)));
                _exceptionTypes.Add(name, new ExceptionTypeInfo(name, new[] { name }, IsBuiltin: true));
            }
        }

        // exception Name [: Base] {  -- called with position at "exception"; leaves position after "{".
        private string CollectExceptionHeader(ref int position)
        {
            position++; // exception
            var name = TokenAt(position++);
            if (name.Kind != TokenKind.Identifier || TryGetNamedBoxType(name.Lexeme, out _) ||
                _enumMembers.ContainsKey(name.Lexeme) || _structs.ContainsKey(name.Lexeme))
                throw Error(name, "Exception name must be a unique type identifier.");
            var baseName = "Exception";
            if (TokenAt(position).Kind == TokenKind.Colon)
            {
                position++;
                var baseToken = TokenAt(position++);
                if (baseToken.Kind != TokenKind.Identifier || !_exceptionTypes.ContainsKey(baseToken.Lexeme))
                    throw Error(baseToken, "An exception base must be a built-in or previously declared exception type.");
                baseName = baseToken.Lexeme;
            }
            ExpectAt(ref position, TokenKind.LBrace);
            _structs.Add(name.Lexeme, new StructSignature(name.Lexeme, ExceptionHeaderFields(name.Lexeme)));
            _exceptionTypes.Add(name.Lexeme, new ExceptionTypeInfo(name.Lexeme,
                new[] { name.Lexeme }.Concat(_exceptionTypes[baseName].Chain).ToArray(), IsBuiltin: false));
            _exceptionDeclarationsPendingBase.Add(name.Lexeme);
            return name.Lexeme;
        }

        // Derived exceptions start with every field of their base, in order (bases are declared earlier).
        private void InheritExceptionFields(StructSignature signature)
        {
            if (!_exceptionDeclarationsPendingBase.Remove(signature.Name))
                return;
            var baseFields = _structs[_exceptionTypes[signature.Name].Chain[1]].Fields.Skip(2);
            foreach (var field in baseFields)
                signature.Fields.Add(field with { Index = signature.Fields.Count });
        }

        private bool IsExceptionType(string? name) => name != null && _exceptionTypes.ContainsKey(name);

        // Built-in ExceptionInfo and built-in exception types are always registered; anything else came from source.
        private bool HasSourceStructTypes()
            => _structs.Keys.Any(name => name != "ExceptionInfo" &&
                !(_exceptionTypes.TryGetValue(name, out var info) && info.IsBuiltin));

        private void RejectExceptionMember(StructSignature signature, AloeToken token)
        {
            if (IsExceptionType(signature.Name))
                throw Error(token, "Exception declarations may declare fields only; methods, properties and constructors are not supported.");
        }

        // throw value;  -- the value must be an exception type (decision 0059).
        private void ParseThrowValue(AloeToken throwToken)
        {
            var type = ParseExpression();
            var name = _lastExpressionClassName;
            if (type != ExprType.Struct || !_exceptionTypes.TryGetValue(name ?? string.Empty, out var info))
                throw Error(throwToken, "throw requires an exception value; declare the type with 'exception'.");
            Expect(TokenKind.Semicolon);
            EmitIntConstant(AddConstant(AloeValue.FromString(string.Join("|", info.Chain))));
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ExceptionThrow);
        }

        private bool TryGetDeclaredExceptionCatch(string typeName, out ExceptionTypeInfo info)
            => _exceptionTypes.TryGetValue(typeName, out info!) && !info.IsBuiltin;

        private void EmitCaughtExceptionValue(string typeName)
        {
            EmitIntConstant(AddConstant(AloeValue.FromString(typeName)));
            EmitIntConstant(_structs[typeName].Fields.Count);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.CaughtExceptionValue);
        }
    }
}
