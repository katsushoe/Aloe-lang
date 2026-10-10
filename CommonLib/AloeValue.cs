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

        public static AloeValue FromByte(byte value)
            => new AloeValue(EnumValueKind.Byte, value);

        public static AloeValue FromChar(char value)
            => new AloeValue(EnumValueKind.Char, value);

        public static AloeValue FromFloat(double value)
            => new AloeValue(EnumValueKind.Float, (float)value);

        public static AloeValue FromBool(bool value)
            => new AloeValue(EnumValueKind.Bool, value);

        public static AloeValue FromString(string? value)
            => new AloeValue(EnumValueKind.String, value ?? string.Empty);

        /// <summary>Create a stable heap ObjectId / Handle value.</summary>
        public static AloeValue FromObject(long objectId)
            => new AloeValue(EnumValueKind.Object, objectId);

        // ★ decimal 用ファクトリ
        public static AloeValue FromDecimal(decimal value)
            => new AloeValue(EnumValueKind.Decimal, value);

        #endregion

        #region Type predicates

        public bool IsNull => Kind == EnumValueKind.Null;
        public bool IsInt => Kind == EnumValueKind.Int;
        public bool IsByte => Kind == EnumValueKind.Byte;
        public bool IsChar => Kind == EnumValueKind.Char;
        public bool IsFloat => Kind == EnumValueKind.Float;
        public bool IsBool => Kind == EnumValueKind.Bool;
        public bool IsString => Kind == EnumValueKind.String;
        public bool IsObject => Kind == EnumValueKind.Object;

        // ★ decimal 判定
        public bool IsDecimal => Kind == EnumValueKind.Decimal;

        // ★ decimal も数値扱いにする
        public bool IsNumber =>
            Kind == EnumValueKind.Int ||
            Kind == EnumValueKind.Byte ||
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

        public byte AsByte
        {
            get
            {
                if (Kind == EnumValueKind.Byte)
                    return (byte)_value!;

                throw new VmException($"AloeValue is not Byte (actual: {Kind}).");
            }
        }

        public char AsChar
        {
            get
            {
                if (Kind == EnumValueKind.Char)
                    return (char)_value!;

                throw new VmException($"AloeValue is not Char (actual: {Kind}).");
            }
        }

        public double AsFloat
        {
            get
            {
                if (Kind == EnumValueKind.Float)
                    return (double)(float)_value!;

                if (Kind == EnumValueKind.Int)
                    return (double)(long)_value!;

                if (Kind == EnumValueKind.Byte)
                    return AsByte;

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

        public long AsObjectId
        {
            get
            {
                if (Kind == EnumValueKind.Object)
                    return (long)_value!;

                throw new VmException($"AloeValue is not Object (actual: {Kind}).");
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
                if (Kind == EnumValueKind.Byte)
                    return AsByte;
                if (Kind == EnumValueKind.Float)
                    return (decimal)(float)_value!;

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

            if (IsInteger(left) && IsInteger(right) && opName is "addition" or "subtraction" or "multiplication")
            {
                var leftInteger = left.IsByte ? left.AsByte : left.AsInt;
                var rightInteger = right.IsByte ? right.AsByte : right.AsInt;
                var integerResult = opName switch
                {
                    "addition" => checked(leftInteger + rightInteger),
                    "subtraction" => checked(leftInteger - rightInteger),
                    _ => checked(leftInteger * rightInteger),
                };
                return FromInt(integerResult);
            }

            var l = left.AsFloat;
            var r = right.AsFloat;
            var usesFloat32 = left.IsFloat || right.IsFloat;
            if (usesFloat32)
            {
                l = (float)l;
                r = (float)r;
            }
            var result = op(l, r);

            if (usesFloat32)
                result = (float)result;

            if ((usesFloat32 ? !float.IsFinite((float)result) : !double.IsFinite(result)) ||
                (result == 0 && l != 0 && r != 0 &&
                 opName is "multiplication" or "division"))
                throw new OverflowException($"Floating-point {opName} overflowed or underflowed.");

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

        private static AloeValue DecimalBinary(
            AloeValue left,
            AloeValue right,
            Func<decimal, decimal, decimal> op,
            string opName)
        {
            if (!left.IsNumber || !right.IsNumber || left.IsFloat || right.IsFloat)
                throw new VmException($"Decimal {opName} requires decimal/int operands; float mixing is unsupported (left={left.Kind}, right={right.Kind}).");

            var l = left.AsDecimal;
            var r = right.AsDecimal;
            var result = op(l, r);
            if (result == 0 && l != 0 && r != 0 && opName is "multiplication" or "division")
                throw new OverflowException($"Decimal {opName} underflowed.");

            return FromDecimal(result);
        }

        private static bool IsZero(AloeValue value)
        {
            if (!value.IsNumber) return false;

            if (value.Kind == EnumValueKind.Int)
                return value.AsInt == 0;

            if (value.IsByte)
                return value.AsByte == 0;

            if (value.IsDecimal)
                return value.AsDecimal == 0;

            // Float/Decimal は AsFloat でまとめて判定
            var f = value.AsFloat;
            return Math.Abs(f) < double.Epsilon;
        }

        private static bool IsInteger(AloeValue value)
            => value.IsInt || value.IsByte;

        #endregion

        #region Operators

        public static AloeValue operator +(AloeValue left, AloeValue right)
            => left.IsDecimal || right.IsDecimal
                ? DecimalBinary(left, right, (a, b) => a + b, "addition")
                : NumericBinary(left, right, (a, b) => a + b, "addition");

        public static AloeValue operator -(AloeValue left, AloeValue right)
            => left.IsDecimal || right.IsDecimal
                ? DecimalBinary(left, right, (a, b) => a - b, "subtraction")
                : NumericBinary(left, right, (a, b) => a - b, "subtraction");

        public static AloeValue operator *(AloeValue left, AloeValue right)
            => left.IsDecimal || right.IsDecimal
                ? DecimalBinary(left, right, (a, b) => a * b, "multiplication")
                : NumericBinary(left, right, (a, b) => a * b, "multiplication");

        public static AloeValue operator /(AloeValue left, AloeValue right)
        {
            if (IsZero(right))
            {
                // 既に作ってある ZeroDivisionException を使う想定
                throw new ZeroDivisionException("Division by zero.");
            }

            return left.IsDecimal || right.IsDecimal
                ? DecimalBinary(left, right, (a, b) => a / b, "division")
                : NumericBinary(left, right, (a, b) => a / b, "division");
        }

        #endregion

        #region ToString

        public override string ToString()
        {
            return Kind switch
            {
                EnumValueKind.Null => "null",
                EnumValueKind.Int => AsInt.ToString(),
                EnumValueKind.Byte => AsByte.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EnumValueKind.Char => AsChar.ToString(),
                EnumValueKind.Float => ((float)_value!).ToString(System.Globalization.CultureInfo.InvariantCulture),
                EnumValueKind.Bool => AsBool ? "true" : "false",
                EnumValueKind.String => AsString,
                EnumValueKind.Decimal => AsDecimal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EnumValueKind.Object => $"object#{AsObjectId}",
                _ => $"<{Kind}>"
            };
        }

        #endregion
    }
}
