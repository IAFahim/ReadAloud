using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

// ReadAloud — speak the current text selection, stdin, or Claude's last reply.
//
// Usage:
//   ReadAloud                speak the currently selected text (press again to interrupt)
//   ReadAloud --stdin        speak text piped in
//   ReadAloud --claude-hook  Claude Code Stop hook mode: reads the hook JSON on stdin,
//                            finds the transcript, speaks Claude's last reply
//   ReadAloud --print-filter [speed [pitch]]   dev check: print the ffplay -af chain
//   ReadAloud --install-inflect                download Inflect model + python env
//   ReadAloud --engine-check                   which voices are ready right now
//   select nothing + run = stop talking
//   touch ~/.claude/tts-off  = mute the Claude hook (rm the file to unmute)
//
// Knobs (speed, pitch, engine, language, MuteClaude) live in ~/.config/readaloud/settings.json —
// see Settings.cs. The tray writes it, every run reads it fresh.

if (args.Contains("--tray"))
{
    Tray.Run();
    return;
}

if (args.Contains("--stop"))
{
    StopPrevious(isHook: false); // manual stop kills whatever is playing
    ForgetLastRead();
    SpeakStatus.Clear();
    return;
}

if (args.Contains("--wiggle-test"))
{
    Environment.Exit(WiggleDetector.SelfCheck() ? 0 : 1);
}

if (args.Contains("--install-inflect"))
{
    Console.WriteLine("Installing Inflect voice…");
    bool ok = InflectEngine.EnsureReady();
    Console.WriteLine(ok ? "Inflect ready at " + InflectEngine.ModelDir : "Inflect install failed (see messages above).");
    Environment.Exit(ok ? 0 : 1);
}

if (args.Contains("--engine-check"))
{
    Console.WriteLine("google:  online (needs internet at speak time)");
    Console.WriteLine("inflect: " + InflectEngine.StatusLine()
                      + "  (" + InflectEngine.ModelDir + ")");
    Console.WriteLine("spd:     " + (HasCmd("spd-say") ? "ready" : "missing"));
    Console.WriteLine("ffplay:  " + (HasCmd("ffplay") ? "ready" : "missing"));
    Console.WriteLine("uv:      " + (HasCmd("uv") ? "ready" : "missing"));
    Console.WriteLine("hf:      " + (HasCmd("hf") || HasCmd("huggingface-cli") ? "ready" : "missing"));
    string live = SpeakStatus.CurrentDetail();
    if (live.Length > 0)
    {
        Console.WriteLine("live:    " + live);
    }
    return;
}

if (args.Contains("--print-filter"))
{
    int at = Array.IndexOf(args, "--print-filter");
    if (at + 1 < args.Length)
    {
        double sp = double.Parse(args[at + 1], CultureInfo.InvariantCulture);
        double pt = at + 2 < args.Length ? double.Parse(args[at + 2], CultureInfo.InvariantCulture) : 1.0;
        Console.WriteLine(GoogleEngine.Filter(sp, pt));
        return;
    }

    Environment.Exit(GoogleEngine.SelfCheck() ? 0 : 1);
}

var settings = Settings.Load();
string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
bool isHook = args.Contains("--claude-hook");
bool isWiggle = args.Contains("--wiggle"); // coarse gesture: may stop, may read, never surprises

string text;
if (isHook)
{
    if (settings.MuteClaude || File.Exists(Path.Combine(home, ".claude", "tts-off")))
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

// Wiggle while something is already talking = stop only (don't restart the same line).
// Clear last-read so the *next* wiggle on that selection plays again instead of
// looking "broken" — SameAsLastRead used to swallow that second gesture.
if (isWiggle && IsSpeaking())
{
    StopPrevious(isHook: false);
    ForgetLastRead();
    SpeakStatus.Clear();
    return;
}

StopPrevious(isHook); // manual use interrupts anything; hook mode waits its turn behind a manual read

text = Clean(text);
if (string.IsNullOrWhiteSpace(text))
{
    return; // nothing to say: we just silenced the old speech, done
}

// Same selection plays again on purpose (select-again, shortcut, or idle wiggle).
// Accidental double-fire is handled by the wiggle detector's 1.5s cooldown, not by
// forever-blocking the last hash — that felt like the app was broken.

RememberLastRead(text);
File.WriteAllText(PidFile(), $"{Environment.ProcessId} {(isHook ? "hook" : "manual")}");

ISpeechEngine engine = settings.Engine switch
{
    "inflect" => new InflectEngine(),
    "spd" => new SpdEngine(),
    _ => new GoogleEngine(),
};
if (!engine.Speak(text, settings))
{
    // primary went quiet: prefer google (nice) then the offline robot — and say so
    string primary = settings.Engine;
    if (engine is not GoogleEngine)
    {
        SpeakStatus.Alert($"{primary} failed — trying Google…");
        if (!new GoogleEngine().Speak(text, settings))
        {
            SpeakStatus.Alert("Google failed — using offline voice");
            new SpdEngine().Speak(text, settings);
        }
    }
    else
    {
        SpeakStatus.Alert("Google failed — using offline voice");
        new SpdEngine().Speak(text, settings);
    }
}

static bool HasCmd(string cmd)
{
    try
    {
        var psi = new ProcessStartInfo("bash", $"-lc \"command -v {cmd}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 && o.Trim().Length > 0;
    }
    catch (Exception)
    {
        return false;
    }
}

static string PidFile() => Path.Combine(Path.GetTempPath(), "readaloud.pid");

static bool IsSpeaking()
{
    try
    {
        string[] parts = File.ReadAllText(PidFile()).Trim().Split(' ');
        int pid = int.Parse(parts[0]);
        var p = Process.GetProcessById(pid);
        if (p.HasExited)
        {
            return false;
        }

        // published binary is "ReadAloud"; be loose — a recycled pid of something else
        // is rare and StopPrevious is still guarded the same way
        string name = p.ProcessName;
        return name.Contains("ReadAloud", StringComparison.OrdinalIgnoreCase)
               || name.Contains("readaloud", StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception)
    {
        return false; // no pidfile, dead pid, or pid reused by something else
    }
}

static string LastReadFile() => Path.Combine(Path.GetTempPath(), "readaloud.last");

static void RememberLastRead(string text)
{
    try
    {
        File.WriteAllText(LastReadFile(), Hash(text));
    }
    catch (Exception)
    {
    }
}

static void ForgetLastRead()
{
    try
    {
        if (File.Exists(LastReadFile()))
        {
            File.Delete(LastReadFile());
        }
    }
    catch (Exception)
    {
    }
}

// stable across processes (string.GetHashCode is randomized per run)
static string Hash(string s) =>
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));

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

    Sh.Run("spd-say", "-C"); // also cancel anything queued on the offline engine
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
