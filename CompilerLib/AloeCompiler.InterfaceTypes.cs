using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private bool IsClassOrInterface(string? name)
            => name != null && (_classes.ContainsKey(name) || _interfaces.ContainsKey(name));

        private void CollectInterfaceParents(InterfaceSignature target, ref int position)
        {
            if (TokenAt(position).Lexeme != "extends")
                return;
            position++;
            while (true)
            {
                var parentToken = TokenAt(position++);
                if (parentToken.Kind != TokenKind.Identifier || parentToken.Lexeme == target.Name ||
                    !_interfaces.TryGetValue(parentToken.Lexeme, out var parent))
                    throw Error(parentToken, "Interface parents must be previously declared interfaces other than itself.");
                if (target.Parents.Contains(parent.Name, StringComparer.Ordinal))
                    throw Error(parentToken, $"Interface parent '{parent.Name}' is listed more than once.");
                target.Parents.Add(parent.Name);
                foreach (var required in parent.Methods)
                {
                    var existing = target.Methods.FirstOrDefault(method => method.Name == required.Name);
                    if (existing == null)
                        target.Methods.Add(required);
                    else if (!HasMatchingMethodSignature(existing.Function, required.Function.Parameters,
                                 required.Function.ReturnType, required.Function.ReturnClassName))
                        throw Error(parentToken, $"Inherited interface method '{required.Name}' has conflicting signatures in '{target.Name}'.");
                }
                if (TokenAt(position).Kind != TokenKind.Comma)
                    break;
                position++;
            }
        }

        private bool IsInterfaceAssignable(string actualName, string expectedName)
        {
            var pending = new Stack<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            pending.Push(actualName);
            while (pending.TryPop(out var name))
            {
                if (!visited.Add(name) || !_interfaces.TryGetValue(name, out var interfaceInfo))
                    continue;
                if (name == expectedName)
                    return true;
                foreach (var parent in interfaceInfo.Parents)
                    pending.Push(parent);
            }
            return false;
        }

        private ExprType ParseReferenceTypeTest(ExprType operandType, string? operandClassName)
        {
            var operatorToken = Advance();
            if (_forWasm)
                throw Error(operatorToken, "Reference type tests are unsupported by direct WASM.");
            if (operandType != ExprType.Object || IsPipeTypeName(operandClassName))
                throw Error(operatorToken, "The 'is' operator requires a class/object/array reference operand.");

            var targetType = ParseTypeName(out var targetName);
            if (targetType != ExprType.Object)
                throw Error(operatorToken, "The 'is' operator supports reference-type targets only.");

            var descriptor = ReferenceCastDescriptor(operatorToken, targetName);
            Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(descriptor)));
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.ObjectTryCast);

            var result = DeclareImplicitLocal($"$typeTestValue${_nextLocalSlot}", ExprType.Object, operandClassName);
            var success = DeclareImplicitLocal($"$typeTestSuccess${_nextLocalSlot}", ExprType.Bool);
            Emit(EnumOpcode.StoreLocal, success.Slot);
            Emit(EnumOpcode.StoreLocal, result.Slot);
            Emit(EnumOpcode.LoadLocal, success.Slot);
            _lastExpressionClassName = null;
            return ExprType.Bool;
        }

    }
}
