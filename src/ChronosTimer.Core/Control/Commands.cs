using System.Text;

namespace ChronosTimer.Control;

/// <summary>Outcome of a text command.</summary>
public sealed record CommandResult(bool Ok, string Message)
{
    public static CommandResult Success(string message) => new(true, message);
    public static CommandResult Error(string message) => new(false, message);
    public override string ToString() => Ok ? Message : "error: " + Message;
}

/// <summary>Description of a text command.</summary>
public sealed record CommandInfo(string Name, string Usage, string Description, IReadOnlyList<string> Aliases);

/// <summary>A registry of text commands ("locate 01:00:00:00", "set duration 5m", …).</summary>
public sealed class CommandSet
{
    private readonly List<(CommandInfo Info, Func<string[], string, CommandResult> Run)> _commands = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called for a first word that isn't a command (e.g. a setting name). Return null if not handled.</summary>
    public Func<string, string, CommandResult?>? Fallback { get; set; }

    public IReadOnlyList<CommandInfo> All => [.. _commands.Select(c => c.Info)];

    /// <summary>Adds a command. <paramref name="run"/> gets the split arguments and the raw text after the command word.</summary>
    public void Add(string name, string usage, string description, Func<string[], string, CommandResult> run, params string[] aliases)
    {
        var info = new CommandInfo(name, usage, description, aliases);
        _commands.Add((info, run));
        _index[name] = _commands.Count - 1;
        foreach (var a in aliases) _index[a] = _commands.Count - 1;
    }

    public CommandInfo? Find(string name) => _index.TryGetValue(name, out int i) ? _commands[i].Info : null;

    /// <summary>Runs one command line. Never throws: errors come back as <see cref="CommandResult.Ok"/> = false.</summary>
    public CommandResult Execute(string line)
    {
        line = (line ?? "").Trim();
        if (line.Length == 0) return CommandResult.Error("Empty command. Type 'help' for the list.");
        if (line.StartsWith('/')) line = line.TrimStart('/').Replace('/', ' ');

        int sp = line.IndexOfAny([' ', '\t']);
        string word = sp < 0 ? line : line[..sp];
        string rest = sp < 0 ? "" : line[(sp + 1)..].Trim();
        string[] args = Split(rest);

        try
        {
            if (_index.TryGetValue(word, out int i)) return _commands[i].Run(args, rest);
            if (Fallback?.Invoke(word, rest) is { } r) return r;
            return CommandResult.Error($"Unknown command '{word}'. Type 'help' for the list of commands, 'settings' for the settings.");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return CommandResult.Error(ex.Message);
        }
    }

    /// <summary>Splits on whitespace, honouring "double quotes".</summary>
    public static string[] Split(string text)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in text)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any) { list.Add(sb.ToString()); sb.Clear(); any = false; }
                continue;
            }
            sb.Append(c); any = true;
        }
        if (any) list.Add(sb.ToString());
        return [.. list];
    }

    /// <summary>Human-readable command list.</summary>
    public string Help()
    {
        var sb = new StringBuilder();
        int w = _commands.Max(c => c.Info.Usage.Length);
        foreach (var (info, _) in _commands)
        {
            sb.Append("  ").Append(info.Usage.PadRight(w + 2)).Append(info.Description);
            if (info.Aliases.Count > 0) sb.Append("  (also: ").Append(string.Join(", ", info.Aliases)).Append(')');
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
