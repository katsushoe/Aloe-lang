using Aloe.Repl;
using System.Text;

Console.WriteLine("Aloe REPL (native)");
Console.WriteLine("Type :help for help. Ctrl+D/Ctrl+Z or :quit exits.");
Console.WriteLine();

var session = new AloeReplSession();
var pending = new StringBuilder();

while (true)
{
    Console.Write(pending.Length == 0 ? "aloe> " : " ...> ");
    var line = Console.ReadLine();
    if (line == null)
        break;

    if (pending.Length == 0 && line.TrimStart().StartsWith(':'))
    {
        var commandResult = session.Submit(line.Trim());
        WriteResult(commandResult);
        if (commandResult.ExitRequested)
            break;
        continue;
    }

    if (pending.Length > 0)
        pending.AppendLine();
    pending.Append(line);

    if (!AloeReplInput.IsComplete(pending.ToString()))
        continue;

    var input = pending.ToString();
    pending.Clear();

    var result = session.Submit(input);
    WriteResult(result);
    if (result.ExitRequested)
        break;
}

static void WriteResult(AloeReplResult result)
{
    if (!string.IsNullOrEmpty(result.Output))
        Console.Write(result.Output);

    if (!result.Success && !string.IsNullOrEmpty(result.Error))
        Console.Error.WriteLine("error: " + result.Error);
}
