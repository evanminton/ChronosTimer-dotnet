using System.Text;
using System.Text.Json;
using ChronosTimer.Settings;

namespace ChronosTimer.Control;

/// <summary>JSON for status, settings and command results (hand-written with Utf8JsonWriter: AOT/trim safe).</summary>
public static class Json
{
    public static string Status(TimerStatus s, string? outputDevice = null, string? inputDevice = null, double? inputLevel = null)
    {
        return Write(w =>
        {
            w.WriteString("display", s.Display);
            w.WriteString("detail", s.Detail);
            w.WriteString("timecode", s.Timecode.ToString());
            w.WriteString("mode", Setting.Token(s.Mode));
            w.WriteString("state", Setting.Token(s.State));
            w.WriteString("phase", Setting.Token(s.Phase));
            w.WriteString("rate", LinearTimecode.LtcFrameRateExtensions.Token(s.Rate));
            w.WriteString("rateName", LinearTimecode.LtcFrameRateExtensions.DisplayName(s.Rate));
            w.WriteNumber("elapsed", Math.Round(s.Elapsed.TotalSeconds, 3));
            if (s.Remaining is { } r) w.WriteNumber("remaining", Math.Round(r.TotalSeconds, 3)); else w.WriteNull("remaining");
            if (s.Length is { } l) w.WriteNumber("length", Math.Round(l.TotalSeconds, 3)); else w.WriteNull("length");
            if (s.Progress is { } p) w.WriteNumber("progress", Math.Round(p, 4)); else w.WriteNull("progress");
            w.WriteNumber("speed", Math.Round(s.Speed, 4));
            w.WriteBoolean("reverse", s.Reverse);
            w.WriteString("chase", Setting.Token(s.Chase));
            if (s.ChaseInput is { } ci) w.WriteString("chaseInput", ci.ToString()); else w.WriteNull("chaseInput");
            w.WriteBoolean("outputActive", s.OutputActive);
            if (outputDevice is not null) w.WriteString("outputDevice", outputDevice);
            if (inputDevice is not null) w.WriteString("inputDevice", inputDevice);
            if (inputLevel is { } il) w.WriteNumber("inputLevel", Math.Round(il, 3));
        });
    }

    public static string Settings(IEnumerable<Setting> settings) => WriteArray(w =>
    {
        foreach (var s in settings)
        {
            w.WriteStartObject();
            w.WriteString("name", s.Name);
            w.WriteString("category", s.Category);
            w.WriteString("kind", Setting.Token(s.Kind));
            w.WriteString("value", s.Value);
            w.WriteString("default", s.Default);
            w.WriteString("description", s.Description);
            w.WriteString("accepts", s.Accepts);
            if (s.Unit is not null) w.WriteString("unit", s.Unit);
            w.WriteStartArray("options");
            foreach (var o in s.Options)
            {
                w.WriteStartObject();
                w.WriteString("value", o.Value);
                w.WriteString("description", o.Description);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
    });

    public static string Commands(IEnumerable<CommandInfo> commands) => WriteArray(w =>
    {
        foreach (var c in commands)
        {
            w.WriteStartObject();
            w.WriteString("name", c.Name);
            w.WriteString("usage", c.Usage);
            w.WriteString("description", c.Description);
            w.WriteStartArray("aliases");
            foreach (var a in c.Aliases) w.WriteStringValue(a);
            w.WriteEndArray();
            w.WriteEndObject();
        }
    });

    public static string Result(CommandResult r) => Write(w =>
    {
        w.WriteBoolean("ok", r.Ok);
        w.WriteString("message", r.Message);
    });

    /// <summary>Settings file: a flat object of name → text value.</summary>
    public static string SettingsFile(IEnumerable<Setting> settings) => Write(w =>
    {
        foreach (var s in settings) w.WriteString(s.Name, s.Value);
    }, indented: true);

    public static Dictionary<string, string> ReadFlat(string json)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var p in doc.RootElement.EnumerateObject())
            d[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString() ?? "",
                JsonValueKind.True => "on",
                JsonValueKind.False => "off",
                _ => p.Value.GetRawText(),
            };
        return d;
    }

    private static string Write(Action<Utf8JsonWriter> body, bool indented = false)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string WriteArray(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartArray();
            body(w);
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
