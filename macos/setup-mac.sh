#!/bin/bash
# ReadAloud for macOS — one-shot setup on a fresh Mac.
#
#   bash <(curl -fsSL https://raw.githubusercontent.com/IAFahim/ReadAloud/master/macos/setup-mac.sh)
#
# It installs the .NET 10 SDK if missing, clones or updates ~/ReadAloud, publishes the macOS
# binary, and then TELLS you the two manual steps macOS
# will not let a script do for you (Accessibility permission, keyboard shortcut).
#
# HONESTY: this script was written and syntax-checked on Linux and has NEVER BEEN RUN ON A MAC.
# Read it before you trust it. It only ever writes to: ~/.dotnet, ~/ReadAloud, ~/.zshrc (one PATH
# block).

set -euo pipefail

REPO_URL="https://github.com/IAFahim/ReadAloud"
REPO_DIR="$HOME/ReadAloud"
DOTNET_DIR="$HOME/.dotnet"
ZSHRC="$HOME/.zshrc"
BIN="$REPO_DIR/macos/publish/ReadAloud"

step() { printf '\n==> %s\n' "$1"; }

if [ "$(uname -s)" != "Darwin" ]; then
  echo "This is the macOS setup. On Linux use ./install.sh in the repo root instead." >&2
  exit 1
fi

# ---------------------------------------------------------------- .NET 10 SDK
# A previous run may have put it in ~/.dotnet, which is not on PATH in a fresh non-login shell.
if [ -d "$DOTNET_DIR" ]; then
  export DOTNET_ROOT="$DOTNET_DIR"
  export PATH="$DOTNET_DIR:$PATH"
fi

have_dotnet10() {
  command -v dotnet >/dev/null 2>&1 || return 1
  dotnet --list-sdks 2>/dev/null | grep -q '^10\.'
}

if have_dotnet10; then
  step ".NET 10 SDK already here: $(command -v dotnet)"
else
  step "Installing the .NET 10 SDK into $DOTNET_DIR (official installer, no sudo, no system changes)"
  TMP_INSTALL="$(mktemp -t dotnet-install)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$TMP_INSTALL"
  bash "$TMP_INSTALL" --channel 10.0 --install-dir "$DOTNET_DIR"
  rm -f "$TMP_INSTALL"
  export DOTNET_ROOT="$DOTNET_DIR"
  export PATH="$DOTNET_DIR:$PATH"

  # make it stick for future terminals — appended once, never duplicated
  if ! grep -q 'added by ReadAloud setup-mac.sh' "$ZSHRC" 2>/dev/null; then
    # shellcheck disable=SC2016  # deliberate: $HOME and $PATH must land in .zshrc UNexpanded
    {
      echo ''
      echo '# added by ReadAloud setup-mac.sh'
      echo 'export DOTNET_ROOT="$HOME/.dotnet"'
      echo 'export PATH="$HOME/.dotnet:$PATH"'
    } >> "$ZSHRC"
    echo "Added the dotnet PATH lines to $ZSHRC (open a new terminal to pick them up)."
  fi

  have_dotnet10 || { echo "dotnet 10 still not visible after install — stopping." >&2; exit 1; }
fi

# ------------------------------------------------------------------- the code
if [ -d "$REPO_DIR/.git" ]; then
  step "Updating $REPO_DIR"
  git -C "$REPO_DIR" pull --ff-only
else
  step "Cloning $REPO_URL into $REPO_DIR"
  git clone "$REPO_URL" "$REPO_DIR"
fi

step "Building the macOS binary"
dotnet publish "$REPO_DIR/macos/ReadAloud.Mac.csproj" -c Release -o "$REPO_DIR/macos/publish"

step "Self-check (pure logic: speed mapping, markdown clean, transcript parse)"
"$BIN" --self-check

# (No Claude Code hook: auto-speaking every agent reply gets chaotic with several sessions
# open. Read on demand instead. The binary still supports --claude-hook for hand-wiring.)

# ------------------------------------------------------------- what YOU must do
cat <<EOF

============================================================
Built: $BIN
Nothing above could grant permissions or bind keys for you. Three steps left.
============================================================

1. GRANT ACCESSIBILITY PERMISSION.
   Reading the selection works by pressing Cmd+C for you, and macOS only lets a
   TRUSTED app do that. The permission belongs to whatever app LAUNCHES ReadAloud
   (Terminal, iTerm, Shortcuts, skhd), not to ReadAloud itself.
   Open: System Settings > Privacy & Security > Accessibility
   Turn on the app you will use. The first run also pops the request by itself.

2. BIND A KEYBOARD SHORTCUT to: $BIN
EOF

if command -v skhd >/dev/null 2>&1; then
  cat <<EOF
   skhd is installed, so this is the short road. Add this line to ~/.skhdrc:

       cmd + alt - s : $BIN

   then run: skhd --restart-service
   (and give skhd itself Accessibility permission in step 1)
EOF
else
  cat <<EOF
   Using the built-in Shortcuts app (no extra software):
     a. Open Shortcuts, click + for a new shortcut.
     b. Add the action "Run Shell Script".
     c. Put this in the script box:  $BIN
     d. Name it "Read Aloud".
     e. In the shortcut's info panel (i), tick "Use as Quick Action" and
        click "Add Keyboard Shortcut" — press Cmd+Option+S.
   Or install skhd for a lighter hotkey daemon:  brew install koekeishiya/formulae/skhd
EOF
fi

cat <<EOF

3. TRY IT. Select any text, press your shortcut, listen.
   Press it again with nothing selected to stop talking.

Handy:
   $BIN --stop          stop talking now
   $BIN --stdin         echo "hello" | that
   $BIN --self-check    the pure-logic tests
   Speed / mute / wiggle feel live in ~/.config/readaloud/settings.json
     WiggleFeel: sensitive | normal | firm | stubborn
     (also WiggleFlips, WiggleWindowMs, WiggleMinPx, WiggleCooldownMs)
   Voice: System Settings > Accessibility > Spoken Content.  \`say -v ?\` lists them all.

REMINDER: this port compiles and its logic tests pass, but you are the first
person ever to run it on a Mac. If something is silent, check step 1 first.
EOF
