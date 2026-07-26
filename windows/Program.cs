// ReadAloud for Windows — the same tool as the Linux original, with every native part swapped:
//
//   speech    System.Speech (SAPI, offline, ships with Windows)  <- Google Translate TTS + ffplay / spd-say
//   selection preserve clipboard -> SendInput Ctrl+C -> read      <- wl-paste/xclip PRIMARY selection
//   hotkey    RegisterHotKey Ctrl+Win+S                           <- a GNOME custom keybinding
//   wiggle    WH_MOUSE_LL hook -> the SHARED WiggleDetector       <- /dev/input evdev reader
//   tray      NotifyIcon                                          <- a D-Bus StatusNotifierItem
//
// Shared with Linux byte for byte: Settings.cs (same ~/.config/readaloud/settings.json, which
// resolves under %USERPROFILE% here) and WiggleDetector in Wiggle.cs (pure maths, no I/O).
//
// *** HONESTY BANNER ***
// This file has never been RUN. It was written and compile-verified on Linux
// (dotnet build -c Release -p:EnableWindowsTargeting=true, 0 errors). Everything below is
// best-effort until the owner starts it on a real Windows box. Lines marked UNTESTED: are the
// ones most likely to need a tweak in front of a real desktop.
//
// The whole file is wrapped in #if READALOUD_WINDOWS: the repo-root ReadAloud.csproj compiles
// **/*.cs and sweeps this folder up too, so without the guard these Windows-only types would
// break the Linux build. Only windows/ReadAloud.Windows.csproj defines the symbol.
#if READALOUD_WINDOWS
// The next two blocks are normally generated into obj/ by the SDK; this project generates nothing
// (see the csproj — obj/*.cs lands in the Linux build's source glob) so they live here by hand.
// These cover this file AND the two shared Linux sources compiled alongside it.
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;

using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Media;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// The SDK normally derives this from the net10.0-windows TFM. Without it every WinForms/SAPI call
// raises CA1416 "this call site is reachable on all platforms".
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]

// One resident process does everything (tray + hotkey + wiggle), unlike Linux where the shortcut
// spawns a one-shot speaker. The only short-lived process is --claude-hook, which Claude Code spawns.
internal static class WinReadAloud
{
    private const int HotkeyId = 1; // ids are per-window and this window is ours alone
    private static readonly SpeechSynthesizer Synth = new();
    private static readonly WiggleDetector Detector = new(); // shared logic, ../Wiggle.cs

    private static Settings cfg = Settings.Load();
    private static NotifyIcon? tray;
    private static HotkeyWindow? hotkeyWindow;
    private static Native.HookProc? mouseProc; // a field, not a local: the hook holds a raw pointer to it
    private static nint mouseHook;
    private static int lastX = int.MinValue;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-check"))
        {
            Native.AttachConsole(-1); // WinExe has no console of its own; borrow the caller's
            return SelfCheck() ? 0 : 1;
        }

