using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Diagnostics;
using System.Text;


namespace Aloe.Repl;


public sealed record AloeReplResult(
    bool Success,
    string Output,
    string? Error = null,
    bool ExitRequested = false);


/// <summary>
/// Incremental Aloe REPL session. The current module is recompiled as a whole,
/// while completed main instructions are skipped and the entry locals and VM heap
/// are retained between successful submissions.
/// </summary>
public sealed class AloeReplSession
{
    private readonly List<string> _history = new();
    private readonly AloeVmSettings? _vmSettingsOverride;
    private AloeVm? _vm;
    private AloeValue[] _locals = Array.Empty<AloeValue>();
    private int _executedInstructionCount;
    private bool _faulted;


    public AloeReplSession(AloeVmSettings? vmSettings = null)
    {
        _vmSettingsOverride = vmSettings;
    }


    public IReadOnlyList<string> History => _history;


    public string CurrentProgramSource => BuildProgram(_history, null);


    public AloeReplResult Submit(string input)
    {
        var source = input?.Trim() ?? string.Empty;
        if (source.Length == 0)
            return new AloeReplResult(true, string.Empty);


        if (source.StartsWith(':'))
            return ExecuteCommand(source);

        if (_faulted)
            return new AloeReplResult(false, string.Empty, "REPL session is faulted after a runtime error; use :reset to start a new session.");


        // A complete program that declares main is executed exactly as entered.
        // It is not added to the interactive snippet history and no synthetic main
        // is wrapped around it. This makes pasted .aloe programs behave naturally
        // in the REPL while preserving the existing interactive session.
        if (ContainsMainFunctionDeclaration(source))
            return SubmitStandaloneProgram(source);


        if (StartsWithKeyword(source, "return"))
        {
            return new AloeReplResult(
                false,
                string.Empty,
                "return is managed by the REPL. Enter an expression or statement instead.");
        }


        return LooksLikeStatement(source)
            ? SubmitStatement(source)
            : SubmitExpression(source);
    }


    public void Reset()
    {
        _history.Clear();
        _vm = null;
        _locals = Array.Empty<AloeValue>();
        _executedInstructionCount = 0;
        _faulted = false;
    }


    private AloeReplResult SubmitStandaloneProgram(string source)
    {
        try
        {
            // Reuse module-level declarations already entered in this REPL session.
            // This lets a pasted main() call functions or instantiate classes that
            // were defined interactively before the complete program was submitted.
            var moduleDeclarations = _history.Where(IsModuleDeclaration).ToArray();
            var program = moduleDeclarations.Length == 0
                ? source
                : string.Join(Environment.NewLine + Environment.NewLine, moduleDeclarations)
                  + Environment.NewLine + Environment.NewLine + source;

            return new AloeReplResult(true, Run(program));
        }
        catch (Exception ex)
        {
            return new AloeReplResult(false, string.Empty, ex.Message);
        }
    }


    private AloeReplResult SubmitStatement(string statement)
    {
        try
        {
            var candidateHistory = _history.Concat(new[] { statement }).ToArray();
            var managedStartBytes = System.GC.GetTotalMemory(false);
            var allocationStart = System.GC.GetTotalAllocatedBytes(false);
            var compileStarted = Stopwatch.GetTimestamp();
            var module = new AloeCompiler().Compile(BuildProgram(candidateHistory, null));
            var compileMs = Stopwatch.GetElapsedTime(compileStarted).TotalMilliseconds;
            var allocationAfterCompile = System.GC.GetTotalAllocatedBytes(false);
            var bodyInstructionCount = GetMainBodyInstructionCount(module);
            var visible = RunIncremental(module, _executedInstructionCount, saveLocals: true,
                compileMs, allocationStart, allocationAfterCompile, managedStartBytes);
            _history.Add(statement);
            _executedInstructionCount = bodyInstructionCount;


            return new AloeReplResult(true, visible);
        }
        catch (Exception ex)
        {
            return new AloeReplResult(false, string.Empty, ex.Message);
        }
    }


    private AloeReplResult SubmitExpression(string expression)
    {
        try
        {
            var statement = $"print({expression});";
            var managedStartBytes = System.GC.GetTotalMemory(false);
            var allocationStart = System.GC.GetTotalAllocatedBytes(false);
            var compileStarted = Stopwatch.GetTimestamp();
            var module = new AloeCompiler().Compile(BuildProgram(_history, statement));
            var compileMs = Stopwatch.GetElapsedTime(compileStarted).TotalMilliseconds;
            var allocationAfterCompile = System.GC.GetTotalAllocatedBytes(false);
            var fullOutput = RunIncremental(module, _executedInstructionCount, saveLocals: false,
                compileMs, allocationStart, allocationAfterCompile, managedStartBytes);
            return new AloeReplResult(
                true,
                fullOutput);
        }
        catch (Exception ex)
        {
            return new AloeReplResult(false, string.Empty, ex.Message);
        }
    }


