using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib.OpCommand;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;


namespace Aloe.RuntimeLib
{
    /// <summary>
    /// Aloe バイトコードを実行する VM 本体。
    /// 
    /// ・Command パターンによるオペコードディスパッチ
    /// ・CallStack / CallFrame による関数呼び出し管理
    /// ・ValueStack によるオペランドスタック
    /// </summary>
    public sealed class AloeVm
    {
        /// <summary>実行対象モジュール（バイトコード＋メタ情報）。</summary>
        private readonly Module _module;


        /// <summary>オペコード → コマンド実装 のディスパッチテーブル。</summary>
        private readonly IReadOnlyDictionary<EnumOpcode, IOpcodeCommand> _opcodeCommands;

        private readonly HostSyscallRegistry _hostSyscalls;


        /// <summary>評価スタック（オペランドスタック）。</summary>
        private readonly Stack<AloeValue> _valueStack = new();


        /// <summary>コールスタック。</summary>
        private readonly CallStack _callStack = new();

        private sealed record PendingInstanceCall(long TargetObjectId, int FunctionIndex, AloeValue[] Arguments);

        /// <summary>Prototype instance CallBuffer. Entries are drained synchronously by tick().</summary>
        private readonly Queue<PendingInstanceCall> _instanceCallBuffer = new();

        private sealed class FilterBinding
        {
            public FilterBinding(int functionIndex, long outputPipeId)
            {
                FunctionIndex = functionIndex;
                OutputPipeId = outputPipeId;
            }

            public int FunctionIndex { get; }
            public long OutputPipeId { get; }
            public VmExecutionContext? Task { get; set; }
        }

        private sealed class VmExecutionContext
        {
            public VmExecutionContext(long inputPipeId = 0, FilterBinding? binding = null)
            {
                InputPipeId = inputPipeId;
                Binding = binding;
            }

            public long InputPipeId { get; }
            public FilterBinding? Binding { get; }
            public bool IsMain => Binding == null;
            public List<CallFrame> SuspendedFramesTopFirst { get; } = new();
            public List<AloeValue> SuspendedValuesTopFirst { get; } = new();
            public long? WaitingWritePipeId { get; set; }
            public long? WaitingReadPipeId { get; set; }
            public bool Started { get; set; }
            public bool IsCompleted { get; set; }
            public bool IsScheduled { get; set; }
        }

        private sealed class PipeState
        {
            public PipeState(string typeName, int? capacity)
            {
                TypeName = typeName;
                Capacity = capacity;
            }

            public string TypeName { get; }
            public int? Capacity { get; }
            public bool IsFull => Capacity is int capacity && Values.Count >= capacity;
            public Queue<AloeValue> Values { get; } = new();
            public List<FilterBinding> Filters { get; } = new();
            public Queue<VmExecutionContext> WaitingWriters { get; } = new();
            public Queue<VmExecutionContext> WaitingReaders { get; } = new();
            public bool IsClosed { get; set; }
        }

        private readonly Dictionary<long, PipeState> _pipes = new();
        private readonly Queue<VmExecutionContext> _readyContexts = new();
        private readonly List<VmExecutionContext> _allContexts = new();
        private VmExecutionContext? _runningContext;
        private const int InstructionQuantum = 1024;
        private bool _contextSuspendRequested;
        private bool _suspendCurrentInstruction;

        /// <summary>
        /// Depth of host-initiated synchronous calls (property getters, tick() instance calls).
        /// These run to completion inside one SYSCALL and cannot be suspended by the scheduler.
        /// </summary>
        private int _synchronousCallDepth;

        /// <summary>True while tick() is executing queued public async instance methods.</summary>
        private bool _tickActive;

        /// <summary>Nested synchronous property-query depth. Field reads use Committed State while positive.</summary>
        private int _committedQueryDepth;


        /// <summary>停止要求フラグ。</summary>
        private bool _haltRequested;

        /// <summary>Whether GC diagnostics were enabled at any point during the current program run.</summary>
        private bool _diagnosticsRequestedDuringRun;

        /// <summary>C# VM reference heap. ObjectId remains stable across GC movement.</summary>
        public AloeHeap Heap { get; }

        /// <summary>Plan -> Sweep/Move -> FreeBlock garbage collector.</summary>
        public AloeGarbageCollector GarbageCollector { get; }

        /// <summary>Dedicated VM logger configured by aloevm.json.</summary>
        public AloeLogger Logger { get; }


        /// <summary>現在のフレーム。コールスタックが空なら null。</summary>
        public CallFrame? CurrentFrame => _callStack.Count > 0 ? _callStack.Peek() : null;


        /// <summary>評価スタック本体。</summary>
        public Stack<AloeValue> ValueStack => _valueStack;


        /// <summary>
        /// 一部のコマンドが OperandStack を参照しているため、
        /// ValueStack の別名として公開する。
        /// </summary>
        public Stack<AloeValue> OperandStack => _valueStack;


        /// <summary>停止要求が出ているかどうか。</summary>
        public bool HaltRequested => _haltRequested;


        /// <summary>
        /// 現在のコールスタックを公開するプロパティ。
        /// ReturnCommand などが深さを確認するために使う。
        /// </summary>
        public CallStack CallStack => _callStack;


        /// <summary>
        /// この VM が現在実行対象としているモジュール。
        /// Command から関数テーブルや定数テーブルにアクセスするために公開する。
        /// </summary>
        public Module Module => _module;


        /// <summary>トレースを有効にするかどうか。</summary>
        public bool TraceEnabled { get; set; }


        /// <summary>
        /// トレース出力先。デフォルトは Console.WriteLine。
        /// null の場合は何もしない。
        /// </summary>
        public Action<string>? TraceWriter { get; set; }


        /// <summary>Program output destination. Defaults to Console.WriteLine.</summary>
        public Action<string>? OutputWriter { get; set; } = Console.WriteLine;

        /// <summary>Host input provider used by readLine().</summary>
        public Func<string?> InputReader { get; set; } = Console.ReadLine;

