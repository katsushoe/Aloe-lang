using System.Globalization;
using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    private void RunNumberFormatFilterQuantum(VmExecutionContext context)
    {
        var input = _pipes[context.InputPipeId];
        var outputId = context.Binding!.OutputPipeId;
        var output = _pipes[outputId];

        if (input.Values.Count == 0)
        {
            if (!input.IsClosed)
            {
                WaitForPipeRead(context.InputPipeId, input, context);
                _contextSuspendRequested = true;
                return;
            }
            output.IsClosed = true;
            WakeAllPipeWriters(outputId, output);
            WakeAllPipeReaders(outputId, output);
            SchedulePipeFilters(outputId, output);
            context.IsCompleted = true;
            return;
        }
        if (output.IsClosed) throw new VmException("Cannot write to a closed pipe.");
        if (output.IsFull)
        {
            WaitForPipeWrite(outputId, output, context);
            _contextSuspendRequested = true;
            return;
        }

        var value = input.Values.Peek();
        if (!value.IsDecimal)
            throw new VmException("The number_format standard filter expects decimal values.");
        var format = context.Binding.StringArgument ?? throw new VmException("Missing number_format spec.");
        string result;
        try
        {
            result = value.AsDecimal.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException exception)
        {
            throw new VmException($"Invalid number_format spec '{format}'.", exception);
        }

        input.Values.Dequeue();
        output.Values.Enqueue(AloeValue.FromString(result));
        WakeOnePipeWriter(context.InputPipeId, input);
        WakeOnePipeReader(outputId, output);
        SchedulePipeFilters(outputId, output);
    }
}