    private AloeReplResult ExecuteCommand(string command)
    {
        switch (command)
        {
            case ":help":
                return new AloeReplResult(true, HelpText);


            case ":reset":
                Reset();
                return new AloeReplResult(true, "Session reset.\n");


            case ":history":
                if (_history.Count == 0)
                    return new AloeReplResult(true, "(empty)\n");


                var history = new StringBuilder();
                for (var i = 0; i < _history.Count; i++)
                    history.Append(i + 1).Append(": ").AppendLine(_history[i]);
                return new AloeReplResult(true, history.ToString());


            case ":source":
                return new AloeReplResult(true, CurrentProgramSource + Environment.NewLine);


            case ":hostgc":
                return RunHostGcDiagnostic();


            case ":quit":
            case ":exit":
                return new AloeReplResult(true, string.Empty, ExitRequested: true);


            default:
                return new AloeReplResult(
                    false,
                    string.Empty,
                    $"Unknown REPL command '{command}'. Type :help for help.");
        }
    }


    private static string Run(string source)
    {
        var managedStartBytes = System.GC.GetTotalMemory(false);
        var allocationStart = System.GC.GetTotalAllocatedBytes(false);

        var compileStarted = Stopwatch.GetTimestamp();
        var module = new AloeCompiler().Compile(source);
        var compileMs = Stopwatch.GetElapsedTime(compileStarted).TotalMilliseconds;
        var allocationAfterCompile = System.GC.GetTotalAllocatedBytes(false);

        var vmCreateStarted = Stopwatch.GetTimestamp();
        var output = new StringBuilder();
        var vm = new AloeVm(module)
        {
            // REPL output always uses LF so results are identical on every host OS.
            OutputWriter = line => output.Append(line).Append('\n'),
            // GC.debug uses TraceWriter independently of VM instruction tracing.
            TraceWriter = line => output.Append(line).Append('\n')
        };
        var vmCreateMs = Stopwatch.GetElapsedTime(vmCreateStarted).TotalMilliseconds;
        var allocationAfterVmCreate = System.GC.GetTotalAllocatedBytes(false);

        var runStarted = Stopwatch.GetTimestamp();
        vm.RunFromEntryPoint();
        var runMs = Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds;
        var allocationAfterRun = System.GC.GetTotalAllocatedBytes(false);
        var managedEndBytes = System.GC.GetTotalMemory(false);

        if (vm.ValueStack.Count != 1)
            throw new InvalidOperationException(
                $"REPL main left {vm.ValueStack.Count} values on the VM stack; expected 1 exit code.");

        var exitCode = vm.Pop();
        if (!exitCode.IsInt)
            throw new InvalidOperationException(
                $"REPL main returned {exitCode.Kind}; expected int.");

        // Emit REPL/host allocation diagnostics only when the program itself requested
        // GC diagnostics. This keeps normal interactive output unchanged.
        if (output.ToString().Contains("[PERF] Program end:", StringComparison.Ordinal))
        {
            var compileAllocated = allocationAfterCompile - allocationStart;
            var vmCreateAllocated = allocationAfterVmCreate - allocationAfterCompile;
            var runAllocated = allocationAfterRun - allocationAfterVmCreate;
            var totalAllocated = allocationAfterRun - allocationStart;
            output.Append(
                $"[REPL-PERF] compileMs={compileMs:F3}, vmCreateMs={vmCreateMs:F3}, runMs={runMs:F3}, " +
                $"compileAllocatedBytes={compileAllocated}, vmCreateAllocatedBytes={vmCreateAllocated}, " +
                $"runAllocatedBytes={runAllocated}, totalAllocatedBytes={totalAllocated}, " +
                $"managedStartBytes={managedStartBytes}, managedEndBytes={managedEndBytes}, " +
                $"managedDeltaBytes={managedEndBytes - managedStartBytes}.").Append('\n');
        }

        return output.ToString();
    }