        return args.Contains("--claude-hook") ? ClaudeHook() : Resident();
    }

    // ---- resident app: tray icon + global hotkey + mouse wiggle ----

    private static int Resident()
    {
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        if (!File.Exists(Settings.FilePath))
        {
            cfg.Save(); // so "Open settings file" always has something to open
        }

        tray = new NotifyIcon
        {
            Icon = SystemIcons.Information, // ponytail: a stock icon, no .ico asset to ship or lose
            Text = "ReadAloud",
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };

        hotkeyWindow = new HotkeyWindow(ReadSelection);
        if (!Native.RegisterHotKey(hotkeyWindow.Handle, HotkeyId,
                Native.MOD_CONTROL | Native.MOD_WIN | Native.MOD_NOREPEAT, Native.VK_S))
        {
            // almost always "a second copy is already running" — say so instead of dying silently
            tray.ShowBalloonTip(5000, "ReadAloud",
                "Ctrl+Win+S is already taken (another copy running?). The tray menu and mouse wiggle still work.",
                ToolTipIcon.Warning);
        }

        mouseProc = MouseHook;
        mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, Native.GetModuleHandle(null), 0);

        try
        {
            Application.Run(); // pumps messages for the hotkey, the hook and the tray menu
        }
        finally
        {
            if (mouseHook != 0)
            {
                Native.UnhookWindowsHookEx(mouseHook);
            }

            Native.UnregisterHotKey(hotkeyWindow.Handle, HotkeyId);
            tray.Visible = false; // else the dead icon lingers in the tray until you hover it
        }

        return 0;
    }

    // ---- tray menu (same items as the Linux one, minus Engine: Windows has exactly one voice) ----

    private static ContextMenuStrip BuildMenu()
    {
        var mute = new ToolStripMenuItem("Mute Claude replies", null, (_, _) => Edit(s => s.MuteClaude = !s.MuteClaude));
        var wiggleOn = new ToolStripMenuItem("Wiggle to read", null, (_, _) => Edit(s => s.WiggleEnabled = !s.WiggleEnabled));
        var wigglePop = new ToolStripMenuItem("Pop sound", null, (_, _) => Edit(s => s.WigglePop = !s.WigglePop));

        var speed = new ToolStripMenuItem("Speed");
        var speeds = new List<(double Value, ToolStripMenuItem Item)>();
        foreach (double preset in new[] { 1.0, 1.5, 2.0, 2.5, 3.0 })
        {
            double v = preset;
            // ponytail: ticked item, not a real radio glyph — ToolStripMenuItem has no radio style
            // and the tick already says "this is the one".
            var item = new ToolStripMenuItem($"{v:0.0}x", null, (_, _) => Edit(s => s.Speed = v));
            speeds.Add((v, item));
            speed.DropDownItems.Add(item);
        }

        var wiggle = new ToolStripMenuItem("Wiggle");
        wiggle.DropDownItems.AddRange([wiggleOn, wigglePop]);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            new ToolStripMenuItem("Stop speaking", null, (_, _) => Stop()),
            mute,
            speed,
            wiggle,
            new ToolStripMenuItem("Open settings file", null, (_, _) => OpenSettings()),
            new ToolStripMenuItem("Quit", null, (_, _) => Quit()),
        ]);

        menu.Opening += (_, _) =>
        {
            cfg = Settings.Load(); // the file is the source of truth; it may have been hand-edited
            mute.Checked = cfg.MuteClaude;
            wiggleOn.Checked = cfg.WiggleEnabled;
            wigglePop.Checked = cfg.WigglePop;
            foreach ((double value, ToolStripMenuItem item) in speeds)
            {
                item.Checked = Math.Abs(cfg.Speed - value) < 0.01;
            }
        };

        return menu;
    }

    private static void Edit(Action<Settings> change)
    {
        Settings s = Settings.Load(); // never save a stale copy over someone else's edit
        change(s);
        s.Save();
        cfg = s;
    }

    private static void OpenSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Settings.FilePath) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            MessageBox.Show($"Cannot open {Settings.FilePath}: {e.Message}", "ReadAloud");
        }
    }

    private static void Quit()
    {
        Stop();
        Application.ExitThread(); // unwinds Application.Run, so the finally block cleans up
    }

    // ---- speaking ----

    // Settings.Speed is a tempo multiplier (0.5 .. 3.0); SAPI wants Rate -10 .. 10, 0 = natural.
    // Straight lerp either side of 1.0, the same shape as the Linux spd-say mapping.
    // UNTESTED: SAPI's rate steps are roughly geometric, so 2.5x lands "about" at Rate 8 rather
    // than exactly 2.5x. Tune the presets by ear; the mapping is deliberately simple.
    internal static int RateFor(double speed) => (int)Math.Round(Math.Clamp(
        speed <= 1.0 ? (speed - 1.0) / 0.5 * 10.0 : (speed - 1.0) / 2.0 * 10.0, -10.0, 10.0));

    private static void Speak(string text)
    {
        StopHookProcess(); // a human asked for this read: nothing else gets to talk over it
        lock (Synth)
        {
            Synth.SpeakAsyncCancelAll(); // a new request always wins
            if (text.Length == 0)
            {
                return; // nothing selected = "shut up", exactly like the Linux build
            }

            Synth.Rate = RateFor(cfg.Speed);
            Synth.SpeakAsync(text);
        }
    }

    private static void Stop()
    {
        lock (Synth)
        {
            Synth.SpeakAsyncCancelAll();
        }

        StopHookProcess();
    }

    // Clipboard juggling plus a wait of up to ~400ms must NOT run on the UI thread: Windows quietly
    // uninstalls a low-level mouse hook whose thread stops pumping messages (LowLevelHooksTimeout),
    // which would kill wiggle-to-read after the first use.
    private static void ReadSelection()
    {
        var t = new Thread(() => Speak(TextUtil.Clean(GetSelection()))) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA); // Clipboard requires STA
        t.Start();
    }

    // ---- selection capture ----

    // Windows has no X11-style PRIMARY selection, so the standard trick is to borrow the clipboard:
    // save it, press Ctrl+C at the focused app, read it, put the old contents back.
    // Known limits, none of them fixable from here:
    //   - the focused app must actually implement Ctrl+C (a terminal with a different copy key,
    //     a PDF viewer with copy disabled, most games: nothing comes back)
    //   - only TEXT is restored. A copied image or file list is lost; that is the price of the trick.
    //   - if the app fills the clipboard slower than the wait below, the read comes back empty.
    private static string GetSelection()
    {
        string saved = ClipboardText();
        try
        {
            Clipboard.Clear(); // so a stale clipboard cannot be mistaken for the new selection
        }
        catch (ExternalException)
        {
            // another app has the clipboard open right now; carry on and let the read decide
        }

        SendCtrlC();

        string got = "";
        for (int i = 0; i < 20 && got.Length == 0; i++)
        {
            Thread.Sleep(20); // up to 400ms for the focused app to answer the keystroke
            got = ClipboardText();
        }

        if (saved.Length > 0)
        {
            SetClipboardText(saved);
        }

        return got;
    }

    private static string ClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (ExternalException)
        {
            return ""; // clipboard locked by another process
        }
    }

    private static void SetClipboardText(string s)
    {
        try
        {
            Clipboard.SetText(s);
        }
        catch (ExternalException)
        {
            // lost the race to restore it; the user keeps whatever the selection was
        }
    }

    private static void SendCtrlC()
    {
        // Ctrl+Win+S is our own hotkey, so Win is almost certainly still physically down when we get
        // here — leaving it down turns our Ctrl+C into Win+Ctrl+C (which switches virtual desktops).
        // UNTESTED: the 40ms grace + the modifier releases below are the classic fix; a real desktop
        // may want a slightly longer pause.
        Thread.Sleep(40);
        Native.Key(Native.VK_LWIN, up: true);
        Native.Key(Native.VK_RWIN, up: true);
        Native.Key(Native.VK_MENU, up: true);
        Native.Key(Native.VK_SHIFT, up: true);

        Native.Key(Native.VK_CONTROL, up: false);
        Native.Key(Native.VK_C, up: false);
        Native.Key(Native.VK_C, up: true);
        Native.Key(Native.VK_CONTROL, up: true);
    }

    // ---- mouse wiggle ----

    // Feeds per-event horizontal deltas to the SAME WiggleDetector the Linux build uses. There the
    // deltas are raw evdev REL_X counts; here they are screen-pixel deltas, which is close enough
    // for a 15px minimum stroke. UNTESTED: at the very edge of the screen the pointer stops moving,
    // so deltas go to 0 and a wiggle there will not register.
    private static nint MouseHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && wParam == Native.WM_MOUSEMOVE && cfg.WiggleEnabled)
        {
            int x = Marshal.ReadInt32(lParam); // MSLLHOOKSTRUCT starts with POINT pt
            if (lastX != int.MinValue
                && Detector.Feed(x - lastX, Environment.TickCount64, cfg.WiggleFlips, cfg.WiggleWindowMs))
            {
                if (cfg.WigglePop)
                {
                    SystemSounds.Asterisk.Play(); // a pop so the shake feels acknowledged
                }

                ReadSelection();
            }

            lastX = x;
        }

        return Native.CallNextHookEx(0, code, wParam, lParam);
    }

    // Invisible window that exists only to receive WM_HOTKEY. RegisterHotKey wants a real HWND.
    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly Action onHotkey;

        public HotkeyWindow(Action onHotkey)
        {
            this.onHotkey = onHotkey;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312) // WM_HOTKEY
            {
                onHotkey();
            }

            base.WndProc(ref m);
        }
    }

    // ---- Claude Code Stop hook ----

    private static string PidFile => Path.Combine(Path.GetTempPath(), "readaloud.pid");

    private static int ClaudeHook()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (cfg.MuteClaude || File.Exists(Path.Combine(home, ".claude", "tts-off")))
        {
            return 0;
        }

        string text = TextUtil.Clean(TextUtil.LastAssistantText(Console.In.ReadToEnd()));
        if (text.Length == 0)
        {
            return 0;
        }

        // This short-lived process IS the voice, so "stop it" means "kill it" — the same pid-file
        // protocol as Linux. ponytail: the Linux build also makes a hook read WAIT politely behind a
        // manual read; that needs cross-process state we do not have here, so a reply simply loses
        // to any newer request instead.
        StopHookProcess();
        try
        {
            File.WriteAllText(PidFile, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        catch (IOException)
        {
            // no pid file means only "stop" cannot reach us; still worth speaking
        }

        Synth.Rate = RateFor(cfg.Speed);
        Synth.Speak(text); // blocking on purpose: the process must outlive the speech
        return 0;
    }

    private static void StopHookProcess()
    {
        try
        {
            int pid = int.Parse(File.ReadAllText(PidFile).Trim(), CultureInfo.InvariantCulture);
            using var p = Process.GetProcessById(pid);
            if (p.Id != Environment.ProcessId && p.ProcessName.Contains("ReadAloud", StringComparison.OrdinalIgnoreCase))
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // no previous hook, already exited, or the pid was recycled by something else
        }
    }

    // ---- self check: `ReadAloud.exe --self-check` (Windows only; cannot run on the build machine) ----

    private static bool SelfCheck()
    {
        bool ok = WiggleDetector.SelfCheck(); // the shared detector from ../Wiggle.cs

        (double Speed, int Want)[] rates = [(0.5, -10), (1.0, 0), (1.5, 2), (2.0, 5), (2.5, 8), (3.0, 10)];
        foreach ((double speed, int want) in rates)
        {
            int got = RateFor(speed);
            bool pass = got == want;
            ok &= pass;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")} speed={speed} -> SAPI Rate {got}"
                              + (pass ? "" : $" (want {want})"));
        }

        return ok;
    }
}

