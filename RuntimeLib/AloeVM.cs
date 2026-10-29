using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib.OpCommand;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;


namespace Aloe.RuntimeLib
{
    /// <summary>
    /// Aloe バイトコードを実行する VM 本体。
    /// 
    /// ・Command パターンによるオペコードディスパッチ
    /// ・CallStack / CallFrame による関数呼び出し管理
    /// ・ValueStack によるオペランドスタック
    /// </summary>
    public sealed partial class AloeVm
    {
        /// <summary>実行対象モジュール（バイトコード＋メタ情報）。</summary>
        private Module _module;


        /// <summary>オペコード → コマンド実装 のディスパッチテーブル。</summary>
        private readonly IReadOnlyDictionary<EnumOpcode, IOpcodeCommand> _opcodeCommands;

        private readonly HostSyscallRegistry _hostSyscalls;


        /// <summary>評価スタック（オペランドスタック）。</summary>
        private sealed class VmThreadState
        {
            public Stack<AloeValue> ValueStack { get; } = new();
            public CallStack CallStack { get; } = new();
            public VmExecutionContext? RunningContext { get; set; }
            public bool ContextSuspendRequested { get; set; }
            public bool SuspendCurrentInstruction { get; set; }
            public int SynchronousCallDepth { get; set; }
            public int CommittedQueryDepth { get; set; }
        }

        private readonly VmThreadState _mainThreadState = new();
        private readonly AsyncLocal<VmThreadState?> _threadState = new();
        private VmThreadState CurrentThreadState => _threadState.Value ?? _mainThreadState;
        private Stack<AloeValue> _valueStack => CurrentThreadState.ValueStack;
        private CallStack _callStack => CurrentThreadState.CallStack;

        private sealed record PendingInstanceCall(long TargetObjectId, int FunctionIndex, AloeValue[] Arguments);
        private sealed record PendingStaticCall(string TypeName, int FunctionIndex, AloeValue[] Arguments);

        /// <summary>Prototype instance CallBuffer. Entries are drained synchronously by tick().</summary>
        private readonly Queue<PendingInstanceCall> _instanceCallBuffer = new();
        private readonly Dictionary<string, Queue<PendingStaticCall>> _staticCallBuffers = new(StringComparer.Ordinal);
        private readonly Queue<string> _readyStaticCallTypes = new();
        private readonly object _callBufferGate = new();
        private readonly object _pipeGate = new();
        private readonly object _hostCallGate = new();
        private readonly object _outputGate = new();
        private readonly HashSet<long> _runningInstanceObjects = new();
        private readonly HashSet<string> _runningStaticTypes = new(StringComparer.Ordinal);
        private Exception? _tickFailure;
        private bool _tickFaulted;
        private int _tickInFlight;
        private bool _tickCoordinatorActive;
        private bool _preferInstanceCall = true;
        private int _maxVmThreads = Math.Max(1, Environment.ProcessorCount);

        private sealed class FilterBinding
        {
            public FilterBinding(int functionIndex, long outputPipeId, string? stringArgument = null, JsonFilterSchema? jsonSchema = null, SortFilterSchema? sortSchema = null)
            {
                FunctionIndex = functionIndex;
                OutputPipeId = outputPipeId;
                StringArgument = stringArgument;
                JsonSchema = jsonSchema;
                SortSchema = sortSchema;
            }

            public int FunctionIndex { get; }
            public string? StringArgument { get; }
            public JsonFilterSchema? JsonSchema { get; }
            public SortFilterSchema? SortSchema { get; }
            public bool IsBuiltinFilter => FunctionIndex is >= -39 and <= -1;
            public long OutputPipeId { get; }
            public VmExecutionContext? Task { get; set; }
        }

        private sealed class VmExecutionContext
        {
            private const int StreamingHashInputBufferSize = 4096;

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
            public Queue<string> PendingLines { get; } = new();
            public StringBuilder LineBuffer { get; } = new();
            public StringBuilder JoinedStrings { get; } = new();
            public bool HasJoinedString { get; set; }
            public bool JoinOutputEmitted { get; set; }
            public List<byte> JoinedBytes { get; } = new();
            public bool ByteAggregateOutputEmitted { get; set; }
            public byte[]? DecodedBytes { get; set; }
            public int DecodedByteIndex { get; set; }
            public string? DecodedText { get; set; }
            public bool Utf8OutputEmitted { get; set; }
            public byte[]? HashResult { get; set; }
            public int HashResultIndex { get; set; }
            public IncrementalHash? StreamingHash { get; set; }
            // Batch updates without retaining the complete pipe contents.
            public byte[] HashInputBuffer { get; } = new byte[StreamingHashInputBufferSize];
            public int HashInputCount { get; set; }
            public Md4HashState? Md4Hash { get; set; }
            public List<long>? SortValues { get; set; }
            public List<AloeValue>? SortItemValues { get; set; }
            public bool SortReady { get; set; }
            public int SortOutputIndex { get; set; }
            public List<AloeValue>? ShuffleValues { get; set; }
            public bool ShuffleInputClosed { get; set; }
            public int ShufflePermutationIndex { get; set; }
            public int ShuffleOutputIndex { get; set; }
            public int JsonInputIndex { get; set; }
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
        private VmExecutionContext? _runningContext
        {
            get => CurrentThreadState.RunningContext;
            set => CurrentThreadState.RunningContext = value;
        }
        private const int InstructionQuantum = 1024;
        private bool _contextSuspendRequested
        {
            get => CurrentThreadState.ContextSuspendRequested;
            set => CurrentThreadState.ContextSuspendRequested = value;
        }
        private bool _suspendCurrentInstruction
        {
            get => CurrentThreadState.SuspendCurrentInstruction;
            set => CurrentThreadState.SuspendCurrentInstruction = value;
        }

        /// <summary>
        /// Depth of host-initiated synchronous calls (property getters, tick() instance calls).
        /// These run to completion inside one SYSCALL and cannot be suspended by the scheduler.
        /// </summary>
        private int _synchronousCallDepth
        {
            get => CurrentThreadState.SynchronousCallDepth;
            set => CurrentThreadState.SynchronousCallDepth = value;
        }

        /// <summary>True while tick() is executing queued public async instance methods.</summary>
        private volatile bool _tickActive;

        /// <summary>Nested synchronous property-query depth. Field reads use Committed State while positive.</summary>
        private int _committedQueryDepth
        {
            get => CurrentThreadState.CommittedQueryDepth;
            set => CurrentThreadState.CommittedQueryDepth = value;
        }


        /// <summary>停止要求フラグ。</summary>
        private volatile bool _haltRequested;

        /// <summary>Whether GC diagnostics were enabled at any point during the current program run.</summary>
        private volatile bool _diagnosticsRequestedDuringRun;

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

        /// <summary>Maximum number of independent async CallBuffer workers used during Tick.</summary>
        public int MaxVmThreads
        {
            get => _maxVmThreads;
            set
            {
                if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
                _maxVmThreads = value;
            }
        }

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
                { EnumOpcode.BitAnd,      new BitwiseCommand()     },
                { EnumOpcode.BitOr,       new BitwiseCommand()     },
                { EnumOpcode.BitXor,      new BitwiseCommand()     },
                { EnumOpcode.BitNot,      new BitwiseCommand()     },
                { EnumOpcode.BitShiftLeft, new BitwiseCommand()    },
                { EnumOpcode.BitShiftRight, new BitwiseCommand()   },
                { EnumOpcode.StructCreate, new StructCommand()     },
                { EnumOpcode.StructGet,    new StructCommand()     },
                { EnumOpcode.StructSet,    new StructCommand()     },


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

                { EnumOpcode.TryBegin,    new ExceptionCommand()   },
                { EnumOpcode.TryEnd,      new ExceptionCommand()   },
                { EnumOpcode.CatchEnd,    new ExceptionCommand()   },
                { EnumOpcode.Rethrow,     new ExceptionCommand()   },
                { EnumOpcode.TryCatch,    new ExceptionCommand()   },


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
            var entry = GetEntryFunction();
            RunFromEntryPoint(entry.EntryIp, new AloeValue[entry.LocalCount]);
        }

        /// <summary>
        /// Rebinds a quiescent VM to a verified module while preserving its heap.
        /// This is used by incremental hosts that keep live object handles between submissions.
        /// </summary>
        public void ReplaceModule(Module module)
        {
            ArgumentNullException.ThrowIfNull(module);
            if (!_callStack.IsEmpty || _valueStack.Count != 0 || _tickCoordinatorActive || _tickInFlight != 0)
                throw new InvalidOperationException("A running VM cannot replace its module.");
            AloeBytecodeVerifier.Verify(module);
            _module = module;
        }

