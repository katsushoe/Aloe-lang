using System;
using System.Collections.Generic;
using Aloe.CommonLib.Constants;

namespace Aloe.CommonLib
{
    /// <summary>
    /// 1 つの関数のメタ情報。
    /// - Name           : 関数名（デバッグ・エラーメッセージ用）
    /// - EntryIp        : モジュール全体の命令配列中で、この関数の先頭命令のインデックス
    /// - ParameterCount : 仮引数の数
    /// - LocalCount     : ローカル変数スロット数（引数を含める／含めないはコンパイラ側ルールに合わせる）
    /// </summary>
    public sealed class FunctionInfo
    {
        /// <summary>関数名（必須ではないがデバッグ用にあると便利）。</summary>
        public string Name { get; }

        /// <summary>
        /// モジュールの命令配列（Module.Code）中で、
        /// この関数の「最初の命令」が存在するインデックス。
        /// AloeVm はここを IP の初期値として使う。
        /// </summary>
        public int EntryIp { get; }

        /// <summary>パラメーター（引数）の個数。</summary>
        public int ParameterCount { get; }

        /// <summary>ローカル変数スロット数。</summary>
        public int LocalCount { get; }

        /// <summary>型付きシグネチャを持つか。false の場合 verifier は構造検証のみ行う。</summary>
        public bool HasTypeMetadata { get; }

        /// <summary>引数型。HasTypeMetadata=false の場合は空。</summary>
        public IReadOnlyList<EnumValueKind> ParameterTypes { get; }

        /// <summary>全ローカルスロット型（引数スロットを含む）。</summary>
        public IReadOnlyList<EnumValueKind> LocalTypes { get; }

        /// <summary>戻り値型。null は void。HasTypeMetadata=false の場合は未指定。</summary>
        public EnumValueKind? ReturnType { get; }

        public FunctionInfo(
            string name,
            int entryIp,
            int parameterCount,
            int localCount)
        {
            if (entryIp < 0)
                throw new ArgumentOutOfRangeException(nameof(entryIp));
            if (parameterCount < 0)
                throw new ArgumentOutOfRangeException(nameof(parameterCount));
            if (localCount < 0)
                throw new ArgumentOutOfRangeException(nameof(localCount));

            Name = name ?? string.Empty;
            EntryIp = entryIp;
            ParameterCount = parameterCount;
            LocalCount = localCount;
            HasTypeMetadata = false;
            ParameterTypes = Array.Empty<EnumValueKind>();
            LocalTypes = Array.Empty<EnumValueKind>();
            ReturnType = null;
        }

        public FunctionInfo(
            string name,
            int entryIp,
            IReadOnlyList<EnumValueKind> parameterTypes,
            IReadOnlyList<EnumValueKind> localTypes,
            EnumValueKind? returnType)
        {
            if (entryIp < 0) throw new ArgumentOutOfRangeException(nameof(entryIp));
            ParameterTypes = parameterTypes ?? throw new ArgumentNullException(nameof(parameterTypes));
            LocalTypes = localTypes ?? throw new ArgumentNullException(nameof(localTypes));
            if (LocalTypes.Count < ParameterTypes.Count)
                throw new ArgumentException("LocalTypes must include all parameter slots.", nameof(localTypes));

            Name = name ?? string.Empty;
            EntryIp = entryIp;
            ParameterCount = ParameterTypes.Count;
            LocalCount = LocalTypes.Count;
            HasTypeMetadata = true;
            ReturnType = returnType;
        }
    }
}
