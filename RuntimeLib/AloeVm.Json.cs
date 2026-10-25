using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    private sealed record JsonFilterField(string Name, string Type);
    private sealed record JsonFilterSchema(string TypeName, IReadOnlyList<JsonFilterField> Fields);
    private sealed record SortFilterSchema(string TypeName, string FieldName, int FieldIndex, bool IsStringKey);

    private static SortFilterSchema ParseSortFilterSchema(string schemaJson)
    {
        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var root = document.RootElement;
            JsonElement fieldType = default;
            var hasFieldType = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("fieldType", out fieldType);
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("typeName", out var typeName) || typeName.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(typeName.GetString()) ||
                !root.TryGetProperty("fieldName", out var fieldName) || fieldName.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(fieldName.GetString()) ||
                !root.TryGetProperty("fieldIndex", out var fieldIndex) || !fieldIndex.TryGetInt32(out var index) || index < 0 ||
                hasFieldType && (fieldType.ValueKind != JsonValueKind.String || fieldType.GetString() is not ("int" or "string")) ||
                root.EnumerateObject().Count() != (hasFieldType ? 4 : 3))
                throw new VmException("The sort filter schema is invalid.");
            return new SortFilterSchema(typeName.GetString()!, fieldName.GetString()!, index,
                hasFieldType && fieldType.GetString() == "string");
        }
        catch (VmException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new VmException("The sort filter schema is invalid.");
        }
    }

    private static JsonFilterSchema ParseJsonFilterSchema(string schemaJson)
    {
        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var root = document.RootElement;
            var rootProperties = ReadExactJsonProperties(root, "typeName", "fields");
            var typeElement = rootProperties["typeName"];
            var fieldsElement = rootProperties["fields"];
            if (typeElement.ValueKind != JsonValueKind.String || fieldsElement.ValueKind != JsonValueKind.Array)
                throw new VmException("The json filter schema is invalid.");
            var typeName = typeElement.GetString();
            if (string.IsNullOrEmpty(typeName)) throw new VmException("The json filter schema is invalid.");

            var fields = new List<JsonFilterField>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in fieldsElement.EnumerateArray())
            {
                var fieldProperties = ReadExactJsonProperties(field, "name", "type");
                var nameElement = fieldProperties["name"];
                var fieldTypeElement = fieldProperties["type"];
                if (nameElement.ValueKind != JsonValueKind.String || fieldTypeElement.ValueKind != JsonValueKind.String)
                    throw new VmException("The json filter schema is invalid.");
                var name = nameElement.GetString();
                var type = fieldTypeElement.GetString();
                if (string.IsNullOrEmpty(name) || !names.Add(name) || type is not ("int" or "bool" or "string"))
                    throw new VmException("The json filter schema is invalid.");
                fields.Add(new JsonFilterField(name, type));
            }
            return new JsonFilterSchema(typeName, fields);
        }
        catch (VmException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new VmException("The json filter schema is invalid.");
        }
    }

    private static Dictionary<string, JsonElement> ReadExactJsonProperties(JsonElement element, params string[] requiredNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new VmException("The json filter schema is invalid.");
        var required = new HashSet<string>(requiredNames, StringComparer.Ordinal);
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!required.Contains(property.Name) || !properties.TryAdd(property.Name, property.Value))
                throw new VmException("The json filter schema is invalid.");
        if (properties.Count != required.Count)
            throw new VmException("The json filter schema is invalid.");
        return properties;
    }

    private void RunJsonFilterQuantum(VmExecutionContext context)
    {
        var binding = context.Binding!;
        var schema = binding.JsonSchema ?? throw new VmException("The json filter schema is missing.");
        var input = _pipes[context.InputPipeId];
        var outputId = binding.OutputPipeId;
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

        var itemIndex = context.JsonInputIndex + 1;
        var inputValue = input.Values.Peek();
        if (!inputValue.IsString)
            throw new VmException($"json input item {itemIndex} must be a string.");
        var decoded = DecodeJsonStruct(inputValue.AsString, schema, itemIndex);
        if (output.IsClosed) throw new VmException("Cannot write to a closed pipe.");
        if (output.IsFull)
        {
            WaitForPipeWrite(outputId, output, context);
            _contextSuspendRequested = true;
            return;
        }

        input.Values.Dequeue();
        context.JsonInputIndex++;
        WakeOnePipeWriter(context.InputPipeId, input);
        output.Values.Enqueue(AloeValue.FromStruct(decoded));
        WakeOnePipeReader(outputId, output);
        SchedulePipeFilters(outputId, output);
    }

    private static AloeStructValue DecodeJsonStruct(string json, JsonFilterSchema schema, int itemIndex)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw JsonItemError(itemIndex, null, "must be a JSON object");

            var values = new AloeValue[schema.Fields.Count];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw JsonItemError(itemIndex, property.Name, "is duplicated");
                var fieldIndex = -1;
                for (var i = 0; i < schema.Fields.Count; i++)
                    if (string.Equals(schema.Fields[i].Name, property.Name, StringComparison.Ordinal))
                    {
                        fieldIndex = i;
                        break;
                    }
                if (fieldIndex < 0) throw JsonItemError(itemIndex, property.Name, "is unknown");

                var field = schema.Fields[fieldIndex];
                values[fieldIndex] = field.Type switch
                {
                    "int" => DecodeJsonInt(property.Value, itemIndex, field.Name),
                    "bool" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False => AloeValue.FromBool(property.Value.GetBoolean()),
                    "string" when property.Value.ValueKind == JsonValueKind.String => AloeValue.FromString(property.Value.GetString()!),
                    "bool" => throw JsonItemError(itemIndex, field.Name, "must be a JSON boolean"),
                    "string" => throw JsonItemError(itemIndex, field.Name, "must be a JSON string"),
                    _ => throw JsonItemError(itemIndex, field.Name, "has an unsupported schema type")
                };
            }

            for (var i = 0; i < schema.Fields.Count; i++)
                if (!seen.Contains(schema.Fields[i].Name))
                    throw JsonItemError(itemIndex, schema.Fields[i].Name, "is missing");
            return new AloeStructValue(schema.TypeName, values);
        }
        catch (JsonException exception)
        {
            throw new VmException($"json input item {itemIndex} is malformed: {exception.Message}");
        }
    }

    private static AloeValue DecodeJsonInt(JsonElement element, int itemIndex, string memberName)
    {
        var raw = element.GetRawText();
        if (element.ValueKind != JsonValueKind.Number ||
            !Regex.IsMatch(raw, @"^-?(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant) ||
            !int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            throw JsonItemError(itemIndex, memberName, "must be a signed int32 JSON integer");
        return AloeValue.FromInt(value);
    }

    private static VmException JsonItemError(int itemIndex, string? member, string detail) =>
        new(member == null
            ? $"json input item {itemIndex} {detail}."
            : $"json input item {itemIndex} member '{member}' {detail}.");
}