        /// <summary>
        /// Runs the entry function from an instruction boundary with caller-owned locals.
        /// The VM heap is preserved; the supplied local array remains the entry frame's storage.
        /// </summary>
        public void RunFromEntryPoint(int instructionPointer, AloeValue[] locals, bool preserveRuntimeState = false)
        {
            ArgumentNullException.ThrowIfNull(locals);
            var programStarted = Stopwatch.GetTimestamp();
            var managedBytesAtStart = System.GC.GetTotalMemory(false);
            var allocatedBytesAtStart = System.GC.GetTotalAllocatedBytes(false);
            var gen0CollectionsAtStart = System.GC.CollectionCount(0);
            var gen1CollectionsAtStart = System.GC.CollectionCount(1);
            var gen2CollectionsAtStart = System.GC.CollectionCount(2);
            GarbageCollector.ResetMetrics();
            _diagnosticsRequestedDuringRun = GarbageCollector.DebugEnabled;

            // エントリポイントの FunctionInfo を取得
            var entryFunction = GetEntryFunction();
            if (locals.Length != entryFunction.LocalCount)
                throw new ArgumentException($"Entry locals length {locals.Length} does not match function local count {entryFunction.LocalCount}.", nameof(locals));
            if (instructionPointer < entryFunction.EntryIp || instructionPointer >= _module.Code.Count)
                throw new ArgumentOutOfRangeException(nameof(instructionPointer));


            // ★ ここがポイント：returnAddress を追加で渡す（エントリフレームなので -1 などの番兵値）
            var frame = new CallFrame(
                module: _module,
                function: entryFunction,
                ip: instructionPointer,
                returnAddress: -1,   // 「戻り先なし」を意味する値
                locals: locals
            );


            _callStack.Clear();
            _valueStack.Clear();
            if (!preserveRuntimeState)
            {
                lock (_callBufferGate)
                {
                    _instanceCallBuffer.Clear();
                    _staticCallBuffers.Clear();
                    _readyStaticCallTypes.Clear();
                    _runningInstanceObjects.Clear();
                    _runningStaticTypes.Clear();
                    _tickFailure = null;
                    _tickFaulted = false;
                    _tickInFlight = 0;
                    _tickCoordinatorActive = false;
                }
                _pipes.Clear();
                _readyContexts.Clear();
                _allContexts.Clear();
                _runningContext = null;
                _contextSuspendRequested = false;
                _suspendCurrentInstruction = false;
                _tickActive = false;
                _committedQueryDepth = 0;
                _synchronousCallDepth = 0;
            }
            _haltRequested = false;


            _callStack.Push(frame);

            if (preserveRuntimeState)
            {
                if (_allContexts.Any(context => !context.IsCompleted))
                    throw new InvalidOperationException("A persistent REPL submission cannot start while an earlier pipe context is still active.");
                _readyContexts.Clear();
                _allContexts.Clear();
            }


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

        private FunctionInfo GetEntryFunction()
        {
            if (_module.EntryPointIndex < 0 || _module.EntryPointIndex >= _module.Functions.Count)
                throw new VmException($"EntryPointIndex is out of range. index={_module.EntryPointIndex}, count={_module.Functions.Count}");
            return _module.Functions[_module.EntryPointIndex];
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

            ExpireExceptionHandlers(frame, ip);

            var instruction = code[ip];
            if (!_opcodeCommands.TryGetValue(instruction.Opcode, out var command))
                throw new VmException($"Unknown opcode: {instruction.Opcode}");

            if (TraceEnabled)
            {
                lock (_outputGate)
                    TraceWriter?.Invoke(FormatTrace(frame, in instruction));
            }

            var beforeIp = ip;
            var beforeFrame = frame;
            try
            {
                command.Execute(this, frame, in instruction);
            }
            catch (Exception exception) when (DispatchException(exception))
            {
                return;
            }

            if (_haltRequested || _callStack.IsEmpty)
                return;

            var currentTop = _callStack.Peek();
            if (!_suspendCurrentInstruction && ReferenceEquals(currentTop, beforeFrame) && currentTop.Ip == beforeIp)
                currentTop.Ip = beforeIp + 1;
        }

        public void BeginExceptionHandler(CallFrame frame, int catchIp, int exceptionTypeConstantIndex = -1)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (catchIp < 0 || catchIp >= frame.Module.Code.Count)
                throw new VmException("TRY_BEGIN has an invalid catch target.");

            var hasCatchTable = frame.Module.Code[catchIp].Opcode == EnumOpcode.TryCatch;
            Type? exceptionType = null;
            if (!hasCatchTable && exceptionTypeConstantIndex >= 0)
            {
                if ((uint)exceptionTypeConstantIndex >= (uint)frame.Module.Constants.Count ||
                    frame.Module.Constants[exceptionTypeConstantIndex].Kind != EnumValueKind.String ||
                    !AloeExceptionTypes.TryResolve(frame.Module.Constants[exceptionTypeConstantIndex].AsString, out exceptionType))
                    throw new VmException("TRY_BEGIN has an invalid exception type descriptor.");
            }
            frame.ExceptionHandlers.Push(new ExceptionHandlerFrame(
                catchIp,
                exceptionType,
                frame.Ip + 1,
                catchIp,
                frame.ControlStack.Count,
                _valueStack.ToArray(),
                frame.CaughtExceptions.Count));
        }

        public void EndExceptionHandler(CallFrame frame)
        {
            if (frame.ExceptionHandlers.Count == 0)
                throw new VmException("TRY_END has no active exception handler.");
            frame.ExceptionHandlers.Pop();
        }

        public void EndCatch(CallFrame frame)
        {
            if (frame.CaughtExceptions.Count == 0)
                throw new VmException("CATCH_END is outside a catch block.");
            frame.CaughtExceptions.Pop();
        }

        public void Rethrow(CallFrame frame)
        {
            if (frame.CaughtExceptions.Count == 0)
                throw new VmException("Rethrow is outside a catch block.");
            // An inner try can handle this rethrow and resume inside the same catch.
            // Dispatch removes only contexts exited on the way to its selected handler.
            var exception = frame.CaughtExceptions.Peek();
            exception.Throw();
        }

        private void ExpireExceptionHandlers(CallFrame frame, int instructionPointer)
        {
            while (frame.ExceptionHandlers.TryPeek(out var handler) &&
                   (instructionPointer < handler.TryStartIp || instructionPointer >= handler.TryEndIp))
            {
                frame.ExceptionHandlers.Pop();
            }
        }

        private bool DispatchException(Exception exception)
        {
            var frames = _callStack.EnumerateFrames().ToArray();
            var framesToUnwind = 0;
            CallFrame? targetFrame = null;
            ExceptionHandlerFrame? targetHandler = null;
            var selectedCatchIp = -1;

            foreach (var candidate in frames)
            {
                ExpireExceptionHandlers(candidate, candidate.Ip);
                while (candidate.ExceptionHandlers.TryPeek(out var handler))
                {
                    if (!TryFindMatchingCatchTarget(candidate, handler, exception, out var catchIp))
                    {
                        candidate.ExceptionHandlers.Pop();
                        continue;
                    }
                    targetFrame = candidate;
                    targetHandler = handler;
                    selectedCatchIp = catchIp;
                    break;
                }
                if (targetHandler != null) break;
                framesToUnwind++;
            }

            if (targetFrame == null || targetHandler == null)
                return false;

            while (framesToUnwind-- > 0)
                _callStack.Pop();
            targetFrame.ExceptionHandlers.Pop();
            while (targetFrame.ControlStack.Count > targetHandler.ControlDepth)
                targetFrame.ControlStack.Pop();
            while (_valueStack.Count > 0)
                _valueStack.Pop();
            for (var i = targetHandler.ValueStackTopFirst.Count - 1; i >= 0; i--)
                _valueStack.Push(targetHandler.ValueStackTopFirst[i]);

            while (targetFrame.CaughtExceptions.Count > targetHandler.CaughtExceptionDepth)
                targetFrame.CaughtExceptions.Pop();
            targetFrame.CaughtExceptions.Push(ExceptionDispatchInfo.Capture(exception));
            targetFrame.Ip = selectedCatchIp;
            return true;
        }

        private static bool TryFindMatchingCatchTarget(
            CallFrame frame,
            ExceptionHandlerFrame handler,
            Exception exception,
            out int catchIp)
        {
            catchIp = -1;
            var code = frame.Module.Code;
            if ((uint)handler.CatchIp < (uint)code.Count &&
                code[handler.CatchIp].Opcode == EnumOpcode.TryCatch)
            {
                for (var index = handler.CatchIp; index < code.Count; index++)
                {
                    var descriptor = code[index];
                    if (descriptor.Opcode != EnumOpcode.TryCatch) break;

                    Type? exceptionType = null;
                    if (descriptor.Operand1 != 0)
                    {
                        var constantIndex = descriptor.Operand1 - 1;
                        if ((uint)constantIndex >= (uint)frame.Module.Constants.Count ||
                            frame.Module.Constants[constantIndex].Kind != EnumValueKind.String ||
                            !AloeExceptionTypes.TryResolve(
                                frame.Module.Constants[constantIndex].AsString, out exceptionType))
                            throw new VmException("TRY_CATCH has an invalid exception type descriptor.");
                    }

                    if (exceptionType == null || exceptionType.IsInstanceOfType(exception))
                    {
                        catchIp = descriptor.Operand0;
                        return true;
                    }
                }
                return false;
            }

            if (handler.ExceptionType != null && !handler.ExceptionType.IsInstanceOfType(exception))
                return false;
            catchIp = handler.CatchIp;
            return true;
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
            if (syscallId is EnumSyscall.PipeCreate or EnumSyscall.PipeWrite or EnumSyscall.PipeClose or
                EnumSyscall.PipeTryRead or EnumSyscall.PipeBindFilter)
            {
                lock (_pipeGate)
                    InvokeSyscallCore(syscallId);
                return;
            }

            InvokeSyscallCore(syscallId);
        }

