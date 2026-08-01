// Live "what is ReadAloud doing right now" surface.
//
// Speaker process writes it; tray menu reads it on open; notify-send -r keeps one
// replacing toast so long Inflect loads / multi-chunk synth don't look frozen.

using System.Globalization;

static class SpeakStatus
{
    // stable replace id so every update refreshes the same bubble instead of stacking
    public const int NotifyId = 884_422;

    public static string FilePath
    {
        get
        {
            string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            string dir = !string.IsNullOrEmpty(runtime) ? runtime : Path.GetTempPath();
            return Path.Combine(dir, "readaloud.status");
        }
    }

    // phase is short machine-ish ("synth", "play", "load"); detail is human.
    public static void Set(string phase, string detail, int expireMs = 8_000)
    {
        string line = phase + "\t" + detail + "\t" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        try
        {
            File.WriteAllText(FilePath, line);
        }
        catch (Exception)
        {
        }

        // low urgency, replaces itself — no toast spam on chunk 12 of 40
        Sh.Run("notify-send",
            "--app-name=ReadAloud",
            "-i", "audio-volume-high-symbolic",
            "-u", "low",
            "-r", NotifyId.ToString(CultureInfo.InvariantCulture),
            "-t", expireMs.ToString(CultureInfo.InvariantCulture),
            "ReadAloud",
            detail);
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception)
        {
        }

        // expire the last toast without a "Done" flash — just let it vanish
        Sh.Run("notify-send",
            "--app-name=ReadAloud",
            "-i", "audio-volume-high-symbolic",
            "-u", "low",
            "-r", NotifyId.ToString(CultureInfo.InvariantCulture),
            "-t", "1",
            "ReadAloud",
            " ");
    }

    // one-shot sticky-ish alert (failures, fallback) — not low, not "Done"
    public static void Alert(string detail, int expireMs = 6_000)
    {
        Set("alert", detail, expireMs);
    }

    // tray / --engine-check: "idle" or the current detail if still fresh (< 3 min)
    public static string CurrentDetail()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return "";
            }

            string[] parts = File.ReadAllText(FilePath).Split('\t');
            if (parts.Length < 3)
            {
                return parts.Length > 1 ? parts[1] : parts[0];
            }

            if (DateTime.TryParse(parts[2], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime when)
                && DateTime.UtcNow - when > TimeSpan.FromMinutes(3))
            {
                return ""; // stale: a killed speak left the file behind
            }

            return parts[1];
        }
        catch (Exception)
        {
            return "";
        }
    }
}
