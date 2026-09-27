using LinearTimecode;
using System.Text;
using ChronosTimer.Settings;

namespace ChronosTimer.Cli;

/// <summary>Full-screen console display with hotkeys and a command line.</summary>
internal sealed class ConsoleUi(TimerHost host)
{
    private readonly Queue<string> _messages = new();
    private string? _prompt;          // non-null while typing a command
    private readonly List<string> _history = [];
    private int _historyPos;
    private string _lastResult = "Type : for a command, ? for help.";
    private bool _quit;
    private int _lastWidth, _lastHeight;

    public int Run()
    {
        host.Message += m => { lock (_messages) { _messages.Enqueue(m); while (_messages.Count > 6) _messages.Dequeue(); } };
        foreach (string l in host.RecentLog.TakeLast(6)) _messages.Enqueue(l);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _quit = true; };
        try { Console.CursorVisible = false; } catch (IOException) { } catch (PlatformNotSupportedException) { }
        Console.Clear();
        try
        {
            while (!_quit)
            {
                while (Console.KeyAvailable) HandleKey(Console.ReadKey(intercept: true));
                Draw();
                Thread.Sleep(40);
            }
        }
        finally
        {
            try { Console.CursorVisible = true; } catch (IOException) { } catch (PlatformNotSupportedException) { }
            Console.ResetColor();
            Console.Clear();
        }
        return 0;
    }

    private void Exec(string line)
    {
        var r = host.Execute(line);
        _lastResult = (r.Ok ? "" : "error: ") + r.Message;
    }

    private void HandleKey(ConsoleKeyInfo k)
    {
        if (_prompt is not null)
        {
            switch (k.Key)
            {
                case ConsoleKey.Enter:
                    string line = _prompt.Trim();
                    _prompt = null;
                    if (line.Length > 0)
                    {
                        _history.Add(line);
                        _historyPos = _history.Count;
                        if (line is "q" or "quit" or "exit") { _quit = true; return; }
                        Exec(line);
                        Console.Clear();
                    }
                    return;
                case ConsoleKey.Escape: _prompt = null; return;
                case ConsoleKey.Backspace: if (_prompt.Length > 0) _prompt = _prompt[..^1]; return;
                case ConsoleKey.UpArrow: if (_historyPos > 0) _prompt = _history[--_historyPos]; return;
                case ConsoleKey.DownArrow: _prompt = _historyPos < _history.Count - 1 ? _history[++_historyPos] : ""; if (_historyPos >= _history.Count - 1) _historyPos = _history.Count; return;
                default: if (!char.IsControl(k.KeyChar)) _prompt += k.KeyChar; return;
            }
        }

        var e = host.Engine;
        switch (k.Key)
        {
            case ConsoleKey.Spacebar: Exec("toggle"); return;
            case ConsoleKey.Enter: Exec("play"); return;
            case ConsoleKey.LeftArrow: Exec("nudge -1"); return;
            case ConsoleKey.RightArrow: Exec("nudge +1"); return;
            case ConsoleKey.UpArrow: Exec("nudge +1s"); return;
            case ConsoleKey.DownArrow: Exec("nudge -1s"); return;
            case ConsoleKey.PageUp: Exec("nudge +1m"); return;
            case ConsoleKey.PageDown: Exec("nudge -1m"); return;
            case ConsoleKey.Escape: return;
        }
        switch (char.ToLowerInvariant(k.KeyChar))
        {
            case 's': Exec("stop"); break;
            case 'p': Exec("pause"); break;
            case 'r': Exec("reset"); break;
            case 'm': Exec("mode"); Console.Clear(); break;
            case 'v': Exec("reverse"); break;
            case 'o': Exec("output toggle"); break;
            case 'i': Exec("input toggle"); break;
            case 'j': Exec("jam"); break;
            case '+' or '=': Exec("add +1m"); break;
            case '-' or '_': Exec("add -1m"); break;
            case '[': Exec(FormattableString.Invariant($"speed {Math.Max(0.01, e.Speed / 2)}")); break;
            case ']': Exec(FormattableString.Invariant($"speed {Math.Min(100, e.Speed * 2)}")); break;
            case '\\': Exec("speed 1"); break;
            case ':' or '/': _prompt = ""; break;
            case '?' or 'h': Exec("help"); Console.Clear(); break;
            case 'q': _quit = true; break;
        }
    }

    private void Draw()
    {
        int w, h;
        try { w = Math.Max(40, Console.WindowWidth); h = Math.Max(16, Console.WindowHeight); }
        catch (IOException) { w = 100; h = 30; }
        if (w != _lastWidth || h != _lastHeight) { Console.Clear(); _lastWidth = w; _lastHeight = h; }

        var s = host.Status;
        var lines = new List<(string Text, ConsoleColor Color)>();
        var fg = ConsoleColor.Gray;

        string title = $" CHRONOS TIMER   {Setting.Token(s.Mode).ToUpperInvariant()} · {Setting.Token(s.State).ToUpperInvariant()} · {s.Rate.DisplayName()} fps" +
                       FormattableString.Invariant($"{(s.Speed != 1 ? $" · ×{s.Speed:0.###}" : "")}{(s.Reverse ? " · REVERSE" : "")}");
        lines.Add((title, ConsoleColor.Cyan));
        lines.Add(("", fg));

        var color = s.Phase switch
        {
            TimerPhase.Warning => ConsoleColor.Yellow,
            TimerPhase.Critical => ConsoleColor.Red,
            TimerPhase.Overrun => DateTime.Now.Millisecond < 500 ? ConsoleColor.Red : ConsoleColor.DarkRed,
            TimerPhase.Ended => ConsoleColor.DarkGray,
            _ => s.State == TransportState.Running ? ConsoleColor.White : ConsoleColor.Gray,
        };
        foreach (string row in BigFont.Render(s.Display, w - 2)) lines.Add(("  " + row, color));
        lines.Add(("", fg));
        lines.Add(("  " + s.Detail, ConsoleColor.Gray));
        if (s.Progress is { } p)
        {
            int bw = Math.Max(10, w - 6);
            int filled = (int)Math.Round(p * bw);
            lines.Add(("  " + new string('█', filled) + new string('░', bw - filled), ConsoleColor.DarkCyan));
        }
        lines.Add(("", fg));
        lines.Add(($"  LTC out: {host.OutputStatus}{(s.OutputActive ? "  ▶ " + host.LastSent : "")}", s.OutputActive ? ConsoleColor.Green : ConsoleColor.DarkGray));
        lines.Add(($"  LTC in:  {host.InputStatus}{(s.Mode == TimerMode.Chase ? "  " + Setting.Token(s.Chase) + (s.ChaseInput is { } ci ? " " + ci : "") : "")}", ConsoleColor.DarkGray));
        string remote = host.Http is { } hs ? hs.Urls[0] : "http off";
        lines.Add(($"  Remote:  {remote}{(host.Osc is { } o ? $"   OSC udp {o.Port}" : "")}", ConsoleColor.DarkGray));
        lines.Add(("", fg));
        lines.Add(("  Space play/pause · S stop · R reset · ←→ frame · ↑↓ second · M mode · V reverse · O output · +/- min · : command · Q quit", ConsoleColor.DarkGray));
        lines.Add(("", fg));

        foreach (string rl in _lastResult.Split('\n').Take(Math.Max(1, h - lines.Count - 9)))
            lines.Add(("  " + rl.TrimEnd('\r'), _lastResult.StartsWith("error", StringComparison.Ordinal) ? ConsoleColor.Red : ConsoleColor.Gray));
        lines.Add(("", fg));
        lock (_messages) foreach (string m in _messages) lines.Add(("  " + m, ConsoleColor.DarkGray));

        Console.SetCursorPosition(0, 0);
        int max = h - 2;
        for (int i = 0; i < Math.Min(lines.Count, max); i++)
        {
            Console.ForegroundColor = lines[i].Color;
            string t = lines[i].Text;
            if (t.Length >= w) t = t[..(w - 1)];
            Console.Write(t.PadRight(w - 1));
            Console.WriteLine();
        }
        Console.ForegroundColor = ConsoleColor.Gray;
        for (int i = Math.Min(lines.Count, max); i < max; i++) { Console.Write(new string(' ', w - 1)); Console.WriteLine(); }
        string promptLine = _prompt is null ? "" : "> " + _prompt + "▌";
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(promptLine.Length >= w ? promptLine[^(w - 1)..] : promptLine.PadRight(w - 1));
        Console.ResetColor();
    }
}

/// <summary>A 5-row block font for the big display.</summary>
internal static class BigFont
{
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = ["███", "█ █", "█ █", "█ █", "███"],
        ['1'] = [" █ ", "██ ", " █ ", " █ ", "███"],
        ['2'] = ["███", "  █", "███", "█  ", "███"],
        ['3'] = ["███", "  █", " ██", "  █", "███"],
        ['4'] = ["█ █", "█ █", "███", "  █", "  █"],
        ['5'] = ["███", "█  ", "███", "  █", "███"],
        ['6'] = ["███", "█  ", "███", "█ █", "███"],
        ['7'] = ["███", "  █", " █ ", " █ ", " █ "],
        ['8'] = ["███", "█ █", "███", "█ █", "███"],
        ['9'] = ["███", "█ █", "███", "  █", "███"],
        [':'] = [" ", "█", " ", "█", " "],
        [';'] = [" ", "█", " ", "█", "▀"],
        ['.'] = [" ", " ", " ", " ", "█"],
        ['+'] = ["   ", " █ ", "███", " █ ", "   "],
        ['-'] = ["   ", "   ", "███", "   ", "   "],
        [' '] = [" ", " ", " ", " ", " "],
    };

    public static string[] Render(string text, int width)
    {
        var rows = new StringBuilder[5];
        for (int r = 0; r < 5; r++) rows[r] = new StringBuilder();
        foreach (char c in text)
        {
            if (!Glyphs.TryGetValue(c, out var g)) g = ["   ", "   ", " ▄ ", "   ", "   "];
            for (int r = 0; r < 5; r++) rows[r].Append(Widen(g[r])).Append(' ');
        }
        var result = rows.Select(r => r.ToString()).ToArray();
        if (result[0].Length > width) // too wide for the window: plain text
            return ["", "", text, "", ""];
        return result;
    }

    // Double every cell horizontally so digits look square in a terminal.
    private static string Widen(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        foreach (char c in s) sb.Append(c).Append(c);
        return sb.ToString();
    }
}
