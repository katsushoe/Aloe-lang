using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

public sealed partial class AloeCompiler
{
    private sealed partial class Parser
    {
        // Decision 0055: element-wise string filters allowed as conditional-stage actions.
        private static readonly HashSet<int> ConditionalStageStringActions =
            [-1, -2, -3, -4, -5, -11, -12, -13, -14, -19, -20, -22, -23, -25, -26];

        /// <summary>
        /// Parses `|: (condition) => action { elseif (condition) => action } [else => action]`
        /// or `|: (condition)` after the `|:` marker. The current token is '('.
        /// </summary>
        private FilterSignature ParseConditionalStage(string pipelineType)
        {
            if (!TryGetPipeElementType(pipelineType, out var elementType))
                throw Error(Current, $"Conditional stage requires pipe<T>, found {pipelineType}.");
            var elementName = pipelineType[5..^1];

            var branches = new JsonArray();
            JsonNode? elseAction = null;
            var first = true;
            while (true)
            {
                var conditionToken = Expect(TokenKind.LParen);
                var condition = ParseConditionOr(elementType, elementName, out var conditionType);
                if (conditionType != ExprType.Bool)
                    throw Error(conditionToken, "A conditional stage condition must be bool.");
                Expect(TokenKind.RParen);

                if (first && !IsArrow())
                {
                    // `|: (condition)` keeps matching elements only.
                    branches.Add(new JsonObject { ["cond"] = condition, ["action"] = Action("keep") });
                    elseAction = Action("drop");
                    break;
                }

                ExpectArrow();
                branches.Add(new JsonObject { ["cond"] = condition, ["action"] = ParseConditionalAction(pipelineType) });
                first = false;

                if (Current.Lexeme == "elseif")
                {
                    Advance();
                    continue;
                }
                if (Current.Lexeme == "else")
                {
                    Advance();
                    ExpectArrow();
                    elseAction = ParseConditionalAction(pipelineType);
                    if (Current.Lexeme is "elseif" or "else")
                        throw Error(Current, "'else' must be the last branch of a conditional stage.");
                }
                break;
            }

            var schema = new JsonObject
            {
                ["branches"] = branches,
                ["else"] = elseAction ?? Action("keep")
            };
            return new FilterSignature("conditional", pipelineType, pipelineType, null, -44,
                schema.ToJsonString());
        }

        private static JsonObject Action(string kind, int index = 0)
            => kind == "filter" ? new JsonObject { ["kind"] = kind, ["index"] = index } : new JsonObject { ["kind"] = kind };

        private bool IsArrow() => Check(TokenKind.Assign) && Peek(1).Kind == TokenKind.Greater;

        private void ExpectArrow()
        {
            if (!IsArrow())
                throw Error(Current, "Expected '=>' in conditional stage.");
            Advance();
            Advance();
        }

        private JsonObject ParseConditionalAction(string pipelineType)
        {
            var token = Expect(TokenKind.Identifier);
            if (token.Lexeme == "drop")
                return Action("drop");
            if (!_filterByName.TryGetValue(token.Lexeme, out var filter) || !ConditionalStageStringActions.Contains(filter.FunctionIndex))
                throw Error(token, $"Conditional stage actions are 'drop' or an element-wise string filter; '{token.Lexeme}' is not supported.");
            if (pipelineType != "pipe<string>")
                throw Error(token, $"Filter '{token.Lexeme}' requires pipe<string>, found {pipelineType}.");
            if (Check(TokenKind.Colon))
                throw Error(Current, "Conditional stage actions do not accept arguments.");
            return Action("filter", filter.FunctionIndex);
        }

        private JsonNode ParseConditionOr(ExprType elementType, string elementName, out ExprType type)
        {
            var left = ParseConditionAnd(elementType, elementName, out type);
            while (Current.Lexeme == "or")
            {
                var op = Advance();
                var right = ParseConditionAnd(elementType, elementName, out var rightType);
                if (type != ExprType.Bool || rightType != ExprType.Bool)
                    throw Error(op, "'or' requires bool operands.");
                left = new JsonObject { ["op"] = "or", ["l"] = left, ["r"] = right };
            }
            return left;
        }

        private JsonNode ParseConditionAnd(ExprType elementType, string elementName, out ExprType type)
        {
            var left = ParseConditionNot(elementType, elementName, out type);
            while (Current.Lexeme == "and")
            {
                var op = Advance();
                var right = ParseConditionNot(elementType, elementName, out var rightType);
                if (type != ExprType.Bool || rightType != ExprType.Bool)
                    throw Error(op, "'and' requires bool operands.");
                left = new JsonObject { ["op"] = "and", ["l"] = left, ["r"] = right };
            }
            return left;
        }