    private string RunIncremental(
        Module module,
        int instructionOffset,
        bool saveLocals,
        double compileMs,
        long allocationStart,
        long allocationAfterCompile,
        long managedStartBytes)
    {
        var function = module.Functions[module.EntryPointIndex];
        var bodyLength = GetMainBodyInstructionCount(module);
        if (instructionOffset < 0 || instructionOffset > bodyLength)
            throw new InvalidOperationException("REPL instruction boundary is outside the synthesized main body.");

        var vmCreateStarted = Stopwatch.GetTimestamp();
        if (_vm == null)
            _vm = _vmSettingsOverride == null
                ? new AloeVm(module)
                : new AloeVm(module, _vmSettingsOverride);
        else
            _vm.ReplaceModule(module);
        var vmCreateMs = Stopwatch.GetElapsedTime(vmCreateStarted).TotalMilliseconds;

        var locals = new AloeValue[function.LocalCount];
        Array.Copy(_locals, locals, Math.Min(_locals.Length, locals.Length));
        var output = new StringBuilder();
        _vm.OutputWriter = line => output.Append(line).Append('\n');
        _vm.TraceWriter = line => output.Append(line).Append('\n');
        var allocationAfterVmCreate = System.GC.GetTotalAllocatedBytes(false);
        var runStarted = Stopwatch.GetTimestamp();
        try
        {
            _vm.RunFromEntryPoint(function.EntryIp + instructionOffset, locals, preserveRuntimeState: true);

            if (_vm.ValueStack.Count != 1)
                throw new InvalidOperationException($"REPL main left {_vm.ValueStack.Count} values on the VM stack; expected 1 exit code.");
            var exitCode = _vm.Pop();
            if (!exitCode.IsInt)
                throw new InvalidOperationException($"REPL main returned {exitCode.Kind}; expected int.");
        }
        catch
        {
            _faulted = true;
            _vm = null;
            _locals = Array.Empty<AloeValue>();
            _executedInstructionCount = 0;
            throw;
        }
        var runMs = Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds;
        var allocationAfterRun = System.GC.GetTotalAllocatedBytes(false);
        var managedEndBytes = System.GC.GetTotalMemory(false);

        if (saveLocals)
            _locals = locals;

        if (output.ToString().Contains("[PERF] Program end:", StringComparison.Ordinal))
        {
            var compileAllocated = allocationAfterCompile - allocationStart;
            var vmCreateAllocated = allocationAfterVmCreate - allocationAfterCompile;
            var runAllocated = allocationAfterRun - allocationAfterVmCreate;
            var totalAllocated = allocationAfterRun - allocationStart;
            output.Append(
                $"[REPL-PERF] compileMs={compileMs:F3}, vmCreateMs={vmCreateMs:F3}, runMs={runMs:F3}, " +
                $"compileAllocatedBytes={compileAllocated}, vmCreateAllocatedBytes={vmCreateAllocated}, " +
                $"runAllocatedBytes={runAllocated}, totalAllocatedBytes={totalAllocated}, " +
                $"managedStartBytes={managedStartBytes}, managedEndBytes={managedEndBytes}, " +
                $"managedDeltaBytes={managedEndBytes - managedStartBytes}.").Append('\n');
        }

        return output.ToString();
    }


    private static int GetMainBodyInstructionCount(Module module)
    {
        if ((uint)module.EntryPointIndex >= (uint)module.Functions.Count)
            throw new InvalidOperationException("REPL compiler returned an invalid entry point.");
        var function = module.Functions[module.EntryPointIndex];
        var end = module.Code.Count;
        if (function.EntryIp > end - 2 ||
            module.Code[end - 2].Opcode != Aloe.CommonLib.Constants.EnumOpcode.PushConst ||
            module.Code[end - 1].Opcode != Aloe.CommonLib.Constants.EnumOpcode.Return)
            throw new InvalidOperationException("REPL main does not have the expected synthesized return sequence.");
        return end - function.EntryIp - 2;
    }


    private static AloeReplResult RunHostGcDiagnostic()
    {
        var beforeBytes = System.GC.GetTotalMemory(false);
        var gen0Before = System.GC.CollectionCount(0);
        var gen1Before = System.GC.CollectionCount(1);
        var gen2Before = System.GC.CollectionCount(2);
        var started = Stopwatch.GetTimestamp();

        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        System.GC.Collect();

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var afterBytes = System.GC.GetTotalMemory(false);
        var reclaimedBytes = beforeBytes - afterBytes;
        var output =
            $"[HOST-GC] elapsedMs={elapsedMs:F3}, beforeBytes={beforeBytes}, afterBytes={afterBytes}, " +
            $"reclaimedBytes={reclaimedBytes}, gen0={System.GC.CollectionCount(0) - gen0Before}, " +
            $"gen1={System.GC.CollectionCount(1) - gen1Before}, gen2={System.GC.CollectionCount(2) - gen2Before}.\n";

        return new AloeReplResult(true, output);
    }


