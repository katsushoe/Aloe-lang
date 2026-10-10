using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Aloe.CommonLib
{
    public static class AloeBcCodec
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ALOEBC");
        public const byte MajorVersion = 0;
        public const byte MinorVersion = 1;
        public const byte BuildVersion = 0;

        private const byte TypeSection = 0x01;
        private const byte ConstantSection = 0x02;
        private const byte FunctionSection = 0x03;
        private const byte CodeSection = 0x04;

        public static byte[] Write(Module module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(MajorVersion);
            writer.Write(MinorVersion);
            writer.Write(BuildVersion);
            writer.Write((byte)0);
            WriteSection(writer, TypeSection, w => WriteTypeSection(w, module));
            WriteSection(writer, ConstantSection, w => WriteConstantSection(w, module));
            WriteSection(writer, FunctionSection, w => WriteFunctionSection(w, module));
            WriteSection(writer, CodeSection, w => WriteCodeSection(w, module));
            writer.Flush();
            return stream.ToArray();
        }

        public static Module Read(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            ReadAndValidateHeader(reader);

            byte[]? typePayload = null;
            byte[]? constantPayload = null;
            byte[]? functionPayload = null;
            byte[]? codePayload = null;

            while (stream.Position < stream.Length)
            {
                var sectionId = ReadByte(reader, "section id");
                var size = ReadInt32(reader, "section size");
                if (size < 0 || stream.Position + size > stream.Length)
                    throw new VmException($"Invalid AloeBC section size {size} for section 0x{sectionId:X2}.");
                var payload = reader.ReadBytes(size);
                if (payload.Length != size)
                    throw new VmException("Unexpected end of AloeBC while reading section payload.");

                switch (sectionId)
                {
                    case TypeSection:
                        if (typePayload != null) throw DuplicateSection(sectionId);
                        typePayload = payload;
                        break;
                    case ConstantSection:
                        if (constantPayload != null) throw DuplicateSection(sectionId);
                        constantPayload = payload;
                        break;
                    case FunctionSection:
                        if (functionPayload != null) throw DuplicateSection(sectionId);
                        functionPayload = payload;
                        break;
                    case CodeSection:
                        if (codePayload != null) throw DuplicateSection(sectionId);
                        codePayload = payload;
                        break;
                    default:
                        if (sectionId < 0x80)
                            throw new VmException($"Unsupported core AloeBC section 0x{sectionId:X2}.");
                        break;
                }
            }

            if (typePayload == null) throw MissingSection(TypeSection);
            if (constantPayload == null) throw MissingSection(ConstantSection);
            if (functionPayload == null) throw MissingSection(FunctionSection);
            if (codePayload == null) throw MissingSection(CodeSection);

            var types = ReadTypeSection(typePayload);
            var constants = ReadConstantSection(constantPayload);
            var functionData = ReadFunctionSection(functionPayload);
            var code = ReadCodeSection(codePayload);

            if (types.Count != functionData.Functions.Count)
                throw new VmException($"AloeBC TYPE/FUNCTION count mismatch: {types.Count} vs {functionData.Functions.Count}.");

            var functions = new FunctionInfo[functionData.Functions.Count];
            for (var i = 0; i < functions.Length; i++)
            {
                var f = functionData.Functions[i];
                var t = types[i];
                functions[i] = t.HasMetadata
                    ? new FunctionInfo(f.Name, f.EntryIp, t.ParameterTypes, t.LocalTypes, t.ReturnType)
                    : new FunctionInfo(f.Name, f.EntryIp, t.LegacyParameterCount, t.LegacyLocalCount);
            }

            return new Module(constants, code, functions, functionData.EntryPointIndex);
        }

        private static void ReadAndValidateHeader(BinaryReader reader)
        {
            var magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || !magic.SequenceEqual(Magic))
                throw new VmException("Invalid AloeBC magic header.");
            var major = ReadByte(reader, "major version");
            var minor = ReadByte(reader, "minor version");
            var build = ReadByte(reader, "build version");
            _ = ReadByte(reader, "flags");
            if (major != MajorVersion || minor != MinorVersion || build != BuildVersion)
                throw new VmException($"Unsupported AloeBC version {major}.{minor}.{build}; expected {MajorVersion}.{MinorVersion}.{BuildVersion}.");
        }

        private static void WriteSection(BinaryWriter writer, byte sectionId, Action<BinaryWriter> writePayload)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                writePayload(w);
                w.Flush();
            }
            var payload = ms.ToArray();
            writer.Write(sectionId);
            writer.Write(payload.Length);
            writer.Write(payload);
        }

        private static void WriteTypeSection(BinaryWriter writer, Module module)
        {
            writer.Write(module.Functions.Count);
            foreach (var function in module.Functions)
            {
                writer.Write(function.HasTypeMetadata ? (byte)1 : (byte)0);
                if (!function.HasTypeMetadata)
                {
                    writer.Write(function.ParameterCount);
                    writer.Write(function.LocalCount);
                    continue;
                }
                writer.Write(function.ParameterTypes.Count);
                foreach (var type in function.ParameterTypes) writer.Write((byte)type);
                writer.Write(function.LocalTypes.Count);
                foreach (var type in function.LocalTypes) writer.Write((byte)type);
                writer.Write(function.ReturnType.HasValue ? (byte)1 : (byte)0);
                if (function.ReturnType is { } returnType) writer.Write((byte)returnType);
            }
        }

        private static void WriteConstantSection(BinaryWriter writer, Module module)
        {
            writer.Write(module.Constants.Count);
            foreach (var value in module.Constants)
            {
                writer.Write((byte)value.Kind);
                switch (value.Kind)
                {
                    case EnumValueKind.Int:
                        writer.Write(value.AsInt);
                        break;
                    case EnumValueKind.Byte:
                        writer.Write(value.AsByte);
                        break;
                    case EnumValueKind.Char:
                        writer.Write((ushort)value.AsChar);
                        break;
                    case EnumValueKind.Float:
                        writer.Write((float)value.AsFloat);
                        break;
                    case EnumValueKind.Decimal:
                        foreach (var bit in decimal.GetBits(value.AsDecimal))
                            writer.Write(bit);
                        break;
                    case EnumValueKind.Bool:
                        writer.Write(value.AsBool ? (byte)1 : (byte)0);
                        break;
                    case EnumValueKind.String:
                        WriteUtf8(writer, value.AsString);
                        break;
                    default:
                        throw new VmException($"AloeBC 0.1 codec does not support constant kind {value.Kind}.");
                }
            }
        }

        private static void WriteFunctionSection(BinaryWriter writer, Module module)
        {
            writer.Write(module.EntryPointIndex);
            writer.Write(module.Functions.Count);
            foreach (var function in module.Functions)
            {
                WriteUtf8(writer, function.Name);
                writer.Write(function.EntryIp);
            }
        }

        private static void WriteCodeSection(BinaryWriter writer, Module module)
        {
            writer.Write(module.Code.Count);
            foreach (var instruction in module.Code)
            {
                writer.Write((ushort)instruction.Opcode);
                writer.Write(instruction.Operand0);
                writer.Write(instruction.Operand1);
            }
        }

        private static List<TypeRecord> ReadTypeSection(byte[] payload)
        {
            using var ms = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            var count = ReadCount(reader, "type record count");
            var records = new List<TypeRecord>(count);
            for (var i = 0; i < count; i++)
            {
                var hasMetadata = ReadByte(reader, "type metadata flag") != 0;
                if (!hasMetadata)
                {
                    records.Add(TypeRecord.Legacy(ReadCount(reader, "legacy parameter count"), ReadCount(reader, "legacy local count")));
                    continue;
                }
                var parameterTypes = ReadTypeList(reader, "parameter type");
                var localTypes = ReadTypeList(reader, "local type");
                var hasReturn = ReadByte(reader, "return type flag") != 0;
                EnumValueKind? returnType = hasReturn ? ReadValueKind(reader, "return type") : null;
                records.Add(TypeRecord.Typed(parameterTypes, localTypes, returnType));
            }
            RequirePayloadConsumed(ms, "TYPE");
            return records;
        }

        private static IReadOnlyList<AloeValue> ReadConstantSection(byte[] payload)
        {
            using var ms = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            var count = ReadCount(reader, "constant count");
            var constants = new List<AloeValue>(count);
            for (var i = 0; i < count; i++)
            {
                var kind = ReadValueKind(reader, "constant kind");
                constants.Add(kind switch
                {
                    EnumValueKind.Int => AloeValue.FromInt(ReadInt64(reader, "int constant")),
                    EnumValueKind.Byte => AloeValue.FromByte(ReadByte(reader, "byte constant")),
                    EnumValueKind.Char => AloeValue.FromChar((char)ReadUInt16(reader, "char constant")),
                    EnumValueKind.Float => AloeValue.FromFloat(ReadFloat32(reader)),
                    EnumValueKind.Decimal => ReadDecimalConstant(reader),
                    EnumValueKind.Bool => AloeValue.FromBool(ReadByte(reader, "bool constant") switch
                    {
                        0 => false,
                        1 => true,
                        var b => throw new VmException($"Invalid AloeBC bool constant byte {b}.")
                    }),
                    EnumValueKind.String => AloeValue.FromString(ReadUtf8(reader)),
                    _ => throw new VmException($"Unsupported AloeBC constant kind {kind}.")
                });
            }
            RequirePayloadConsumed(ms, "CONSTANT");
            return constants;
        }

        private static AloeValue ReadDecimalConstant(BinaryReader reader)
        {
            var bits = new int[4];
            for (var i = 0; i < bits.Length; i++)
                bits[i] = ReadInt32(reader, "decimal constant bits");
            try
            {
                return AloeValue.FromDecimal(new decimal(bits));
            }
            catch (ArgumentException ex)
            {
                throw new VmException("Invalid decimal constant in AloeBC.", ex);
            }
        }

        private static float ReadFloat32(BinaryReader reader)
        {
            try
            {
                return reader.ReadSingle();
            }
            catch (EndOfStreamException ex)
            {
                throw new VmException("Unexpected end of AloeBC while reading float32 constant.", ex);
            }
        }

        private static FunctionSectionData ReadFunctionSection(byte[] payload)
        {
            using var ms = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            var entryPointIndex = ReadInt32(reader, "entry point index");
            var count = ReadCount(reader, "function count");
            var functions = new List<FunctionRecord>(count);
            for (var i = 0; i < count; i++)
                functions.Add(new FunctionRecord(ReadUtf8(reader), ReadInt32(reader, "function entry IP")));
            RequirePayloadConsumed(ms, "FUNCTION");
            return new FunctionSectionData(entryPointIndex, functions);
        }

        private static IReadOnlyList<Instruction> ReadCodeSection(byte[] payload)
        {
            using var ms = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            var count = ReadCount(reader, "instruction count");
            var code = new List<Instruction>(count);
            for (var i = 0; i < count; i++)
            {
                var raw = ReadUInt16(reader, "opcode");
                var opcode = (EnumOpcode)raw;
                if (!Enum.IsDefined(typeof(EnumOpcode), opcode))
                    throw new VmException($"Unknown AloeBC opcode value 0x{raw:X4}.");
                code.Add(new Instruction(opcode, ReadInt32(reader, "operand0"), ReadInt32(reader, "operand1")));
            }
            RequirePayloadConsumed(ms, "CODE");
            return code;
        }

        private static List<EnumValueKind> ReadTypeList(BinaryReader reader, string context)
        {
            var count = ReadCount(reader, $"{context} count");
            var result = new List<EnumValueKind>(count);
            for (var i = 0; i < count; i++) result.Add(ReadValueKind(reader, context));
            return result;
        }

        private static EnumValueKind ReadValueKind(BinaryReader reader, string context)
        {
            var raw = ReadByte(reader, context);
            var kind = (EnumValueKind)raw;
            if (!Enum.IsDefined(typeof(EnumValueKind), kind))
                throw new VmException($"Unknown AloeBC value kind byte {raw} in {context}.");
            return kind;
        }

        private static void WriteUtf8(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadUtf8(BinaryReader reader)
        {
            var length = ReadCount(reader, "UTF-8 string length");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new VmException("Unexpected end of AloeBC while reading UTF-8 string.");
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException ex) { throw new VmException("Invalid UTF-8 string in AloeBC.", ex); }
        }

        private static int ReadCount(BinaryReader reader, string context)
        {
            var value = ReadInt32(reader, context);
            if (value < 0) throw new VmException($"Negative {context}: {value}.");
            return value;
        }

        private static byte ReadByte(BinaryReader reader, string context)
        {
            try { return reader.ReadByte(); }
            catch (EndOfStreamException ex) { throw new VmException($"Unexpected end of AloeBC while reading {context}.", ex); }
        }

        private static ushort ReadUInt16(BinaryReader reader, string context)
        {
            try { return reader.ReadUInt16(); }
            catch (EndOfStreamException ex) { throw new VmException($"Unexpected end of AloeBC while reading {context}.", ex); }
        }

        private static int ReadInt32(BinaryReader reader, string context)
        {
            try { return reader.ReadInt32(); }
            catch (EndOfStreamException ex) { throw new VmException($"Unexpected end of AloeBC while reading {context}.", ex); }
        }

        private static long ReadInt64(BinaryReader reader, string context)
        {
            try { return reader.ReadInt64(); }
            catch (EndOfStreamException ex) { throw new VmException($"Unexpected end of AloeBC while reading {context}.", ex); }
        }

        private static void RequirePayloadConsumed(Stream stream, string section)
        {
            if (stream.Position != stream.Length) throw new VmException($"AloeBC {section} section contains trailing bytes.");
        }

        private static VmException DuplicateSection(byte sectionId) => new($"Duplicate AloeBC section 0x{sectionId:X2}.");
        private static VmException MissingSection(byte sectionId) => new($"Missing required AloeBC section 0x{sectionId:X2}.");

        private sealed record TypeRecord(bool HasMetadata, IReadOnlyList<EnumValueKind> ParameterTypes, IReadOnlyList<EnumValueKind> LocalTypes, EnumValueKind? ReturnType, int LegacyParameterCount, int LegacyLocalCount)
        {
            public static TypeRecord Typed(IReadOnlyList<EnumValueKind> parameters, IReadOnlyList<EnumValueKind> locals, EnumValueKind? returnType)
                => new(true, parameters, locals, returnType, 0, 0);
            public static TypeRecord Legacy(int parameters, int locals)
                => new(false, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), null, parameters, locals);
        }

        private sealed record FunctionRecord(string Name, int EntryIp);
        private sealed record FunctionSectionData(int EntryPointIndex, IReadOnlyList<FunctionRecord> Functions);
    }
}
