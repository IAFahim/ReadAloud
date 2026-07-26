#!/bin/bash
# One-shot setup on a new PC (GNOME). Needs: dotnet 10 SDK, ffplay (ffmpeg), spd-say, xclip or wl-paste.
set -e
cd "$(dirname "$0")"

dotnet publish -c Release -o publish

# the shortcut lives in settings.json so the tray/settings file stays the single source of truth
SETTINGS="$HOME/.config/readaloud/settings.json"
BINDING='<Control><Super>s'
if [[ -f "$SETTINGS" ]]; then
  BINDING=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("Shortcut") or "")' "$SETTINGS" 2>/dev/null || true)
  [[ -n "$BINDING" ]] || BINDING='<Control><Super>s'
fi

BASE=org.gnome.settings-daemon.plugins.media-keys
KB=/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/readaloud/
cur=$(gsettings get $BASE custom-keybindings)
if [[ "$cur" != *readaloud* ]]; then
  if [[ "$cur" == "@as []" || "$cur" == "[]" ]]; then new="['$KB']"; else new="${cur%]}, '$KB']"; fi
  gsettings set $BASE custom-keybindings "$new"
fi
gsettings set $BASE.custom-keybinding:$KB name 'Read Aloud selection'
gsettings set $BASE.custom-keybinding:$KB command "$PWD/publish/ReadAloud"
gsettings set $BASE.custom-keybinding:$KB binding "$BINDING"

# top-bar icon at every login
mkdir -p "$HOME/.config/autostart"
cat > "$HOME/.config/autostart/readaloud-tray.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=ReadAloud tray
Comment=Top-bar controls for ReadAloud
Exec=$PWD/publish/ReadAloud --tray
X-GNOME-Autostart-enabled=true
NoDisplay=true
EOF

if pgrep -f "ReadAloud --tray" >/dev/null; then
  echo "Tray already running — pick Quit in its menu and rerun to load the new build."
else
  # if the input group was granted but this login session predates it, sg gives the
  # tray mouse access (wiggle) right now instead of waiting for the next login
  if grep "^input:" /etc/group | grep -qw "$USER" && ! id -nG | tr ' ' '\n' | grep -qx input; then
    sg input -c "setsid '$PWD/publish/ReadAloud' --tray >/dev/null 2>&1 < /dev/null &"
  else
    setsid "$PWD/publish/ReadAloud" --tray >/dev/null 2>&1 < /dev/null &
  fi
  echo "Tray started."
fi

echo "Done. $BINDING reads the current selection; the top-bar icon has the rest."
id -nG | tr ' ' '\n' | grep -qx input || \
  echo "Wiggle-to-read needs input access: sudo usermod -aG input \$USER   (then log out and back in)"
echo
echo "For Claude Code auto-speak, add this entry to the Stop hooks in ~/.claude/settings.json:"
echo "  { \"hooks\": [ { \"type\": \"command\", \"command\": \"$PWD/publish/ReadAloud --claude-hook\", \"timeout\": 600, \"async\": true } ] }"
