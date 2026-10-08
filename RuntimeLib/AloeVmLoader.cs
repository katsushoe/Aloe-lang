using Aloe.CommonLib;
using System;

namespace Aloe.RuntimeLib
{
    /// <summary>
    /// Creates a verified AloeVM instance from serialized AloeBC bytes.
    /// AloeVm performs verification in its constructor.
    /// </summary>
    public static class AloeVmLoader
    {
        public static AloeVm FromAloeBc(byte[] bytes)
            => FromAloeBc(bytes, AloeVmSettings.Load());

        public static AloeVm FromAloeBc(byte[] bytes, AloeVmSettings settings)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return new AloeVm(AloeBcCodec.Read(bytes), settings);
        }
    }
}
