using System.Globalization;
using System.Net.Http;
using System.Text;
using ChronosTimer;
using ChronosTimer.Audio;
using ChronosTimer.Settings;

namespace ChronosTimer.Cli;

internal static class Program
{
    private const string Usage = """
chronos-timer — portable, remote-controllable timer with SMPTE LTC output and input

USAGE
  chronos-timer [options] [--<setting> <value> …]     run the timer (full-screen console display)
  chronos-timer --headless [options]                  run without a display; commands on stdin, one per line
  chronos-timer send <host[:port]> <command …>        send a command to a running Chronos Timer (HTTP)
  chronos-timer status <host[:port]>                  print a running timer's status (JSON)
  chronos-timer help | commands | settings [name] | devices    reference

OPTIONS
  --settings <file>   settings file to load/save (default: chronos-timer.json next to the program if it exists
                      — portable mode — else the user settings folder)
  --portable          use (and create) chronos-timer.json next to the program
  --no-load           don't load the settings file
  --save              save the settings (after applying the command line) and continue
  --play              start running immediately
  --headless          no display: read commands from stdin, print results
  --<setting> <value> any setting, e.g. --mode count-down --duration 10m --output on --level -18
                      (a toggle given without a value means on: --output)

EXAMPLES
  chronos-timer --mode timecode --start 01:00:00:00 --rate 25 --output on --play
  chronos-timer --mode count-down --duration 5m --end-action continue --warning 1m --critical 10s
  chronos-timer --mode time-of-day --rate 29.97df --user-bits-mode date --output on --play
  chronos-timer --mode chase --input on --output on --play          (read LTC, regenerate it)
  chronos-timer send 192.168.1.20 locate 10:00:00:00
  chronos-timer send studio-pc:8480 play

KEYS (console display)
  Space play/pause · Enter play · S stop · R reset · ←/→ ±1 frame · ↑/↓ ±1 s · PgUp/PgDn ±1 min
  M next mode · V reverse · O output on/off · +/- add/remove 1 min · J jam · : or / command line · Q quit
""";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length > 0)
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "help" or "-h" or "--help" or "-?" or "/?":
                        Console.Write(Usage);
                        return 0;
                    case "--version" or "version":
                        Console.WriteLine("chronos-timer " + typeof(Program).Assembly.GetName().Version);
                        return 0;
                    case "send":
                        return Send(args.Skip(1).ToArray());
                    case "status" when args.Length == 2:
                        return Send([args[1], "__status"]);
                    case "commands" or "settings" or "devices":
                        {
                            using var h = new TimerHost(AudioBackends.CreateDefault());
                            string rest = string.Join(' ', args.Skip(1));
                            var r = h.Execute(args[0].ToLowerInvariant() == "commands" ? "help " + rest : args[0] + " " + rest);
                            Console.WriteLine(r.Message);
                            return r.Ok ? 0 : 1;
                        }
                }
            }
            return Run(args);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("chronos-timer: " + ex.Message);
            return 2;
        }
    }

    // ───────────────────────────── remote client ─────────────────────────────

    private static int Send(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: chronos-timer send <host[:port]> <command …>"); return 2; }
        string host = args[0];
        if (!host.Contains("://", StringComparison.Ordinal)) host = "http://" + host;
        var uri = new UriBuilder(host);
        if (uri.Port is 80 or -1 && !args[0].Contains(':')) uri.Port = 8480;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        if (args[1] == "__status")
        {
            uri.Path = "/api/status";
            Console.WriteLine(http.GetStringAsync(uri.Uri).GetAwaiter().GetResult());
            return 0;
        }
        uri.Path = "/api/command";
        string line = string.Join(' ', args.Skip(1).Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
        using var resp = http.PostAsync(uri.Uri, new StringContent(line, Encoding.UTF8, "text/plain")).GetAwaiter().GetResult();
        string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var d = Control.Json.ReadFlat(body);
        Console.WriteLine(d.GetValueOrDefault("message", body));
        return resp.IsSuccessStatusCode ? 0 : 1;
    }

    // ───────────────────────────── run ─────────────────────────────

    private static int Run(string[] args)
    {
        using var host = new TimerHost(AudioBackends.CreateDefault());
        bool headless = Console.IsInputRedirected, save = false, play = false, load = true;
        string? settingsPath = null;
        var values = new List<KeyValuePair<string, string>>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected '{a}'. Options start with --; see 'chronos-timer help'.");
            string name = a[2..];
            string? inline = null;
            int eq = name.IndexOf('=');
            if (eq >= 0) { inline = name[(eq + 1)..]; name = name[..eq]; }
            switch (name.ToLowerInvariant())
            {
                case "headless": headless = true; continue;
                case "save": save = true; continue;
                case "play": play = true; continue;
                case "no-load": load = false; continue;
                case "portable": settingsPath = SettingsFile.PortablePath; continue;
                case "settings": settingsPath = inline ?? (i + 1 < args.Length ? args[++i] : throw new ArgumentException("--settings needs a file.")); continue;
            }
            var s = host.FindSetting(name) ?? throw new ArgumentException($"Unknown option --{name}. 'chronos-timer settings' lists every setting.");
            string value;
            if (inline is not null) value = inline;
            else if (i + 1 < args.Length && !(args[i + 1].StartsWith("--", StringComparison.Ordinal) && s.Kind == SettingKind.Toggle)) value = args[++i];
            else if (s.Kind == SettingKind.Toggle) value = "on";
            else throw new ArgumentException($"--{name} needs a value ({s.Accepts}).");
            values.Add(new(s.Name, value));
        }

        host.SettingsPath = settingsPath;
        string path = settingsPath ?? SettingsFile.DefaultPath();
        if (load && File.Exists(path))
        {
            foreach (string p in host.Load(path)) Console.Error.WriteLine($"settings {path}: {p}");
        }
        var problems = host.Apply(values);
        if (problems.Count > 0) throw new ArgumentException(string.Join("; ", problems));
        if (save) Console.Error.WriteLine("saved " + host.Save(path));

        host.Start();
        if (play) host.Engine.Play();

        return headless ? Headless(host) : new ConsoleUi(host).Run();
    }

    private static int Headless(TimerHost host)
    {
        host.Message += line => Console.Error.WriteLine(line);
        foreach (string l in host.RecentLog) Console.Error.WriteLine(l);
        Console.Error.WriteLine("chronos-timer running headless. Type commands ('help'), 'quit' to exit.");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var reader = Task.Run(() =>
        {
            string? line;
            while (!cts.IsCancellationRequested && (line = Console.ReadLine()) is not null)
            {
                string t = line.Trim();
                if (t.Length == 0) continue;
                if (t is "quit" or "exit" or "q") { cts.Cancel(); break; }
                var r = host.Execute(t);
                Console.WriteLine(r.Ok ? r.Message : "error: " + r.Message);
            }
        });
        try { reader.Wait(cts.Token); } catch (OperationCanceledException) { }
        // stdin closed: keep serving remote control until Ctrl+C (service use)
        if (!cts.IsCancellationRequested && reader.IsCompleted && Console.IsInputRedirected)
        {
            Console.Error.WriteLine("stdin closed; still running for remote control. Ctrl+C to exit.");
            cts.Token.WaitHandle.WaitOne();
        }
        return 0;
    }
}
