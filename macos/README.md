# ReadAloud for macOS

Select text anywhere, press your shortcut, hear it. Claude Code replies get read
out loud on their own.

## Read this first

This port compiles and its logic tests pass. **Nobody has ever run it on a Mac.**
It was written on Linux, where a Mac cannot be tested. The build gate was
`dotnet build -c Release`, zero errors, and `--self-check`, sixteen tests passing.
Neither of those makes a sound come out of a speaker.

So treat your first run as bring-up, not as a working tool. The most likely
problem by far is the Accessibility permission in step two below.

## What works, in theory

- Press a shortcut with text selected, and it speaks.
- Press it again with nothing selected, and it stops.
- Press it with new text selected, and it drops the old speech and reads the new.
- Claude Code answers are spoken automatically at two and a half times speed.
- Your clipboard is put back exactly as you left it after every read.

## Install

Paste this in a terminal.

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/IAFahim/ReadAloud/master/macos/setup-mac.sh)
```

It installs the .NET 10 SDK if you lack it, into your home folder, no admin
password. It clones the repo to `~/ReadAloud`, builds the binary, runs the self
check, and adds the Claude Code hook. Then it prints the manual steps.

## Step one, the permission that everything depends on

To read your selection, ReadAloud presses Command C for you. macOS only allows a
trusted app to press keys. The permission belongs to the app that **launches**
ReadAloud, not to ReadAloud itself. So allow your terminal, or Shortcuts, or
skhd, whichever you bind the key in.

Open **System Settings**, then **Privacy and Security**, then **Accessibility**,
and switch that app on. The first run also asks you by itself.

Without this, the hotkey looks broken. It will silently do nothing at all,
because an empty selection means stop.

## Step two, the shortcut

The setup script prints this too, and detects which of the two you can use.

With the built-in Shortcuts app, no extra software:

1. Open Shortcuts and click plus for a new one.
2. Add the action Run Shell Script.
3. Put `~/ReadAloud/macos/publish/ReadAloud` in the script box, with your real
   home folder spelled out.
4. Name it Read Aloud.
5. Open the info panel, tick Use as Quick Action, click Add Keyboard Shortcut,
   and press Command Option S.

Or with skhd, a small hotkey daemon, `brew install koekeishiya/formulae/skhd`,
then one line in `~/.skhdrc`:

```
cmd + alt - s : /Users/YOURNAME/ReadAloud/macos/publish/ReadAloud
```

## The voice and the speed

The voice is `say`, which ships with macOS and sounds good with no internet.
Pick the voice in System Settings, Accessibility, Spoken Content. Run `say -v ?`
to list every voice you have.

The speed lives in `~/.config/readaloud/settings.json`, the same file the Linux
version uses. `Speed` is a multiplier. One means two hundred words a minute,
two and a half means five hundred, and it is capped between one hundred and
seven hundred and twenty.

Set `MuteClaude` to true to silence the Claude reading, or just
`touch ~/.claude/tts-off` and `rm` it later.

The other settings in that file are Linux only and are ignored here. Those are
Engine, Pitch, GoogleLang, SpdRate, the Wiggle ones, and Shortcut.

## Commands

```
ReadAloud                speak the selection
ReadAloud --stop         stop talking
ReadAloud --stdin        speak text piped in
ReadAloud --claude-hook  the Claude Code Stop hook
ReadAloud --self-check   the logic tests, sixteen of them
```

## What version one skips, and why

**No menu bar icon.** The Linux tray talks to the desktop over D-Bus, which does
not exist on macOS. A real one needs `NSStatusItem` from AppKit, which means
either a small Swift or Objective-C host app wrapped around this binary, or a
bridge library. The cheap alternative that would take an afternoon: a
[SwiftBar](https://swiftbar.app) plugin, a shell script that prints menu lines
and calls this same binary for each action. Until then, edit the settings file.

**No wiggle to read.** The Linux version reads mouse events straight from
`/dev/input`. macOS has no such thing. The equivalent is a `CGEventTap` on
`kCGEventMouseMoved`, installed through a P/Invoke into `ApplicationServices`,
feeding the same flip-counting detector that is already written and already
tested in `../Wiggle.cs`. The detector logic ports over unchanged. Only the
event source has to be rewritten, and the tap needs the same Accessibility
permission as above.

Neither is hard. Both were left out because an untested feature stacked on an
untested port is two unknowns instead of one.

## A note for whoever maintains this repo

Adding this folder breaks `dotnet build` **in the repo root**, and the fix cannot
live in this folder. The root `ReadAloud.csproj` compiles every `.cs` file below
it, so it now also swallows `macos/Program.cs` and produces
`error CS8802: Only one compilation unit can have top-level statements`.

One line in the root `ReadAloud.csproj` fixes it, adding `macos/**` to the
exclude list that is already there:

```xml
<DefaultItemExcludes>$(DefaultItemExcludes);bin/**;obj/**;bin-tray/**;obj-tray/**;publish/**;macos/**</DefaultItemExcludes>
```

That edit was deliberately not made here, because this work was scoped to
`macos/` only. The macOS project itself builds fine either way, since it names
`../Settings.cs` explicitly rather than globbing upward.

## Hack it

Everything is `macos/Program.cs`, one file, plus `../Settings.cs` shared with the
Linux build. Rebuild with:

```bash
dotnet publish -c Release -o publish macos/ReadAloud.Mac.csproj
```
