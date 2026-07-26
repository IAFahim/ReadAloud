# ReadAloud

Select text anywhere, press **Ctrl+Super+S** — or just **wiggle the mouse** — and hear it.

Three platforms, each the native way:

- **Linux (this folder)** — the original, fully tested: Google voice + offline fallback,
  top-bar tray, wiggle, Claude Code auto-read.
- **[windows/](windows/)** — built-in Windows voice, system-tray icon, Ctrl+Win+S, wiggle
  via mouse hook. One-liner: `irm https://raw.githubusercontent.com/IAFahim/ReadAloud/master/windows/setup.ps1 | iex`
- **[macos/](macos/)** — built-in `say` voices, clipboard-safe Cmd+C capture.
  One-liner: `bash <(curl -fsSL https://raw.githubusercontent.com/IAFahim/ReadAloud/master/macos/setup-mac.sh)`

The ports compile clean and pass their logic self-checks, but were written on Linux —
**not yet run on real Windows/macOS**. First thing to run there: `ReadAloud --self-check`.

- Press again with a new selection: interrupts and reads the new one.
- Press with nothing selected: stops talking.
- Voice is the free Google Translate voice (needs internet); falls back to the offline
  spd-say robot automatically when offline.
- Want Claude Code replies auto-spoken? Deliberately NOT installed by default — with several
  agent sessions open the voices trample each other. Select + wiggle instead. To opt in
  anyway, add a Stop hook running `publish/ReadAloud --claude-hook` (timeout 600, async).

## Top-bar controls

A speaker icon sits in the GNOME top bar (`ReadAloud --tray`, started at login by
`~/.config/autostart/readaloud-tray.desktop`). Click it for:

- **Stop speaking** — shuts the voice up right now.
- **Mute Claude replies** — tick to stop auto-reading Claude Code answers.
- **Speed** — 1.0x to 3.0x, the current one is ticked.
- **Engine** — Google voice (needs internet) or Offline voice.
- **Wiggle to read** — select text, shake the mouse left-right, it speaks (with a little pop).
  Needs one-time access: `sudo usermod -aG input $USER`, then log out and back in.
- **Open settings file** — opens `~/.config/readaloud/settings.json` in your editor.
- **Quit** — removes the icon until next login.

Every click writes that file, and every read picks it up, so nothing needs a restart.
Edit the file by hand if you prefer; the menu re-reads it each time you open it.

## Hack it

Speaker logic in `Program.cs` + `Engines.cs`, top-bar icon in `Tray.cs`, shared knobs in
`Settings.cs` → `~/.config/readaloud/settings.json` (speed, pitch, engine, language, shortcut).
Rebuild after edits:

```bash
dotnet publish -c Release -o publish
```

The GNOME shortcut runs `publish/ReadAloud`; change the key in Settings → Keyboard → Custom Shortcuts.

## New PC setup — one line

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/IAFahim/ReadAloud/master/setup.sh)
```

Installs everything: packages, .NET 10, the app, the shortcut, the top-bar icon,
autostart, mouse permission for wiggle, and the Claude Code hook. Or manually:

```bash
git clone https://github.com/IAFahim/ReadAloud
cd ReadAloud && ./install.sh
```

`install.sh` builds, registers the shortcut (the `Shortcut` field in
`~/.config/readaloud/settings.json`, default Ctrl+Super+S), installs the tray autostart entry,
starts the tray, and prints the Claude Code hook snippet to paste into `~/.claude/settings.json`.
Keep the hook `timeout` high (600) — a short timeout kills the voice mid-answer.

## Where next

See `IDEAS.md` — 15 ranked upgrades (speech daemon, per-agent Piper voices,
gist-first summaries, OCR, dictation, karaoke overlay…).
