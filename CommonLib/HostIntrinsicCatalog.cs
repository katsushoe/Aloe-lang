using Aloe.CommonLib.Constants;
using System;
using System.Collections.Generic;

namespace Aloe.CommonLib
{
    public enum AloeHostValueType
    {
        Int,
        String,
        Bool,
        Void,
    }

    public sealed record AloeHostIntrinsic(
        string Name,
        EnumSyscall Syscall,
        IReadOnlyList<AloeHostValueType> ParameterTypes,
        AloeHostValueType ReturnType,
        AloeHostCapability RequiredCapability);

    public static class HostIntrinsicCatalog
    {
        private static readonly IReadOnlyDictionary<string, AloeHostIntrinsic> ByName =
            new Dictionary<string, AloeHostIntrinsic>(StringComparer.Ordinal)
            {
                ["readLine"] = new(
                    "readLine",
                    EnumSyscall.HostReadLine,
                    Array.Empty<AloeHostValueType>(),
                    AloeHostValueType.String,
                    AloeHostCapability.ConsoleInput),
                ["sleep"] = new(
                    "sleep",
                    EnumSyscall.HostSleep,
                    new[] { AloeHostValueType.Int },
                    AloeHostValueType.Void,
                    AloeHostCapability.Timer),
            };

        private static readonly IReadOnlyDictionary<EnumSyscall, AloeHostIntrinsic> BySyscall =
            BuildBySyscall();

        public static IEnumerable<AloeHostIntrinsic> All => ByName.Values;

        public static bool TryGet(string name, out AloeHostIntrinsic intrinsic)
            => ByName.TryGetValue(name, out intrinsic!);

        public static bool TryGet(EnumSyscall syscall, out AloeHostIntrinsic intrinsic)
            => BySyscall.TryGetValue(syscall, out intrinsic!);

        public static AloeHostIntrinsic GetRequired(string name)
            => TryGet(name, out var intrinsic)
                ? intrinsic
                : throw new KeyNotFoundException($"Unknown host intrinsic '{name}'.");

        public static AloeHostIntrinsic GetRequired(EnumSyscall syscall)
            => TryGet(syscall, out var intrinsic)
                ? intrinsic
                : throw new KeyNotFoundException($"Unknown host syscall '{syscall}'.");

        private static IReadOnlyDictionary<EnumSyscall, AloeHostIntrinsic> BuildBySyscall()
        {
            var result = new Dictionary<EnumSyscall, AloeHostIntrinsic>();
            foreach (var intrinsic in ByName.Values)
                result.Add(intrinsic.Syscall, intrinsic);
            return result;
        }
    }
}