        private JsonNode ParseConditionNot(ExprType elementType, string elementName, out ExprType type)
        {
            if (Current.Lexeme == "not")
            {
                var op = Advance();
                var operand = ParseConditionNot(elementType, elementName, out var operandType);
                if (operandType != ExprType.Bool)
                    throw Error(op, "'not' requires a bool operand.");
                type = ExprType.Bool;
                return new JsonObject { ["op"] = "not", ["e"] = operand };
            }
            return ParseConditionComparison(elementType, elementName, out type);
        }

        private JsonNode ParseConditionComparison(ExprType elementType, string elementName, out ExprType type)
        {
            var left = ParseConditionPrimary(elementType, elementName, out type, out var leftClass);
            if (Current.Kind is not (TokenKind.EqualEqual or TokenKind.BangEqual or TokenKind.Less or
                TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual))
                return left;
            var op = Advance();
            var right = ParseConditionPrimary(elementType, elementName, out var rightType, out var rightClass);
            if (type != rightType || type == ExprType.Enum && leftClass != rightClass)
                throw Error(op, $"Cannot compare {FormatType(type)} with {FormatType(rightType)} in a condition.");
            var ordered = op.Kind is not (TokenKind.EqualEqual or TokenKind.BangEqual);
            if (ordered && type is ExprType.Bool or ExprType.Enum)
                throw Error(op, $"Ordering comparison is not defined for {FormatType(type)}.");
            var kind = type switch
            {
                ExprType.Int or ExprType.Enum => "int",
                ExprType.Byte => "byte",
                ExprType.Bool => "bool",
                ExprType.String => "string",
                ExprType.Char => "char",
                ExprType.Float => "float",
                ExprType.Decimal => "decimal",
                _ => throw Error(op, $"Values of type {FormatType(type)} cannot be compared in a condition.")
            };
            type = ExprType.Bool;
            return new JsonObject { ["op"] = "cmp", ["c"] = op.Lexeme, ["k"] = kind, ["l"] = left, ["r"] = right };
        }

        private JsonNode ParseConditionPrimary(ExprType elementType, string elementName, out ExprType type, out string? className)
        {
            className = null;
            if (Check(TokenKind.LParen))
            {
                Advance();
                var inner = ParseConditionOr(elementType, elementName, out type);
                Expect(TokenKind.RParen);
                return inner;
            }
            if (Check(TokenKind.Dot))
            {
                var dot = Advance();
                if (!Check(TokenKind.Identifier))
                {
                    if (elementType is ExprType.Struct or ExprType.Object)
                        throw Error(dot, $"'.' cannot be used directly for {elementName} elements; use '.Field'.");
                    type = elementType;
                    return new JsonObject { ["op"] = "elem" };
                }
                var fieldToken = Advance();
                if (elementType != ExprType.Struct || !_structs.TryGetValue(elementName, out var structSignature))
                    throw Error(fieldToken, $"'.{fieldToken.Lexeme}' requires a pipe of a declared struct type.");
                var fieldIndex = structSignature.Fields.FindIndex(f => f.Name == fieldToken.Lexeme);
                if (fieldIndex < 0)
                    throw Error(fieldToken, $"Struct '{elementName}' has no field '{fieldToken.Lexeme}'.");
                var field = structSignature.Fields[fieldIndex];
                if (Check(TokenKind.Dot))
                    throw Error(Current, "Conditions accept only one direct field name.");
                type = field.Type;
                className = field.ClassName;
                return new JsonObject { ["op"] = "field", ["i"] = fieldIndex };
            }
            if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Dot &&
                _enumMembers.TryGetValue(Current.Lexeme, out var members))
            {
                var enumToken = Advance();
                Advance(); // .
                var member = Expect(TokenKind.Identifier);
                if (!members.TryGetValue(member.Lexeme, out var value))
                    throw Error(member, $"Enum '{enumToken.Lexeme}' has no member '{member.Lexeme}'.");
                type = ExprType.Enum;
                className = enumToken.Lexeme;
                return Literal("int", value.ToString(CultureInfo.InvariantCulture));
            }

