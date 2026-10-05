#!/bin/bash
# ReadAloud one-line install (Linux / Ubuntu GNOME):
#   bash <(curl -fsSL https://raw.githubusercontent.com/IAFahim/ReadAloud/master/setup.sh)
#
# Installs deps, .NET 10, the app, shortcut, tray, autostart, wiggle permission.
# Safe to re-run. Skip the ~40 MB Inflect model with: READALOUD_SKIP_INFLECT=1 bash …
set -euo pipefail

REPO=https://github.com/IAFahim/ReadAloud
DIR="$HOME/ReadAloud"
# already inside a checkout? use it (don't force ~/ReadAloud)
if [[ -f ./install.sh && -f ./Program.cs && -f ./ReadAloud.csproj ]]; then
  DIR="$(pwd -P)"
fi

need_sudo() {
  if [[ "$(id -u)" -eq 0 ]]; then
    "$@"
  else
    sudo "$@"
  fi
}

echo "==> system packages"
need=""
have() { command -v "$1" >/dev/null 2>&1; }
have git      || need="$need git"
have ffplay   || need="$need ffmpeg"
have spd-say  || need="$need speech-dispatcher"
have xclip    || need="$need xclip"
have wl-paste || need="$need wl-clipboard"
have curl     || need="$need curl"
have notify-send || need="$need libnotify-bin"
have python3  || need="$need python3"
# gsettings is part of glib2 / libglib2.0-bin on Debian
have gsettings || need="$need libglib2.0-bin"

if [[ -n "${need// }" ]]; then
  echo "  installing:$need"
  if have apt-get; then
    need_sudo apt-get update -qq
    # shellcheck disable=SC2086
    need_sudo apt-get install -y $need
  elif have pacman; then
    # Arch / Omarchy: map the Debian names above to pacman packages
    pkgs=""
    # shellcheck disable=SC2086
    for p in $need; do
      case "$p" in
        speech-dispatcher) pkgs="$pkgs speech-dispatcher" ;;
        libnotify-bin)     pkgs="$pkgs libnotify" ;;
        libglib2.0-bin)    pkgs="$pkgs glib2" ;;
        *)                 pkgs="$pkgs $p" ;;
      esac
    done
    # shellcheck disable=SC2086
    need_sudo pacman -S --needed --noconfirm $pkgs
  else
    echo "  no apt-get/pacman — install these by hand:$need" >&2
    exit 1
  fi
else
  echo "  all present"
fi

echo "==> .NET 10 SDK"
export PATH="${HOME}/.dotnet:${HOME}/.local/bin:${PATH}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
if ! have dotnet || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  echo "  installing into ~/.dotnet"
  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
  if [[ -f "$HOME/.bashrc" ]] && ! grep -q 'DOTNET_ROOT' "$HOME/.bashrc" 2>/dev/null; then
    printf '\n# ReadAloud / .NET\nexport DOTNET_ROOT="$HOME/.dotnet"\nexport PATH="$HOME/.dotnet:$PATH"\n' >> "$HOME/.bashrc"
  fi
else
  echo "  $(dotnet --list-sdks | grep '^10\.' | tail -1)"
fi
have dotnet || { echo "dotnet still missing after install" >&2; exit 1; }

echo "==> uv (Inflect python toolchain)"
if ! have uv; then
  curl -LsSf https://astral.sh/uv/install.sh | sh
  export PATH="$HOME/.local/bin:$PATH"
fi
have uv && echo "  $(command -v uv)" || echo "  warn: uv missing — Inflect install may fail"

echo "==> Hugging Face CLI (model download)"
if ! have hf && ! have huggingface-cli; then
  uv tool install huggingface_hub || true
  export PATH="$HOME/.local/bin:$PATH"
fi
if have hf || have huggingface-cli; then
  echo "  ok"
else
  echo "  warn: hf CLI missing — Inflect download may need a manual: uv tool install huggingface_hub"
fi

echo "==> repo → $DIR"
if [[ ! -d "$DIR/.git" ]]; then
  git clone "$REPO" "$DIR"
else
  git -C "$DIR" pull --ff-only || git -C "$DIR" pull || true
fi
cd "$DIR"

echo "==> install.sh"
./install.sh

echo "==> wiggle permission (input group)"
if id -nG | tr ' ' '\n' | grep -qx input; then
  echo "  already in input group"
elif grep -q "^input:" /etc/group 2>/dev/null; then
  echo "  granting mouse access for wiggle-to-read (sudo)…"
  need_sudo usermod -aG input "$USER"
  echo "  granted — log out and back in for this session to pick it up"
else
  echo "  warn: no 'input' group on this system — wiggle may not work"
fi

echo
echo "Boom. Select text and press Ctrl+Super+S — or wiggle the mouse."
echo "  Top-bar icon → Speed / Engine / Wiggle feel (Sensitive · Normal · Firm · Stubborn)"
echo "  Engines: Google / Inflect (local neural) / Offline"
echo "  Check:   $DIR/publish/ReadAloud --engine-check"