// Copied from the Linux Program.cs rather than shared: both are local functions inside its
// top-level statements, so <Compile Include> would drag the whole Linux entry point along.
internal static class TextUtil
{
    public static string Clean(string s)
    {
        // make markdown and code listenable instead of torture
        s = Regex.Replace(s, @"```.*?```", " code block. ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"`([^`]*)`", "$1");
        s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1"); // [text](url) -> text
        s = Regex.Replace(s, @"https?://\S+", " link ");
        s = Regex.Replace(s, @"[#*_>|~]", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    // Claude Code hook JSON -> transcript path -> last assistant message text
    public static string LastAssistantText(string hookJson)
    {
        try
        {
            using JsonDocument hook = JsonDocument.Parse(hookJson);
            string? path = hook.RootElement.TryGetProperty("transcript_path", out JsonElement tp) ? tp.GetString() : null;
            if (path is null || !File.Exists(path))
            {
                return "";
            }

            string last = "";
            foreach (string line in File.ReadLines(path))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("type", out JsonElement t) && t.GetString() == "assistant"
                        && !(root.TryGetProperty("isSidechain", out JsonElement sc) && sc.ValueKind == JsonValueKind.True)
                        && root.TryGetProperty("message", out JsonElement msg)
                        && msg.TryGetProperty("content", out JsonElement content)
                        && content.ValueKind == JsonValueKind.Array)
                    {
                        string joined = string.Join(" ",
                            content.EnumerateArray()
                                .Where(c => c.TryGetProperty("type", out JsonElement ct) && ct.GetString() == "text")
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
}

// win32 bits. Only what is actually used — no wrapper library, no interop package.
internal static class Native
{
    public const int WH_MOUSE_LL = 14;
    public const nint WM_MOUSEMOVE = 0x0200;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000; // holding the hotkey must not machine-gun reads
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12; // Alt
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;
    public const ushort VK_C = 0x43;
    public const uint VK_S = 0x53;

    public delegate nint HookProc(int code, nint wParam, nint lParam);

    public static void Key(ushort vk, bool up)
    {
        INPUT input = default;
        input.type = 1; // INPUT_KEYBOARD
        input.ki = new KEYBDINPUT
        {
            wVk = vk,
            wScan = 0,
            dwFlags = up ? 2u : 0u, // KEYEVENTF_KEYUP
            time = 0,
            dwExtraInfo = 0,
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint threadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint GetModuleHandle(string? name);

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    // INPUT is 40 bytes on x64: 4 type + 4 padding + a 32-byte union whose largest member is
    // MOUSEINPUT. Declaring the size beats declaring a MOUSEINPUT we never touch. win-x64 only,
    // which is exactly what the csproj publishes.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)]
        public uint type;

        [FieldOffset(8)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }
}
#endif
