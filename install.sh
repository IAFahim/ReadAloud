#!/bin/bash
# One-shot setup on a Linux PC (GNOME preferred). Needs: dotnet 10 SDK, ffplay, spd-say,
# xclip or wl-paste. Optional Inflect: uv + hf (pulled by --install-inflect if missing).
#
#   ./install.sh
#   READALOUD_SKIP_INFLECT=1 ./install.sh   # skip ~40 MB model download
#
# Idempotent: re-run after `git pull` to rebuild, re-bind the shortcut, refresh autostart, restart tray.
set -euo pipefail
cd "$(dirname "$0")"
ROOT="$(pwd -P)"
BIN="$ROOT/publish/ReadAloud"
WORKER="$ROOT/publish/inflect_worker.py"
SETTINGS="${XDG_CONFIG_HOME:-$HOME/.config}/readaloud/settings.json"
AUTOSTART="${XDG_CONFIG_HOME:-$HOME/.config}/autostart/readaloud-tray.desktop"
BASE=org.gnome.settings-daemon.plugins.media-keys
KB_PATH=/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/readaloud/
BINDING='<Control><Super>s'

die() { echo "install: $*" >&2; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }

echo "==> preflight"
have dotnet || die "dotnet SDK missing (need 10.x). Run setup.sh, or: https://dotnet.microsoft.com/download"
dotnet --list-sdks 2>/dev/null | grep -q '^10\.' \
  || die "dotnet is present but not 10.x — install the .NET 10 SDK"
have ffplay || die "ffplay missing (package: ffmpeg)"
have spd-say || die "spd-say missing (package: speech-dispatcher)"
have notify-send || echo "  warn: notify-send missing (package: libnotify-bin) — progress toasts won't show"
if ! have wl-paste && ! have xclip; then
  die "need wl-paste (wl-clipboard) or xclip to read the selection"
fi

echo "==> publish"
dotnet publish -c Release -o publish
[[ -x "$BIN" ]] || die "publish produced no binary at $BIN"
[[ -f "$WORKER" ]] || die "publish missing inflect_worker.py (csproj CopyToPublishDirectory)"

echo "==> self-check"
"$BIN" --print-filter >/dev/null
"$BIN" --wiggle-test
"$BIN" --engine-check

