using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib.OpCommand;
using System;
using System.Collections.Generic;

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

        /// <summary>評価スタック（オペランドスタック）。</summary>
        private readonly Stack<AloeValue> _valueStack = new();

        /// <summary>コールスタック。</summary>
        private readonly CallStack _callStack = new();

        /// <summary>停止要求フラグ。</summary>
        private bool _haltRequested;

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
        public Action<string>? TraceWriter { get; set; } = Console.WriteLine;

        /// <summary>
        /// VM インスタンスを生成する。
        /// </summary>
        /// <param name="module">実行するモジュール。</param>
        /// <param name="opcodeCommands">
        /// EnumOpcode ごとの IOpcodeCommand 実装。
        /// 例: new Dictionary&lt;EnumOpcode, IOpcodeCommand&gt; { { EnumOpcode.Add, new AddCommand() }, ... }
        /// </param>
        public AloeVm(Module module)
        {
            _module = module ?? throw new ArgumentNullException(nameof(module));
            _opcodeCommands = CreateDefaultOpcodeCommands();
        }

        private static IReadOnlyDictionary<EnumOpcode, IOpcodeCommand> CreateDefaultOpcodeCommands()
        {
            // プロジェクト内で実装済みのコマンドだけ登録してください。
            // （まだ無いものがあればコメントアウトでOK）
            return new Dictionary<EnumOpcode, IOpcodeCommand>
            {
                { EnumOpcode.PushConst,   new PushConstCommand()   },
                { EnumOpcode.LoadLocal,   new LoadLocalCommand()   },
                { EnumOpcode.StoreLocal,  new StoreLocalCommand()  },

                { EnumOpcode.Add,         new AddCommand()         },
                { EnumOpcode.Sub,         new SubCommand()         },
                { EnumOpcode.Mul,         new MulCommand()         },
                { EnumOpcode.Div,         new DivCommand()         },

                { EnumOpcode.CmpLt,       new CmpLtCommand()       },

                { EnumOpcode.Jump,        new JumpCommand()        },
                { EnumOpcode.JumpIfFalse, new JumpIfFalseCommand() },

                { EnumOpcode.Call,        new CallCommand()        },
                { EnumOpcode.Return,      new ReturnCommand()      },

                { EnumOpcode.Syscall,     new SyscallCommand()     },
                { EnumOpcode.Halt,        new HaltCommand()        },
            };
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
            _haltRequested = false;

            _callStack.Push(frame);

            Run();
        }

        /// <summary>
        /// 現在のコールスタック状態から実行を開始 / 継続するメインループ。
        /// 事前に最低 1 つの CallFrame が Push されている必要がある。
        /// </summary>
        public void Run()
        {
            while (!_haltRequested && !_callStack.IsEmpty)
            {
                // 現在のフレーム（トップ）
                var frame = _callStack.Peek();

                var code = _module.Code;
                var ip = frame.Ip;
                var codeCount = code.Count;

                // ★ 範囲チェック
                if (ip < 0)
                {
                    throw new VmException(
                        $"Instruction pointer out of range. ip={ip}, codeCount={codeCount}");
                }

                // ★ 関数末尾（暗黙の return）
                if (ip == codeCount)
                {
                    _callStack.Pop();

                    if (_callStack.IsEmpty)
                    {
                        // すべてのフレームを実行し終えたので VM 終了
                        break;
                    }

                    // 呼び出し元に戻って次のループ
                    continue;
                }

                if (ip > codeCount)
                {
                    // 明らかにバグなので例外
                    throw new VmException(
                        $"Instruction pointer out of range. ip={ip}, codeCount={codeCount}");
                }

                // ★ 命令フェッチ
                var instruction = code[ip];

                if (!_opcodeCommands.TryGetValue(instruction.Opcode, out var command))
                {
                    throw new VmException($"Unknown opcode: {instruction.Opcode}");
                }

                if (TraceEnabled)
                {
                    TraceWriter?.Invoke(FormatTrace(frame, in instruction));
                }

                // ★ 実行前の状態を覚えておく
                var beforeIp = ip;
                var beforeFrame = frame;

                // ★ 命令実行
                command.Execute(this, frame, in instruction);

                // ★ 実行後に終了/スタック空なら即ループ条件へ戻る
                if (_haltRequested || _callStack.IsEmpty)
                {
                    continue;
                }

                // ★ フレーム/Ip の変化を確認してデフォルトインクリメント
                var currentTop = _callStack.Peek();

                // まだ同じフレームがトップにいる場合のみ扱う
                if (ReferenceEquals(currentTop, beforeFrame))
                {
                    // コマンド側で Ip を書き換えていないなら、ここで +1 する
                    if (currentTop.Ip == beforeIp)
                    {
                        currentTop.Ip = beforeIp + 1;
                    }
                    // 既に Jump / Call / Return が Ip を変えていれば、その値を尊重（ここでは何もしない）
                }
                // もしフレームが変わっていれば（Call/Return 済み）、そのまま次ループで新トップを使う
            }
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
                        Console.WriteLine(v.ToString());
                        break;
                    }

                // 将来拡張:
                // case EnumSyscall.WriteStderr:
                //     ...
                //     break;

                default:
                    throw new VmException($"Unknown syscall id: {syscallId}");
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