            var negative = false;
            if (Check(TokenKind.Minus) && Peek(1).Kind is TokenKind.IntegerLiteral or TokenKind.FloatLiteral or TokenKind.DecimalLiteral)
            {
                Advance();
                negative = true;
            }
            var token = Current;
            switch (token.Kind)
            {
                case TokenKind.IntegerLiteral:
                    Advance();
                    var integer = ParseInteger(token) * (negative ? -1 : 1);
                    if (integer is < int.MinValue or > int.MaxValue)
                        throw Error(token, "Integer literal is outside signed int32.");
                    type = ExprType.Int;
                    return Literal("int", integer.ToString(CultureInfo.InvariantCulture));
                case TokenKind.FloatLiteral:
                    Advance();
                    type = ExprType.Float;
                    return Literal("float", ((negative ? -1 : 1) * ParseFloat(token)).ToString("R", CultureInfo.InvariantCulture));
                case TokenKind.DecimalLiteral:
                    Advance();
                    type = ExprType.Decimal;
                    return Literal("decimal", ((negative ? -1 : 1) * ParseDecimal(token)).ToString(CultureInfo.InvariantCulture));
            }
            if (negative)
                throw Error(token, "'-' must be followed by a numeric literal in a condition.");
            switch (token.Kind)
            {
                case TokenKind.StringLiteral:
                    Advance();
                    type = ExprType.String;
                    return Literal("string", ParseString(token));
                case TokenKind.CharLiteral:
                    Advance();
                    type = ExprType.Char;
                    return Literal("char", ((int)ParseChar(token)).ToString(CultureInfo.InvariantCulture));
                case TokenKind.BoolLiteral:
                    Advance();
                    type = ExprType.Bool;
                    return Literal("bool", token.Lexeme == "true" ? "true" : "false");
            }
            throw Error(token, "Conditions accept '.', '.Field', literals, enum members, comparisons, and/or/not and parentheses only.");
        }

        /// <summary>
        /// Decision 0056: `|: switch (key) : value [or value] => action, ..., default|_ => action`.
        /// Compiled to the decision 0055 evaluator (index -44) as equality branches.
        /// The current token is 'switch'.
        /// </summary>
        private FilterSignature ParseSwitchStage(string pipelineType)
        {
            if (!TryGetPipeElementType(pipelineType, out var elementType))
                throw Error(Current, $"Switch stage requires pipe<T>, found {pipelineType}.");
            var elementName = pipelineType[5..^1];
            Advance(); // switch
            Expect(TokenKind.LParen);
            var keyToken = Current;
            var key = ParseConditionPrimary(elementType, elementName, out var keyType, out var keyClass);
            if (key["op"]?.GetValue<string>() is not ("elem" or "field"))
                throw Error(keyToken, "A switch key must be '.' or '.Field'.");
            Expect(TokenKind.RParen);
            Expect(TokenKind.Colon);
            var kind = keyType switch
            {
                ExprType.Int or ExprType.Enum => "int",
                ExprType.Byte => "byte",
                ExprType.Bool => "bool",
                ExprType.String => "string",
                ExprType.Char => "char",
                ExprType.Float => "float",
                ExprType.Decimal => "decimal",
                _ => throw Error(keyToken, $"Switch keys of type {FormatType(keyType)} are not supported.")
            };

            var branches = new JsonArray();
            JsonNode? defaultAction = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                if (Current.Lexeme == "default" || Check(TokenKind.Underscore))
                {
                    Advance();
                    ExpectArrow();
                    defaultAction = ParseConditionalAction(pipelineType);
                    if (Check(TokenKind.Comma))
                        throw Error(Current, "'default' must be the last branch of a switch stage.");
                    break;
                }

                JsonNode? condition = null;
                while (true)
                {
                    var valueToken = Current;
                    var value = ParseConditionPrimary(elementType, elementName, out var valueType, out var valueClass);
                    if (value["op"]?.GetValue<string>() != "lit")
                        throw Error(valueToken, "Switch values must be literals or enum members.");
                    if (valueType != keyType || keyType == ExprType.Enum && valueClass != keyClass)
                        throw Error(valueToken, $"Switch value type {FormatType(valueType)} does not match key type {FormatType(keyType)}.");
                    var identity = value["k"]!.GetValue<string>() + ":" + value["v"]!.GetValue<string>();
                    if (!seen.Add(identity))
                        throw Error(valueToken, "Duplicate switch value.");
                    var test = new JsonObject { ["op"] = "cmp", ["c"] = "==", ["k"] = kind, ["l"] = key.DeepClone(), ["r"] = value };
                    condition = condition == null ? test : new JsonObject { ["op"] = "or", ["l"] = condition, ["r"] = test };
                    if (Current.Lexeme != "or")
                        break;
                    Advance();
                }
                ExpectArrow();
                branches.Add(new JsonObject { ["cond"] = condition, ["action"] = ParseConditionalAction(pipelineType) });
                if (!Check(TokenKind.Comma))
                    break;
                Advance();
            }
            if (branches.Count == 0)
                throw Error(Current, "A switch stage needs at least one value branch.");
            var schema = new JsonObject { ["branches"] = branches, ["else"] = defaultAction ?? Action("keep") };
            return new FilterSignature("switch", pipelineType, pipelineType, null, -44, schema.ToJsonString());
        }

        private static JsonObject Literal(string kind, string value)
            => new() { ["op"] = "lit", ["k"] = kind, ["v"] = value };
    }
}
