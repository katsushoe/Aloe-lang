using Aloe.CompilerLib.Lexer;

namespace Aloe.CompilerLib;

internal sealed record TaskParameter(string Name, string TypeName);
internal sealed record TaskField(string Name, string TypeName);
internal sealed record TaskBody(int Start, int End);
internal sealed record TaskMethod(string Name, IReadOnlyList<TaskParameter> Parameters, TaskBody Body);
internal sealed record TaskDeclaration(string Name, IReadOnlyList<TaskField> Fields,
    IReadOnlyList<TaskParameter> ConstructorParameters, TaskBody? ConstructorBody,
    IReadOnlyList<TaskMethod> Methods);

/// <summary>Reads the adopted initial Task declaration grammar; method bodies await the execution binder.</summary>
internal sealed class TaskDeclarationReader
{
    private static readonly HashSet<string> Scalars = new(StringComparer.Ordinal)
        { "int", "bool", "boolean", "byte", "char", "float", "decimal", "string" };
    private static readonly HashSet<string> ReservedTypes = new(Scalars, StringComparer.Ordinal)
        { "void", "object", "ExceptionInfo", "Integer", "Byte", "Char", "Float", "Decimal", "String", "Boolean" };
    private readonly IReadOnlyList<AloeToken> _tokens;
    private readonly HashSet<string> _classes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tasks = new(StringComparer.Ordinal);
    private readonly List<int> _starts = new();
    private int _position;

    private TaskDeclarationReader(IReadOnlyList<AloeToken> tokens) => _tokens = tokens;