        private void InvokeSyscallCore(EnumSyscall syscallId)
        {
            switch (syscallId)
            {
                case EnumSyscall.ArrayCreate:
                case EnumSyscall.ArrayGet:
                case EnumSyscall.ArraySet:
                case EnumSyscall.ArrayLength:
                    InvokeArraySyscall(syscallId);
                    break;
                case EnumSyscall.ListAdd:
                case EnumSyscall.ListRemoveAt:
                    InvokeListMutation(syscallId);
                    break;
                case EnumSyscall.SetAdd:
                case EnumSyscall.SetRemove:
                    InvokeSetMutation(syscallId);
                    break;
                case EnumSyscall.SetClear:
                    InvokeSetClear();
                    break;
                case EnumSyscall.SetUnionWith:
                case EnumSyscall.SetIntersectWith:
                case EnumSyscall.SetExceptWith:
                    InvokeSetAlgebra(syscallId);
                    break;
                case EnumSyscall.MapCreate:
                case EnumSyscall.MapGet:
                case EnumSyscall.MapSet:
                case EnumSyscall.MapRemove:
                case EnumSyscall.MapContainsKey:
                case EnumSyscall.MapCount:
                case EnumSyscall.MapKeyAt:
                    InvokeMapSyscall(syscallId);
                    break;
                case EnumSyscall.SetContains:
                    InvokeSetContains();
                    break;
                case EnumSyscall.ObjectCast:
                case EnumSyscall.ObjectTryCast:
                    InvokeObjectCast(syscallId);
                    break;
                  case EnumSyscall.PrimitiveCast:
                  case EnumSyscall.PrimitiveTryCast:
                      InvokePrimitiveCast(syscallId);
                      break;
                  case EnumSyscall.PrimitiveBox:
                      InvokePrimitiveBox();
                      break;
                  case EnumSyscall.PrimitiveUnbox:
                      InvokeConsumingUnbox();
                      break;
                  case EnumSyscall.ReferenceRequireLive:
                      {
                          var reference = Pop();
                          RequireLiveReference(reference);
                          Push(reference);
                          break;
                      }
                  case EnumSyscall.ReferenceInvalid:
                      Push(AloeValue.Null);
                      break;
                  case EnumSyscall.ReferenceArrayInvalidate:
                  case EnumSyscall.ReferenceFieldInvalidate:
                  case EnumSyscall.ReferenceFieldGet:
                      InvokeReferenceSlot(syscallId);
                      break;
                case EnumSyscall.Print:
                    {
                        // スタックトップを取り出して、そのまま ToString() して出力
                        var v = Pop();
                        lock (_outputGate)
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

                        lock (_callBufferGate)
                        {
                            ThrowIfTickFaulted();
                            _instanceCallBuffer.Enqueue(new PendingInstanceCall(
                                target.AsObjectId,
                                (int)functionIndexValue.AsInt,
                                arguments));
                            Monitor.PulseAll(_callBufferGate);
                        }
                        break;
                    }

                case EnumSyscall.InstanceVirtualAsyncEnqueue:
                    {
                        var argumentCountValue = Pop();
                        var dispatchIndexValue = Pop();
                        if (!argumentCountValue.IsInt || argumentCountValue.AsInt < 0 || argumentCountValue.AsInt > int.MaxValue)
                            throw new VmException("Virtual async enqueue expects a non-negative argument count.");
                        if (!dispatchIndexValue.IsInt || dispatchIndexValue.AsInt < 0 || dispatchIndexValue.AsInt >= _module.Constants.Count)
                            throw new VmException("Virtual async enqueue expects a valid dispatch descriptor constant index.");
                        var dispatchDescriptor = _module.Constants[(int)dispatchIndexValue.AsInt];
                        if (!dispatchDescriptor.IsString)
                            throw new VmException("Virtual async enqueue dispatch descriptor must be a string constant.");

                        var argumentCount = (int)argumentCountValue.AsInt;
                        var arguments = new AloeValue[argumentCount];
                        for (var i = argumentCount - 1; i >= 0; i--)
                            arguments[i] = Pop();

                        var target = Pop();
                        if (!target.IsObject)
                            throw new VmException($"Virtual async enqueue expects an object target; found {target.Kind}.");

                        var typeName = Heap.GetEntry(target.AsObjectId).TypeName;
                        if (typeName == null)
                            throw new VmException("Virtual async enqueue target has no runtime class type name.");
                        var functionIndex = -1;
                        foreach (var entry in dispatchDescriptor.AsString.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var separator = entry.LastIndexOf('=');
                            if (separator <= 0 || !int.TryParse(entry[(separator + 1)..], out var candidateIndex))
                                throw new VmException("Virtual async enqueue dispatch descriptor is malformed.");
                            if (string.Equals(entry[..separator], typeName, StringComparison.Ordinal))
                            {
                                functionIndex = candidateIndex;
                                break;
                            }
                        }
                        if ((uint)functionIndex >= (uint)_module.Functions.Count)
                            throw new VmException($"No virtual method implementation is registered for runtime type '{typeName}'.");

                        lock (_callBufferGate)
                        {
                            ThrowIfTickFaulted();
                            _instanceCallBuffer.Enqueue(new PendingInstanceCall(target.AsObjectId, functionIndex, arguments));
                            Monitor.PulseAll(_callBufferGate);
                        }
                        break;
                    }

                case EnumSyscall.StaticAsyncEnqueue:
                    {
                        var argumentCountValue = Pop();
                        var functionIndexValue = Pop();
                        if (!argumentCountValue.IsInt || argumentCountValue.AsInt < 0 || argumentCountValue.AsInt > int.MaxValue)
                            throw new VmException("Static async enqueue expects a non-negative argument count.");
                        if (!functionIndexValue.IsInt || functionIndexValue.AsInt < 0 || functionIndexValue.AsInt >= _module.Functions.Count)
                            throw new VmException("Static async enqueue expects a valid function index.");

                        var argumentCount = (int)argumentCountValue.AsInt;
                        var arguments = new AloeValue[argumentCount];
                        for (var i = argumentCount - 1; i >= 0; i--)
                            arguments[i] = Pop();

                        var functionIndex = (int)functionIndexValue.AsInt;
                        var function = _module.Functions[functionIndex];
                        if (function.ParameterCount != argumentCount || function.Name.IndexOf('.', StringComparison.Ordinal) <= 0)
                            throw new VmException("Static async enqueue target has an invalid signature.");
                        var typeName = function.Name[..function.Name.IndexOf('.', StringComparison.Ordinal)];
                        lock (_callBufferGate)
                        {
                            ThrowIfTickFaulted();
                            if (!_staticCallBuffers.TryGetValue(typeName, out var buffer))
                            {
                                buffer = new Queue<PendingStaticCall>();
                                _staticCallBuffers.Add(typeName, buffer);
                            }
                            if (buffer.Count == 0)
                                _readyStaticCallTypes.Enqueue(typeName);
                            buffer.Enqueue(new PendingStaticCall(typeName, functionIndex, arguments));
                            Monitor.PulseAll(_callBufferGate);
                        }
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
                            if (_synchronousCallDepth > 0 || !ReferenceEquals(CurrentThreadState, _mainThreadState))
                                throw new VmException(
                                    "Pipe write would block inside a synchronous instance call or VMThread Pool worker " +
                                    "(property getter or tick()); blocking pipe operations are not allowed there.");

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
                        else if (!pipe.IsClosed && !ReferenceEquals(CurrentThreadState, _mainThreadState))
                        {
                            throw new VmException(
                                "Pipe read would block inside a synchronous instance call or VMThread Pool worker " +
                                "(property getter or tick()); blocking pipe operations are not allowed there.");
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
                        if (!functionIndexValue.IsInt || functionIndexValue.AsInt < -39 || functionIndexValue.AsInt >= _module.Functions.Count)
                            throw new VmException("PipeBindFilter expects a valid filter function index.");
                        string? stringArgument = null;
                        if (functionIndexValue.AsInt is -7 or -8 or -9 or -10 or -30 or -34 or -35)
                        {
                            var argumentValue = Pop();
                            if (!argumentValue.IsString)
                                throw new VmException("Parameterized string filters require a string argument.");
                            stringArgument = argumentValue.AsString;
                        }
                        var outputValue = Pop();
                        var inputValue = Pop();
                        var input = GetPipe(inputValue);
                        var output = GetPipe(outputValue);
                        JsonFilterSchema? jsonSchema = null;
                        SortFilterSchema? sortSchema = null;
                        if (functionIndexValue.AsInt is -15 or -16)
                        {
                            if (input.TypeName != "pipe<byte>" || output.TypeName != "pipe<string>")
                                throw new VmException("The bin2hex/base64encode standard filters require pipe<byte> input and pipe<string> output.");
                        }
                        else if (functionIndexValue.AsInt == -17)
                        {
                            if (input.TypeName != "pipe<string>" || output.TypeName != "pipe<byte>")
                                throw new VmException("The base64decode standard filter requires pipe<string> input and pipe<byte> output.");
                        }
                        else if (functionIndexValue.AsInt == -18)
                        {
                            if (input.TypeName != "pipe<byte>" || output.TypeName != "pipe<string>")
                                throw new VmException("The utf8 standard filter requires pipe<byte> input and pipe<string> output.");
                        }
                        else if (functionIndexValue.AsInt == -21)
                        {
                            if (input.TypeName != "pipe<byte>" || output.TypeName != "pipe<byte>")
                                throw new VmException("The sha256 standard filter requires pipe<byte> input and output.");
                        }
                        else if (functionIndexValue.AsInt == -24)
                        {
                            if (input.TypeName != "pipe<byte>" || output.TypeName != "pipe<byte>")
                                throw new VmException("The sha1 standard filter requires pipe<byte> input and output.");
                        }
                        else if (functionIndexValue.AsInt == -27)
                        {
                            if (input.TypeName != "pipe<string[]>" || output.TypeName != "pipe<string>")
                                throw new VmException("The csvenode standard filter requires pipe<string[]> input and pipe<string> output.");
                        }
                        else if (functionIndexValue.AsInt == -28)
                        {
                            if (input.TypeName != "pipe<string>" || output.TypeName != "pipe<string[]>")
                                throw new VmException("The csvdecode standard filter requires pipe<string> input and pipe<string[]> output.");
                        }
                        else if (functionIndexValue.AsInt == -29)
                        {
                            if (input.TypeName != "pipe<string>" || output.TypeName != "pipe<map<string/string>>")
                                throw new VmException("The parse_str standard filter requires pipe<string> input and pipe<map<string/string>> output.");
                        }
                        else if (functionIndexValue.AsInt == -30)
                        {
                            if (input.TypeName != "pipe<decimal>" || output.TypeName != "pipe<string>")
                                throw new VmException("The number_format standard filter requires pipe<decimal> input and pipe<string> output.");
                        }
                        else if (functionIndexValue.AsInt == -32)
                        {
                            if (stringArgument != null || input.TypeName != "pipe<int>" || output.TypeName != "pipe<int>")
                                throw new VmException("The sort standard filter requires pipe<int> input and output and accepts no argument.");
                        }
                        else if (functionIndexValue.AsInt == -35)
                        {
                            if (stringArgument == null)
                                throw new VmException("The keyed sort standard filter requires a field schema.");
                            sortSchema = ParseSortFilterSchema(stringArgument);
                            var expectedPipe = $"pipe<{sortSchema.TypeName}>";
                            if (input.TypeName != expectedPipe || output.TypeName != expectedPipe)
                                throw new VmException("The keyed sort standard filter requires matching struct pipes.");
                        }
                        else if (functionIndexValue.AsInt == -36)
                        {
                            if (stringArgument != null || input.TypeName != "pipe<string>" || output.TypeName != "pipe<string>")
                                throw new VmException("The string sort standard filter requires pipe<string> input and output and accepts no argument.");
                        }
                        else if (functionIndexValue.AsInt == -37)
                        {
                            if (stringArgument != null || input.TypeName != "pipe<string>" || output.TypeName != "pipe<string>")
                                throw new VmException("The string shuffle standard filter requires pipe<string> input and output and accepts no argument.");
                        }
                        else if (functionIndexValue.AsInt == -38)
                        {
                            if (stringArgument != null || input.TypeName != "pipe<byte>" || output.TypeName != "pipe<byte>")
                                throw new VmException("The byte sort standard filter requires pipe<byte> input and output and accepts no argument.");
                        }
                        else if (functionIndexValue.AsInt == -39)
                        {
                            if (stringArgument != null || input.TypeName != "pipe<byte>" || output.TypeName != "pipe<byte>")
                                throw new VmException("The byte shuffle standard filter requires pipe<byte> input and output and accepts no argument.");
                        }
                        else if (functionIndexValue.AsInt == -33)
                        {
                            if (input.TypeName != "pipe<int>" || output.TypeName != "pipe<int>")
                                throw new VmException("The shuffle standard filter requires pipe<int> input and output.");
                        }
                        else if (functionIndexValue.AsInt == -34)
                        {
                            if (stringArgument == null || input.TypeName != "pipe<string>")
                                throw new VmException("The json standard filter requires a valid struct schema, pipe<string> input and matching struct output.");
                            jsonSchema = ParseJsonFilterSchema(stringArgument);
                            if (output.TypeName != $"pipe<{jsonSchema.TypeName}>")
                                throw new VmException("The json standard filter requires a valid struct schema, pipe<string> input and matching struct output.");
                        }
                        else if (functionIndexValue.AsInt == -31)
                        {
                            if (input.TypeName != "pipe<byte>" || output.TypeName != "pipe<byte>")
                                throw new VmException("The md4 standard filter requires pipe<byte> input and output.");
                        }
                        else if ((functionIndexValue.AsInt is >= -20 and <= -1 or -22 or -23 or -25 or -26) &&
                            (input.TypeName != "pipe<string>" || output.TypeName != "pipe<string>"))
                            throw new VmException("Standard string filters require pipe<string> input and output.");
                        input.Filters.Add(new FilterBinding((int)functionIndexValue.AsInt, outputValue.AsObjectId, stringArgument, jsonSchema, sortSchema));
                        if (input.IsClosed || input.Values.Count > 0)
                            SchedulePipeFilters(inputValue.AsObjectId, input);
                        break;
                    }


                // 将来拡張:
                // case EnumSyscall.WriteStderr:
                //     ...
                //     break;


                default:
                    lock (_hostCallGate)
                    {
                        if (_syscalls.TryGetValue(syscallId, out var registered))
                        {
                            registered(this);
                            break;
                        }
                        if (_hostSyscalls.TryInvoke(syscallId, this, AllowedHostCapabilities))
                            break;
                    }
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
            if (context.Binding?.IsBuiltinFilter == true)
            {
                _runningContext = context;
                _contextSuspendRequested = false;
                try
                {
                    RunStringTransformFilterQuantum(context);
                }
                finally
                {
                    _runningContext = null;
                }
                if (!context.IsCompleted && !_contextSuspendRequested)
                    EnqueueContext(context);
                _contextSuspendRequested = false;
                return;
            }
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

        private void RunStringTransformFilterQuantum(VmExecutionContext context)
        {
            if (context.Binding!.FunctionIndex == -34)
            {
                RunJsonFilterQuantum(context);
                return;
            }
            if (context.Binding!.FunctionIndex is -33 or -37 or -39)
            {
                RunShuffleFilterQuantum(context);
                return;
            }
            if (context.Binding!.FunctionIndex is -32 or -35 or -36 or -38)
            {
                RunSortFilterQuantum(context);
                return;
            }
            if (context.Binding!.FunctionIndex == -30)
            {
                RunNumberFormatFilterQuantum(context);
                return;
            }
            if (context.Binding!.FunctionIndex == -6)
            {
                RunLineSplitFilterQuantum(context);
                return;
            }
            if (context.Binding!.FunctionIndex == -10)
            {
                RunJoinFilterQuantum(context);
                return;
            }
            if (context.Binding!.FunctionIndex == -27)
            {
                RunCsvEncodeFilterQuantum(context);
                return;
            }
            if (context.Binding!.FunctionIndex == -28)
            {
                RunCsvDecodeFilterQuantum(context);
                return;
            }
            if (context.Binding!.FunctionIndex == -29)
            {
                RunParseStrFilterQuantum(context);
                return;
            }
            if (context.Binding!.FunctionIndex is -15 or -16)
            {
                RunByteAggregateToStringFilterQuantum(context);
                return;
            }
            if (context.Binding!.FunctionIndex == -17)
            {
                RunBase64DecodeFilterQuantum(context);
                return;
            }
            if (context.Binding!.FunctionIndex == -18)
            {
                RunUtf8DecodeFilterQuantum(context);
                return;
            }
            if (context.Binding!.FunctionIndex == -21)
            {
                RunSha256FilterQuantum(context);
                return;
            }
            if (context.Binding!.FunctionIndex == -24)
            {
                RunSha1FilterQuantum(context);
                return;
            }
            if (context.Binding!.FunctionIndex == -31)
            {
                RunMd4FilterQuantum(context);
                return;
            }

            var input = _pipes[context.InputPipeId];
            var outputId = context.Binding!.OutputPipeId;
            var output = _pipes[outputId];

            if (input.Values.Count == 0)
            {
                if (!input.IsClosed)
                {
                    WaitForPipeRead(context.InputPipeId, input, context);
                    _contextSuspendRequested = true;
                    return;
                }

                output.IsClosed = true;
                WakeAllPipeWriters(outputId, output);
                WakeAllPipeReaders(outputId, output);
                SchedulePipeFilters(outputId, output);
                context.IsCompleted = true;
                return;
            }

            if (output.IsClosed)
                throw new VmException("Cannot write to a closed pipe.");
            if (output.IsFull)
            {
                WaitForPipeWrite(outputId, output, context);
                _contextSuspendRequested = true;
                return;
            }

            var value = input.Values.Dequeue();
            if (!value.IsString)
                throw new VmException("The standard string filter expects string values.");
            if (context.Binding!.FunctionIndex is -7 or -8 or -9)
            {
                var argument = context.Binding!.StringArgument ?? throw new VmException("Missing string-filter argument.");
                var index = value.AsString.IndexOf(argument, StringComparison.Ordinal);
                var matches = context.Binding!.FunctionIndex switch
                {
                    -7 => value.AsString.StartsWith(argument, StringComparison.Ordinal),
                    -8 => index >= 0,
                    -9 => index >= 0,
                    _ => false
                };
                if (matches)
                {
                    var result = context.Binding!.FunctionIndex == -9
                        ? value.AsString[index..]
                        : value.AsString;
                    output.Values.Enqueue(AloeValue.FromString(result));
                    WakeOnePipeReader(outputId, output);
                }
                WakeOnePipeWriter(context.InputPipeId, input);
                SchedulePipeFilters(outputId, output);
                return;
            }
            var converted = context.Binding!.FunctionIndex switch
            {
                -1 => value.AsString.ToLowerInvariant(),
                -2 => value.AsString.ToUpperInvariant(),
                -3 => value.AsString.TrimStart(),
                -4 => value.AsString.TrimEnd(),
                -5 => value.AsString.Trim(),
                -11 or -12 or -13 => ConvertCaseStyle(value.AsString, context.Binding!.FunctionIndex),
                -14 => Regex.Escape(value.AsString),
                -19 => UrlEncodeComponent(value.AsString),
                -20 => UrlDecodeComponent(value.AsString),
                -22 => HtmlEscapeText(value.AsString),
                -23 => HtmlUnescapeCanonicalText(value.AsString),
                -25 => WebUtility.HtmlDecode(value.AsString),
                -26 => StripHtmlTags(value.AsString),
                _ => throw new VmException("Unknown standard string filter.")
            };
            output.Values.Enqueue(AloeValue.FromString(converted));
            WakeOnePipeWriter(context.InputPipeId, input);
            WakeOnePipeReader(outputId, output);
            SchedulePipeFilters(outputId, output);
        }

        private void RunCsvEncodeFilterQuantum(VmExecutionContext context)
        {
            var input = _pipes[context.InputPipeId];
            var outputId = context.Binding!.OutputPipeId;
            var output = _pipes[outputId];
            if (input.Values.Count == 0)
            {
                if (!input.IsClosed)
                {
                    WaitForPipeRead(context.InputPipeId, input, context);
                    _contextSuspendRequested = true;
                    return;
                }
                output.IsClosed = true;
                WakeAllPipeWriters(outputId, output);
                WakeAllPipeReaders(outputId, output);
                SchedulePipeFilters(outputId, output);
                context.IsCompleted = true;
                return;
            }
            if (output.IsClosed) throw new VmException("Cannot write to a closed pipe.");
            if (output.IsFull)
            {
                WaitForPipeWrite(outputId, output, context);
                _contextSuspendRequested = true;
                return;
            }

            var row = input.Values.Dequeue();
            if (!row.IsObject || !Heap.ContainsObject(row.AsObjectId) ||
                Heap.GetEntry(row.AsObjectId).TypeName != "string[]")
                throw new VmException("The csvenode standard filter expects a string[] value.");
            var entry = Heap.GetEntry(row.AsObjectId);
            var fields = new string[entry.FieldCount];
            for (var i = 0; i < fields.Length; i++)
            {
                var field = entry.GetCurrentField(i);
                if (!field.IsString) throw new VmException("The csvenode standard filter expects string[] elements.");
                fields[i] = field.AsString;
            }
            output.Values.Enqueue(AloeValue.FromString(EncodeCsvRecord(fields)));
            WakeOnePipeWriter(context.InputPipeId, input);
            WakeOnePipeReader(outputId, output);
            SchedulePipeFilters(outputId, output);
        }

        private static string EncodeCsvRecord(IReadOnlyList<string> fields)
        {
            if (fields.Count == 0) return string.Empty;
            var output = new StringBuilder();
            for (var i = 0; i < fields.Count; i++)
            {
                if (i > 0) output.Append(',');
                var field = fields[i];
                if (fields.Count == 1 && field.Length == 0)
                {
                    output.Append("\"\"");
                    continue;
                }
                if (field.IndexOfAny([',', '"', '\r', '\n']) < 0)
                {
                    output.Append(field);
                    continue;
                }
                output.Append('"');
                foreach (var character in field)
                {
                    if (character == '"') output.Append("\"\"");
                    else output.Append(character);
                }
                output.Append('"');
            }
            return output.ToString();
        }

        private void RunCsvDecodeFilterQuantum(VmExecutionContext context)
        {
            var input = _pipes[context.InputPipeId];
            var outputId = context.Binding!.OutputPipeId;
            var output = _pipes[outputId];
            if (input.Values.Count == 0)
            {
                if (!input.IsClosed)
                {
                    WaitForPipeRead(context.InputPipeId, input, context);
                    _contextSuspendRequested = true;
                    return;
                }
                output.IsClosed = true;
                WakeAllPipeWriters(outputId, output);
                WakeAllPipeReaders(outputId, output);
                SchedulePipeFilters(outputId, output);
                context.IsCompleted = true;
                return;
            }
            if (output.IsClosed) throw new VmException("Cannot write to a closed pipe.");
            if (output.IsFull)
            {
                WaitForPipeWrite(outputId, output, context);
                _contextSuspendRequested = true;
                return;
            }

            var value = input.Values.Peek();
            if (!value.IsString) throw new VmException("The csvdecode standard filter expects string values.");
            var fields = DecodeCsvRecord(value.AsString);
            var handle = Heap.Allocate(ReadOnlySpan<byte>.Empty, fieldCount: fields.Count, typeName: "string[]");
            for (var i = 0; i < fields.Count; i++) Heap.SetField(handle, i, AloeValue.FromString(fields[i]));
            input.Values.Dequeue();
            output.Values.Enqueue(AloeValue.FromObject(handle));
            WakeOnePipeWriter(context.InputPipeId, input);
            WakeOnePipeReader(outputId, output);
            SchedulePipeFilters(outputId, output);
        }

        private static List<string> DecodeCsvRecord(string record)
        {
            var fields = new List<string>();
            if (record.Length == 0) return fields;
            var field = new StringBuilder();
            var position = 0;
            while (true)
            {
                if (position < record.Length && record[position] == '"')
                {
                    position++;
                    var closed = false;
                    while (position < record.Length)
                    {
                        var character = record[position++];
                        if (character != '"')
                        {
                            field.Append(character);
                            continue;
                        }
                        if (position < record.Length && record[position] == '"')
                        {
                            field.Append('"');
                            position++;
                            continue;
                        }
                        closed = true;
                        break;
                    }
                    if (!closed) throw new VmException("The csvdecode standard filter received an unterminated quoted field.");
                    if (position < record.Length && record[position] != ',')
                        throw new VmException("The csvdecode standard filter received characters after a quoted field.");
                }
                else
                {
                    while (position < record.Length && record[position] != ',')
                    {
                        var character = record[position++];
                        if (character is '"' or '\r' or '\n')
                            throw new VmException("The csvdecode standard filter received an invalid unquoted field.");
                        field.Append(character);
                    }
                }
                fields.Add(field.ToString());
                field.Clear();
                if (position == record.Length) break;
                position++; // comma
                if (position == record.Length)
                {
                    fields.Add(string.Empty);
                    break;
                }
            }
            return fields;
        }

        private void RunJoinFilterQuantum(VmExecutionContext context)
        {
            var input = _pipes[context.InputPipeId];
            var outputId = context.Binding!.OutputPipeId;
            var output = _pipes[outputId];

            if (input.Values.Count > 0)
            {
                var value = input.Values.Dequeue();
                if (!value.IsString)
                    throw new VmException("The join standard filter expects string values.");
                if (context.HasJoinedString)
                    context.JoinedStrings.Append(context.Binding!.StringArgument ?? throw new VmException("Missing join separator."));
                context.JoinedStrings.Append(value.AsString);
                context.HasJoinedString = true;
                WakeOnePipeWriter(context.InputPipeId, input);
                return;
            }

            if (!input.IsClosed)
            {
                WaitForPipeRead(context.InputPipeId, input, context);
                _contextSuspendRequested = true;
                return;
            }

            if (!context.JoinOutputEmitted)
            {
                if (output.IsClosed)
                    throw new VmException("Cannot write to a closed pipe.");
                if (output.IsFull)
                {
                    WaitForPipeWrite(outputId, output, context);
                    _contextSuspendRequested = true;
                    return;
                }
                output.Values.Enqueue(AloeValue.FromString(context.JoinedStrings.ToString()));
                context.JoinOutputEmitted = true;
                WakeOnePipeReader(outputId, output);
                SchedulePipeFilters(outputId, output);
                return;
            }

            output.IsClosed = true;
            WakeAllPipeWriters(outputId, output);
            WakeAllPipeReaders(outputId, output);
            SchedulePipeFilters(outputId, output);
            context.IsCompleted = true;
        }

        private void RunByteAggregateToStringFilterQuantum(VmExecutionContext context)
        {
            var input = _pipes[context.InputPipeId];
            var outputId = context.Binding!.OutputPipeId;
            var output = _pipes[outputId];
            if (input.Values.Count > 0)
            {
                var value = input.Values.Dequeue();
                if (!value.IsByte)
                throw new VmException("The bin2hex/base64encode standard filters expect byte values.");
                context.JoinedBytes.Add(value.AsByte);
                WakeOnePipeWriter(context.InputPipeId, input);
                return;
            }

            if (!input.IsClosed)
            {
                WaitForPipeRead(context.InputPipeId, input, context);
                _contextSuspendRequested = true;
                return;
            }

            if (!context.ByteAggregateOutputEmitted)
            {
                if (output.IsClosed)
                    throw new VmException("Cannot write to a closed pipe.");
                if (output.IsFull)
                {
                    WaitForPipeWrite(outputId, output, context);
                    _contextSuspendRequested = true;
                    return;
                }
                var bytes = context.JoinedBytes.ToArray();
                var result = context.Binding!.FunctionIndex == -15
                    ? Convert.ToHexString(bytes)
                    : Convert.ToBase64String(bytes);
                output.Values.Enqueue(AloeValue.FromString(result));
                context.ByteAggregateOutputEmitted = true;
                WakeOnePipeReader(outputId, output);
                SchedulePipeFilters(outputId, output);
                return;
            }

            output.IsClosed = true;
            WakeAllPipeWriters(outputId, output);
            WakeAllPipeReaders(outputId, output);
            SchedulePipeFilters(outputId, output);
            context.IsCompleted = true;
        }

        private void RunBase64DecodeFilterQuantum(VmExecutionContext context)
        {
            var input = _pipes[context.InputPipeId];
            var outputId = context.Binding!.OutputPipeId;
            var output = _pipes[outputId];

            if (context.DecodedBytes == null)
            {
                if (input.Values.Count > 0)
                {
                    var value = input.Values.Dequeue();
                    if (!value.IsString)
                        throw new VmException("The base64decode standard filter expects string values.");
                    context.JoinedStrings.Append(value.AsString);
                    WakeOnePipeWriter(context.InputPipeId, input);
                    return;
                }

                if (!input.IsClosed)
                {
                    WaitForPipeRead(context.InputPipeId, input, context);
                    _contextSuspendRequested = true;
                    return;
                }

                try
                {
                    context.DecodedBytes = Convert.FromBase64String(context.JoinedStrings.ToString());
                }
                catch (FormatException ex)
                {
                    throw new VmException("The base64decode standard filter received invalid Base64 data.", ex);
                }
            }

            if (context.DecodedByteIndex < context.DecodedBytes.Length)
            {
                if (output.IsClosed)
                    throw new VmException("Cannot write to a closed pipe.");
                if (output.IsFull)
                {
                    WaitForPipeWrite(outputId, output, context);
                    _contextSuspendRequested = true;
                    return;
                }
                output.Values.Enqueue(AloeValue.FromByte(context.DecodedBytes[context.DecodedByteIndex++]));
                WakeOnePipeReader(outputId, output);
                SchedulePipeFilters(outputId, output);
                return;
            }

            output.IsClosed = true;
            WakeAllPipeWriters(outputId, output);
            WakeAllPipeReaders(outputId, output);
            SchedulePipeFilters(outputId, output);
            context.IsCompleted = true;
        }

        private void RunUtf8DecodeFilterQuantum(VmExecutionContext context)
        {
            var input = _pipes[context.InputPipeId];
            var outputId = context.Binding!.OutputPipeId;
            var output = _pipes[outputId];

            if (context.DecodedText == null)
            {
                if (input.Values.Count > 0)
                {
                    var value = input.Values.Dequeue();
                    if (!value.IsByte)
                        throw new VmException("The utf8 standard filter expects byte values.");
                    context.JoinedBytes.Add(value.AsByte);
                    WakeOnePipeWriter(context.InputPipeId, input);
                    return;
                }

                if (!input.IsClosed)
                {
                    WaitForPipeRead(context.InputPipeId, input, context);
                    _contextSuspendRequested = true;
                    return;
                }

                try
                {
                    context.DecodedText = new UTF8Encoding(false, true).GetString(context.JoinedBytes.ToArray());
                }
                catch (DecoderFallbackException ex)
                {
                    throw new VmException("The utf8 standard filter received invalid UTF-8 data.", ex);
                }
            }

            if (!context.Utf8OutputEmitted)
            {
                if (output.IsClosed)
                    throw new VmException("Cannot write to a closed pipe.");
                if (output.IsFull)
                {
                    WaitForPipeWrite(outputId, output, context);
                    _contextSuspendRequested = true;
                    return;
                }
                output.Values.Enqueue(AloeValue.FromString(context.DecodedText));
                context.Utf8OutputEmitted = true;
                WakeOnePipeReader(outputId, output);
                SchedulePipeFilters(outputId, output);
                return;
            }

            output.IsClosed = true;
            WakeAllPipeWriters(outputId, output);
            WakeAllPipeReaders(outputId, output);
            SchedulePipeFilters(outputId, output);
            context.IsCompleted = true;
        }

        private void RunSha256FilterQuantum(VmExecutionContext context)
        {
            RunStreamingHashFilterQuantum(context, HashAlgorithmName.SHA256, "sha256");
        }

        private void RunSha1FilterQuantum(VmExecutionContext context)
        {
            RunStreamingHashFilterQuantum(context, HashAlgorithmName.SHA1, "sha1");
        }

        private void RunStreamingHashFilterQuantum(VmExecutionContext context, HashAlgorithmName algorithm, string filterName)
        {
            var input = _pipes[context.InputPipeId];
            var outputId = context.Binding!.OutputPipeId;
            var output = _pipes[outputId];

            if (context.HashResult == null)
            {
                while (context.HashInputCount < context.HashInputBuffer.Length && input.Values.Count > 0)
                {
                    var value = input.Values.Dequeue();
                    if (!value.IsByte)
                        throw new VmException($"The {filterName} standard filter expects byte values.");
                    context.StreamingHash ??= IncrementalHash.CreateHash(algorithm);
                    context.HashInputBuffer[context.HashInputCount++] = value.AsByte;
                    WakeOnePipeWriter(context.InputPipeId, input);
                }

                if (context.HashInputCount == context.HashInputBuffer.Length)
                {
                    context.StreamingHash!.AppendData(context.HashInputBuffer, 0, context.HashInputCount);
                    context.HashInputCount = 0;
                    return;
                }

                if (!input.IsClosed)
                {
                    WaitForPipeRead(context.InputPipeId, input, context);
                    _contextSuspendRequested = true;
                    return;
                }

                context.StreamingHash ??= IncrementalHash.CreateHash(algorithm);
                if (context.HashInputCount > 0)
                {
                    context.StreamingHash.AppendData(context.HashInputBuffer, 0, context.HashInputCount);
                    context.HashInputCount = 0;
                }
                context.HashResult = context.StreamingHash.GetHashAndReset();
                context.StreamingHash.Dispose();
                context.StreamingHash = null;
            }

            if (context.HashResultIndex < context.HashResult.Length)
            {
                if (output.IsClosed)
                    throw new VmException("Cannot write to a closed pipe.");
                if (output.IsFull)
                {
                    WaitForPipeWrite(outputId, output, context);
                    _contextSuspendRequested = true;
                    return;
                }
                output.Values.Enqueue(AloeValue.FromByte(context.HashResult[context.HashResultIndex++]));
                WakeOnePipeReader(outputId, output);
                SchedulePipeFilters(outputId, output);
                return;
            }

            output.IsClosed = true;
            WakeAllPipeWriters(outputId, output);
            WakeAllPipeReaders(outputId, output);
            SchedulePipeFilters(outputId, output);
            context.IsCompleted = true;
        }

        private static string UrlEncodeComponent(string value)
        {
            byte[] bytes;
            try
            {
                bytes = new UTF8Encoding(false, true).GetBytes(value);
            }
            catch (EncoderFallbackException ex)
            {
                throw new VmException("The urlencode standard filter received invalid UTF-16 text.", ex);
            }

            const string hex = "0123456789ABCDEF";
            var encoded = new StringBuilder(bytes.Length);
            foreach (var b in bytes)
            {
                if (b >= (byte)'A' && b <= (byte)'Z' || b >= (byte)'a' && b <= (byte)'z' ||
                    b >= (byte)'0' && b <= (byte)'9' || b == (byte)'-' || b == (byte)'.' ||
                    b == (byte)'_' || b == (byte)'~')
                {
                    encoded.Append((char)b);
                }
                else
                {
                    encoded.Append('%').Append(hex[b >> 4]).Append(hex[b & 0x0F]);
                }
            }
            return encoded.ToString();
        }

        private static string UrlDecodeComponent(string value)
        {
            byte[] source;
            try
            {
                source = new UTF8Encoding(false, true).GetBytes(value);
            }
            catch (EncoderFallbackException ex)
            {
                throw new VmException("The urldecode standard filter received invalid UTF-16 text.", ex);
            }

            var decoded = new List<byte>(source.Length);
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] != (byte)'%')
                {
                    decoded.Add(source[i]);
                    continue;
                }

                if (i + 2 >= source.Length || !TryHex(source[i + 1], out var high) || !TryHex(source[i + 2], out var low))
                    throw new VmException("The urldecode standard filter received a malformed percent escape.");
                decoded.Add((byte)((high << 4) | low));
                i += 2;
            }

            try
            {
                return new UTF8Encoding(false, true).GetString(decoded.ToArray());
            }
            catch (DecoderFallbackException ex)
            {
                throw new VmException("The urldecode standard filter decoded invalid UTF-8 data.", ex);
            }

            static bool TryHex(byte value, out int digit)
            {
                if (value is >= (byte)'0' and <= (byte)'9') digit = value - (byte)'0';
                else if (value is >= (byte)'A' and <= (byte)'F') digit = value - (byte)'A' + 10;
                else if (value is >= (byte)'a' and <= (byte)'f') digit = value - (byte)'a' + 10;
                else { digit = 0; return false; }
                return true;
            }
        }

