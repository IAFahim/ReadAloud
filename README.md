# ReadAloud

Select text anywhere, press **Ctrl+Super+S** — or just **wiggle the mouse** — and hear it.

- Press again with a new selection: interrupts and reads the new one.
- Press with nothing selected: stops talking.
- Claude Code replies are auto-spoken: a Stop hook in `~/.claude/settings.json` runs
  `publish/ReadAloud --claude-hook`, which parses the transcript itself — pure C#, no shell script.
  Mute with `touch ~/.claude/tts-off`, unmute with `rm ~/.claude/tts-off`.
- Voice is the free Google Translate voice (needs internet); falls back to the offline
  spd-say robot automatically when offline.

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
