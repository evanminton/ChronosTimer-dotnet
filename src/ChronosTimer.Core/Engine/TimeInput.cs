using System.Globalization;
using LinearTimecode;

namespace ChronosTimer;

/// <summary>
/// A time typed by a person or sent by a controller. Accepts every common form:
/// <list type="bullet">
/// <item><c>01:00:00:00</c>, <c>01:00:00;00</c> — timecode (hours:minutes:seconds:frames; <c>;</c> = drop-frame)</item>
/// <item><c>1:30:00</c> — hours:minutes:seconds</item>
/// <item><c>5:00</c>, <c>4:59.5</c> — minutes:seconds (with optional decimals)</item>
/// <item><c>90</c>, <c>90.5</c> — seconds</item>
/// <item><c>1h30m</c>, <c>5m</c>, <c>45s</c>, <c>12f</c>, <c>1h2m3s4f</c> — units</item>
/// </list>
/// A leading <c>+</c> or <c>-</c> marks the value as relative (for nudge / add).
/// </summary>
public readonly record struct TimeInput(int Hours, int Minutes, double Seconds, int Frames, int Sign, bool Relative, bool DropFrameSeparator, bool HasFrames)
{
    /// <summary>Human-readable summary of the accepted forms.</summary>
    public const string Help = "HH:MM:SS:FF timecode (';' before frames = drop-frame), H:MM:SS, M:SS, seconds (90, 4.5), or units (1h30m, 5m, 45s, 12f); '+'/'-' prefix = relative";

    public static TimeInput Parse(string text) =>
        TryParse(text, out var t, out string? error) ? t : throw new FormatException(error);

    public static bool TryParse(string? text, out TimeInput value) => TryParse(text, out value, out _);

    public static bool TryParse(string? text, out TimeInput value, out string? error)
    {
        value = default;
        error = null;
        string s = (text ?? "").Trim();
        if (s.Length == 0) { error = "Empty time. Use " + Help + "."; return false; }

        int sign = 1;
        bool relative = false;
        if (s[0] is '+' or '-')
        {
            relative = true;
            sign = s[0] == '-' ? -1 : 1;
            s = s[1..].Trim();
            if (s.Length == 0) { error = $"'{text}' has no value after the sign."; return false; }
        }

        // Units form: 1h30m15s12f
        if (s.Any(char.IsLetter))
        {
            int h = 0, m = 0, f = 0; double sec = 0; bool hasF = false;
            int i = 0;
            while (i < s.Length)
            {
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (start == i) { error = $"'{text}': expected a number before '{s[i]}'. Use " + Help + "."; return false; }
                if (!double.TryParse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) { error = $"'{s[start..i]}' is not a number."; return false; }
                while (i < s.Length && s[i] == ' ') i++;
                int us = i;
                while (i < s.Length && char.IsLetter(s[i])) i++;
                string unit = s[us..i].ToLowerInvariant();
                while (i < s.Length && s[i] == ' ') i++;
                switch (unit)
                {
                    case "h" or "hr" or "hrs" or "hour" or "hours": sec += n * 3600; break;
                    case "m" or "min" or "mins" or "minute" or "minutes": sec += n * 60; break;
                    case "" or "s" or "sec" or "secs" or "second" or "seconds": sec += n; break;
                    case "ms": sec += n / 1000; break;
                    case "f" or "fr" or "frame" or "frames": f += (int)n; hasF = true; break;
                    default: error = $"Unknown unit '{unit}' in '{text}'. Use h, m, s, ms or f."; return false;
                }
            }
            // normalise whole hours/minutes out of the seconds so ToTimecode can build a label
            long whole = (long)Math.Floor(sec);
            double frac = sec - whole;
            h = (int)(whole / 3600); m = (int)(whole / 60 % 60);
            value = new TimeInput(h, m, whole % 60 + frac, f, sign, relative, false, hasF);
            return true;
        }

        bool df = s.Contains(';') || (s.Count(c => c == ':') == 2 && s.Contains(','));
        string[] parts = s.Replace(';', ':').Replace(',', ':').Split(':');
        if (parts.Any(p => p.Length == 0)) { error = $"'{text}' has an empty field. Use " + Help + "."; return false; }

        if (parts.Length == 4)
        {
            if (!TryInts(parts, out int[] v)) { error = $"'{text}': timecode fields must be whole numbers."; return false; }
            value = new TimeInput(v[0], v[1], v[2], v[3], sign, relative, df, true);
            return Check(value, text, out error);
        }

        if (parts.Length > 4) { error = $"'{text}' has too many fields. Use " + Help + "."; return false; }

        // last field may have decimals
        if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double last) || last < 0)
        { error = $"'{parts[^1]}' is not a valid number of seconds."; return false; }
        if (!TryInts(parts[..^1], out int[] hm)) { error = $"'{text}': hours and minutes must be whole numbers."; return false; }

        value = parts.Length switch
        {
            1 => FromSeconds(last, sign, relative),
            2 => new TimeInput(hm[0] / 60, hm[0] % 60, last, 0, sign, relative, false, false),
            _ => new TimeInput(hm[0], hm[1], last, 0, sign, relative, false, false),
        };
        return Check(value, text, out error);
    }

    private static TimeInput FromSeconds(double sec, int sign, bool relative)
    {
        long whole = (long)Math.Floor(sec);
        return new TimeInput((int)(whole / 3600), (int)(whole / 60 % 60), whole % 60 + (sec - whole), 0, sign, relative, false, false);
    }

    private static bool Check(TimeInput t, string? text, out string? error)
    {
        error = null;
        if (t.Minutes is < 0 or > 59 && t.Hours > 0) error = $"'{text}': minutes must be 0–59.";
        else if (t.Seconds >= 60 && (t.Minutes > 0 || t.Hours > 0)) error = $"'{text}': seconds must be below 60.";
        return error is null;
    }

    private static bool TryInts(string[] parts, out int[] values)
    {
        values = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return false;
        return true;
    }

    /// <summary>The value in seconds (signed) — frames converted at <paramref name="rate"/>'s codeword rate.</summary>
    public double ToSeconds(LtcFrameRate rate) =>
        Sign * (Hours * 3600.0 + Minutes * 60.0 + Seconds + (HasFrames ? Frames / rate.CodewordRate() : 0));

    /// <summary>
    /// The value as a signed number of addresses (codewords) at <paramref name="rate"/>, counted like timecode labels:
    /// one second = <see cref="LtcFrameRateExtensions.FramesPerSecond"/> addresses. A drop-frame timecode (<c>;</c>)
    /// is counted with drop-frame arithmetic.
    /// </summary>
    public long ToFrames(LtcFrameRate rate)
    {
        if (DropFrameSeparator && rate.WithDropFrame(true).IsDropFrame())
            return Sign * (long)ToTimecode(rate).TotalFrames;
        int fps = rate.FramesPerSecond();
        long whole = (long)Math.Floor(Seconds);
        return Sign * (((long)Hours * 3600 + Minutes * 60 + whole) * fps + (long)Math.Round((Seconds - whole) * fps) + Frames);
    }

    /// <summary>
    /// The value as a time address label at <paramref name="rate"/> (hours:minutes:seconds:frames as written; wraps at
    /// 24 hours). Fractional seconds become frames. A typed drop-frame address that doesn't exist throws; a time given
    /// without frames lands on the first existing address.
    /// </summary>
    public Timecode ToTimecode(LtcFrameRate rate)
    {
        var r = DropFrameSeparator ? rate.WithDropFrame(true) : rate;
        int fps = r.FramesPerSecond();
        long whole = (long)Math.Floor(Seconds);
        long frames = Frames + (long)Math.Floor((Seconds - whole) * fps + 1e-9);
        long secs = (long)Hours * 3600 + Minutes * 60 + whole + frames / fps;
        frames %= fps;
        secs %= 86_400;
        int h = (int)(secs / 3600), m = (int)(secs / 60 % 60), s = (int)(secs % 60);
        if (r.IsDropFrame() && s == 0 && frames < 2 && m % 10 != 0)
        {
            if (HasFrames && DropFrameSeparator && Frames < fps)
                throw new FormatException($"{m:00}:{s:00};{frames:00} does not exist in drop-frame counting (frames 00 and 01 are skipped at the start of each minute except every tenth).");
            frames = 2;
        }
        return Timecode.CreateUnchecked(h, m, s, (int)frames, r);
    }

    public override string ToString()
    {
        string sign = Relative ? (Sign < 0 ? "-" : "+") : "";
        return HasFrames
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{Hours:00}:{Minutes:00}:{(int)Seconds:00}{(DropFrameSeparator ? ';' : ':')}{Frames:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{Hours:00}:{Minutes:00}:{Seconds:00.###}");
    }
}