    private static string BuildProgram(
        IEnumerable<string> history,
        string? transientStatement)
    {
        var snippets = history.ToArray();
        var sb = new StringBuilder();


        // Module declarations live at module scope even when they were entered
        // after ordinary REPL statements. Statements remain inside synthesized main.
        foreach (var declaration in snippets.Where(IsModuleDeclaration))
        {
            sb.AppendLine(declaration);
            sb.AppendLine();
        }


        sb.AppendLine("function main(args: string[]): int {");


        foreach (var statement in snippets.Where(s => !IsModuleDeclaration(s)))
            AppendIndented(sb, statement);


        if (!string.IsNullOrWhiteSpace(transientStatement))
            AppendIndented(sb, transientStatement);


        sb.AppendLine("    return 0;");
        sb.AppendLine("}");
        return sb.ToString();
    }


    private static bool IsModuleDeclaration(string source)
    {
        var trimmed = source.TrimStart();
        return StartsWithKeyword(trimmed, "function") ||
               StartsWithKeyword(trimmed, "class");
    }


    private static bool ContainsMainFunctionDeclaration(string source)
    {
        // Keep this lexical check deliberately small: the compiler remains the
        // authority for whether the submitted complete program is valid Aloe.
        for (var i = 0; i < source.Length;)
        {
            if (!char.IsLetter(source[i]) && source[i] != '_')
            {
                i++;
                continue;
            }

            var start = i++;
            while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                i++;

            if (!source.AsSpan(start, i - start).SequenceEqual("function".AsSpan()))
                continue;

            while (i < source.Length && char.IsWhiteSpace(source[i]))
                i++;

            var nameStart = i;
            while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                i++;

            if (source.AsSpan(nameStart, i - nameStart).SequenceEqual("main".AsSpan()))
            {
                while (i < source.Length && char.IsWhiteSpace(source[i]))
                    i++;
                if (i < source.Length && source[i] == '(')
                    return true;
            }
        }

        return false;
    }


    private static void AppendIndented(StringBuilder sb, string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
            sb.Append("    ").AppendLine(line);
    }


    private static bool LooksLikeStatement(string source)
    {
        if (StartsWithKeyword(source, "function") ||
            StartsWithKeyword(source, "class") ||
            StartsWithKeyword(source, "var") ||
            StartsWithKeyword(source, "let") ||
            StartsWithKeyword(source, "if") ||
            StartsWithKeyword(source, "while") ||
            StartsWithKeyword(source, "print"))
            return true;


        if (source.Contains('{') || source.Contains('}'))
            return true;


        // Aloe statements require ';'. Comparisons such as x == 1
        // stay expressions when entered without a trailing semicolon.
        return source.EndsWith(';');
    }


    private static bool StartsWithKeyword(string source, string keyword)
    {
        if (!source.StartsWith(keyword, StringComparison.Ordinal))
            return false;


        return source.Length == keyword.Length ||
               char.IsWhiteSpace(source[keyword.Length]) ||
               source[keyword.Length] == '(';
    }


    public const string HelpText =
        "Aloe REPL commands:\n" +
        "  :help     show this help\n" +
        "  :history  show persistent snippets\n" +
        "  :source   show the synthesized interactive program\n" +
        "  :reset    clear the session\n" +
        "  :hostgc   force host .NET GC and report before/after managed memory\n" +
        "  :quit     exit the native REPL\n" +
        "\n" +
        "Paste a complete program containing function main(...) to run it as-is.\n" +
        "Current language subset: functions, minimal class declarations, new Type(), typed parameters, recursion, " +
        "var, let, assignment,\n" +
        "if/else, while, break/continue, print, int/string/bool literals, " +
        "local reads,\n" +
        "+ - * / %, == != < <= > >=, not/and/or.\n";
}


public static class AloeReplInput
{
    /// <summary>
    /// Returns false while braces/parentheses/brackets remain open.
    /// String literals and // comments are ignored for balancing purposes.
    /// </summary>
    public static bool IsComplete(string source)
    {
        var braces = 0;
        var parens = 0;
        var brackets = 0;
        var inString = false;
        var escape = false;
        var lineComment = false;


        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];


            if (lineComment)
            {
                if (c == '\n') lineComment = false;
                continue;
            }


            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }


                if (c == '\\')
                {
                    escape = true;
                    continue;
                }


                if (c == '"')
                    inString = false;


                continue;
            }


            if (c == '"')
            {
                inString = true;
                continue;
            }


            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                lineComment = true;
                i++;
                continue;
            }


            switch (c)
            {
                case '{': braces++; break;
                case '}': braces--; break;
                case '(': parens++; break;
                case ')': parens--; break;
                case '[': brackets++; break;
                case ']': brackets--; break;
            }
        }


        return !inString && braces <= 0 && parens <= 0 && brackets <= 0;
    }
}
