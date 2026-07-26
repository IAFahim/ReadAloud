using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

// ReadAloud for macOS — speak the current selection, stdin, or Claude's last reply.
//
// !! HONESTY BANNER !!  This file has been COMPILED but NEVER RUN on a Mac. It was written and
// build-gated on Linux. Every macOS-specific thing here is a child process (say / osascript /
// pbpaste / pbcopy / killall), chosen exactly so the C# compiles and self-checks anywhere — but
// nobody has yet watched it speak. Treat first run as a bring-up, not a regression test.
//
// Usage:
//   ReadAloud                speak the currently selected text (run again with nothing selected = stop)
//   ReadAloud --stdin        speak text piped in
//   ReadAloud --stop         shut up right now
//   ReadAloud --claude-hook  Claude Code Stop hook mode: reads the hook JSON on stdin,
//                            finds the transcript, speaks Claude's last reply
//   ReadAloud --self-check   pure-logic tests (wpm mapping, markdown clean, transcript parse).
//                            Runs on any OS — this is the only part verified so far.
//   touch ~/.claude/tts-off  = mute the Claude hook (rm the file to unmute)
//
// Knobs live in ~/.config/readaloud/settings.json (shared ../Settings.cs, same file as Linux).
// This port honours Speed and MuteClaude. Engine / Pitch / GoogleLang / SpdRate / Wiggle* /
// Shortcut are Linux-only knobs and are ignored here: macOS always uses the built-in `say`,
// whose voice is picked in System Settings > Accessibility > Spoken Content (`say -v ?` lists them).

if (args.Contains("--self-check"))
{
    Environment.Exit(SelfCheck() ? 0 : 1);
}

if (args.Contains("--stop"))
{
    StopPrevious(isHook: false); // manual stop kills whatever is playing
    return;
}

var settings = Settings.Load();
string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
bool isHook = args.Contains("--claude-hook");

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

StopPrevious(isHook); // manual use interrupts anything; hook mode waits its turn behind a manual read

text = Clean(text);
if (string.IsNullOrWhiteSpace(text))
{
    return; // nothing to say: we just silenced the old speech, done
}

File.WriteAllText(PidFile(), $"{Environment.ProcessId} {(isHook ? "hook" : "manual")}");
Speak(text, settings);

// $TMPDIR on macOS is per-user, so the hook run and the manual run see the same file. Good.
static string PidFile() => Path.Combine(Path.GetTempPath(), "readaloud.pid");

// Why a pidfile AND killall, when the brief said pick one:
//   The pidfile is load-bearing for a CONTRACT a bare `killall say` cannot express — it records
//   whether the current read was asked for by a human ("manual") or by the Claude hook, so the
//   hook can politely WAIT instead of talking over a read the user requested. That is the Linux
//   original's behaviour and it is worth the six lines.
//   `killall say` is then just the sweep at the end: it kills an orphaned `say` left behind by a
//   run that was SIGKILLed before its child died, which the pidfile can never reach. It is the
//   exact analogue of the Linux version's trailing `spd-say -C`.
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
                p.Kill(entireProcessTree: true); // takes the running `say` down with it
            }
        }
    }
    catch (Exception)
    {
        // no previous instance, already dead, or pid was reused by something else
    }

    Sh("killall", "say"); // sweep any orphan; exits non-zero when there is none, which we ignore
}

// The whole voice: macOS ships `say`, which has genuinely good offline voices.
// With no string argument and no -f, `say` reads the text from stdin — so long selections and
// quoting-hostile text (quotes, backticks, newlines) never touch a shell or an argv limit.
static void Speak(string text, Settings s)
{
    var psi = new ProcessStartInfo("say") { RedirectStandardInput = true };
    psi.ArgumentList.Add("-r");
    psi.ArgumentList.Add(Wpm(s.Speed).ToString(CultureInfo.InvariantCulture));
    try
    {
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(text);
        p.StandardInput.Close();
        p.WaitForExit(); // stay alive while it talks: our pid in the pidfile IS the speech
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"readaloud: `say` failed: {e.Message}");
    }
}

// Settings.Speed is a tempo multiplier; `say -r` wants words per minute.
// Anchors from the brief: 1.0 = 200 wpm (normal reading), 2.5 = 500 wpm (the Claude-hook default).
// Straight line through both, clamped to a range `say` actually handles.
static int Wpm(double speed) => (int)Math.Round(Math.Clamp(speed * 200.0, 100.0, 720.0));

