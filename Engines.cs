using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

// Speech engines. Program.cs picks one from Settings.Engine and falls back to spd when it goes quiet.
public interface ISpeechEngine
{
    // true = the text came out (possibly partly via a fallback); false = nothing was spoken at all
    bool Speak(string text, Settings s);
}

// Free Google Translate voice: one MP3 per sentence chunk, played by ffplay. Needs internet.
public sealed class GoogleEngine : ISpeechEngine
{
    private const int SampleRate = 24000; // translate_tts always returns 24 kHz MP3

    public bool Speak(string text, Settings s)
    {
        var chunks = Chunks(text).ToList();
        string filter = Filter(s.Speed, s.Pitch);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        http.Timeout = TimeSpan.FromSeconds(10);

        Task<byte[]>? next = Fetch(http, chunks[0], s.GoogleLang);
        for (int i = 0; i < chunks.Count; i++)
        {
            byte[] mp3;
            try
            {
                mp3 = next!.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // offline or rate-limited mid-read: NEVER drop the rest — the offline voice finishes it
                if (i == 0)
                {
                    return false; // nothing spoken yet: the caller hands the whole text to spd
                }

                new SpdEngine().Speak(string.Join(" ", chunks.Skip(i)), s);
                return true;
            }

            next = i + 1 < chunks.Count ? Fetch(http, chunks[i + 1], s.GoogleLang) : null; // prefetch while this plays

            string f = Path.Combine(Path.GetTempPath(), $"readaloud-{i}.mp3");
            File.WriteAllBytes(f, mp3);
            string[] play = ["-nodisp", "-autoexit", "-loglevel", "quiet", f];
            Sh.Run("ffplay", filter.Length == 0 ? play : ["-af", filter, .. play]);
        }

        return true;
    }

    // ffplay -af chain; empty string = play the file untouched.
    // atempo only accepts 0.5..2.0 per stage, so anything bigger is a chain of stages.
    // Pitch shifts by resampling (asetrate), which drags tempo along with it, so the tempo
    // chain divides that back out: net tempo = speed, net pitch = pitch.
    public static string Filter(double speed, double pitch)
    {
        bool shift = Math.Abs(pitch - 1.0) > 0.001;
        var parts = new List<string>();
        if (shift)
        {
            parts.Add($"asetrate={(int)Math.Round(SampleRate * pitch)}");
        }

        parts.AddRange(Atempo(shift ? speed / pitch : speed));
        if (shift)
        {
            parts.Add($"aresample={SampleRate}");
        }

        return string.Join(",", parts);
    }

    private static IEnumerable<string> Atempo(double f)
    {
        while (f > 2.0)
        {
            yield return "atempo=2.0";
            f /= 2.0;
        }

        while (f < 0.5)
        {
            yield return "atempo=0.5";
            f /= 0.5;
        }

        if (Math.Abs(f - 1.0) > 0.001)
        {
            yield return "atempo=" + f.ToString("0.0###", CultureInfo.InvariantCulture);
        }
    }

    // `ReadAloud --print-filter` runs this; returns false if the chain builder drifted.
    public static bool SelfCheck()
    {
        (double Speed, double Pitch, string Want)[] cases =
        [
            (0.75, 1.0, "atempo=0.75"),
            (1.0, 1.0, ""),
            (2.0, 1.0, "atempo=2.0"),
            (2.5, 1.0, "atempo=2.0,atempo=1.25"),
            (3.0, 1.0, "atempo=2.0,atempo=1.5"),
            (0.25, 1.0, "atempo=0.5,atempo=0.5"),
            (2.5, 1.2, "asetrate=28800,atempo=2.0,atempo=1.0417,aresample=24000"),
        ];

        bool ok = true;
        foreach (var (speed, pitch, want) in cases)
        {
            string got = Filter(speed, pitch);
            bool pass = got == want;
            ok &= pass;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")} speed={speed} pitch={pitch} -> \"{got}\""
                              + (pass ? "" : $" (want \"{want}\")"));
        }

        // same speed knob, offline engine: spd-say -r
        (double Speed, int SpdRate, int Want)[] rates =
        [
            (0.5, 0, -100),
            (1.0, 0, 0),
            (1.0, 30, 30),
            (2.5, 0, 75),
            (3.0, 0, 100),
            (0.75, 20, -40),
        ];

        foreach (var (speed, spdRate, want) in rates)
        {
            int got = SpdEngine.Rate(new Settings { Speed = speed, SpdRate = spdRate });
            bool pass = got == want;
            ok &= pass;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")} speed={speed} SpdRate={spdRate} -> spd -r {got}"
                              + (pass ? "" : $" (want {want})"));
        }

        return ok;
    }

    private static Task<byte[]> Fetch(HttpClient http, string chunk, string lang) =>
        http.GetByteArrayAsync("https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob"
                               + $"&tl={lang}&q={Uri.EscapeDataString(chunk)}");

    // split on sentence ends, keep each request under the Translate endpoint's limit
    private static IEnumerable<string> Chunks(string s, int max = 180)
    {
        string cur = "";
        foreach (string part in Regex.Split(s, @"(?<=[.!?;:])\s+"))
        {
            string p = part;
            while (p.Length > max)
            {
                if (cur.Length > 0) { yield return cur; cur = ""; }
                yield return p[..max];
                p = p[max..];
            }
            if (cur.Length + p.Length + 1 > max)
            {
                if (cur.Length > 0) yield return cur;
                cur = p;
            }
            else
            {
                cur = cur.Length == 0 ? p : cur + " " + p;
            }
        }
        if (cur.Length > 0)
        {
            yield return cur;
        }
    }
}

// Offline speech-dispatcher robot. Always available, never pretty.
public sealed class SpdEngine : ISpeechEngine
{
    public bool Speak(string text, Settings s)
    {
        var psi = new ProcessStartInfo("spd-say") { RedirectStandardInput = true };
        psi.ArgumentList.Add("-e"); // pipe mode: text on stdin, no argv limits
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add(Rate(s).ToString());
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(Pitch(s.Pitch).ToString());
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(text);
            p.StandardInput.Close();
            p.WaitForExit();
            return true;
        }
        catch (Exception)
        {
            return false; // speech-dispatcher missing entirely: nothing sensible left to do
        }
    }

    // Speed is a tempo multiplier, spd-say wants -100..100. Speed 1.0 keeps the user's SpdRate,
    // 0.5 bottoms out, 3.0 tops out. ponytail: straight lerp either side of 1.0, good enough for a robot voice.
    internal static int Rate(Settings s) => Clamp(s.Speed <= 1.0
        ? -100 + ((s.Speed - 0.5) / 0.5 * (s.SpdRate + 100))
        : s.SpdRate + ((s.Speed - 1.0) / 2.0 * (100 - s.SpdRate)));

    internal static int Pitch(double pitch) => Clamp((pitch - 1.0) * 100);

    private static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, -100, 100));
}

internal static class Sh
{
    public static void Run(string cmd, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd);
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            p?.WaitForExit();
        }
        catch (Exception)
        {
            // tool missing, ignore
        }
    }
}
