#!/bin/bash
# ReadAloud one-line install (Linux / Ubuntu GNOME):
#   bash <(curl -fsSL https://raw.githubusercontent.com/IAFahim/ReadAloud/master/setup.sh)
# Installs deps, .NET 10, the app, shortcut, tray, autostart, wiggle permission, Claude hook. Boom.
set -e

REPO=https://github.com/IAFahim/ReadAloud
DIR="$HOME/ReadAloud"
if [[ -f ./install.sh && -f ./Program.cs ]]; then DIR="$PWD"; fi

# --- system packages ---
need=""
command -v git     >/dev/null || need="$need git"
command -v ffplay  >/dev/null || need="$need ffmpeg"
command -v spd-say >/dev/null || need="$need speech-dispatcher"
command -v xclip   >/dev/null || need="$need xclip"
command -v wl-paste >/dev/null || need="$need wl-clipboard"
if [[ -n "$need" ]]; then
  echo "Installing:$need"
  sudo apt-get update -qq && sudo apt-get install -y $need
fi

# --- .NET 10 SDK ---
if ! command -v dotnet >/dev/null || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  echo "Installing .NET 10 SDK into ~/.dotnet"
  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
  export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
  grep -q 'DOTNET_ROOT' "$HOME/.bashrc" 2>/dev/null || \
    printf 'export DOTNET_ROOT="$HOME/.dotnet"\nexport PATH="$HOME/.dotnet:$PATH"\n' >> "$HOME/.bashrc"
fi

# --- the app ---
if [[ ! -d "$DIR/.git" ]]; then git clone "$REPO" "$DIR"; fi
cd "$DIR"
git pull -q || true
./install.sh

# --- wiggle permission (mouse access) ---
if ! id -nG | tr ' ' '\n' | grep -qx input; then
  echo "Granting mouse access for wiggle-to-read (sudo)…"
  sudo usermod -aG input "$USER"
  echo "Mouse access granted — it fully applies after your next login."
fi

echo
echo "Boom. Select text and press Ctrl+Super+S — or wiggle the mouse. The top-bar icon has the controls."
