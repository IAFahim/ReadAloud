#!/bin/bash
# One-shot setup on a new PC (GNOME). Needs: dotnet 10 SDK, ffplay (ffmpeg), spd-say, xclip or wl-paste.
set -e
cd "$(dirname "$0")"

dotnet publish -c Release -o publish

BASE=org.gnome.settings-daemon.plugins.media-keys
KB=/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/readaloud/
cur=$(gsettings get $BASE custom-keybindings)
if [[ "$cur" != *readaloud* ]]; then
  if [[ "$cur" == "@as []" || "$cur" == "[]" ]]; then new="['$KB']"; else new="${cur%]}, '$KB']"; fi
  gsettings set $BASE custom-keybindings "$new"
fi
gsettings set $BASE.custom-keybinding:$KB name 'Read Aloud selection'
gsettings set $BASE.custom-keybinding:$KB command "$PWD/publish/ReadAloud"
gsettings set $BASE.custom-keybinding:$KB binding '<Control><Super>s'

echo "Done. Ctrl+Super+S reads the current selection."
echo
echo "For Claude Code auto-speak, add this entry to the Stop hooks in ~/.claude/settings.json:"
echo "  { \"hooks\": [ { \"type\": \"command\", \"command\": \"$PWD/publish/ReadAloud --claude-hook\", \"timeout\": 600, \"async\": true } ] }"
