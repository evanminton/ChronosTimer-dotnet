using System.Globalization;
using ChronosTimer.Control;
using ChronosTimer.Link;
using ChronosTimer.Settings;
using ChronosTimer.Show;

namespace ChronosTimer;

// Show features: scheduled start/end with hold, cue light, messages, and linked timers (master / followers).
public sealed partial class TimerHost
{
    /// <summary>Scheduled show, hold, cue light, messages and the timer link.</summary>
    public ShowController Show { get; }

    internal bool ApplySuspended => _suspendApply;

    private void BuildShowSettings()
    {
        var sh = Show;
        const string S = "Show", L = "Link";

        Add(new Setting("show-start", S, SettingKind.Text, "Scheduled show start: the timer arms as a countdown of the show's length and starts itself at this time, running show-preroll early ('none' = no show). " + ShowTime.Help + ".",
            () => ShowTime.Format(sh.ScheduledStart), v =>
            {
                var now = sh.WallClock();
                var start = ShowTime.Parse(v, now);
                var end = start is null ? null : sh.ScheduledEnd;
                if (end is { } e && start is { } s && e <= s) end = null; // an old end before the new start is dropped
                sh.Schedule(start, end);
            }));
        Add(new Setting("show-end", S, SettingKind.Text, "Scheduled show end ('none' = count up from the start). Holds move it later so the show keeps its length. " + ShowTime.Help + ".",
            () => ShowTime.Format(sh.ScheduledEnd), v =>
            {
                var start = sh.ScheduledStart;
                var end = ShowTime.Parse(v, sh.WallClock(), start);
                if (end is not null && start is null) throw new FormatException("Set show-start first.");
                sh.Schedule(start, end);
            }));
        Add(new Setting("show-preroll", S, SettingKind.Duration, "Timecode preroll: the timer and its LTC start rolling this long before the show start, so receivers are locked at the start (0 = none; seconds, or frames as 12f). A hold delays it too.",
            () => TimerEngine.Fmt(sh.Preroll), v =>
            {
                var p = ParseSpan(v, Engine.Rate);
                if (p < TimeSpan.Zero || p > TimeSpan.FromHours(1)) throw new FormatException("The preroll must be 0 to 1 hour.");
                sh.Preroll = p;
            }));
        Add(new Setting("cue-auto", S, SettingKind.Toggle, "The cue light turns to warning at the countdown's warning time and to end at zero (unless it is on standby or stop).",
            () => Setting.Bool(sh.CueAuto), v => sh.CueAuto = Setting.ParseBool(v)));

        Add(new Setting("link-role", L, SettingKind.Choice, "Link with other Chronos Timers: one master, any number of followers that mirror its time, cue light and messages.",
            () => Setting.Token(sh.Role), v => sh.Role = Setting.ParseEnum<LinkRole>(v),
            Opts(("off", "Not linked"), ("master", "Serve this timer's time, show schedule and cue light to followers"), ("follower", "Show the master's time and cue light; send messages"))));
        Add(new Setting("link-name", L, SettingKind.Text, $"This timer's name on the link ('auto' = {LinkProtocol.AppName}## : the master is {LinkProtocol.NodeName(1)}, followers are numbered by the master).",
            () => sh.Name, v => sh.Name = v));
        Add(new Setting("link-node", L, SettingKind.Text, "The node name the master gave this timer last time; asked for again when it reconnects ('none' = next free).",
            () => sh.LastNode.Length == 0 ? "none" : sh.LastNode, v => sh.LastNode = v));
        Add(new Setting("link-bind", L, SettingKind.Choice, "Network the master listens and announces itself on: apipa (169.254.x.x link-local, the default), all, or one interface.",
            () => sh.Bind, v => sh.Bind = v, dynamicOptions: NetworkBinding.Options));
        Add(new Setting("link-port", L, SettingKind.Number, "TCP port of the link (masters announce themselves on UDP 8491).",
            () => sh.Port.ToString(CultureInfo.InvariantCulture), v => sh.Port = (int)Setting.ParseNumber(v, 1, 65_535)));
        Add(new Setting("link-master", L, SettingKind.Text, "Follower: the master's address (host or host:port), or 'auto' to find it on the network.",
            () => sh.MasterAddress, v => sh.MasterAddress = v));
        Add(new Setting("link-key", L, SettingKind.Text, "Shared key followers must give to join ('off' = none: any Chronos Timer on the network can join and send messages).",
            () => sh.Key.Length == 0 ? "off" : sh.Key, v => sh.Key = v));
    }

    private void BuildShowCommands()
    {
        var sh = Show;
        var c = Commands;
        c.Add("show", "show [start <when>|end <when>|preroll <time>|hold|release|clear]",
            "Scheduled show: set the start/end (" + ShowTime.Help + ") or the timecode preroll before the start (5s, 125f), hold the start (the end moves with the delay), release it, or clear it. No argument: show status.",
            (a, raw) =>
            {
                string sub = a.Length > 0 ? a[0].ToLowerInvariant() : "";
                string rest = raw.Length > sub.Length ? raw[sub.Length..].Trim() : "";
                switch (sub)
                {
                    case "":
                    case "status":
                        break;
                    case "start":
                        Set("show-start", rest);
                        break;
                    case "end":
                        Set("show-end", rest);
                        break;
                    case "preroll" or "pre-roll":
                        Set("show-preroll", rest);
                        break;
                    case "hold":
                        sh.SetHold(true);
                        break;
                    case "release" or "go" or "resume":
                        sh.SetHold(false);
                        break;
                    case "toggle":
                        sh.SetHold(!sh.Holding);
                        break;
                    case "clear" or "none" or "off":
                        sh.Schedule(null, null);
                        break;
                    default:
                        return CommandResult.Error($"Unknown show command '{sub}'. Use: show start <when>, show end <when>, show preroll <time>, show hold, show release, show clear.");
                }
                return Ok(sh.StatusText());
            });
        c.Add("cue", "cue [off|standby|go|warning|end|stop|ack]", "Set the cue light every linked timer shows (master), or acknowledge it (follower). No argument: show it.",
            (a, raw) =>
            {
                if (raw.Length == 0) return Ok("cue " + Setting.Token(sh.Cue));
                if (raw.Trim().Equals("ack", StringComparison.OrdinalIgnoreCase) || raw.Trim().Equals("acknowledge", StringComparison.OrdinalIgnoreCase))
                {
                    sh.Acknowledge();
                    return Ok("cue acknowledged");
                }
                sh.SetCue(Setting.ParseEnum<CueLight>(raw), t_source);
                return Ok("cue " + Setting.Token(sh.Cue));
            }, "light");
        c.Add("message", "message [@name] <text>", "Send a message to every linked timer, or to one (message @ChronosTimer02 places please).",
            (a, raw) =>
            {
                if (raw.Length == 0) return Ok(string.Join('\n', sh.Messages.TakeLast(20)));
                return Ok("sent: " + sh.Send(raw));
            }, "msg", "say");
        c.Add("link", "link", "Show the link state and the connected timers.", (_, _) =>
            Ok("link " + sh.LinkStatus + string.Concat(sh.Followers.Select(f => $"\n  {f.Name}  {f.Address}{(f.Acknowledged ? "  cue acknowledged" : "")}"))));
    }
}
