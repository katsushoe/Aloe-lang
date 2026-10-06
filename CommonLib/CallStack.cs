using System;
using System.Collections;
using System.Collections.Generic;

namespace Aloe.CommonLib
{
    /// <summary>
    /// 関数呼び出し用のコールスタック。
    /// 
    /// AloeVm からは主に
    ///   - Push(frame)
    ///   - Pop()
    ///   - Peek()
    ///   - Count / IsEmpty
    /// を使う想定。
    /// </summary>
    public sealed class CallStack
    {
        private readonly Stack<CallFrame> _frames;

        /// <summary>現在スタックに積まれているフレーム数。</summary>
        public int Count => _frames.Count;

        /// <summary>スタックが空かどうか。</summary>
        public bool IsEmpty => _frames.Count == 0;

        /// <summary>
        /// 現在のコールフレーム（トップ）を返す。
        /// ReturnCommand などから参照される。
        /// </summary>
        public CallFrame Current
        {
            get
            {
                if (_frames.Count == 0)
                {
                    throw new InvalidOperationException("CallStack is empty.");
                }
                return _frames.Peek();
            }
        }

        public CallStack(int initialCapacity = 16)
        {
            _frames = initialCapacity > 0
                ? new Stack<CallFrame>(initialCapacity)
                : new Stack<CallFrame>();
        }

        /// <summary>フレームをプッシュする。</summary>
        public void Push(CallFrame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            _frames.Push(frame);
        }

        /// <summary>
        /// フレームをポップして返す。
        /// 空の場合は <see cref="InvalidOperationException"/>。
        /// </summary>
        public CallFrame Pop()
        {
            if (_frames.Count == 0)
            {
                throw new InvalidOperationException("CallStack.Pop: stack is empty.");
            }

            return _frames.Pop();
        }

        /// <summary>
        /// スタックトップを参照する（取り出さない）。
        /// 空の場合は <see cref="InvalidOperationException"/>。
        /// </summary>
        public CallFrame Peek()
        {
            if (_frames.Count == 0)
            {
                throw new InvalidOperationException("CallStack.Peek: stack is empty.");
            }

            return _frames.Peek();
        }

        /// <summary>
        /// 空の場合でも例外を投げずにトップを取得したい場合用。
        /// </summary>
        public bool TryPeek(out CallFrame? frame)
        {
            if (_frames.Count == 0)
            {
                frame = null;
                return false;
            }

            frame = _frames.Peek();
            return true;
        }

        /// <summary>スタックをすべてクリアする。</summary>
        public void Clear()
        {
            _frames.Clear();
        }
    }
}
