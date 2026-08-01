# ReadAloud

Select text anywhere, press **Ctrl+Super+S** — or just **wiggle the mouse** — and hear it.

Three platforms, each the native way:

- **Linux (this folder)** — the original, fully tested: Google voice + offline fallback,
  top-bar tray, wiggle-to-read.
- **[windows/](windows/)** — built-in Windows voice, system-tray icon, Ctrl+Win+S, wiggle
  via mouse hook. One-liner: `irm https://raw.githubusercontent.com/IAFahim/ReadAloud/master/windows/setup.ps1 | iex`
- **[macos/](macos/)** — built-in `say` voices, clipboard-safe Cmd+C capture.
  One-liner: `bash <(curl -fsSL https://raw.githubusercontent.com/IAFahim/ReadAloud/master/macos/setup-mac.sh)`

The ports compile clean and pass their logic self-checks, but were written on Linux —
**not yet run on real Windows/macOS**. First thing to run there: `ReadAloud --self-check`.

- Press again with a new selection: interrupts and reads the new one.
- Press with nothing selected: stops talking.
- Voice engines (tray → **Engine**):
  - **Google** — free Translate voice (needs internet); falls back if it fails.
  - **Inflect** — local neural voice ([Inflect-Micro-v2](https://huggingface.co/owensong/Inflect-Micro-v2)),
    ~40 MB ONNX under `~/.local/share/readaloud/`. Warm worker keeps the model loaded.
    English, one fixed voice. Install once: `publish/ReadAloud --install-inflect`
    (or tray → **Inflect** → Install / update). Needs `uv` + `hf` once.
  - **Offline** — `spd-say` robot, always available, never pretty.
- Check what is wired: `publish/ReadAloud --engine-check`
- Want Claude Code replies auto-spoken? Deliberately NOT installed by default — with several
  agent sessions open the voices trample each other. Select + wiggle instead. To opt in
  anyway, add a Stop hook running `publish/ReadAloud --claude-hook` (timeout 600, async).

## Top-bar controls

A speaker icon sits in the GNOME top bar (`ReadAloud --tray`, started at login by
`~/.config/autostart/readaloud-tray.desktop`). Click it for:

- **Stop speaking** — shuts the voice up right now.
- **Speed** — 1.0x to 3.0x, the current one is ticked.
- **Pitch** — Lower / Natural / Higher.
- **Engine** — Google / Inflect (local neural) / Offline.
- **Inflect** — Install / update, Warm up, Stop worker, Steady / Natural / Expressive delivery.
- **Wiggle to read** — select text, shake the mouse left-right, it speaks (with a little pop).
  Needs one-time access: `sudo usermod -aG input $USER`, then log out and back in.
  Sensitivity is on the tray (**Wiggle → Sensitive / Normal / Firm / Stubborn**). Too twitchy?
  pick **Firm** or **Stubborn**. Fine-tune the four knobs in `settings.json` if you want.
- **Open settings file** — opens `~/.config/readaloud/settings.json` in your editor.
- **Quit** — removes the icon (and stops the Inflect worker) until next login.

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
autostart, and mouse permission for wiggle. Or manually:

```bash
git clone https://github.com/IAFahim/ReadAloud
cd ReadAloud && ./install.sh
```

`install.sh` builds, registers the shortcut (the `Shortcut` field in
`~/.config/readaloud/settings.json`, default Ctrl+Super+S), installs the tray autostart entry,
downloads the Inflect voice (skip with `READALOUD_SKIP_INFLECT=1`), and starts the tray.
No Claude Code hook is installed.

## Where next

See `IDEAS.md` — 15 ranked upgrades (speech daemon, per-agent Piper voices,
gist-first summaries, OCR, dictation, karaoke overlay…).
