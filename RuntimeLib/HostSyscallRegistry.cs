using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using System;
using System.Collections.Generic;

namespace Aloe.RuntimeLib
{
    public sealed class HostSyscallRegistry
    {
        private sealed record Entry(AloeHostIntrinsic Intrinsic, Action<AloeVm> Handler);

        private readonly Dictionary<EnumSyscall, Entry> _entries = new();

        public void Register(AloeHostIntrinsic intrinsic, Action<AloeVm> handler)
        {
            if (intrinsic == null) throw new ArgumentNullException(nameof(intrinsic));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _entries[intrinsic.Syscall] = new Entry(intrinsic, handler);
        }

        public bool TryInvoke(
            EnumSyscall id,
            AloeVm vm,
            AloeHostCapability allowedCapabilities)
        {
            if (!_entries.TryGetValue(id, out var entry))
                return false;

            var requiredCapability = entry.Intrinsic.RequiredCapability;
            if ((allowedCapabilities & requiredCapability) != requiredCapability)
            {
                throw new VmException(
                    $"Host intrinsic '{entry.Intrinsic.Name}' requires capability '{requiredCapability}', " +
                    $"but VM allows '{allowedCapabilities}'.");
            }

            entry.Handler(vm);
            return true;
        }

        public static HostSyscallRegistry CreateDefault()
        {
            var registry = new HostSyscallRegistry();

            registry.Register(
                HostIntrinsicCatalog.GetRequired("readLine"),
                vm => vm.Push(AloeValue.FromString(vm.InputReader?.Invoke() ?? string.Empty)));

            registry.Register(
                HostIntrinsicCatalog.GetRequired("sleep"),
                vm =>
                {
                    var value = vm.Pop();
                    if (!value.IsInt || value.AsInt < 0 || value.AsInt > int.MaxValue)
                        throw new VmException("sleep(ms) expects a non-negative int within Int32 range.");
                    vm.SleepHandler((int)value.AsInt);
                });

            return registry;
        }
    }
}
