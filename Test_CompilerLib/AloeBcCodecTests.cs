using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.RuntimeLib;
using NUnit.Framework;
using System;
using System.Text;

namespace Aloe.CompilerLib.Tests
{
    [TestFixture]
    public sealed class AloeBcCodecTests
    {
        [Test]
        public void RoundTrip_PreservesTypedModuleAndExecutes()
        {
            var module = new Module(
                new[] { AloeValue.FromInt(42) },
                new[] { new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.Return) },
                new[] { new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Int) },
                0);

            var bytes = AloeBcCodec.Write(module);
            Assert.That(Encoding.ASCII.GetString(bytes, 0, 6), Is.EqualTo("ALOEBC"));
            Assert.That(bytes[6], Is.EqualTo(AloeBcCodec.MajorVersion));
            Assert.That(bytes[7], Is.EqualTo(AloeBcCodec.MinorVersion));
            Assert.That(bytes[8], Is.EqualTo(AloeBcCodec.BuildVersion));

            var restored = AloeBcCodec.Read(bytes);
            Assert.That(restored.Constants[0].AsInt, Is.EqualTo(42));
            Assert.That(restored.Functions[0].HasTypeMetadata, Is.True);
            Assert.That(restored.Functions[0].ReturnType, Is.EqualTo(EnumValueKind.Int));

            var vm = new AloeVm(restored);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
        }

        [Test]
        public void RoundTrip_PreservesLegacyFunctionMetadata()
        {
            var module = new Module(Array.Empty<AloeValue>(), new[] { new Instruction(EnumOpcode.Return) }, new[] { new FunctionInfo("main", 0, 0, 2) }, 0);
            var restored = AloeBcCodec.Read(AloeBcCodec.Write(module));
            Assert.That(restored.Functions[0].HasTypeMetadata, Is.False);
            Assert.That(restored.Functions[0].LocalCount, Is.EqualTo(2));
        }

        [Test]
        public void Read_RejectsInvalidMagic()
        {
            var bytes = AloeBcCodec.Write(new Module(Array.Empty<AloeValue>(), new[] { new Instruction(EnumOpcode.Return) }, new[] { new FunctionInfo("main", 0, 0, 0) }, 0));
            bytes[0] = (byte)'X';
            Assert.That(() => AloeBcCodec.Read(bytes), Throws.Exception);
        }

        [Test]
        public void RoundTrip_PreservesStringAndBoolConstants()
        {
            var module = new Module(
                new[] { AloeValue.FromString("Aloe"), AloeValue.FromBool(true) },
                new[] { new Instruction(EnumOpcode.Return) },
                new[] { new FunctionInfo("main", 0, 0, 0) },
                0);
            var restored = AloeBcCodec.Read(AloeBcCodec.Write(module));
            Assert.That(restored.Constants[0].AsString, Is.EqualTo("Aloe"));
            Assert.That(restored.Constants[1].AsBool, Is.True);
        }

        [Test]
        public void RoundTrip_PreservesFloat32ConstantExactly()
        {
            const double sourceValue = 1.2345678901234567;
            var expected = (double)(float)sourceValue;
            var module = new Module(
                new[] { AloeValue.FromFloat(expected) },
                new[] { new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.Return) },
                new[] { new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Float) },
                0);

            var restored = AloeBcCodec.Read(AloeBcCodec.Write(module));
            Assert.That(restored.Constants[0].Kind, Is.EqualTo(EnumValueKind.Float));
            Assert.That(BitConverter.SingleToInt32Bits((float)restored.Constants[0].AsFloat), Is.EqualTo(BitConverter.SingleToInt32Bits((float)expected)));

            var vm = new AloeVm(restored);
            vm.RunFromEntryPoint();
            Assert.That(BitConverter.SingleToInt32Bits((float)vm.Pop().AsFloat), Is.EqualTo(BitConverter.SingleToInt32Bits((float)expected)));
        }
    }
}