        /// <summary>Host sleep provider used by sleep(ms). Replaceable for deterministic tests.</summary>
        public Action<int> SleepHandler { get; set; } = Thread.Sleep;

        /// <summary>Capabilities granted to host-facing syscalls.</summary>
        public AloeHostCapability AllowedHostCapabilities { get; }


        /// <summary>
        /// VM インスタンスを生成する。
        /// </summary>
        /// <param name="module">実行するモジュール。</param>
        /// <param name="opcodeCommands">
        /// EnumOpcode ごとの IOpcodeCommand 実装。
        /// 例: new Dictionary&lt;EnumOpcode, IOpcodeCommand&gt; { { EnumOpcode.Add, new AddCommand() }, ... }
        /// </param>
        public AloeVm(Module module)
            : this(module, AloeVmSettings.Load())
        {
        }

        public AloeVm(Module module, AloeVmSettings settings)
        {
            _module = module ?? throw new ArgumentNullException(nameof(module));
            settings ??= new AloeVmSettings();
            AloeBytecodeVerifier.Verify(_module);
            _opcodeCommands = CreateDefaultOpcodeCommands();
            _hostSyscalls = HostSyscallRegistry.CreateDefault();
            Heap = new AloeHeap();
            GarbageCollector = new AloeGarbageCollector(Heap);
            Logger = new AloeLogger(settings.Logging, settings.ConfigDirectory);
            TraceEnabled = settings.Debug;
            TraceWriter = message => Logger.Debug(message);
            GarbageCollector.DebugEnabled = settings.Debug;
            GarbageCollector.LogWriter = TraceWriter;
            AllowedHostCapabilities = settings.AllowedHostCapabilities;
        }


        private static IReadOnlyDictionary<EnumOpcode, IOpcodeCommand> CreateDefaultOpcodeCommands()
        {
            // プロジェクト内で実装済みのコマンドだけ登録してください。
            // （まだ無いものがあればコメントアウトでOK）
            return new Dictionary<EnumOpcode, IOpcodeCommand>
            {
                { EnumOpcode.Nop,         new NopCommand()         },
                { EnumOpcode.PushConst,   new PushConstCommand()   },
                { EnumOpcode.LoadLocal,   new LoadLocalCommand()   },
                { EnumOpcode.StoreLocal,  new StoreLocalCommand()  },


                { EnumOpcode.Add,         new AddCommand()         },
                { EnumOpcode.Sub,         new SubCommand()         },
                { EnumOpcode.Mul,         new MulCommand()         },
                { EnumOpcode.Div,         new DivCommand()         },


                { EnumOpcode.Mod,         new ModCommand()         },


                { EnumOpcode.CmpEq,       new CmpEqCommand()       },
                { EnumOpcode.CmpNe,       new CmpNeCommand()       },
                { EnumOpcode.CmpLt,       new CmpLtCommand()       },
                { EnumOpcode.CmpLe,       new CmpLeCommand()       },
                { EnumOpcode.CmpGt,       new CmpGtCommand()       },
                { EnumOpcode.CmpGe,       new CmpGeCommand()       },


                { EnumOpcode.Block,       new BlockCommand()       },
                { EnumOpcode.Loop,        new LoopCommand()        },
                { EnumOpcode.If,          new IfCommand()          },
                { EnumOpcode.Else,        new ElseCommand()        },
                { EnumOpcode.End,         new EndCommand()         },
                { EnumOpcode.Br,          new BrCommand()          },
                { EnumOpcode.BrIf,        new BrIfCommand()        },


                { EnumOpcode.Call,        new CallCommand()        },
                { EnumOpcode.Return,      new ReturnCommand()      },


                { EnumOpcode.Syscall,     new SyscallCommand()     },
                { EnumOpcode.Halt,        new HaltCommand()        },
            };
        }


        private (int EndIp, int ElseIp) FindControlBounds(int startIp)
        {
            var code = _module.Code;
            if ((uint)startIp >= (uint)code.Count)
                throw new VmException($"Control start IP out of range: {startIp}.");

            var startOpcode = code[startIp].Opcode;
            if (startOpcode is not (EnumOpcode.Block or EnumOpcode.Loop or EnumOpcode.If))
                throw new VmException($"Instruction at {startIp} is not a structured-control opener.");

            var depth = 0;
            var elseIp = -1;

            for (var ip = startIp + 1; ip < code.Count; ip++)
            {
                switch (code[ip].Opcode)
                {
                    case EnumOpcode.Block:
                    case EnumOpcode.Loop:
                    case EnumOpcode.If:
                        depth++;
                        break;

                    case EnumOpcode.Else:
                        if (depth == 0 && startOpcode == EnumOpcode.If)
                            elseIp = ip;
                        break;

                    case EnumOpcode.End:
                        if (depth == 0)
                            return (ip, elseIp);
                        depth--;
                        break;
                }
            }

            throw new VmException($"Unclosed structured-control instruction at IP {startIp}.");
        }

        public void EnterControl(CallFrame frame, ControlFrameKind kind, int startIp)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            var bounds = FindControlBounds(startIp);
            frame.ControlStack.Push(new ControlFrame(kind, startIp, bounds.EndIp, bounds.ElseIp));
        }

        public void EnterIf(CallFrame frame, int startIp, bool condition)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            var bounds = FindControlBounds(startIp);
            var control = new ControlFrame(ControlFrameKind.If, startIp, bounds.EndIp, bounds.ElseIp);
            frame.ControlStack.Push(control);

