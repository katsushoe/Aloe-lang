using Aloe.CommonLib.Constants;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        private enum TaskNotificationEffect { MayNotify, Unknown }
        private sealed record TaskEffectWitness(TaskNotificationEffect Kind, string Path);
        private sealed record ProtectedTaskCall(int Target, AloeToken Token);
        private readonly Dictionary<int, HashSet<int>> _taskSynchronousEdges = new();
        private readonly Dictionary<int, TaskEffectWitness> _taskNotificationSeeds = new();
        private readonly List<ProtectedTaskCall> _protectedTaskCalls = new();

        private void ResetTaskNotificationEffects()
        {
            _taskSynchronousEdges.Clear();
            _taskNotificationSeeds.Clear();
            _protectedTaskCalls.Clear();
        }

        private void RecordTaskSynchronousCall(int target, AloeToken token)
        {
            if (_taskDeclarations.Count == 0 || _currentFunction == null) return;
            if (!_taskSynchronousEdges.TryGetValue(_currentFunction.Index, out var targets))
                _taskSynchronousEdges.Add(_currentFunction.Index, targets = new HashSet<int>());
            targets.Add(target);
            if (InTask && _taskProtectedDepth > 0)
                _protectedTaskCalls.Add(new ProtectedTaskCall(target, token));
        }

        private void RecordTaskNotification(TaskNotificationEffect kind, string reason)
        {
            if (_taskDeclarations.Count == 0 || _currentFunction == null) return;
            if (InTask && _taskProtectedDepth > 0)
                throw Error(TokenAt(Math.Max(0, _position - 1)),
                    "Task protected call must not transitively notify or have unknown notification effects: " + kind + ": " + reason + ".");
            _taskNotificationSeeds.TryAdd(_currentFunction.Index,
                new TaskEffectWitness(kind, _currentFunction.Name + " -> " + reason));
        }

        // All direct CALL emission routes (including constructors, setters and struct
        // members) pass here. Task and committed getter syscalls are recorded semantically.
        private void RecordTaskCallEmission(EnumOpcode opcode, int operand)
        {
            if (_taskDeclarations.Count == 0 || _currentFunction == null) return;
            if (opcode == EnumOpcode.Call)
                RecordTaskSynchronousCall(operand, TokenAt(Math.Max(0, _position - 1)));
            else if (opcode == EnumOpcode.Syscall)
            {
                var syscall = (EnumSyscall)operand;
                if (syscall is EnumSyscall.InstanceAsyncEnqueue or EnumSyscall.StaticAsyncEnqueue or
                    EnumSyscall.InstanceVirtualAsyncEnqueue)
                    RecordTaskNotification(TaskNotificationEffect.MayNotify, syscall.ToString());
                else if (syscall is EnumSyscall.Tick or EnumSyscall.PipeBindFilter or
                    EnumSyscall.PipeWrite or EnumSyscall.PipeClose or EnumSyscall.PipeTryRead)
                    // Tick and pipes can execute callbacks registered outside this
                    // function. No source-only proof of those dynamic bindings exists.
                    RecordTaskNotification(TaskNotificationEffect.Unknown, syscall + " callback dispatch");
            }
        }

        private void EmitResolvedPropertyGetter(FunctionSignature getter, AloeToken token)
        {
            RecordTaskSynchronousCall(getter.Index, token);
            EmitIntConstant(getter.Index);
            Emit(EnumOpcode.Syscall, (int)EnumSyscall.InstancePropertyGet);
        }

        private void ValidateTaskNotificationEffects()
        {
            if (_protectedTaskCalls.Count == 0) return;
            var callers = new Dictionary<int, List<int>>();
            foreach (var (caller, targets) in _taskSynchronousEdges)
                foreach (var target in targets)
                {
                    if (!callers.TryGetValue(target, out var parents))
                        callers.Add(target, parents = new List<int>());
                    parents.Add(caller);
                }
            var effects = new Dictionary<int, TaskEffectWitness>(_taskNotificationSeeds);
            var pending = new Queue<int>(effects.Keys);
            while (pending.TryDequeue(out var target))
            {
                if (!callers.TryGetValue(target, out var parents)) continue;
                foreach (var caller in parents)
                    if (!effects.ContainsKey(caller))
                    {
                        var witness = effects[target];
                        effects.Add(caller, new TaskEffectWitness(witness.Kind,
                            _functions[caller].Name + " -> " + witness.Path));
                        pending.Enqueue(caller);
                    }
            }
            foreach (var call in _protectedTaskCalls)
                if (effects.TryGetValue(call.Target, out var witness))
                    throw Error(call.Token, "Task protected call must not transitively notify or have unknown notification effects: " +
                        witness.Kind + ": " + witness.Path + ".");
        }
    }
}
