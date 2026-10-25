using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    private void RunShuffleFilterQuantum(VmExecutionContext context)
    {
        var input = _pipes[context.InputPipeId];
        var outputId = context.Binding!.OutputPipeId;
        var output = _pipes[outputId];
        var values = context.ShuffleValues ??= new List<AloeValue>();

        if (!context.ShuffleInputClosed)
        {
            if (input.Values.Count > 0)
            {
                var value = input.Values.Peek();
                if (context.Binding.FunctionIndex == -37)
                {
                    if (!value.IsString)
                        throw new VmException("The string shuffle standard filter expects string values.");
                }
                else if (!value.IsInt || value.AsInt is < int.MinValue or > int.MaxValue)
                    throw new VmException("The shuffle standard filter expects valid int32 values.");
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
            context.ShuffleInputClosed = true;
            context.ShufflePermutationIndex = values.Count - 1;
        }

        // One Fisher-Yates swap per quantum keeps other pipe contexts runnable.
        if (context.ShufflePermutationIndex > 0)
        {
            var index = context.ShufflePermutationIndex--;
            var other = Random.Shared.Next(index + 1);
            (values[index], values[other]) = (values[other], values[index]);
            return;
        }

        if (context.ShuffleOutputIndex < values.Count)
        {
            if (output.IsClosed) throw new VmException("Cannot write to a closed pipe.");
            if (output.IsFull)
            {
                WaitForPipeWrite(outputId, output, context);
                _contextSuspendRequested = true;
                return;
            }
            output.Values.Enqueue(values[context.ShuffleOutputIndex++]);
            WakeOnePipeReader(outputId, output);
            SchedulePipeFilters(outputId, output);
            return;
        }

        context.ShuffleValues = null;
        output.IsClosed = true;
        WakeAllPipeWriters(outputId, output);
        WakeAllPipeReaders(outputId, output);
        SchedulePipeFilters(outputId, output);
        context.IsCompleted = true;
    }
}
