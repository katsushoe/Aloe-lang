using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private IReadOnlyList<TaskDeclaration> _taskDeclarations = Array.Empty<TaskDeclaration>();
        private int _taskProtectedDepth;
        private bool InTask => _currentFunction?.DeclaringClass is string name && _classes.TryGetValue(name, out var type) && type.IsTask;

        private void CollectTaskFunctions()
        {
            foreach (var task in _taskDeclarations)
                _classes.Add(task.Name, new ClassSignature(task.Name, null, true) { IsTask = true });
            foreach (var task in _taskDeclarations)
            {
                var type = _classes[task.Name];
                foreach (var field in task.Fields)
                {
                    var kind = TaskType(field.TypeName, out var name);
                    type.Fields.Add(new FieldSignature(field.Name, type.Fields.Count, kind, name, DeclaringClass: task.Name));
                }
                if (task.ConstructorBody is TaskBody constructor)
                    type.Constructor = TaskFunction(task.Name, "construct", task.ConstructorParameters, constructor, true);
                else if (type.Fields.Count != 0)
                    throw new AloeCompileException("Task fields require a constructor to initialize them.");
                foreach (var method in task.Methods)
                    type.Methods.Add(new MethodSignature(method.Name, TaskFunction(task.Name, method.Name, method.Parameters, method.Body, false)));
            }
        }

        private FunctionSignature TaskFunction(string owner, string name, IReadOnlyList<TaskParameter> parameters, TaskBody body, bool constructor)
        {
            var bindings = parameters.Select(parameter =>
            {
                var kind = TaskType(parameter.TypeName, out var typeName);
                var token = Enumerable.Range(0, body.Start).Last(index => _tokens[index].Lexeme == parameter.Name && TokenAt(index + 1).Kind == TokenKind.Colon);
                return new ParameterSignature(_tokens[token], kind, typeName);
            }).ToArray();
            var function = new FunctionSignature(owner + "." + name, _functions.Count, bindings, ExprType.Void,
                body.Start + 1, body.End - 1, false, DeclaringClass: owner, IsConstructor: constructor);
            _functions.Add(function);
            return function;
        }

        private ExprType TaskType(string type, out string? name)
        {
            name = null;
            var kind = type switch
            {
                "int" => ExprType.Int, "bool" => ExprType.Bool, "byte" => ExprType.Byte, "char" => ExprType.Char,
                "float" => ExprType.Float, "decimal" => ExprType.Decimal, "string" => ExprType.String,
                "object" => ExprType.Object, _ => ExprType.Unknown
            };
            if (kind != ExprType.Unknown) return kind;
            if (!_classes.TryGetValue(type, out var reference) || reference.IsTask)
                throw new AloeCompileException("Task signatures accept scalars and ordinary class/object references only.");
            name = type; return ExprType.Object;
        }

        private static EnumValueKind TaskKind(ExprType type) => type switch
        {
            ExprType.Int => EnumValueKind.Int, ExprType.Bool => EnumValueKind.Bool, ExprType.Byte => EnumValueKind.Byte,
            ExprType.Char => EnumValueKind.Char, ExprType.Float => EnumValueKind.Float, ExprType.Decimal => EnumValueKind.Decimal,
            ExprType.String => EnumValueKind.String, ExprType.Object => EnumValueKind.Object,
            _ => throw new AloeCompileException("Unsupported Task signature kind.")
        };

        private IReadOnlyList<AloeTaskTypeInfo> EmitTaskTypes()
            => _taskDeclarations.Select(task => _classes[task.Name]).Select(type => new AloeTaskTypeInfo(type.Name,
                type.Fields.Select(field => TaskKind(field.Type)).ToArray(), type.Constructor?.Index,
                type.Constructor?.Parameters.Select(parameter => TaskKind(parameter.Type)).ToArray() ?? Array.Empty<EnumValueKind>(),
                type.Methods.Select(method => new AloeTaskMethodInfo(method.Name, method.Function.Index,
                    method.Function.Parameters.Select(parameter => TaskKind(parameter.Type)).ToArray())).ToArray())).ToArray();

        private int ParseTaskArguments(IReadOnlyList<ParameterSignature> parameters, AloeToken name)
        {
            Expect(TokenKind.LParen); var count = 0;
            if (!Check(TokenKind.RParen))
                while (true)
                {
                    if (count >= parameters.Count) throw Error(Current, "Too many Task arguments.");
                    var parameter = parameters[count++]; var start = _code.Count; var token = Current;
                    var actual = ParseExpectedExpression(parameter.Type, parameter.ClassName);
                    actual = AdaptByteLiteral(token, parameter.Type, actual, start);
                    RequireAssignable(token, parameter.Type, actual, parameter.ClassName, _lastExpressionClassName);
                    if (!Check(TokenKind.Comma)) break; Advance();
                }
            Expect(TokenKind.RParen);
            if (count != parameters.Count) throw Error(name, "Task argument count does not match its signature.");
            return count;
        }

        private ExprType ParseNewTask(ClassSignature task, AloeToken token)
        {
            var count = ParseTaskArguments(task.Constructor?.Parameters ?? Array.Empty<ParameterSignature>(), token);
            Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(task.Name))); EmitIntConstant(count);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.TaskConstruct); _lastExpressionClassName = task.Name; return ExprType.Task;
        }

        // Receiver token has already been consumed; runtime identity decides alias self dispatch.
        private void ParseTaskCall(LocalInfo target, AloeToken receiver, bool throughThis)
        {
            Expect(TokenKind.Dot); var member = Expect(TokenKind.Identifier);
            if (member.Lexeme == "run") throw Error(member, "Task run can only be called by the scheduler.");
            Emit(EnumOpcode.LoadLocal, target.Slot);
            if (member.Lexeme is "start" or "stop")
            {
                ParseTaskArguments(Array.Empty<ParameterSignature>(), member);
                Emit(EnumOpcode.Syscall, (int)(member.Lexeme == "start" ? EnumSyscall.TaskStart : EnumSyscall.TaskStop));
            }
            else
            {
                if (InTask && _taskProtectedDepth > 0 && !throughThis)
                    throw Error(receiver, "Task protected regions require proven synchronous self calls; use this for a self receiver.");
                var method = FindMethod(target.ClassName!, member.Lexeme)
                    ?? throw Error(member, "Unknown Task method.");
                var count = ParseTaskArguments(method.Function.Parameters, member);
                Emit(EnumOpcode.PushConst, AddConstant(AloeValue.FromString(member.Lexeme))); EmitIntConstant(count);
                Emit(EnumOpcode.Syscall, (int)EnumSyscall.TaskInvoke);
            }
            Expect(TokenKind.Semicolon);
        }

        private bool TryParseTaskStatement()
        {
            if (InTask && Current.Lexeme == "this" && Peek(1).Kind == TokenKind.Dot && Peek(3).Kind == TokenKind.LParen)
            {
                var receiver = Advance(); ParseTaskCall(ResolveLocal(receiver), receiver, true); return true;
            }
            if (Current.Lexeme != "delete") return false;
            if (InTask && Peek(1).Lexeme == "this") throw Error(Current, "Task self-delete unwinding is not implemented yet.");
            if (Peek(1).Kind != TokenKind.Identifier || !TryResolveLocal(Peek(1).Lexeme, out var local) || local.Type != ExprType.Task) return false;
            Advance(); Advance(); Expect(TokenKind.Semicolon); Emit(EnumOpcode.LoadLocal, local.Slot); Emit(EnumOpcode.Syscall, (int)EnumSyscall.TaskDelete); return true;
        }

        private ExprType ParseTaskThisRead()
        {
            Advance(); Emit(EnumOpcode.LoadLocal, 0);
            if (!Check(TokenKind.Dot)) { _lastExpressionClassName = _currentFunction!.DeclaringClass; return ExprType.Task; }
            Advance(); var member = Expect(TokenKind.Identifier);
            if (Check(TokenKind.LParen)) throw Error(member, "Task methods return void and cannot be used as values.");
            var field = ResolveField(_currentFunction!.DeclaringClass!, member);
            EmitIntConstant(field.Index); Emit(EnumOpcode.Syscall, (int)EnumSyscall.TaskFieldGet);
            _lastExpressionClassName = field.ClassName; return field.Type;
        }

        private bool ParseTryCatch()
        {
            if (InTask) _taskProtectedDepth++;
            try { return ParseTryCatchWithFinally(); }
            finally { if (InTask) _taskProtectedDepth--; }
        }
    }
}