        private static string HtmlEscapeText(string value)
        {
            var escaped = new StringBuilder(value.Length);
            foreach (var ch in value)
                escaped.Append(ch switch
                {
                    '&' => "&amp;",
                    '<' => "&lt;",
                    '>' => "&gt;",
                    '"' => "&quot;",
                    '\'' => "&#39;",
                    _ => ch.ToString()
                });
            return escaped.ToString();
        }

        private static string StripHtmlTags(string value)
        {
            var output = new StringBuilder(value.Length);
            for (var position = 0; position < value.Length; position++)
            {
                if (value[position] != '<' || !TryFindHtmlMarkupEnd(value, position, out var end))
                {
                    output.Append(value[position]);
                    continue;
                }
                position = end;
            }
            return output.ToString();
        }

        private static bool TryFindHtmlMarkupEnd(string value, int start, out int end)
        {
            end = start;
            if (value.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = value.IndexOf("-->", start + 4, StringComparison.Ordinal);
                if (commentEnd < 0) return false;
                end = commentEnd + 2;
                return true;
            }

            var position = start + 1;
            if (position >= value.Length) return false;
            if (value[position] == '/') position++;
            else if (value[position] is '!' or '?')
            {
                position++;
                if (position >= value.Length || char.IsWhiteSpace(value[position]) || value[position] == '>') return false;
                return TryFindMarkupDelimiter(value, position, out end);
            }
            else if (!IsHtmlTagNameStart(value[position])) return false;

            if (position >= value.Length || !IsHtmlTagNameStart(value[position])) return false;
            position++;
            while (position < value.Length && IsHtmlTagNamePart(value[position])) position++;
            if (position < value.Length && !char.IsWhiteSpace(value[position]) && value[position] is not ('/' or '>'))
                return false;

            char quote = '\0';
            for (; position < value.Length; position++)
            {
                var current = value[position];
                if (quote != '\0')
                {
                    if (current == quote) quote = '\0';
                }
                else if (current is '\'' or '"') quote = current;
                else if (current == '>')
                {
                    end = position;
                    return true;
                }
            }
            return false;
        }

