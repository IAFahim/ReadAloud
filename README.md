# ReadAloud

Select text anywhere, press **Ctrl+Super+S**, hear it. Free and local (speech-dispatcher).

- Press again with a new selection: interrupts and reads the new one.
- Press with nothing selected: stops talking.
- Claude Code replies are auto-spoken: a Stop hook in `~/.claude/settings.json` runs
  `publish/ReadAloud --claude-hook`, which parses the transcript itself — pure C#, no shell script.
  Mute with `touch ~/.claude/tts-off`, unmute with `rm ~/.claude/tts-off`.
- Voice is the free Google Translate voice (needs internet); falls back to the offline
  spd-say robot automatically when offline.

## Hack it

Everything lives in `Program.cs`. Knobs at the top: `Engine` ("google"/"spd"), `GoogleLang`, `SpdRate`.
Rebuild after edits:

```bash
dotnet publish -c Release -o publish
```

The GNOME shortcut runs `publish/ReadAloud`; change the key in Settings → Keyboard → Custom Shortcuts.

## New PC setup

```bash
git clone https://github.com/IAFahim/ReadAloud
cd ReadAloud && ./install.sh
```

`install.sh` builds, registers the Ctrl+Super+S shortcut, and prints the Claude Code hook
snippet to paste into `~/.claude/settings.json`. Keep the hook `timeout` high (600) —
a short timeout kills the voice mid-answer.

## Where next

See `IDEAS.md` — 15 ranked upgrades (speech daemon, per-agent Piper voices,
gist-first summaries, OCR, dictation, karaoke overlay…).
