using System.Collections.Generic;
using System.IO;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.CommonLib;

public static partial class AloeBcCodec
{
    private static void WriteTaskSection(BinaryWriter writer, IReadOnlyList<AloeTaskTypeInfo> tasks)
    {
        writer.Write(tasks.Count);
        foreach (var task in tasks)
        {
            WriteUtf8(writer, task.Name);
            WriteTaskKinds(writer, task.Fields);
            writer.Write(task.ConstructorIndex ?? -1);
            WriteTaskKinds(writer, task.ConstructorParameters);
            writer.Write(task.Methods.Count);
            foreach (var method in task.Methods)
            {
                WriteUtf8(writer, method.Name);
                writer.Write(method.FunctionIndex);
                WriteTaskKinds(writer, method.Parameters);
            }
        }
    }

    private static void WriteTaskKinds(BinaryWriter writer, IReadOnlyList<EnumValueKind> kinds)
    {
        writer.Write(kinds.Count);
        foreach (var kind in kinds) writer.Write((byte)kind);
    }

    private static IReadOnlyList<AloeTaskTypeInfo> ReadTaskSection(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream);
        var tasks = new List<AloeTaskTypeInfo>();
        var count = ReadCount(reader, "Task type count");
        for (var index = 0; index < count; index++)
        {
            var name = ReadUtf8(reader);
            var fields = ReadTypeList(reader, "Task fields");
            var constructor = ReadInt32(reader, "Task constructor index");
            if (constructor < -1) throw new VmException("Invalid Task constructor index.");
            var parameters = ReadTypeList(reader, "Task constructor parameters");
            var methods = new List<AloeTaskMethodInfo>();
            var methodCount = ReadCount(reader, "Task method count");
            for (var method = 0; method < methodCount; method++)
                methods.Add(new AloeTaskMethodInfo(ReadUtf8(reader), ReadInt32(reader, "Task function index"), ReadTypeList(reader, "Task method parameters")));
            tasks.Add(new AloeTaskTypeInfo(name, fields, constructor == -1 ? null : constructor, parameters, methods));
        }
        RequirePayloadConsumed(stream, "TASK");
        return tasks;
    }
}