        private static bool TryFindMarkupDelimiter(string value, int position, out int end)
        {
            char quote = '\0';
            for (; position < value.Length; position++)
            {
                var current = value[position];
                if (quote != '\0')
                {
                    if (current == quote) quote = '\0';
                }
                else if (current is '\'' or '"') quote = current;
                else if (current == '>')
                {
                    end = position;
                    return true;
                }
            }
            end = 0;
            return false;
        }

        private static bool IsHtmlTagNameStart(char value)
            => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

        private static bool IsHtmlTagNamePart(char value)
            => IsHtmlTagNameStart(value) || value is >= '0' and <= '9' or '-' or '_' or ':' or '.';

        private static string HtmlUnescapeCanonicalText(string value)
            => value.Replace("&quot;", "\"", StringComparison.Ordinal)
                .Replace("&#39;", "'", StringComparison.Ordinal)
                .Replace("&gt;", ">", StringComparison.Ordinal)
                .Replace("&lt;", "<", StringComparison.Ordinal)
                .Replace("&amp;", "&", StringComparison.Ordinal);

        private static string ConvertCaseStyle(string value, int functionIndex)
        {
            var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return string.Empty;
            if (functionIndex == -13)
                return string.Join("_", words.Select(word => word.ToLowerInvariant()));
            if (functionIndex == -11)
                return words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(ToTitleWord));
            if (functionIndex == -12)
                return string.Concat(words.Select(ToTitleWord));
            throw new VmException("Unknown case-style standard filter.");

