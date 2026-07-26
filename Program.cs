using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

// ReadAloud — speak the current text selection, stdin, or Claude's last reply.
//
// Usage:
//   ReadAloud                speak the currently selected text (press again to interrupt)
//   ReadAloud --stdin        speak text piped in
//   ReadAloud --claude-hook  Claude Code Stop hook mode: reads the hook JSON on stdin,
//                            finds the transcript, speaks Claude's last reply
//   select nothing + run = stop talking
//   touch ~/.claude/tts-off  = mute the Claude hook (rm the file to unmute)

// ---- knobs to play with ----
const string Engine = "google"; // "google" = Google Translate voice (needs internet), "spd" = offline robot
const string GoogleLang = "en"; // "en", "en-GB", "en-AU", ...
const int SpdRate = 0;          // -100 slow .. 100 fast (spd engine only)
// ----------------------------

string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
bool isHook = args.Contains("--claude-hook");

string text;
if (isHook)
{
    if (File.Exists(Path.Combine(home, ".claude", "tts-off")))
    {
        return;
    }
    text = LastAssistantText(Console.In.ReadToEnd());
}
else if (args.Contains("--stdin"))
{
    text = Console.In.ReadToEnd();
}
else
{
    text = GetSelection();
}

StopPrevious(isHook); // manual use interrupts anything; hook mode waits its turn behind a manual read

text = Clean(text);
if (string.IsNullOrWhiteSpace(text))
{
    return; // nothing to say: we just silenced the old speech, done
}

File.WriteAllText(PidFile(), $"{Environment.ProcessId} {(isHook ? "hook" : "manual")}");

if (Engine != "google" || !GoogleSpeak(text))
{
    SpdSpeak(text); // offline fallback so TTS never fully dies
}

static string PidFile() => Path.Combine(Path.GetTempPath(), "readaloud.pid");

static void StopPrevious(bool isHook)
{
    try
    {
        string[] parts = File.ReadAllText(PidFile()).Trim().Split(' ');
        int pid = int.Parse(parts[0]);
        string mode = parts.Length > 1 ? parts[1] : "manual";
        var p = Process.GetProcessById(pid);
        if (p.ProcessName.Contains("ReadAloud"))
        {
            if (isHook && mode == "manual")
            {
                // a human asked for that read — never talk over it, wait for it to finish
                while (!p.HasExited)
                {
                    Thread.Sleep(200);
                }
            }
            else
            {
                p.Kill(entireProcessTree: true); // takes the running ffplay down with it
            }
        }
    }
    catch (Exception)
    {
        // no previous instance, already dead, or pid was reused by something else
    }

    Run("spd-say", "-C"); // also cancel anything queued on the offline engine
}

static bool GoogleSpeak(string text)
{
    var chunks = Chunks(text).ToList();
    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
    http.Timeout = TimeSpan.FromSeconds(10);

    Task<byte[]>? next = Fetch(http, chunks[0]);
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
                return false;
            }
            SpdSpeak(string.Join(" ", chunks.Skip(i)));
            return true;
        }

        next = i + 1 < chunks.Count ? Fetch(http, chunks[i + 1]) : null; // prefetch while this one plays

        string f = Path.Combine(Path.GetTempPath(), $"readaloud-{i}.mp3");
        File.WriteAllBytes(f, mp3);
        Run("ffplay", $"-nodisp -autoexit -loglevel quiet \"{f}\"");
    }

    return true;
}

static Task<byte[]> Fetch(HttpClient http, string chunk) =>
    http.GetByteArrayAsync("https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob"
                           + $"&tl={GoogleLang}&q={Uri.EscapeDataString(chunk)}");

static void SpdSpeak(string text)
{
    var psi = new ProcessStartInfo("spd-say") { RedirectStandardInput = true };
    psi.ArgumentList.Add("-e"); // pipe mode: text on stdin, no argv limits
    psi.ArgumentList.Add("-r");
    psi.ArgumentList.Add(SpdRate.ToString());
    try
    {
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(text);
        p.StandardInput.Close();
        p.WaitForExit();
    }
    catch (Exception)
    {
        // speech-dispatcher missing entirely: nothing sensible left to do
    }
}

// split on sentence ends, keep each request under the Translate endpoint's limit
static IEnumerable<string> Chunks(string s, int max = 180)
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

// Claude Code hook JSON -> transcript path -> last assistant message text
static string LastAssistantText(string hookJson)
{
    try
    {
        using var hook = JsonDocument.Parse(hookJson);
        string? path = hook.RootElement.TryGetProperty("transcript_path", out var tp) ? tp.GetString() : null;
        if (path is null || !File.Exists(path))
        {
            return "";
        }

        string last = "";
        foreach (string line in File.ReadLines(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var t) && t.GetString() == "assistant"
                    && !(root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True)
                    && root.TryGetProperty("message", out var msg)
                    && msg.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array)
                {
                    string joined = string.Join(" ",
                        content.EnumerateArray()
                            .Where(c => c.TryGetProperty("type", out var ct) && ct.GetString() == "text")
                            .Select(c => c.GetProperty("text").GetString() ?? ""));
                    if (!string.IsNullOrWhiteSpace(joined))
                    {
                        last = joined;
                    }
                }
            }
            catch (JsonException)
            {
                // partial/foreign line in the transcript, skip it
            }
        }
        return last;
    }
    catch (Exception)
    {
        return "";
    }
}

static string GetSelection()
{
    // wl-paste covers native Wayland apps, xclip covers X11/XWayland apps. First one that answers wins.
    var sources = new (string Cmd, string Args)[]
    {
        ("wl-paste", "--primary --no-newline"),
        ("xclip", "-o -selection primary"),
    };

    foreach (var (cmd, cmdArgs) in sources)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd, cmdArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                return output;
            }
        }
        catch (Exception)
        {
            // tool not installed, try the next one
        }
    }

    return "";
}

static string Clean(string s)
{
    // make markdown and code listenable instead of torture
    s = Regex.Replace(s, @"```.*?```", " code block. ", RegexOptions.Singleline);
    s = Regex.Replace(s, @"`([^`]*)`", "$1");
    s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1"); // [text](url) -> text
    s = Regex.Replace(s, @"https?://\S+", " link ");
    s = Regex.Replace(s, @"[#*_>|~]", " ");
    return Regex.Replace(s, @"\s+", " ").Trim();
}

static void Run(string cmd, string cmdArgs)
{
    try
    {
        using var p = Process.Start(new ProcessStartInfo(cmd, cmdArgs));
        p?.WaitForExit();
    }
    catch (Exception)
    {
        // tool missing, ignore
    }
}
