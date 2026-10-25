using System.Text;
using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    private void RunParseStrFilterQuantum(VmExecutionContext context)
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
        if (!value.IsString) throw new VmException("The parse_str standard filter expects string values.");
        var pairs = ParseQueryString(value.AsString);
        var handle = Heap.Allocate(ReadOnlySpan<byte>.Empty, fieldCount: pairs.Count * 2, typeName: "map:string[]|string[]");
        var field = 0;
        foreach (var pair in pairs)
        {
            Heap.SetField(handle, field++, AloeValue.FromString(pair.Key));
            Heap.SetField(handle, field++, AloeValue.FromString(pair.Value));
        }
        input.Values.Dequeue();
        output.Values.Enqueue(AloeValue.FromObject(handle));
        WakeOnePipeWriter(context.InputPipeId, input);
        WakeOnePipeReader(outputId, output);
        SchedulePipeFilters(outputId, output);
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in query.Split('&'))
        {
            if (segment.Length == 0) continue;
            var separator = segment.IndexOf('=');
            var encodedKey = separator < 0 ? segment : segment[..separator];
            var encodedValue = separator < 0 ? string.Empty : segment[(separator + 1)..];
            pairs[DecodeQueryComponent(encodedKey)] = DecodeQueryComponent(encodedValue);
        }
        return pairs;
    }

    private static string DecodeQueryComponent(string component)
    {
        var bytes = new List<byte>(component.Length);
        Span<byte> encoded = stackalloc byte[4];
        for (var index = 0; index < component.Length;)
        {
            var character = component[index];
            if (character == '%')
            {
                if (index + 2 >= component.Length || !TryHex(component[index + 1], out var high) || !TryHex(component[index + 2], out var low))
                    throw new VmException("The parse_str standard filter received a malformed percent escape.");
                bytes.Add((byte)((high << 4) | low));
                index += 3;
                continue;
            }
            if (character == '+')
            {
                bytes.Add((byte)' ');
                index++;
                continue;
            }
            if (!Rune.TryGetRuneAt(component, index, out var rune))
                throw new VmException("The parse_str standard filter received invalid UTF-16 text.");
            var count = rune.EncodeToUtf8(encoded);
            for (var offset = 0; offset < count; offset++) bytes.Add(encoded[offset]);
            index += rune.Utf16SequenceLength;
        }
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException exception)
        {
            throw new VmException("The parse_str standard filter received invalid UTF-8 percent-encoded text.", exception);
        }
    }

    private static bool TryHex(char character, out int value)
    {
        if (character is >= '0' and <= '9') { value = character - '0'; return true; }
        if (character is >= 'a' and <= 'f') { value = character - 'a' + 10; return true; }
        if (character is >= 'A' and <= 'F') { value = character - 'A' + 10; return true; }
        value = 0;
        return false;
    }
}