            static string ToTitleWord(string word)
            {
                var lower = word.ToLowerInvariant();
                return lower[..1].ToUpperInvariant() + lower[1..];
            }
        }

        private void RunLineSplitFilterQuantum(VmExecutionContext context)
        {
            var input = _pipes[context.InputPipeId];
            var outputId = context.Binding!.OutputPipeId;
            var output = _pipes[outputId];

            if (context.PendingLines.Count == 0)
            {
                if (input.Values.Count > 0)
                {
                    var chunk = input.Values.Dequeue();
                    if (!chunk.IsString)
                        throw new VmException("The lineSplit standard filter expects string values.");
                    foreach (var character in chunk.AsString)
                    {
                        if (character == '\n')
                        {
                            var line = context.LineBuffer.ToString();
                            if (line.EndsWith('\r')) line = line[..^1];
                            context.PendingLines.Enqueue(line);
                            context.LineBuffer.Clear();
                        }
                        else context.LineBuffer.Append(character);
                    }
                    WakeOnePipeWriter(context.InputPipeId, input);
                }
                else if (input.IsClosed)
                {
                    if (context.LineBuffer.Length > 0)
                    {
                        context.PendingLines.Enqueue(context.LineBuffer.ToString());
                        context.LineBuffer.Clear();
                    }
                    else
                    {
                        output.IsClosed = true;
                        WakeAllPipeWriters(outputId, output);
                        WakeAllPipeReaders(outputId, output);
                        SchedulePipeFilters(outputId, output);
                        context.IsCompleted = true;
                        return;
                    }
                }
                else
                {
                    WaitForPipeRead(context.InputPipeId, input, context);
                    _contextSuspendRequested = true;
                    return;
                }
            }

            if (context.PendingLines.Count == 0)
            {
                if (input.Values.Count == 0 && !input.IsClosed)
                {
                    WaitForPipeRead(context.InputPipeId, input, context);
                    _contextSuspendRequested = true;
                }
                return;
            }
            if (output.IsClosed)
                throw new VmException("Cannot write to a closed pipe.");
            if (output.IsFull)
            {
                WaitForPipeWrite(outputId, output, context);
                _contextSuspendRequested = true;
                return;
            }

            output.Values.Enqueue(AloeValue.FromString(context.PendingLines.Dequeue()));
            WakeOnePipeReader(outputId, output);
            SchedulePipeFilters(outputId, output);
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
            => typeName is "pipe<int>" or "pipe<decimal>" or "pipe<byte>" or "pipe<string>" or "pipe<bool>" or "pipe<string[]>" or "pipe<map<string/string>>" ||
                typeName.StartsWith("pipe<", StringComparison.Ordinal) && typeName.EndsWith('>') && typeName.Length > 6;

        private bool MatchesPipeElement(string typeName, AloeValue value)
            => typeName switch
            {
                "pipe<int>" => value.IsInt,
                "pipe<decimal>" => value.IsDecimal,
                "pipe<byte>" => value.IsByte,
                "pipe<string>" => value.IsString,
                "pipe<bool>" => value.IsBool,
                "pipe<string[]>" => value.IsObject && Heap.ContainsObject(value.AsObjectId) &&
                    Heap.GetEntry(value.AsObjectId).TypeName == "string[]",
                "pipe<map<string/string>>" => value.IsObject && Heap.ContainsObject(value.AsObjectId) &&
                    Heap.GetEntry(value.AsObjectId).TypeName == "map:string[]|string[]",
                _ when typeName.StartsWith("pipe<", StringComparison.Ordinal) && typeName.EndsWith('>') && typeName.Length > 6 =>
                    value.Kind == Aloe.CommonLib.Constants.EnumValueKind.Struct && value.AsStruct.TypeName == typeName[5..^1],
                _ => false
            };


        /// <summary>
        /// Runs one budgeted GC work step, separate from Aloe tick(). VM stack, frame-local
        /// Object handles, and pending Instance CallBuffer Object handles are refreshed as
        /// Runtime Temporary References before the three GC phases run.
        /// </summary>
        public AloeGcTickResult RunGcTick()
        {
            return RequireGc();
        }

        /// <summary>
        /// Implements Aloe GC.require(): refresh roots and perform one normal budgeted
        /// Plan -> Sweep/Move -> FreeBlock GC work step.
        /// </summary>
        public AloeGcTickResult RequireGc()
        {
            EnsureGcAllowedDuringTick();
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

            lock (_hostCallGate)
                GarbageCollector.DebugEnabled = enabled;
            GarbageCollector.LogWriter = TraceWriter;
            lock (_outputGate)
                TraceWriter?.Invoke(enabled ? "[GC] Debug enabled." : "[GC] Debug disabled.");
        }

        private void EnsureGcAllowedDuringTick()
        {
            lock (_callBufferGate)
            {
                if (_tickCoordinatorActive)
                    throw new VmException("GC cannot run while tick() is draining the VMThread Pool.");
            }
        }

        /// <summary>
        /// Implements Aloe tick(): drain Instance and Static CallBuffers to quiescence,
        /// commit per-field Volatile State to Committed State, refresh reference state,
        /// and return without running physical GC Plan / Sweep / Move / FreeBlock work.
        /// </summary>
        public AloeReferenceGraphUpdateResult Tick()
        {
            lock (_callBufferGate)
            {
                ThrowIfTickFaulted();
                if (_tickCoordinatorActive)
                    throw new VmException("Nested or concurrent tick() execution is not supported.");
                _tickCoordinatorActive = true;
            }

            var wasTickActive = _tickActive;
            _tickActive = true;
            try
            {
                ProcessCallBuffersToQuiescence();
            }
            finally
            {
                lock (_callBufferGate)
                {
                    _tickActive = wasTickActive;
                    _tickCoordinatorActive = false;
                    Monitor.PulseAll(_callBufferGate);
                }
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

        private void ProcessCallBuffersToQuiescence()
        {
            while (true)
            {
                lock (_callBufferGate)
                {
                    if (_tickFailure != null)
                        _tickFaulted = true;

                    if (!_tickFaulted)
                    {
                        while (_tickInFlight < _maxVmThreads &&
                               TryTakeRunnableCall(out var instanceCall, out var staticCall))
                        {
                            _tickInFlight++;
                            try
                            {
                                if (instanceCall != null)
                                    _ = Task.Run(() => ExecutePendingInstanceCall(instanceCall));
                                else if (staticCall != null)
                                    _ = Task.Run(() => ExecutePendingStaticCall(staticCall));
                                else
                                    throw new InvalidOperationException("CallBuffer scheduler selected an empty call.");
                            }
                            catch (Exception ex)
                            {
                                if (instanceCall != null) _runningInstanceObjects.Remove(instanceCall.TargetObjectId);
                                if (staticCall != null) _runningStaticTypes.Remove(staticCall.TypeName);
                                _tickInFlight--;
                                _tickFailure ??= ex;
                                _tickFaulted = true;
                            }
                        }
                    }

                    if (_tickInFlight == 0)
                    {
                        if (_tickFaulted)
                        {
                            _instanceCallBuffer.Clear();
                            foreach (var buffer in _staticCallBuffers.Values) buffer.Clear();
                            _readyStaticCallTypes.Clear();
                            var failure = _tickFailure ?? new VmException("VMThread Pool failed during tick().");
                            ExceptionDispatchInfo.Capture(failure).Throw();
                        }

                        if (!HasPendingCalls())
                            return;

                        throw new VmException("CallBuffer scheduler has pending calls but no runnable worker.");
                    }

                    // Workers signal both when they enqueue child calls and when they finish.
                    // The Tick thread remains the only scheduler and waits for complete quiescence.
                    Monitor.Wait(_callBufferGate);
                }
            }
        }

        private bool TryTakeRunnableCall(
            out PendingInstanceCall? instanceCall,
            out PendingStaticCall? staticCall)
        {
            instanceCall = null;
            staticCall = null;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var tryInstance = attempt == 0 ? _preferInstanceCall : !_preferInstanceCall;
                if (tryInstance && TryTakeRunnableInstance(out instanceCall))
                {
                    _preferInstanceCall = false;
                    return true;
                }
                if (!tryInstance && TryTakeRunnableStatic(out staticCall))
                {
                    _preferInstanceCall = true;
                    return true;
                }
            }

            return TryTakeRunnableInstance(out instanceCall) || TryTakeRunnableStatic(out staticCall);
        }

        private bool TryTakeRunnableInstance(out PendingInstanceCall? call)
        {
            call = null;
            var queuedCount = _instanceCallBuffer.Count;
            for (var i = 0; i < queuedCount; i++)
            {
                var candidate = _instanceCallBuffer.Dequeue();
                if (call == null && _runningInstanceObjects.Add(candidate.TargetObjectId))
                {
                    call = candidate;
                }
                else _instanceCallBuffer.Enqueue(candidate);
            }
            // Complete the pass so skipped busy calls keep their order before later calls.
            return call != null;
        }

        private bool TryTakeRunnableStatic(out PendingStaticCall? call)
        {
            call = null;
            var readyCount = _readyStaticCallTypes.Count;
            for (var i = 0; i < readyCount; i++)
            {
                var typeName = _readyStaticCallTypes.Dequeue();
                if (!_staticCallBuffers.TryGetValue(typeName, out var buffer) || buffer.Count == 0)
                    continue;

                if (!_runningStaticTypes.Add(typeName))
                {
                    _readyStaticCallTypes.Enqueue(typeName);
                    continue;
                }

                call = buffer.Dequeue();
                if (buffer.Count > 0)
                    _readyStaticCallTypes.Enqueue(typeName);
                return true;
            }
            return false;
        }

        private bool HasPendingCalls()
            => _instanceCallBuffer.Count > 0 || _readyStaticCallTypes.Count > 0;

        private void ExecutePendingInstanceCall(PendingInstanceCall pending)
        {
            ExecutePendingCall(
                pending.TargetObjectId,
                pending.FunctionIndex,
                pending.Arguments,
                isStatic: false,
                typeName: null);
        }

        private void ExecutePendingStaticCall(PendingStaticCall pending)
        {
            ExecutePendingCall(
                targetObjectId: null,
                functionIndex: pending.FunctionIndex,
                arguments: pending.Arguments,
                isStatic: true,
                typeName: pending.TypeName);
        }

        private void ExecutePendingCall(
            long? targetObjectId,
            int functionIndex,
            IReadOnlyList<AloeValue> arguments,
            bool isStatic,
            string? typeName)
        {
            var previousState = _threadState.Value;
            _threadState.Value = new VmThreadState();
            try
            {
                if ((uint)functionIndex >= (uint)_module.Functions.Count)
                    throw new VmException($"Async call function index is out of range: {functionIndex}.");

                var function = _module.Functions[functionIndex];
                var expectedParameters = arguments.Count + (isStatic ? 0 : 1);
                if (function.ParameterCount != expectedParameters ||
                    function.Name.IndexOf('.', StringComparison.Ordinal) <= 0)
                    throw new VmException("Async call target has an invalid signature.");

                if (targetObjectId is long objectId && !Heap.ContainsObject(objectId))
                    throw new VmException($"Instance call target Object {objectId} no longer exists.");

                var locals = new AloeValue[Math.Max(function.LocalCount, function.ParameterCount)];
                var argumentOffset = 0;
                if (!isStatic)
                {
                    locals[0] = AloeValue.FromObject(targetObjectId!.Value);
                    argumentOffset = 1;
                }
                for (var i = 0; i < arguments.Count; i++)
                    locals[i + argumentOffset] = arguments[i];

                _callStack.Push(new CallFrame(
                    module: _module,
                    function: function,
                    ip: function.EntryIp,
                    locals: locals));

                while (!_callStack.IsEmpty)
                    ExecuteOneInstruction();
            }
            catch (Exception ex)
            {
                lock (_callBufferGate)
                {
                    _tickFailure ??= ex;
                    _tickFaulted = true;
                }
            }
            finally
            {
                _threadState.Value = previousState;
                lock (_callBufferGate)
                {
                    if (targetObjectId is long objectId)
                        _runningInstanceObjects.Remove(objectId);
                    if (isStatic && typeName != null)
                        _runningStaticTypes.Remove(typeName);
                    _tickInFlight--;
                    Monitor.PulseAll(_callBufferGate);
                }
            }
        }

        private void ThrowIfTickFaulted()
        {
            if (!_tickFaulted) return;
            if (_tickFailure != null)
                ExceptionDispatchInfo.Capture(_tickFailure).Throw();
            throw new VmException("VMThread Pool is faulted and cannot execute another Tick.");
        }

        private void InvokeStaticFunctionSynchronously(int functionIndex, IReadOnlyList<AloeValue> arguments)
        {
            if ((uint)functionIndex >= (uint)_module.Functions.Count)
                throw new VmException($"Static async call function index is out of range: {functionIndex}.");
            var function = _module.Functions[functionIndex];
            if (function.ParameterCount != arguments.Count || function.Name.IndexOf('.', StringComparison.Ordinal) <= 0)
                throw new VmException("Static async call target has an invalid signature.");
            var caller = CurrentFrame ?? throw new VmException("Static async call requires an active caller frame.");
            var depthBeforeCall = _callStack.Count;
            var callerIp = caller.Ip;
            foreach (var argument in arguments)
                Push(argument);

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
                foreach (var handler in frame.ExceptionHandlers)
                {
                    foreach (var value in handler.ValueStackTopFirst)
                    {
                        if (value.IsObject)
                            roots.Add(value.AsObjectId);
                    }
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

            foreach (var buffer in _staticCallBuffers.Values)
            {
                foreach (var pending in buffer)
                    foreach (var argument in pending.Arguments)
                        if (argument.IsObject)
                            roots.Add(argument.AsObjectId);
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
                {
                    foreach (var value in frame.Locals)
                        if (value.IsObject) roots.Add(value.AsObjectId);
                    foreach (var handler in frame.ExceptionHandlers)
                        foreach (var value in handler.ValueStackTopFirst)
                            if (value.IsObject) roots.Add(value.AsObjectId);
                }
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
            EnsureGcAllowedDuringTick();
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
