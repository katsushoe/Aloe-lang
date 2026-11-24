using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    private void RunSortFilterQuantum(VmExecutionContext context)
    {
        var input = _pipes[context.InputPipeId];
        var outputId = context.Binding!.OutputPipeId;
        var output = _pipes[outputId];
        var schema = context.Binding.SortSchema;
        if (schema != null || context.Binding.FunctionIndex is -36 or -43)
        {
            RunItemSortFilterQuantum(context, input, outputId, output, schema);
            return;
        }
        var values = context.SortValues ??= new List<long>();

        if (!context.SortReady)
        {
            if (input.Values.Count > 0)
            {
                var value = input.Values.Peek();
                if (context.Binding.FunctionIndex == -40)
                {
                    if (!value.IsBool) throw new VmException("The bool sort standard filter expects bool values.");
                    values.Add(value.AsBool ? 1 : 0);
                }
                else if (context.Binding.FunctionIndex == -38)
                {
                    if (!value.IsByte) throw new VmException("The byte sort standard filter expects byte values.");
                    values.Add(value.AsByte);
                }
                else
                {
                    if (!value.IsInt || value.AsInt is < int.MinValue or > int.MaxValue)
                        throw new VmException("The sort standard filter expects valid signed int32 values.");
                    values.Add(value.AsInt);
                }
                input.Values.Dequeue();
                WakeOnePipeWriter(context.InputPipeId, input);
                return;
            }
            if (!input.IsClosed)
            {
                WaitForPipeRead(context.InputPipeId, input, context);
                _contextSuspendRequested = true;
                return;
            }
            values.Sort();
            context.SortReady = true;
        }

        if (context.SortOutputIndex < values.Count)
        {
            if (output.IsClosed) throw new VmException("Cannot write to a closed pipe.");
            if (output.IsFull)
            {
                WaitForPipeWrite(outputId, output, context);
                _contextSuspendRequested = true;
                return;
            }
            var next = values[context.SortOutputIndex++];
            output.Values.Enqueue(context.Binding.FunctionIndex switch
            {
                -40 => AloeValue.FromBool(next != 0),
                -38 => AloeValue.FromByte((byte)next),
                _ => AloeValue.FromInt(next)
            });
            WakeOnePipeReader(outputId, output);
            SchedulePipeFilters(outputId, output);
            return;
        }

        context.SortValues = null;
        output.IsClosed = true;
        WakeAllPipeWriters(outputId, output);
        WakeAllPipeReaders(outputId, output);
        SchedulePipeFilters(outputId, output);
        context.IsCompleted = true;
    }

    private void RunItemSortFilterQuantum(VmExecutionContext context, PipeState input, long outputId,
        PipeState output, SortFilterSchema? schema)
    {
        var values = context.SortItemValues ??= new List<AloeValue>();
        if (!context.SortReady)
        {
            if (input.Values.Count > 0)
            {
                var value = input.Values.Peek();
                var option = context.Binding!.OptionSortSchema;
                if (schema == null)
                {
                    var element = option?.Element ?? "string";
                    if (!IsSortableScalar(value, element))
                        throw new VmException($"The {element} sort standard filter expects {element} values.");
                }
                else
                {
                    if (value.Kind != Aloe.CommonLib.Constants.EnumValueKind.Struct || value.AsStruct.TypeName != schema.TypeName ||
                        schema.FieldIndex >= value.AsStruct.FieldCount)
                        throw new VmException($"The sort filter expects struct '{schema.TypeName}' with field '{schema.FieldName}'.");
                    var key = value.AsStruct.GetField(schema.FieldIndex);
                    if (!IsSortableScalar(key, schema.KeyType))
                        throw new VmException(schema.KeyType == "int"
                            ? $"The sort key '{schema.FieldName}' must be a signed int32 field."
                            : $"The sort key '{schema.FieldName}' must be a {schema.KeyType} field.");
                }
                values.Add(value);
                input.Values.Dequeue();
                WakeOnePipeWriter(context.InputPipeId, input);
                return;
            }
            if (!input.IsClosed)
            {
                WaitForPipeRead(context.InputPipeId, input, context);
                _contextSuspendRequested = true;
                return;
            }
            {
                var option = context.Binding!.OptionSortSchema;
                var keyType = schema?.KeyType ?? option?.Element ?? "string";
                var descending = schema?.Descending ?? option?.Descending ?? false;
                Func<AloeValue, AloeValue> key = schema == null ? v => v : v => v.AsStruct.GetField(schema.FieldIndex);
                var comparer = Comparer<AloeValue>.Create((x, y) => CompareSortScalars(x, y, keyType));
                // LINQ ordering is stable: ties keep input order in both directions.
                values = descending
                    ? values.OrderByDescending(key, comparer).ToList()
                    : values.OrderBy(key, comparer).ToList();
            }
            context.SortItemValues = values;
            context.SortReady = true;
        }

        if (context.SortOutputIndex < values.Count)
        {
            if (output.IsClosed) throw new VmException("Cannot write to a closed pipe.");
            if (output.IsFull)
            {
                WaitForPipeWrite(outputId, output, context);
                _contextSuspendRequested = true;
                return;
            }
            output.Values.Enqueue(values[context.SortOutputIndex++]);
            WakeOnePipeReader(outputId, output);
            SchedulePipeFilters(outputId, output);
            return;
        }

        context.SortItemValues = null;
        output.IsClosed = true;
        WakeAllPipeWriters(outputId, output);
        WakeAllPipeReaders(outputId, output);
        SchedulePipeFilters(outputId, output);
        context.IsCompleted = true;
    }

    private static bool IsSortableScalar(AloeValue value, string kind) => kind switch
    {
        "int" => value.IsInt && value.AsInt is >= int.MinValue and <= int.MaxValue,
        "string" => value.IsString,
        "byte" => value.IsByte,
        "bool" => value.IsBool,
        "char" => value.IsChar,
        "float" => value.IsFloat && double.IsFinite(value.AsFloat),
        "decimal" => value.IsDecimal,
        _ => false
    };

    // Natural ascending order per kind (decision 0054). Equal values (for example 1.0/1.00 or
    // -0.0/0.0) compare equal so stable ordering preserves their input order and representation.
    private static int CompareSortScalars(AloeValue x, AloeValue y, string kind) => kind switch
    {
        "int" => x.AsInt.CompareTo(y.AsInt),
        "string" => string.CompareOrdinal(x.AsString, y.AsString),
        "byte" => x.AsByte.CompareTo(y.AsByte),
        "bool" => x.AsBool.CompareTo(y.AsBool),
        "char" => x.AsChar.CompareTo(y.AsChar),
        "float" => x.AsFloat == y.AsFloat ? 0 : x.AsFloat < y.AsFloat ? -1 : 1,
        "decimal" => x.AsDecimal.CompareTo(y.AsDecimal),
        _ => throw new VmException($"Unsupported sort kind '{kind}'.")
    };
}
