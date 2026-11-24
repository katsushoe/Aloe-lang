using System.Globalization;
using System.Text.Json;
using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    // Decision 0055: `|: (condition) => action { elseif ... } [else => action]` and `|: (condition)`.
    private sealed record ConditionalStageSchema(IReadOnlyList<(ConditionNode Condition, StageAction Action)> Branches, StageAction Else);

    private sealed record StageAction(string Kind, int FilterIndex);

    private sealed record ConditionNode(string Op, ConditionNode? Left, ConditionNode? Right, string? Comparison,
        string? Kind, int FieldIndex, AloeValue Literal);

    private static readonly HashSet<int> ConditionalStringActions = [-1, -2, -3, -4, -5, -11, -12, -13, -14, -19, -20, -22, -23, -25, -26];

    private static ConditionalStageSchema ParseConditionalStageSchema(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var branches = new List<(ConditionNode, StageAction)>();
            foreach (var branch in root.GetProperty("branches").EnumerateArray())
                branches.Add((ParseConditionNode(branch.GetProperty("cond"), 0), ParseStageAction(branch.GetProperty("action"))));
            if (branches.Count == 0)
                throw new VmException("The conditional stage schema is invalid.");
            return new ConditionalStageSchema(branches, ParseStageAction(root.GetProperty("else")));
        }
        catch (VmException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new VmException("The conditional stage schema is invalid.");
        }
    }

    private static StageAction ParseStageAction(JsonElement element)
    {
        var kind = element.GetProperty("kind").GetString();
        if (kind is "keep" or "drop")
            return new StageAction(kind, 0);
        if (kind == "filter")
        {
            var index = element.GetProperty("index").GetInt32();
            if (!ConditionalStringActions.Contains(index))
                throw new VmException("The conditional stage action is not an element-wise string filter.");
            return new StageAction(kind, index);
        }
        throw new VmException("The conditional stage action is invalid.");
    }

    private static ConditionNode ParseConditionNode(JsonElement element, int depth)
    {
        if (depth > 64)
            throw new VmException("The conditional stage condition is too deeply nested.");
        var op = element.GetProperty("op").GetString();
        return op switch
        {
            "and" or "or" => new ConditionNode(op, ParseConditionNode(element.GetProperty("l"), depth + 1),
                ParseConditionNode(element.GetProperty("r"), depth + 1), null, null, 0, default),
            "not" => new ConditionNode(op, ParseConditionNode(element.GetProperty("e"), depth + 1), null, null, null, 0, default),
            "cmp" => new ConditionNode(op, ParseConditionNode(element.GetProperty("l"), depth + 1),
                ParseConditionNode(element.GetProperty("r"), depth + 1),
                element.GetProperty("c").GetString() is var c && c is "==" or "!=" or "<" or "<=" or ">" or ">=" ? c
                    : throw new VmException("The conditional stage comparison is invalid."),
                element.GetProperty("k").GetString(), 0, default),
            "elem" => new ConditionNode(op, null, null, null, null, 0, default),
            "field" => new ConditionNode(op, null, null, null, null, element.GetProperty("i").GetInt32() is var i && i >= 0 ? i
                : throw new VmException("The conditional stage field index is invalid."), default),
            "lit" => new ConditionNode(op, null, null, null, null, 0,
                ParseConditionLiteral(element.GetProperty("k").GetString(), element.GetProperty("v").GetString()!)),
            _ => throw new VmException("The conditional stage condition is invalid.")
        };
    }

    private static AloeValue ParseConditionLiteral(string? kind, string value) => kind switch
    {
        "int" => AloeValue.FromInt(long.Parse(value, CultureInfo.InvariantCulture)),
        "float" => AloeValue.FromFloat(double.Parse(value, CultureInfo.InvariantCulture)),
        "decimal" => AloeValue.FromDecimal(decimal.Parse(value, CultureInfo.InvariantCulture)),
        "string" => AloeValue.FromString(value),
        "char" => AloeValue.FromChar((char)int.Parse(value, CultureInfo.InvariantCulture)),
        "bool" => AloeValue.FromBool(value == "true"),
        _ => throw new VmException("The conditional stage literal is invalid.")
    };

    private static AloeValue EvaluateCondition(ConditionNode node, AloeValue element) => node.Op switch
    {
        "and" => AloeValue.FromBool(EvaluateCondition(node.Left!, element).AsBool && EvaluateCondition(node.Right!, element).AsBool),
        "or" => AloeValue.FromBool(EvaluateCondition(node.Left!, element).AsBool || EvaluateCondition(node.Right!, element).AsBool),
        "not" => AloeValue.FromBool(!EvaluateCondition(node.Left!, element).AsBool),
        "elem" => element,
        "field" => element.Kind == Aloe.CommonLib.Constants.EnumValueKind.Struct && node.FieldIndex < element.AsStruct.FieldCount
            ? element.AsStruct.GetField(node.FieldIndex)
            : throw new VmException("The conditional stage expects a struct element with the referenced field."),
        "lit" => node.Literal,
        "cmp" => AloeValue.FromBool(CompareCondition(node, element)),
        _ => throw new VmException("The conditional stage condition is invalid.")
    };

    private static bool CompareCondition(ConditionNode node, AloeValue element)
    {
        var left = EvaluateCondition(node.Left!, element);
        var right = EvaluateCondition(node.Right!, element);
        var kind = node.Kind!;
        if (!IsSortableScalar(left, kind) || !IsSortableScalar(right, kind))
            throw new VmException($"The conditional stage comparison expects {kind} values.");
        var order = CompareSortScalars(left, right, kind);
        return node.Comparison switch
        {
            "==" => order == 0,
            "!=" => order != 0,
            "<" => order < 0,
            "<=" => order <= 0,
            ">" => order > 0,
            ">=" => order >= 0,
            _ => throw new VmException("The conditional stage comparison is invalid.")
        };
    }

    private void RunConditionalStageQuantum(VmExecutionContext context)
    {
        var input = _pipes[context.InputPipeId];
        var outputId = context.Binding!.OutputPipeId;
        var output = _pipes[outputId];
        var schema = context.Binding.ConditionalSchema ?? throw new VmException("Missing conditional stage schema.");

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

        if (output.IsClosed)
            throw new VmException("Cannot write to a closed pipe.");
        if (output.IsFull)
        {
            WaitForPipeWrite(outputId, output, context);
            _contextSuspendRequested = true;
            return;
        }

        var value = input.Values.Dequeue();
        var action = schema.Else;
        foreach (var (condition, branchAction) in schema.Branches)
        {
            var result = EvaluateCondition(condition, value);
            if (!result.IsBool)
                throw new VmException("The conditional stage condition must be bool.");
            if (result.AsBool)
            {
                action = branchAction;
                break;
            }
        }

        if (action.Kind != "drop")
        {
            var emitted = action.Kind == "filter"
                ? value.IsString
                    ? AloeValue.FromString(ApplyStringFilter(action.FilterIndex, value.AsString))
                    : throw new VmException("The conditional stage string filter expects string values.")
                : value;
            output.Values.Enqueue(emitted);
            WakeOnePipeReader(outputId, output);
        }
        WakeOnePipeWriter(context.InputPipeId, input);
        SchedulePipeFilters(outputId, output);
    }
}
