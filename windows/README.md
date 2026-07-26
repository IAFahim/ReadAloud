# ReadAloud for Windows

Select text anywhere, press **Ctrl plus Win plus S** — or just **wiggle the mouse** — and hear it.

> **Not yet run on Windows.** This port was written and compiled on Linux. The build is clean
> (zero errors, zero warnings, cross compiled with EnableWindowsTargeting), but no one has
> started the program on a real Windows machine yet. Treat the first run as a test. The list of
> things most likely to need a nudge is at the bottom of this page.

## Setup

Open PowerShell and paste one line.

```powershell
irm https://raw.githubusercontent.com/IAFahim/ReadAloud/master/windows/setup.ps1 | iex
```

That installs the dot net ten S D K if you do not have it, clones the repo into your home
folder, builds a single ReadAloud dot exe, adds a Start Menu shortcut and a Startup shortcut so
it runs at every login, and wires the Claude Code hook. Run it again any time to update.

## What it does

- **Ctrl plus Win plus S** reads the selected text out loud.
- **Press it again** with a new selection: the old speech stops, the new one starts.
- **Press it with nothing selected**: it stops talking.
- **Wiggle the mouse** left and right a few times over a selection: same thing, no keyboard. A
  small pop sound confirms the shake was caught.
- **Claude Code replies are read to you** automatically at whatever speed you picked, through a
  Stop hook in your Claude settings file.

The voice is the one built into Windows, so nothing is sent over the internet and it works
offline. There is no ffplay, no speech dispatcher, no Google request — that part of the Linux
version is gone here.

## The tray icon

A speaker icon sits near the clock. Right click it for:

- **Stop speaking** — silence, right now.
- **Mute Claude replies** — stop auto reading Claude Code answers.
- **Speed** — one point zero up to three point zero. The current one is ticked.
- **Wiggle** — turn wiggle to read on or off, and turn the pop sound on or off.
- **Open settings file** — opens the settings file in your editor.
- **Quit** — removes the icon until the next login.

## Where the settings live

```
%USERPROFILE%\.config\readaloud\settings.json
```

Same path and same file format as the Linux version on purpose, so one settings file can be
copied between machines. Every menu click writes it and every read picks it up, so nothing needs
a restart. You can also edit it by hand; the menu re-reads it each time you open it.

To mute Claude replies without opening the menu, create an empty file at
`%USERPROFILE%\.claude\tts-off`. Delete it to unmute.

## Honest list: what is untested

Everything below compiles, and none of it has ever run.

1. **Reading the selection.** Windows has no separate selection buffer, so the program saves your
   clipboard, sends Ctrl plus C to the focused app, reads the clipboard, and puts the old text
   back. If an app does not answer Ctrl plus C, nothing is read. A copied image or file list is
   not restored, only text.
2. **The hotkey firing while you still hold the Win key.** The program releases Win, Alt and
   Shift before sending Ctrl plus C, after a forty millisecond pause. That pause may need to be
   longer on a real desktop.
3. **The speed mapping.** Speed one point zero to three point zero is mapped onto the Windows
   voice rate of minus ten to plus ten by a straight line. The Windows rate steps are not exactly
   proportional, so two point five times will sound close to, but not exactly, two and a half
   times. Tune it by ear in the settings file.
4. **Wiggle sensitivity.** The shake detector is the exact same code as the Linux version, but it
   is now fed screen pixels instead of raw mouse counts. If it triggers too easily or not easily
   enough, change WiggleFlips or WiggleWindowMs in the settings file. Wiggling at the very edge
   of the screen will not register, because the pointer stops moving there.
5. **Registering the hotkey.** If something else on your machine already owns Ctrl plus Win plus
   S, a warning balloon appears and only the tray menu and the wiggle will work.

There is a built in check for the pure logic. Run it from a terminal:

```powershell
& "$HOME\ReadAloud\windows\publish\ReadAloud.exe" --self-check
```

It prints one PASS or FAIL line per case for the shake detector and the speed mapping, and exits
with code zero when everything passed. It cannot test speech, the clipboard or the hotkey — only
a human at the keyboard can.

## For the person maintaining this

`windows\Program.cs` is the whole port. It shares `Settings.cs` and the shake detector in
`Wiggle.cs` with the Linux build by direct file reference, so a fix to the shared logic lands on
both platforms at once.

Every file in `windows` is wrapped in `#if READALOUD_WINDOWS`, because the Linux project at the
repo root compiles every C sharp file it can find and would otherwise pull the Windows only code
into its build. For the same reason this project generates no files into its obj folder — the
usual generated ones are written by hand at the top of `Program.cs`.

Build it from any machine, including Linux:

```
dotnet build -c Release -p:EnableWindowsTargeting=true
```
