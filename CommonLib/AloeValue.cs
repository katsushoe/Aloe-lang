using System;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.CommonLib
{
    /// <summary>
    /// Aloe VM 上で扱う値の共通コンテナ。
    /// </summary>
    public readonly struct AloeValue
    {
        public EnumValueKind Kind { get; }
        private readonly object? _value;

        private AloeValue(EnumValueKind kind, object? value)
        {
            Kind = kind;
            _value = value;
        }

        #region Static singletons / factories

        public static readonly AloeValue Null = new AloeValue(EnumValueKind.Null, null);

        public static AloeValue FromInt(long value)
            => new AloeValue(EnumValueKind.Int, value);

        public static AloeValue FromFloat(double value)
            => new AloeValue(EnumValueKind.Float, value);

        public static AloeValue FromBool(bool value)
            => new AloeValue(EnumValueKind.Bool, value);

        public static AloeValue FromString(string? value)
            => new AloeValue(EnumValueKind.String, value ?? string.Empty);

        // ★ decimal 用ファクトリ
        public static AloeValue FromDecimal(decimal value)
            => new AloeValue(EnumValueKind.Decimal, value);

        #endregion

        #region Type predicates

        public bool IsNull => Kind == EnumValueKind.Null;
        public bool IsInt => Kind == EnumValueKind.Int;
        public bool IsFloat => Kind == EnumValueKind.Float;
        public bool IsBool => Kind == EnumValueKind.Bool;
        public bool IsString => Kind == EnumValueKind.String;

        // ★ decimal 判定
        public bool IsDecimal => Kind == EnumValueKind.Decimal;

        // ★ decimal も数値扱いにする
        public bool IsNumber =>
            Kind == EnumValueKind.Int ||
            Kind == EnumValueKind.Float ||
            Kind == EnumValueKind.Decimal;

        #endregion

        #region Accessors

        public long AsInt
        {
            get
            {
                if (Kind == EnumValueKind.Int)
                    return (long)_value!;

                throw new VmException($"AloeValue is not Int (actual: {Kind}).");
            }
        }

        public double AsFloat
        {
            get
            {
                if (Kind == EnumValueKind.Float)
                    return (double)_value!;

                if (Kind == EnumValueKind.Int)
                    return (double)(long)_value!;

                // ★ Decimal も Float に変換して扱えるようにする
                if (Kind == EnumValueKind.Decimal)
                    return (double)(decimal)_value!;

                throw new VmException($"AloeValue is not Float/Int/Decimal (actual: {Kind}).");
            }
        }

        public bool AsBool
        {
            get
            {
                if (Kind == EnumValueKind.Bool)
                    return (bool)_value!;

                throw new VmException($"AloeValue is not Bool (actual: {Kind}).");
            }
        }

        public string AsString
        {
            get
            {
                if (Kind == EnumValueKind.String)
                    return (string)_value!;

                throw new VmException($"AloeValue is not String (actual: {Kind}).");
            }
        }

        // ★ Decimal アクセサ
        public decimal AsDecimal
        {
            get
            {
                if (Kind == EnumValueKind.Decimal)
                    return (decimal)_value!;

                // 必要なら Int/Float からの変換も許可する
                if (Kind == EnumValueKind.Int)
                    return (decimal)(long)_value!;
                if (Kind == EnumValueKind.Float)
                    return (decimal)(double)_value!;

                throw new VmException($"AloeValue is not Decimal/Int/Float (actual: {Kind}).");
            }
        }

        #endregion

        #region Arithmetic helpers

        private static AloeValue NumericBinary(
            AloeValue left,
            AloeValue right,
            Func<double, double, double> op,
            string opName
        )
        {
            if (!left.IsNumber || !right.IsNumber)
            {
                throw new VmException(
                    $"Cannot apply {opName} to {left.Kind} and {right.Kind}.");
            }

            var l = left.AsFloat;
            var r = right.AsFloat;
            var result = op(l, r);

            // 両方 Int だった場合、結果が Int にきれいに収まるなら Int に戻す
            if (left.Kind == EnumValueKind.Int &&
                right.Kind == EnumValueKind.Int &&
                result >= long.MinValue &&
                result <= long.MaxValue)
            {
                var rounded = Math.Round(result);
                if (Math.Abs(result - rounded) < double.Epsilon)
                {
                    return FromInt((long)rounded);
                }
            }

            // ★ 元が Decimal を含んでいた場合は Decimal に戻したいならここで頑張る
            //   とりあえず現状は Float に統一しておく
            if (left.Kind == EnumValueKind.Decimal ||
                right.Kind == EnumValueKind.Decimal)
            {
                return FromFloat(result);
            }

            return FromFloat(result);
        }

        private static bool IsZero(AloeValue value)
        {
            if (!value.IsNumber) return false;

            if (value.Kind == EnumValueKind.Int)
                return value.AsInt == 0;

            // Float/Decimal は AsFloat でまとめて判定
            var f = value.AsFloat;
            return Math.Abs(f) < double.Epsilon;
        }

        #endregion

        #region Operators

        public static AloeValue operator +(AloeValue left, AloeValue right)
            => NumericBinary(left, right, (a, b) => a + b, "addition");

        public static AloeValue operator -(AloeValue left, AloeValue right)
            => NumericBinary(left, right, (a, b) => a - b, "subtraction");

        public static AloeValue operator *(AloeValue left, AloeValue right)
            => NumericBinary(left, right, (a, b) => a * b, "multiplication");

        public static AloeValue operator /(AloeValue left, AloeValue right)
        {
            if (IsZero(right))
            {
                // 既に作ってある ZeroDivisionException を使う想定
                throw new ZeroDivisionException("Division by zero.");
            }

            return NumericBinary(left, right, (a, b) => a / b, "division");
        }

        #endregion

        #region ToString

        public override string ToString()
        {
            return Kind switch
            {
                EnumValueKind.Null => "null",
                EnumValueKind.Int => AsInt.ToString(),
                EnumValueKind.Float => AsFloat.ToString(),
                EnumValueKind.Bool => AsBool ? "true" : "false",
                EnumValueKind.String => AsString,
                EnumValueKind.Decimal => AsDecimal.ToString(),   // ★ 追加
                _ => $"<{Kind}>"
            };
        }

        #endregion
    }
}