    internal static IReadOnlyList<TaskDeclaration> Read(IReadOnlyList<AloeToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0 || tokens[^1].Kind != TokenKind.EndOfFile)
            throw new ArgumentException("Token stream must end with EOF.", nameof(tokens));
        var reader = new TaskDeclarationReader(tokens);
        reader.CollectTypes();
        return reader._starts.Select(reader.ReadDeclaration).ToArray();
    }

    private void CollectTypes()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var depth = 0;
        for (var index = 0; index < _tokens.Count - 1; index++)
        {
            var token = _tokens[index];
            if (depth == 0 && token.Kind == TokenKind.Keyword &&
                token.Lexeme is "task" or "class" or "struct" or "interface" or "enum")
            {
                var name = _tokens[index + 1];
                if (token.Lexeme == "task" && ReservedTypes.Contains(name.Lexeme))
                    throw Error(name, "Task name conflicts with a built-in type.");
                if (name.Kind != TokenKind.Identifier || !names.Add(name.Lexeme))
                    throw Error(name, "Type names must be unique identifiers.");
                if (token.Lexeme == "class") _classes.Add(name.Lexeme);
                if (token.Lexeme == "task")
                {
                    if (index > 0 && _tokens[index - 1].Lexeme is "sealed" or "public" or "private" or "protected" or "async")
                        throw Error(token, "Task declaration modifiers are outside the initial subset.");
                    _tasks.Add(name.Lexeme);
                    _starts.Add(index);
                }
            }
            if (token.Kind == TokenKind.LBrace) depth++;
            else if (token.Kind == TokenKind.RBrace) depth--;
            if (depth < 0) throw Error(token, "Unexpected closing brace.");
        }
        if (_starts.Count == 0 && _tokens.Any(token => token.Kind == TokenKind.Keyword && token.Lexeme == "task"))
            throw Error(_tokens.First(token => token.Lexeme == "task" && token.Kind == TokenKind.Keyword), "Task declarations must be top-level.");
    }

    private TaskDeclaration ReadDeclaration(int start)
    {
        _position = start + 1;
        var name = Identifier();
        Expect(TokenKind.LBrace);
        var fields = new List<TaskField>();
        var methods = new List<TaskMethod>();
        IReadOnlyList<TaskParameter> constructorParameters = Array.Empty<TaskParameter>();
        TaskBody? constructor = null;
        var members = new HashSet<string>(StringComparer.Ordinal);
        while (Current.Kind != TokenKind.RBrace)
        {
            var member = Advance();
            if (member.Kind != TokenKind.Keyword) throw Error(member, "Expected a Task member keyword.");
            switch (member.Lexeme)
            {
                case "field":
                {
                    var field = Identifier();
                    if (!members.Add(field.Lexeme)) throw Error(field, "Task members must have unique names.");
                    Expect(TokenKind.Colon);
                    var type = TypeName();
                    Expect(TokenKind.Semicolon);
                    fields.Add(new TaskField(field.Lexeme, type));
                    break;
                }
                case "construct":
                    if (constructor != null) throw Error(member, "Task may declare only one constructor.");
                    constructorParameters = Parameters();
                    constructor = Body();
                    break;
                case "method":
                {
                    var method = Identifier();
                    if (method.Lexeme is "start" or "stop") throw Error(method, "Task start and stop are built-in methods.");
                    if (!members.Add(method.Lexeme)) throw Error(method, "Task members must have unique names.");
                    var parameters = Parameters();
                    Expect(TokenKind.Colon);
                    var result = Advance();
                    if (result.Kind is not (TokenKind.Identifier or TokenKind.Keyword) || result.Lexeme != "void")
                        throw Error(result, "Initial Task methods must return void.");
                    if (method.Lexeme == "run" && parameters.Count != 0)
                        throw Error(method, "Task run must have no arguments.");
                    methods.Add(new TaskMethod(method.Lexeme, parameters, Body()));
                    break;
                }
                default:
                    throw Error(member, "Expected field, construct or method in the initial Task subset.");
            }
        }
        Advance();
        if (!methods.Any(method => method.Name == "run")) throw Error(name, "Task must declare method run(): void.");
        return new TaskDeclaration(name.Lexeme, fields.ToArray(), constructorParameters, constructor, methods.ToArray());
    }

    private IReadOnlyList<TaskParameter> Parameters()
    {
        Expect(TokenKind.LParen);
        var parameters = new List<TaskParameter>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (Current.Kind != TokenKind.RParen)
        {
            while (true)
            {
                var name = Identifier();
                if (!names.Add(name.Lexeme)) throw Error(name, "Task parameter names must be unique.");
                Expect(TokenKind.Colon);
                parameters.Add(new TaskParameter(name.Lexeme, TypeName()));
                if (Current.Kind != TokenKind.Comma) break;
                Advance();
            }
        }
        Expect(TokenKind.RParen);
        return parameters.ToArray();
    }

    private string TypeName()
    {
        var name = Identifier();
        if (_tasks.Contains(name.Lexeme)) throw Error(name, "Task handles cannot be parameters or fields.");
        if (!Scalars.Contains(name.Lexeme) && name.Lexeme != "object" && !_classes.Contains(name.Lexeme))
            throw Error(name, "Initial Task signatures accept scalars and declared class/object references only.");
        if (Current.Kind is TokenKind.LBracket or TokenKind.LBrace or TokenKind.LParen or TokenKind.Less)
            throw Error(Current, "Collection signatures are outside the initial Task subset.");
        return name.Lexeme == "boolean" ? "bool" : name.Lexeme;
    }

    private TaskBody Body()
    {
        var start = _position;
        Expect(TokenKind.LBrace);
        var depth = 1;
        while (depth > 0)
        {
            var token = Advance();
            if (token.Kind == TokenKind.EndOfFile) throw Error(token, "Unterminated Task member body.");
            if (token.Kind == TokenKind.Keyword && token.Lexeme == "task")
                throw Error(token, "Nested Task declarations are not supported.");
            if (token.Kind == TokenKind.LBrace) depth++;
            else if (token.Kind == TokenKind.RBrace) depth--;
        }
        return new TaskBody(start, _position);
    }

    private AloeToken Current => _tokens[Math.Min(_position, _tokens.Count - 1)];
    private AloeToken Advance() { var token = Current; if (_position < _tokens.Count - 1) _position++; return token; }
    private AloeToken Identifier()
    {
        var token = Advance();
        if (token.Kind != TokenKind.Identifier || token.Lexeme == "this") throw Error(token, "Expected a non-reserved identifier.");
        return token;
    }
    private void Expect(TokenKind kind)
    {
        var token = Advance();
        if (token.Kind != kind) throw Error(token, "Expected " + kind + ".");
    }
    private static AloeCompileException Error(AloeToken token, string message)
        => new($"{message} (line {token.Line}, col {token.Column})");
}
