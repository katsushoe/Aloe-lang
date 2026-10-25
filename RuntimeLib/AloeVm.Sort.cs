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
        if (schema != null || context.Binding.FunctionIndex == -36)
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
                if (!value.IsInt || value.AsInt is < int.MinValue or > int.MaxValue)
                    throw new VmException("The sort standard filter expects valid signed int32 values.");
                values.Add(value.AsInt);
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
            output.Values.Enqueue(AloeValue.FromInt(values[context.SortOutputIndex++]));
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
                if (schema == null)
                {
                    if (!value.IsString) throw new VmException("The string sort standard filter expects string values.");
                }
                else
                {
                    if (value.Kind != Aloe.CommonLib.Constants.EnumValueKind.Struct || value.AsStruct.TypeName != schema.TypeName ||
                        schema.FieldIndex >= value.AsStruct.FieldCount)
                        throw new VmException($"The sort filter expects struct '{schema.TypeName}' with field '{schema.FieldName}'.");
                    var key = value.AsStruct.GetField(schema.FieldIndex);
                    if (schema.IsStringKey)
                    {
                        if (!key.IsString)
                            throw new VmException($"The sort key '{schema.FieldName}' must be a string field.");
                    }
                    else if (!key.IsInt || key.AsInt < int.MinValue || key.AsInt > int.MaxValue)
                        throw new VmException($"The sort key '{schema.FieldName}' must be a signed int32 field.");
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
            values = schema == null
                ? values.OrderBy(value => value.AsString, StringComparer.Ordinal).ToList()
                : schema.IsStringKey
                    ? values.OrderBy(value => value.AsStruct.GetField(schema.FieldIndex).AsString, StringComparer.Ordinal).ToList()
                    : values.OrderBy(value => value.AsStruct.GetField(schema.FieldIndex).AsInt).ToList();
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
}