# settings: create if missing; never clobber user's knobs on re-install
echo "==> settings"
mkdir -p "$(dirname "$SETTINGS")"
if [[ -f "$SETTINGS" ]]; then
  # read preferred shortcut if set; leave the rest alone (app Normalize() heals 0-int fields)
  BINDING=$(python3 -c 'import json,sys
p=sys.argv[1]
s=json.load(open(p))
print((s.get("Shortcut") or "").strip() or sys.argv[2])
' "$SETTINGS" "$BINDING")
  # back-fill wiggle fields that older files omit (0 would make wiggle hyper-sensitive)
  python3 -c '
import json, sys
p = sys.argv[1]
s = json.load(open(p))
feel = (s.get("WiggleFeel") or "normal").strip() or "normal"
presets = {
  "sensitive": (3, 700, 12, 1200),
  "firm": (6, 450, 35, 2200),
  "stubborn": (8, 400, 50, 2800),
  "normal": (5, 500, 25, 1800),
}
flips, window, minpx, cool = presets.get(feel, presets["normal"])
changed = False
def need(key, lo, hi, val):
    global changed
    v = s.get(key)
    if not isinstance(v, int) or v < lo or v > hi:
        s[key] = val
        return True
    return False
changed |= need("WiggleFlips", 2, 20, flips)
changed |= need("WiggleWindowMs", 100, 5000, window)
changed |= need("WiggleMinPx", 5, 200, minpx)
changed |= need("WiggleCooldownMs", 200, 10000, cool)
if not s.get("WiggleFeel"):
    s["WiggleFeel"] = feel
    changed = True
if not s.get("Shortcut"):
    s["Shortcut"] = sys.argv[2]
    changed = True
if changed:
    json.dump(s, open(p, "w"), indent=2)
    open(p, "a").write("\n")
    print("  healed settings (wiggle/shortcut defaults)")
else:
    print("  settings ok:", p)
' "$SETTINGS" "$BINDING"
else
  python3 -c '
import json, sys
s = {
  "Speed": 2.5,
  "Pitch": 1.0,
  "Engine": "google",
  "GoogleLang": "en",
  "SpdRate": 0,
  "InflectSeed": 7,
  "InflectVariation": 0.667,
  "MuteClaude": True,
  "WiggleEnabled": True,
  "WigglePop": True,
  "WiggleFeel": "normal",
  "WiggleFlips": 5,
  "WiggleWindowMs": 500,
  "WiggleMinPx": 25,
  "WiggleCooldownMs": 1800,
  "Shortcut": sys.argv[1],
}
json.dump(s, open(sys.argv[2], "w"), indent=2)
open(sys.argv[2], "a").write("\n")
print("  wrote fresh settings:", sys.argv[2])
' "$BINDING" "$SETTINGS"
fi

# keep the share-dir worker in sync with the published one (warm worker loads from share)
SHARE="${XDG_DATA_HOME:-$HOME/.local/share}/readaloud"
mkdir -p "$SHARE"
cp -f "$WORKER" "$SHARE/inflect_worker.py"
chmod 755 "$SHARE/inflect_worker.py"

# GNOME shortcut (absolute path so moving the repo doesn't silently break it)
echo "==> shortcut + autostart"
HYP_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/hypr"
HYP=0
if have gsettings && gsettings list-schemas 2>/dev/null | grep -qx "$BASE"; then
  cur=$(gsettings get "$BASE" custom-keybindings)
  if [[ "$cur" != *readaloud* ]]; then
    if [[ "$cur" == "@as []" || "$cur" == "[]" ]]; then
      new="['$KB_PATH']"
    else
      new="${cur%]}, '$KB_PATH']"
    fi
    gsettings set "$BASE" custom-keybindings "$new"
  fi
  gsettings set "$BASE.custom-keybinding:$KB_PATH" name 'Read Aloud selection'
  gsettings set "$BASE.custom-keybinding:$KB_PATH" command "$BIN"
  gsettings set "$BASE.custom-keybinding:$KB_PATH" binding "$BINDING"
  # verify
  got=$(gsettings get "$BASE.custom-keybinding:$KB_PATH" command | tr -d "'")
  [[ "$got" == "$BIN" ]] || die "gsettings command mismatch: got $got"
  echo "  bound $BINDING → $BIN"
elif have hyprctl && [[ -f "$HYP_DIR/bindings.lua" ]]; then
  HYP=1
  # Omarchy / Hyprland: append marker-guarded bind + tray autostart (idempotent).
  # SUPER+CTRL+S is Omarchy's "Share", so use a different key; M = mouth.
  HYP_KEY='SUPER + CTRL + M'
  if ! grep -q 'ReadAloud' "$HYP_DIR/bindings.lua"; then
    cat >> "$HYP_DIR/bindings.lua" <<EOF

-- ReadAloud: speak the selected text (added by install.sh)
-- Note: Omarchy binds SUPER CTRL + S to "Share" — this uses a different key.
o.bind("$HYP_KEY", "ReadAloud", "$BIN")
EOF
    echo "  hyprland: added $HYP_KEY → $BIN in $HYP_DIR/bindings.lua"
  else
    echo "  hyprland: ReadAloud already bound in $HYP_DIR/bindings.lua"
  fi
  if [[ -f "$HYP_DIR/autostart.lua" ]] && ! grep -q 'ReadAloud' "$HYP_DIR/autostart.lua"; then
    cat >> "$HYP_DIR/autostart.lua" <<EOF

-- ReadAloud tray: speaker icon in the bar (added by install.sh)
o.launch_on_start("$BIN --tray")
EOF
    echo "  hyprland: added tray autostart to $HYP_DIR/autostart.lua"
  fi
  if have omarchy && omarchy menu keybindings --print 2>/dev/null | grep -F "$HYP_KEY" | grep -qv ReadAloud; then
    echo "  warn: $HYP_KEY is already bound to something else — edit $HYP_DIR/bindings.lua"
  fi
  hyprctl reload >/dev/null 2>&1 || true
  echo "  bound $HYP_KEY → $BIN (hyprland)"
else
  echo "  warn: gsettings/GNOME media-keys not available — bind a hotkey yourself to:"
  echo "        $BIN"
fi

if [[ "$HYP" == 1 ]]; then
  # Hyprland autostarts the tray via autostart.lua — drop the GNOME entry, else
  # both fire after a reboot and two wiggle listeners cancel each other out
  rm -f "$AUTOSTART"
  echo "  autostart → $HYP_DIR/autostart.lua (GNOME .desktop removed)"
else
  mkdir -p "$(dirname "$AUTOSTART")"
  cat > "$AUTOSTART" <<EOF
[Desktop Entry]
Type=Application
Name=ReadAloud tray
Comment=Top-bar controls for ReadAloud (speed, engine, wiggle feel)
Exec=$BIN --tray
X-GNOME-Autostart-enabled=true
NoDisplay=true
EOF
  # verify desktop Exec points at a real binary
  exec_bin=$(grep -E '^Exec=' "$AUTOSTART" | head -1 | cut -d= -f2- | awk '{print $1}')
  [[ -x "$exec_bin" ]] || die "autostart Exec not executable: $exec_bin"
  echo "  autostart → $AUTOSTART"
fi

# Inflect local neural voice
if [[ "${READALOUD_SKIP_INFLECT:-}" != "1" ]]; then
  echo "==> Inflect voice (set READALOUD_SKIP_INFLECT=1 to skip)"
  if "$BIN" --install-inflect; then
    # warm so the first speak is not a 30s silent load
    "$BIN" --engine-check >/dev/null || true
  else
    echo "  Inflect install skipped/failed — tray → Inflect → Install later"
    echo "  or: $BIN --install-inflect"
  fi
else
  echo "==> skipping Inflect (READALOUD_SKIP_INFLECT=1)"
fi

# restart tray (and stray one-shot speakers from a previous broken run)
echo "==> tray"
BIN="$BIN" python3 - <<'PY'
import os, signal, subprocess, time
bin_path = os.environ["BIN"]
out = subprocess.check_output(["ps", "-eo", "pid,args"], text=True)
killed = []
for line in out.splitlines():
    if "ReadAloud" not in line:
        continue
    # our published binary, or any tray/wiggle speaker process
    if bin_path in line or "--tray" in line or ( "--wiggle" in line and "ReadAloud" in line):
        try:
            pid = int(line.split(None, 1)[0])
            if pid == os.getpid():
                continue
            os.kill(pid, signal.SIGTERM)
            killed.append(pid)
        except (ProcessLookupError, ValueError, PermissionError):
            pass
print("  stopped:", killed or "(none)")
time.sleep(0.5)
PY

# if the input group was granted but this login session predates it, sg gives the
# tray mouse access (wiggle) right now instead of waiting for the next login.
# Tray output goes to a log, not /dev/null — silent wiggle failures are miserable to debug.
TRAY_LOG="$SHARE/tray.log"
if grep -q "^input:.*\b${USER}\b" /etc/group 2>/dev/null && ! id -nG | tr ' ' '\n' | grep -qx input; then
  if have sg; then
    sg input -c "setsid '$BIN' --tray >>'$TRAY_LOG' 2>&1 < /dev/null &"
  elif have newgrp; then
    # Arch / Omarchy ship no sg but do have newgrp — feed it the command on stdin
    newgrp input <<EOF
setsid '$BIN' --tray >>'$TRAY_LOG' 2>&1 < /dev/null &
EOF
  else
    echo "  warn: no sg/newgrp — tray starts without wiggle until you log out and back in"
    setsid "$BIN" --tray >>"$TRAY_LOG" 2>&1 < /dev/null &
  fi
else
  setsid "$BIN" --tray >>"$TRAY_LOG" 2>&1 < /dev/null &
fi
echo "  tray log: $TRAY_LOG"
sleep 0.4
if ps -eo args= | grep -F -- "$BIN --tray" | grep -vq grep; then
  echo "  tray running"
else
  echo "  warn: tray may not have started — try: $BIN --tray"
fi

echo
echo "Done."
echo "  Speak:  $BINDING  (or wiggle the mouse over a selection)"
echo "  Tray:   top-bar speaker → Speed / Engine / Wiggle feel (Sensitive…Stubborn)"
echo "  Check:  $BIN --engine-check"
echo "  Config: $SETTINGS"
if ! id -nG | tr ' ' '\n' | grep -qx input; then
  if grep -q "^input:.*\b${USER}\b" /etc/group 2>/dev/null; then
    echo "  Wiggle: input group is set — log out/in once so this session picks it up"
  else
    echo "  Wiggle: sudo usermod -aG input \$USER   (then log out and back in)"
  fi
fi