// Grab the selection by pressing Cmd+C for the user, then putting their clipboard back.
//
// ACCESSIBILITY PERMISSION REQUIRED. Synthesising a keystroke is a privileged act on macOS, and
// the permission is granted to the app that INVOKES this binary, not to the binary itself — so
// allow Terminal / iTerm / Shortcuts / skhd (whichever launches it) under
//   System Settings > Privacy & Security > Accessibility.
// The first run pops the request automatically. Until it is granted, osascript fails and this
// returns "" — which reads as "nothing selected", i.e. it silently just stops speech. If the
// hotkey seems to do nothing on a fresh Mac, this permission is the first thing to check.
static string GetSelection()
{
    string saved = Out("pbpaste");

    // Blank the pasteboard first so we can tell "copied nothing" from "copied the same thing
    // that was already on the clipboard". Without this, pressing the hotkey with no selection
    // would re-read the old clipboard instead of stopping.
    SetClipboard("");

    Sh("osascript", "-e", "tell application \"System Events\" to keystroke \"c\" using command down");
    Thread.Sleep(150); // the copy is asynchronous in the target app; give the pasteboard time to land

    string selection = Out("pbpaste");
    SetClipboard(saved); // the user's clipboard is theirs — never keep what we borrowed
    return selection;
}

static void SetClipboard(string s)
{
    try
    {
        var psi = new ProcessStartInfo("pbcopy") { RedirectStandardInput = true };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(s);
        p.StandardInput.Close();
        p.WaitForExit();
    }
    catch (Exception)
    {
        // no pbcopy: not a Mac, or a stripped image. Nothing sensible left to do.
    }
}

// run a command, hand back stdout; "" on any failure
static string Out(string cmd, params string[] args)
{
    try
    {
        var psi = new ProcessStartInfo(cmd) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : "";
    }
    catch (Exception)
    {
        return "";
    }
}

// fire and forget a command, ignoring a missing tool or a non-zero exit
static void Sh(string cmd, params string[] args)
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

// Claude Code hook JSON -> transcript path -> last assistant message text.
// Copied from the Linux Program.cs on purpose: the hook payload contract is shared, and a shim
// project that reached back into the Linux file would drag ffplay/spd-say along with it.
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

// make markdown and code listenable instead of torture (copied from the Linux Program.cs)
static string Clean(string s)
{
    s = Regex.Replace(s, @"```.*?```", " code block. ", RegexOptions.Singleline);
    s = Regex.Replace(s, @"`([^`]*)`", "$1");
    s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1"); // [text](url) -> text
    s = Regex.Replace(s, @"https?://\S+", " link ");
    s = Regex.Replace(s, @"[#*_>|~]", " ");
    return Regex.Replace(s, @"\s+", " ").Trim();
}

// `ReadAloud --self-check` — the one runnable check. Pure logic, no macOS calls, so it is the
// only part of this port that has actually been EXECUTED (on Linux). Speech itself is untested.
static bool SelfCheck()
{
    bool ok = true;

    void Check(string name, bool cond)
    {
        Console.WriteLine($"{(cond ? "PASS" : "FAIL")} {name}");
        ok &= cond;
    }

    (double Speed, int Want)[] rates =
    [
        (1.0, 200),   // the brief's anchor: normal reading pace
        (2.5, 500),   // the brief's anchor: Claude-hook speed
        (0.5, 100),   // 100 wpm floor
        (0.25, 100),  // below the floor clamps up
        (2.0, 400),
        (3.0, 600),
        (3.6, 720),   // 720 wpm ceiling
        (10.0, 720),  // above the ceiling clamps down
    ];

    foreach (var (speed, want) in rates)
    {
        int got = Wpm(speed);
        Check($"speed={speed} -> say -r {got}" + (got == want ? "" : $" (want {want})"), got == want);
    }

    Check("clean strips a code fence", Clean("see ```var x = 1;``` now") == "see code block. now");
    Check("clean unwraps inline code", Clean("run `dotnet build` ok") == "run dotnet build ok");
    Check("clean keeps link text", Clean("[the docs](https://x.dev/a)") == "the docs");
    Check("clean says 'link' for a bare url", Clean("go to https://x.dev/a now") == "go to link now");
    Check("clean drops markdown noise", Clean("## **Bold** _it_") == "Bold it");

    // the hook parser, against a transcript shaped like a real one
    string tmp = Path.Combine(Path.GetTempPath(), $"readaloud-selfcheck-{Environment.ProcessId}.jsonl");
    try
    {
        File.WriteAllLines(tmp,
        [
            """{"type":"user","message":{"content":[{"type":"text","text":"hi"}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"first"}]}}""",
            "not json at all",
            """{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"subagent noise"}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read"},{"type":"text","text":"second"}]}}""",
        ]);

        string got = LastAssistantText($$"""{"transcript_path":{{JsonSerializer.Serialize(tmp)}}}""");
        Check($"hook picks the last assistant text (got \"{got}\")", got == "second");
        Check("hook survives a missing transcript", LastAssistantText("""{"transcript_path":"/nope/x.jsonl"}""") == "");
        Check("hook survives junk json", LastAssistantText("not json") == "");
    }
    finally
    {
        File.Delete(tmp);
    }

    return ok;
}