            if (!condition)
            {
                // Keep the IF frame active so END can close it normally.
                frame.Ip = control.ElseIp >= 0 ? control.ElseIp + 1 : control.EndIp;
            }
        }

        public void EnterElse(CallFrame frame, int elseIp)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (frame.ControlStack.Count == 0)
                throw new VmException($"ELSE at IP {elseIp} has no active IF frame.");

            var control = frame.ControlStack.Peek();
            if (control.Kind != ControlFrameKind.If || control.ElseIp != elseIp)
                throw new VmException($"ELSE at IP {elseIp} does not match the active IF frame.");

            // The then branch completed, so skip the else body but execute END to pop the IF frame.
            frame.Ip = control.EndIp;
        }

        public void LeaveControl(CallFrame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (frame.ControlStack.Count == 0)
                throw new VmException($"END at IP {frame.Ip} has no active control frame.");

            var control = frame.ControlStack.Peek();
            if (control.EndIp != frame.Ip)
                throw new VmException(
                    $"END at IP {frame.Ip} does not match active {control.Kind} ending at {control.EndIp}.");

            frame.ControlStack.Pop();
        }

        public void Branch(CallFrame frame, int depth)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (depth < 0 || depth >= frame.ControlStack.Count)
                throw new VmException(
                    $"Invalid branch depth {depth}; active labels={frame.ControlStack.Count}.");

            var labels = frame.ControlStack.ToArray(); // innermost first
            var target = labels[depth];

            if (target.Kind == ControlFrameKind.Loop)
            {
                // Discard labels nested inside the loop, but keep the loop label active.
                for (var i = 0; i < depth; i++)
                    frame.ControlStack.Pop();

                frame.Ip = target.StartIp + 1;
                return;
            }

            // Branching to BLOCK/IF exits the target label as well as all inner labels.
            for (var i = 0; i <= depth; i++)
                frame.ControlStack.Pop();

            frame.Ip = target.EndIp + 1;
        }

        /// <summary>
        /// VM に停止要求を出す。
        /// Halt 命令（HaltCommand）から呼び出される想定。
        /// </summary>
        public void RequestHalt()
        {
            _haltRequested = true;
        }


        /// <summary>
        /// 新しいコールフレームをプッシュする（関数呼び出し時に使用）。
        /// </summary>
        public void PushFrame(CallFrame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            _callStack.Push(frame);
        }


        /// <summary>
        /// 現在のコールフレームをポップする（return 時に使用）。
        /// </summary>
        public CallFrame PopFrame()
        {
            return _callStack.Pop();
        }


        /// <summary>
        /// エントリポイント（module.EntryPointIndex）から実行するヘルパー。
        /// </summary>
        public void RunFromEntryPoint()
        {
            var programStarted = Stopwatch.GetTimestamp();
            var managedBytesAtStart = System.GC.GetTotalMemory(false);
            var allocatedBytesAtStart = System.GC.GetTotalAllocatedBytes(false);
            var gen0CollectionsAtStart = System.GC.CollectionCount(0);
            var gen1CollectionsAtStart = System.GC.CollectionCount(1);
            var gen2CollectionsAtStart = System.GC.CollectionCount(2);
            GarbageCollector.ResetMetrics();
            _diagnosticsRequestedDuringRun = GarbageCollector.DebugEnabled;

            // エントリポイントの FunctionInfo を取得
            if (_module.EntryPointIndex < 0 || _module.EntryPointIndex >= _module.Functions.Count)
            {
                throw new VmException(
                    $"EntryPointIndex is out of range. index={_module.EntryPointIndex}, count={_module.Functions.Count}");
            }


            var entryFunction = _module.Functions[_module.EntryPointIndex];


            // ローカル変数領域の確保
            var localCount = entryFunction.LocalCount;   // FunctionInfo に LocalCount プロパティがある前提
            var locals = localCount > 0
                ? new AloeValue[localCount]
                : Array.Empty<AloeValue>();


            // 関数エントリの IP（命令インデックス）
            // ※ FunctionInfo 側のプロパティ名に合わせてここは調整してください。
            //   たとえば EntryIp / CodeOffset / EntryPoint など。
            var entryIp = entryFunction.EntryIp; // もし EntryIp が無ければ適宜修正


            // ★ ここがポイント：returnAddress を追加で渡す（エントリフレームなので -1 などの番兵値）
            var frame = new CallFrame(
                module: _module,
                function: entryFunction,
                ip: entryIp,
                returnAddress: -1,   // 「戻り先なし」を意味する値
                locals: locals
            );


            _callStack.Clear();
            _valueStack.Clear();
            _instanceCallBuffer.Clear();
            _pipes.Clear();
            _readyContexts.Clear();
            _allContexts.Clear();
            _runningContext = null;
            _contextSuspendRequested = false;
            _suspendCurrentInstruction = false;
            _tickActive = false;
            _committedQueryDepth = 0;
            _synchronousCallDepth = 0;
            _haltRequested = false;


            _callStack.Push(frame);


            Run();

            if (_diagnosticsRequestedDuringRun)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(programStarted).TotalMilliseconds;
                var managedBytesAtEnd = System.GC.GetTotalMemory(false);
                var allocatedBytesAtEnd = System.GC.GetTotalAllocatedBytes(false);
                var gen0Collections = System.GC.CollectionCount(0) - gen0CollectionsAtStart;
                var gen1Collections = System.GC.CollectionCount(1) - gen1CollectionsAtStart;
                var gen2Collections = System.GC.CollectionCount(2) - gen2CollectionsAtStart;
                TraceWriter?.Invoke(
                    $"[PERF] Program end: elapsedMs={elapsedMs:F3}, gcElapsedMs={GarbageCollector.TotalElapsedMilliseconds:F3}, " +
                    $"heapUsedBytes={Heap.UsedBytes}, heapReservedBytes={Heap.ReservedBytes}, " +
                    $"managedBytes={managedBytesAtEnd}, managedStartBytes={managedBytesAtStart}, " +
                    $"managedDeltaBytes={managedBytesAtEnd - managedBytesAtStart}, allocatedDeltaBytes={allocatedBytesAtEnd - allocatedBytesAtStart}, " +
                    $"hostGcGen0={gen0Collections}, hostGcGen1={gen1Collections}, hostGcGen2={gen2Collections}.");
            }
        }


        /// <summary>
        /// 現在のコールスタック状態から実行を開始 / 継続するメインループ。
        /// 事前に最低 1 つの CallFrame が Push されている必要がある。
        /// </summary>
        public void Run()
        {
            if (_runningContext != null)
                throw new VmException("Nested scheduler execution is not supported.");

            // A fresh entry frame becomes the main context. A second Run() with
            // no frames or ready contexts must be a harmless no-op.
            VmExecutionContext? mainContext = null;
            if (!_callStack.IsEmpty)
            {
                if (_allContexts.Count != 0)
                    throw new VmException("Cannot start a new main context while a scheduler exists.");
                mainContext = new VmExecutionContext { Started = true };
                _allContexts.Add(mainContext);
                SaveContext(mainContext);
                EnqueueContext(mainContext);
            }
            else
            {
                mainContext = _allContexts.FirstOrDefault(context => context.IsMain);
            }

            while (!_haltRequested && _readyContexts.Count > 0)
            {
                var context = _readyContexts.Dequeue();
                context.IsScheduled = false;
                if (!context.IsCompleted)
                    RunContextQuantum(context);
            }

            // A suspended filter is just as important as a suspended main.
            // Otherwise the scheduler silently reports success with unfinished work.
            if (!_haltRequested && _allContexts.Any(context => !context.IsCompleted))
            {
                var waitingReaders = _allContexts.Count(context => !context.IsCompleted && context.WaitingReadPipeId != null);
                var waitingWriters = _allContexts.Count(context => !context.IsCompleted && context.WaitingWritePipeId != null);
                var stalledContexts = _allContexts.Count(context => !context.IsCompleted);
                throw new VmException(
                    $"Pipe scheduler deadlock: no runnable execution context remains " +
                    $"(stalled={stalledContexts}, readers={waitingReaders}, writers={waitingWriters}).");
            }

            if (mainContext != null && mainContext.IsCompleted)
            {
                // Keep the externally observable main return value on ValueStack.
                for (var i = mainContext.SuspendedValuesTopFirst.Count - 1; i >= 0; i--)
                    _valueStack.Push(mainContext.SuspendedValuesTopFirst[i]);
                mainContext.SuspendedValuesTopFirst.Clear();
            }
        }

        private void RunUntilCallDepth(int depth)
        {
            while (!_haltRequested && _callStack.Count > depth)
                ExecuteOneInstruction();
        }

        private void ExecuteOneInstruction()
        {
            _suspendCurrentInstruction = false;
            var frame = _callStack.Peek();
            var code = _module.Code;
            var ip = frame.Ip;
            var codeCount = code.Count;

            if (ip < 0)
                throw new VmException($"Instruction pointer out of range. ip={ip}, codeCount={codeCount}");

            if (ip == codeCount)
            {
                _callStack.Pop();
                return;
            }

            if (ip > codeCount)
                throw new VmException($"Instruction pointer out of range. ip={ip}, codeCount={codeCount}");

            var instruction = code[ip];
            if (!_opcodeCommands.TryGetValue(instruction.Opcode, out var command))
                throw new VmException($"Unknown opcode: {instruction.Opcode}");

            if (TraceEnabled)
                TraceWriter?.Invoke(FormatTrace(frame, in instruction));

            var beforeIp = ip;
            var beforeFrame = frame;
            command.Execute(this, frame, in instruction);

            if (_haltRequested || _callStack.IsEmpty)
                return;

            var currentTop = _callStack.Peek();
            if (!_suspendCurrentInstruction && ReferenceEquals(currentTop, beforeFrame) && currentTop.Ip == beforeIp)
                currentTop.Ip = beforeIp + 1;
        }

        private string FormatTrace(CallFrame frame, in Instruction instruction)
        {
            // スタック上位 5 個くらいだけ見る（上が一番右）
            var stackPreview = _valueStack
                .Reverse()         // bottom ... top にする
                .Take(5)
                .Select(v => v.ToString())
                .ToArray();


            var stackText = stackPreview.Length == 0
                ? "(empty)"
                : string.Join(", ", stackPreview);


            return
                $"IP={frame.Ip:0000} " +
                $"OP={instruction.Opcode} " +
                $"O0={instruction.Operand0} " +
                $"O1={instruction.Operand1} " +
                $"| STACK=[{stackText}]";
        }


        /// <summary>
        /// 評価スタックに値をプッシュするヘルパー。
        /// </summary>
        public void Push(AloeValue value)
        {
            _valueStack.Push(value);
        }


        /// <summary>
        /// 評価スタックから 1 つポップするヘルパー。
        /// </summary>
        public AloeValue Pop()
        {
            return _valueStack.Pop();
        }


        /// <summary>
        /// 評価スタックの先頭をポップせず参照するヘルパー。
        /// </summary>
        public AloeValue Peek()
        {
            return _valueStack.Peek();
        }


        // システムコール ID -> 実装
        private readonly Dictionary<EnumSyscall, Action<AloeVm>> _syscalls
            = new Dictionary<EnumSyscall, Action<AloeVm>>();


        /// <summary>
        /// システムコールを登録する。
        /// 例えば id=0 を print、id=1 を readLine など。
        /// </summary>
        public void RegisterSyscall(EnumSyscall id, Action<AloeVm> impl)
        {
            if (impl == null) throw new ArgumentNullException(nameof(impl));
            _syscalls[id] = impl;
        }


        /// <summary>
        /// SyscallCommand から呼び出される内部 API。
        /// </summary>
        public void InvokeSyscall(EnumSyscall syscallId)
        {
            switch (syscallId)
            {
                case EnumSyscall.Print:
                    {
                        // スタックトップを取り出して、そのまま ToString() して出力
                        var v = Pop();
                        OutputWriter?.Invoke(v.ToString());
                        break;
                    }

                case EnumSyscall.GCRequire:
                    RequireGc();
                    break;

                case EnumSyscall.GCFinish:
                    FinishGc();
                    break;

                case EnumSyscall.Tick:
                    Tick();
                    break;

                case EnumSyscall.GCSetDebug:
                    {
                        var value = Pop();
                        if (!value.IsBool)
                            throw new VmException($"GC.debug expects bool; found {value.Kind}.");
                        SetGcDebug(value.AsBool);
                        break;
                    }

                case EnumSyscall.GCGetDebug:
                    Push(AloeValue.FromBool(GarbageCollector.DebugEnabled));
                    break;

                case EnumSyscall.ObjectAllocate:
                    {
                        var fieldCountValue = Pop();
                        var typeNameValue = Pop();
                        if (!fieldCountValue.IsInt || fieldCountValue.AsInt < 0 || fieldCountValue.AsInt > int.MaxValue)
                            throw new VmException("Object allocation expects a non-negative int field count.");
                        if (!typeNameValue.IsString)
                            throw new VmException($"Object allocation expects a class type name string; found {typeNameValue.Kind}.");

                        var fieldCount = (int)fieldCountValue.AsInt;
                        var typeName = typeNameValue.AsString;
                        var objectId = Heap.Allocate(ReadOnlySpan<byte>.Empty, fieldCount: fieldCount, typeName: typeName);
                        Push(AloeValue.FromObject(objectId));
                        if (GarbageCollector.DebugEnabled)
                            TraceWriter?.Invoke($"[GC] Allocate: object={objectId}, type={typeName}, fields={fieldCount}, bytes={Heap.GetEntry(objectId).AllocatedBytes}.");
                        break;
                    }

                case EnumSyscall.ObjectFieldGet:
                    {
                        var fieldIndexValue = Pop();
                        var objectValue = Pop();
                        if (!fieldIndexValue.IsInt || fieldIndexValue.AsInt < 0 || fieldIndexValue.AsInt > int.MaxValue)
                            throw new VmException("Object field read expects a non-negative int field index.");
                        if (!objectValue.IsObject)
                            throw new VmException($"Object field read expects an object; found {objectValue.Kind}.");

                        var fieldIndex = (int)fieldIndexValue.AsInt;
                        var fieldValue = _committedQueryDepth > 0
                            ? Heap.GetCommittedField(objectValue.AsObjectId, fieldIndex)
                            : Heap.GetField(objectValue.AsObjectId, fieldIndex);
                        Push(fieldValue);
                        break;
                    }

                case EnumSyscall.ObjectFieldSet:
                    {
                        var value = Pop();
                        var fieldIndexValue = Pop();
                        var objectValue = Pop();
                        if (!fieldIndexValue.IsInt || fieldIndexValue.AsInt < 0 || fieldIndexValue.AsInt > int.MaxValue)
                            throw new VmException("Object field write expects a non-negative int field index.");
                        if (!objectValue.IsObject)
                            throw new VmException($"Object field write expects an object; found {objectValue.Kind}.");

                        var fieldIndex = (int)fieldIndexValue.AsInt;
                        if (_tickActive && _committedQueryDepth == 0)
                            Heap.SetVolatileField(objectValue.AsObjectId, fieldIndex, value);
                        else
                            Heap.SetField(objectValue.AsObjectId, fieldIndex, value);
                        break;
                    }

                case EnumSyscall.InstancePropertyGet:
                    {
                        var functionIndexValue = Pop();
                        var target = Pop();
                        if (!functionIndexValue.IsInt || functionIndexValue.AsInt < 0 || functionIndexValue.AsInt >= _module.Functions.Count)
                            throw new VmException("Property getter expects a valid function index.");
                        if (!target.IsObject)
                            throw new VmException($"Property getter expects an object target; found {target.Kind}.");

                        InvokeInstanceFunctionSynchronously(
                            target.AsObjectId,
                            (int)functionIndexValue.AsInt,
                            Array.Empty<AloeValue>(),
                            committedQuery: true);
                        break;
                    }

                case EnumSyscall.InstanceAsyncEnqueue:
                    {
                        var argumentCountValue = Pop();
                        var functionIndexValue = Pop();
                        if (!argumentCountValue.IsInt || argumentCountValue.AsInt < 0 || argumentCountValue.AsInt > int.MaxValue)
                            throw new VmException("Async enqueue expects a non-negative argument count.");
                        if (!functionIndexValue.IsInt || functionIndexValue.AsInt < 0 || functionIndexValue.AsInt >= _module.Functions.Count)
                            throw new VmException("Async enqueue expects a valid function index.");

                        var argumentCount = (int)argumentCountValue.AsInt;
                        var arguments = new AloeValue[argumentCount];
                        for (var i = argumentCount - 1; i >= 0; i--)
                            arguments[i] = Pop();

                        var target = Pop();
                        if (!target.IsObject)
                            throw new VmException($"Async enqueue expects an object target; found {target.Kind}.");

                        _instanceCallBuffer.Enqueue(new PendingInstanceCall(
                            target.AsObjectId,
                            (int)functionIndexValue.AsInt,
                            arguments));
                        break;
                    }


                case EnumSyscall.PipeCreate:
                    {
                        var capacityValue = Pop();
                        var typeNameValue = Pop();
                        if (!typeNameValue.IsString)
                            throw new VmException($"PipeCreate expects a pipe type name string; found {typeNameValue.Kind}.");
                        if (!capacityValue.IsInt)
                            throw new VmException($"PipeCreate expects an int capacity; found {capacityValue.Kind}.");
                        var typeName = typeNameValue.AsString;
                        if (!IsSupportedPipeTypeName(typeName))
                            throw new VmException($"Unsupported pipe type '{typeName}'.");

                        int? capacity;
                        if (capacityValue.AsInt == -1)
                        {
                            capacity = null;
                        }
                        else
                        {
                            if (capacityValue.AsInt <= 0 || capacityValue.AsInt > int.MaxValue)
                                throw new VmException("Pipe capacity must be a positive int.");
                            capacity = (int)capacityValue.AsInt;
                        }

                        var objectId = Heap.Allocate(ReadOnlySpan<byte>.Empty, fieldCount: 0, typeName: typeName);
                        _pipes[objectId] = new PipeState(typeName, capacity);
                        Push(AloeValue.FromObject(objectId));
                        break;
                    }

                case EnumSyscall.PipeWrite:
                    {
                        var value = Pop();
                        var pipeValue = Peek();
                        var pipe = GetPipe(pipeValue);
                        if (pipe.IsClosed)
                            throw new VmException("Cannot write to a closed pipe.");
                        if (!MatchesPipeElement(pipe.TypeName, value))
                            throw new VmException($"Value {value.Kind} does not match {pipe.TypeName}.");

                        if (pipe.IsFull)
                        {
                            if (_synchronousCallDepth > 0)
                                throw new VmException(
                                    "Pipe write would block inside a synchronous instance call (property getter or tick()); " +
                                    "blocking pipe operations are not allowed there.");

                            Push(value); // preserve [pipe, value] so the same syscall can retry after resume
                            if (_runningContext == null)
                                throw new VmException("Pipe write cannot suspend outside scheduler execution.");

                            WaitForPipeWrite(pipeValue.AsObjectId, pipe, _runningContext);
                            _contextSuspendRequested = true;
                            _suspendCurrentInstruction = true;
                            break;
                        }

                        Pop(); // pipe handle
                        pipe.Values.Enqueue(value);
                        SchedulePipeFilters(pipeValue.AsObjectId, pipe);
                        WakeOnePipeReader(pipeValue.AsObjectId, pipe);
                        break;
                    }

                case EnumSyscall.PipeClose:
                    {
                        var pipeValue = Pop();
                        var pipeId = pipeValue.AsObjectId;
                        var pipe = GetPipe(pipeValue);
                        if (!pipe.IsClosed)
                            pipe.IsClosed = true;
                        WakeAllPipeWriters(pipeId, pipe);
                        WakeAllPipeReaders(pipeId, pipe);
                        SchedulePipeFilters(pipeId, pipe);
                        break;
                    }

                case EnumSyscall.PipeTryRead:
                    {
                        var pipeValue = Peek();
                        var pipe = GetPipe(pipeValue);
                        if (pipe.Values.Count > 0)
                        {
                            Pop();
                            Push(pipe.Values.Dequeue());
                            Push(AloeValue.FromBool(true));
                            WakeOnePipeWriter(pipeValue.AsObjectId, pipe);
                        }
                        else if (!pipe.IsClosed && _synchronousCallDepth > 0)
                        {
                            throw new VmException(
                                "Pipe read would block inside a synchronous instance call (property getter or tick()); " +
                                "blocking pipe operations are not allowed there.");
                        }
                        else if (!pipe.IsClosed && _runningContext != null)
                        {
                            WaitForPipeRead(pipeValue.AsObjectId, pipe, _runningContext);
                            _contextSuspendRequested = true;
                            _suspendCurrentInstruction = true;
                        }
                        else
                        {
                            Pop();
                            Push(AloeValue.Null);
                            Push(AloeValue.FromBool(false));
                        }
                        break;
                    }

                case EnumSyscall.PipeBindFilter:
                    {
                        var functionIndexValue = Pop();
                        var outputValue = Pop();
                        var inputValue = Pop();
                        if (!functionIndexValue.IsInt || functionIndexValue.AsInt < 0 || functionIndexValue.AsInt >= _module.Functions.Count)
                            throw new VmException("PipeBindFilter expects a valid filter function index.");
                        var input = GetPipe(inputValue);
                        GetPipe(outputValue);
                        input.Filters.Add(new FilterBinding((int)functionIndexValue.AsInt, outputValue.AsObjectId));
                        if (input.IsClosed || input.Values.Count > 0)
                            SchedulePipeFilters(inputValue.AsObjectId, input);
                        break;
                    }


                // 将来拡張:
                // case EnumSyscall.WriteStderr:
                //     ...
                //     break;


                default:
                    if (_hostSyscalls.TryInvoke(syscallId, this, AllowedHostCapabilities))
                        break;
                    throw new VmException($"Unknown syscall id: {syscallId}");
            }
        }




        private void WaitForPipeWrite(long pipeId, PipeState pipe, VmExecutionContext context)
        {
            if (context.WaitingWritePipeId == pipeId) return;
            context.WaitingWritePipeId = pipeId;
            pipe.WaitingWriters.Enqueue(context);
        }

        private void WaitForPipeRead(long pipeId, PipeState pipe, VmExecutionContext context)
        {
            if (context.WaitingReadPipeId == pipeId) return;
            context.WaitingReadPipeId = pipeId;
            pipe.WaitingReaders.Enqueue(context);
        }

        private void WakeOnePipeWriter(long pipeId, PipeState pipe)
        {
            while (pipe.WaitingWriters.Count > 0)
            {
                var context = pipe.WaitingWriters.Dequeue();
                if (context.IsCompleted || context.WaitingWritePipeId != pipeId) continue;
                context.WaitingWritePipeId = null;
                EnqueueContext(context);
                break;
            }
        }

        private void WakeAllPipeWriters(long pipeId, PipeState pipe)
        {
            while (pipe.WaitingWriters.Count > 0)
                WakeOnePipeWriter(pipeId, pipe);
        }

        private void WakeOnePipeReader(long pipeId, PipeState pipe)
        {
            while (pipe.WaitingReaders.Count > 0)
            {
                var context = pipe.WaitingReaders.Dequeue();
                if (context.IsCompleted || context.WaitingReadPipeId != pipeId) continue;
                context.WaitingReadPipeId = null;
                EnqueueContext(context);
                break;
            }
        }

        private void WakeAllPipeReaders(long pipeId, PipeState pipe)
        {
            while (pipe.WaitingReaders.Count > 0)
                WakeOnePipeReader(pipeId, pipe);
        }

        private void EnqueueContext(VmExecutionContext context)
        {
            if (context.IsCompleted || context.IsScheduled || ReferenceEquals(context, _runningContext)) return;
            context.IsScheduled = true;
            _readyContexts.Enqueue(context);
        }

        private void SchedulePipeFilters(long inputPipeId, PipeState input)
        {
            foreach (var binding in input.Filters)
            {
                var context = binding.Task;
                if (context == null)
                {
                    context = new VmExecutionContext(inputPipeId, binding);
                    binding.Task = context;
                    _allContexts.Add(context);
                }
                // A filter blocked on a different pipe must not run until woken.
                if (context.WaitingWritePipeId == null && context.WaitingReadPipeId == null)
                    EnqueueContext(context);
            }
        }

        private void RunContextQuantum(VmExecutionContext context)
        {
            if (!_callStack.IsEmpty || _valueStack.Count != 0)
                throw new VmException("Scheduler encountered a nonempty shared VM stack.");
            if (!context.Started)
            {
                StartFilterContextFrame(context);
                context.Started = true;
            }
            else RestoreContext(context);

            _runningContext = context;
            _contextSuspendRequested = false;
            try
            {
                int remaining = InstructionQuantum;
                while (!_haltRequested && !_contextSuspendRequested && !_callStack.IsEmpty && remaining-- > 0)
                    ExecuteOneInstruction();

                if (_callStack.IsEmpty || _haltRequested)
                {
                    context.IsCompleted = true;
                    if (context.IsMain)
                    {
                        context.SuspendedValuesTopFirst.Clear();
                        while (_valueStack.Count > 0) context.SuspendedValuesTopFirst.Add(_valueStack.Pop());
                    }
                    else _valueStack.Clear();
                }
                else
                {
                    SaveContext(context);
                }
            }
            finally
            {
                _runningContext = null;
                _suspendCurrentInstruction = false;
            }
            if (!context.IsCompleted && !_contextSuspendRequested)
                EnqueueContext(context);
            _contextSuspendRequested = false;
        }

        private void StartFilterContextFrame(VmExecutionContext context)
        {
            var binding = context.Binding ?? throw new VmException("Missing filter binding.");
            var functionIndex = binding.FunctionIndex;
            if ((uint)functionIndex >= (uint)_module.Functions.Count)
                throw new VmException($"Filter function index is out of range: {functionIndex}.");
            var function = _module.Functions[functionIndex];
            if (function.ParameterCount != 2)
                throw new VmException($"Filter function '{function.Name}' must have exactly two parameters.");
            var locals = new AloeValue[Math.Max(function.LocalCount, function.ParameterCount)];
            locals[0] = AloeValue.FromObject(context.InputPipeId);
            locals[1] = AloeValue.FromObject(binding.OutputPipeId);
            _callStack.Push(new CallFrame(module: _module, function: function, ip: function.EntryIp, locals: locals));
        }

        private void SaveContext(VmExecutionContext context)
        {
            context.SuspendedFramesTopFirst.Clear();
            while (!_callStack.IsEmpty) context.SuspendedFramesTopFirst.Add(_callStack.Pop());
            context.SuspendedValuesTopFirst.Clear();
            while (_valueStack.Count > 0) context.SuspendedValuesTopFirst.Add(_valueStack.Pop());
        }

        private void RestoreContext(VmExecutionContext context)
        {
            for (int i = context.SuspendedValuesTopFirst.Count - 1; i >= 0; i--)
                _valueStack.Push(context.SuspendedValuesTopFirst[i]);
            for (int i = context.SuspendedFramesTopFirst.Count - 1; i >= 0; i--)
                _callStack.Push(context.SuspendedFramesTopFirst[i]);
            context.SuspendedValuesTopFirst.Clear();
            context.SuspendedFramesTopFirst.Clear();
        }

        private PipeState GetPipe(AloeValue value)
        {
            if (!value.IsObject)
                throw new VmException($"Pipe operation expects a pipe handle; found {value.Kind}.");
            if (!_pipes.TryGetValue(value.AsObjectId, out var pipe))
                throw new VmException($"Object {value.AsObjectId} is not an active pipe.");
            return pipe;
        }

        private static bool IsSupportedPipeTypeName(string typeName)
            => typeName is "pipe<int>" or "pipe<string>" or "pipe<bool>";

        private static bool MatchesPipeElement(string typeName, AloeValue value)
            => typeName switch
            {
                "pipe<int>" => value.IsInt,
                "pipe<string>" => value.IsString,
                "pipe<bool>" => value.IsBool,
                _ => false
            };


        /// <summary>
        /// Runs one Tick worth of GC work. VM stack, frame-local Object handles, and
        /// pending Instance CallBuffer Object handles are refreshed as Runtime Temporary
        /// References before the three GC phases run.
        /// </summary>
        public AloeGcTickResult RunGcTick()
        {
            return RequireGc();
        }

        /// <summary>
        /// Implements Aloe GC.require(): refresh roots and perform one normal budgeted
        /// Plan -> Sweep/Move -> FreeBlock GC pass.
        /// </summary>
        public AloeGcTickResult RequireGc()
        {
            RefreshRuntimeTemporaryRoots();
            GarbageCollector.LogWriter = TraceWriter;
            return GarbageCollector.RunTick();
        }


        /// <summary>Current value of the built-in GC.debug public static property.</summary>
        public bool GcDebugEnabled => GarbageCollector.DebugEnabled;

        /// <summary>Enable or disable GC diagnostic logging without changing VM instruction tracing.</summary>
        public void SetGcDebug(bool enabled)
        {
            if (enabled)
                _diagnosticsRequestedDuringRun = true;

            GarbageCollector.DebugEnabled = enabled;
            GarbageCollector.LogWriter = TraceWriter;
            TraceWriter?.Invoke(enabled ? "[GC] Debug enabled." : "[GC] Debug disabled.");
        }

        /// <summary>
        /// Implements Aloe tick(): drain the prototype Instance CallBuffer to quiescence,
        /// commit per-field Volatile State to Committed State, refresh reference state,
        /// and return without running physical GC Plan / Sweep / Move / FreeBlock work.
        /// </summary>
        public AloeReferenceGraphUpdateResult Tick()
        {
            _tickActive = true;
            try
            {
                ProcessInstanceCallBuffer();
            }
            finally
            {
                _tickActive = false;
            }

            var committedFields = Heap.CommitVolatileFields();
            var rootCount = RefreshRuntimeTemporaryRoots();
            var update = GarbageCollector.ReferenceGraph.Update();

            if (TraceEnabled)
            {
                TraceWriter?.Invoke(
                    $"[TICK] Reference state committed: roots={rootCount}, committedFields={committedFields}, groups={update.GroupCount}, " +
                    $"rebuilt={update.GroupsRebuilt}, fullRebuild={update.FullRebuild}, " +
                    $"analyzedObjects={update.AnalyzedObjectCount}.");
            }

            return update;
        }

        private void ProcessInstanceCallBuffer()
        {
            while (_instanceCallBuffer.Count > 0)
            {
                var pending = _instanceCallBuffer.Dequeue();
                InvokeInstanceFunctionSynchronously(
                    pending.TargetObjectId,
                    pending.FunctionIndex,
                    pending.Arguments,
                    committedQuery: false);
            }
        }

        private void InvokeInstanceFunctionSynchronously(
            long targetObjectId,
            int functionIndex,
            IReadOnlyList<AloeValue> arguments,
            bool committedQuery)
        {
            if (!Heap.ObjectTable.ContainsKey(targetObjectId))
                throw new VmException($"Instance call target Object {targetObjectId} no longer exists.");
            if ((uint)functionIndex >= (uint)_module.Functions.Count)
                throw new VmException($"Instance call function index is out of range: {functionIndex}.");

            var caller = CurrentFrame ?? throw new VmException("Synchronous instance call requires an active caller frame.");
            var depthBeforeCall = _callStack.Count;
            // CallCommand advances the caller IP as if a CALL instruction had executed.
            // A host-initiated synchronous call happens inside the caller's current
            // SYSCALL, so the caller IP must be restored; otherwise tick() with N
            // pending calls would skip N instructions.
            var callerIp = caller.Ip;

            Push(AloeValue.FromObject(targetObjectId));
            foreach (var argument in arguments)
                Push(argument);

            if (committedQuery)
                _committedQueryDepth++;
            _synchronousCallDepth++;

            try
            {
                var instruction = new Instruction(EnumOpcode.Call, functionIndex);
                new CallCommand().Execute(this, caller, in instruction);
                RunUntilCallDepth(depthBeforeCall);
                caller.Ip = callerIp;
            }
            finally
            {
                _synchronousCallDepth--;
                if (committedQuery)
                    _committedQueryDepth--;
            }
        }


        private int RefreshRuntimeTemporaryRoots()
        {
            var roots = new List<long>();

            foreach (var value in _valueStack)
            {
                if (value.IsObject)
                    roots.Add(value.AsObjectId);
            }

            foreach (var frame in _callStack.EnumerateFrames())
            {
                foreach (var value in frame.Locals)
                {
                    if (value.IsObject)
                        roots.Add(value.AsObjectId);
                }
            }

            foreach (var pending in _instanceCallBuffer)
            {
                roots.Add(pending.TargetObjectId);
                foreach (var argument in pending.Arguments)
                {
                    if (argument.IsObject)
                        roots.Add(argument.AsObjectId);
                }
            }

            foreach (var pipeId in _pipes.Keys)
                roots.Add(pipeId);

            foreach (var pipe in _pipes.Values)
            {
                foreach (var value in pipe.Values)
                {
                    if (value.IsObject)
                        roots.Add(value.AsObjectId);
                }

                foreach (var binding in pipe.Filters)
                    roots.Add(binding.OutputPipeId);
            }

            // Suspended contexts remain GC roots. Main's return value is also
            // retained after main completes, until Run() publishes it externally.
            foreach (var context in _allContexts)
            {
                if (context.IsCompleted && !context.IsMain) continue;
                if (!context.IsMain)
                {
                    roots.Add(context.InputPipeId);
                    if (context.Binding != null) roots.Add(context.Binding.OutputPipeId);
                }
                foreach (var value in context.SuspendedValuesTopFirst)
                    if (value.IsObject) roots.Add(value.AsObjectId);
                foreach (var frame in context.SuspendedFramesTopFirst)
                    foreach (var value in frame.Locals)
                        if (value.IsObject) roots.Add(value.AsObjectId);
            }

            Heap.SetRuntimeTemporaryRoots(roots);
            if (TraceEnabled)
                TraceWriter?.Invoke($"[GC] Runtime roots refreshed: {roots.Count} reference(s).");
            return roots.Count;
        }

        /// <summary>
        /// Implements Aloe GC.finish(): synchronously finish one requested GC cycle.
        /// The normal movement budget is temporarily lifted for this explicit blocking
        /// operation; object liveness rules are unchanged.
        /// </summary>
        public AloeGcTickResult FinishGc()
        {
            // GC.finish() only completes an already requested, unfinished cycle.
            // If GC.require() already completed (or no cycle was requested), this is a no-op.
            if (!GarbageCollector.HasActivePlan)
            {
                if (GarbageCollector.DebugEnabled)
                    TraceWriter?.Invoke("[GC] Finish: no active plan; no-op.");
                return new AloeGcTickResult(0, 0, 0, 0, true);
            }

            var settings = GarbageCollector.Settings;
            var savedObjects = settings.MaxMovedObjectsPerTick;
            var savedBytes = settings.MaxMovedBytesPerTick;

            var swept = 0;
            var moved = 0;
            long movedBytes = 0;
            var freedBanks = 0;

            try
            {
                settings.MaxMovedObjectsPerTick = int.MaxValue;
                settings.MaxMovedBytesPerTick = long.MaxValue;

                AloeGcTickResult result;
                do
                {
                    result = RequireGc();
                    swept += result.SweptObjects;
                    moved += result.MovedObjects;
                    movedBytes += result.MovedBytes;
                    freedBanks += result.FreedBanks;
                }
                while (!result.PlanCompleted);

                return new AloeGcTickResult(swept, moved, movedBytes, freedBanks, true);
            }
            finally
            {
                settings.MaxMovedObjectsPerTick = savedObjects;
                settings.MaxMovedBytesPerTick = savedBytes;
            }
        }

        /// <summary>
        /// バイトコードオペランド（int）から呼びたいとき用のブリッジ。
        /// </summary>
        internal void InvokeSyscall(int id)
        {
            var enumId = (EnumSyscall)id;
            InvokeSyscall(enumId);
        }
    }
}