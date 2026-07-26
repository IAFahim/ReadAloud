# ReadAloud — upgrade ideas (ranked)

Brainstormed by an Opus 5 agent, 2026-07-27. Build order: 1, 2, 3 first; 4 is a
three-hour tax cut that makes everything better.

## 1. Speech daemon with a queue (`readaloud --daemon`) — ⭐ BUILD FIRST · ~1 day
Right now every trigger cancels the previous speech, so a second agent finishing wipes the first mid-sentence, and there is no pause, rewind, or "skip this one". A tiny always-on process with a priority queue turns speech into a channel you can steer — and every idea below plugs into it.
*Build:* `UnixDomainSocketEndpoint` at `$XDG_RUNTIME_DIR/readaloud.sock`, one `Channel<Utterance>` consumer, verbs `speak/stop/skip/pause/prev/next/priority`. Talk to speech-dispatcher over raw SSIP (TCP localhost:6560, plain-text `SPEAK`/`INDEX MARK` protocol) instead of spawning a process per call — index marks give position tracking for ideas 8 and 10. `systemd --user` unit; the existing binary becomes a thin client.

## 2. Agent soundstage — Piper voices + stereo position per agent — ⭐ BUILD FIRST · ~1 day
Espeak/robot voices are fatiguing after ten minutes; Piper gives free local neural voices pleasant for hours. Give each concurrent Claude agent its own voice **and** its own pan position (main agent centre, subagent 1 left ear, subagent 2 right) and a six-agent session becomes something you can follow by ear alone.
*Build:* `piper --model en_US-lessac-medium.onnx --output_raw` piped to `pw-cat --playback --format s16 --rate 22050 -` (PipeWire is already there). Pan/pitch via `ffmpeg -af "pan=stereo|c0=..,c1=..,asetrate=..."`, or register Piper as an `sd_generic` module. Voice index = `agentName.GetHashCode() % voices.Length` — stable per agent, zero config.

## 3. Gist first, full text on demand — ⭐ BUILD FIRST · ~2 days
The real win is not reading faster, it is reading less: speak a two-sentence "what happened / what you need to do" instead of a 600-word reply, with the full text one keypress away. Biggest single cut to daily reading load — 80% of agent output is confirmation you only need the shape of.
*Build:* ponytail version first, zero deps — speak first sentence + any line starting with a verb + the last sentence, full text on request. Then upgrade: `llama-server` (llama.cpp, Qwen3-1.7B-Q4 GGUF, ~1GB, CPU-fine) on localhost:8080, one `HttpClient` POST to `/completion` with "two sentences, imperative, no code", cache by SHA of input; if the socket refuses, speak the raw text. Never block on the model.

## 4. Code-aware pronouncer (`lexicon.tsv`) — ~3 hours, highest value per hour
`kCameraLTW`, `m_projSettings`, GUIDs and hex read aloud are noise you have to mentally re-decode. Split camelCase, expand your own jargon (LTW → "local to world", ECB, SubScene, `→` → "becomes"), spell GUIDs as characters, speak paths as "…Scripts, PlayerSystem dot C S".
*Build:* one more regex pass in `Clean()` plus a user-editable `~/.config/readaloud/lexicon.tsv` (`pattern<TAB>spoken`) reloaded on file change; SSIP/SSML `<say-as interpret-as="characters">` for hashes.

## 5. Live agent room — narrate mid-turn, unmute the subagents — ~1 day
The Stop hook skips sidechains, so subagents are silent and the main agent only speaks after the whole turn — you sit watching a spinner. Speaking the text before each tool call, in that agent's voice, turns a session into overheard colleagues: "reading PlayerSystem… found the double writer… editing".
*Build:* `SubagentStart`, `SubagentStop`, `TeammateIdle`, `PermissionRequest`, `PostToolUse` hooks are already wired in settings.json. `PostToolUse` → last text block from `transcript_path` → daemon with `source=<agent name>` (idea 2 picks the voice). Dedupe by hash so repeated blocks don't re-speak.

## 6. Earcons — the session soundtrack — ~4 hours
A 120ms click for a file read, a wooden knock for an edit, rising two-tone for tests green, falling for red, a chime when an agent spawns, a thud when it dies. Monitor a 40-minute run ambiently with zero reading effort; tune in only when the sound is wrong.
*Build:* pregenerate ~8 wavs with `ffmpeg -f lavfi -i "sine=frequency=660:duration=0.09"` + fade, `paplay` from the hooks (or through the daemon so they duck the voice). Pitch-shift per agent with `asetrate` so you hear which agent just edited.

## 7. "Just say the errors" — build/test/Unity-log distiller — ~1 day
Unity and dotnet build produce thousands of lines to hide the four that matter. A mode that speaks "three errors. CS0246, unknown type FlinchProbe, PlayerSystem line 42. Two more" collapses the worst reading task of the day into one sentence.
*Build:* `readaloud --watch <file>` with `FileSystemWatcher` + seek-to-end tail; regex `error CS\d+` / `Assets/.*\((\d+),(\d+)\)` / NUnit `Failed:`; speak count + top 3 + earcon, suppress duplicates. Point it at Editor.log permanently as a user service.

## 8. Sentence scrubber — rewind, repeat, spell that — ~1 day (needs idea 1)
Every dyslexic re-reads; TTS with no rewind forces replaying the whole block. Super+Left/Right to step sentences, Super+Up to repeat, hold to spell the current word, +/- to slow just that sentence.
*Build:* daemon splits input into sentences and holds an index; SSIP `INDEX MARK` events report which is playing. Four GNOME shortcuts bound to `readaloud prev|next|repeat|spell`, each a socket write.

## 9. Push-to-talk dictation (whisper.cpp) — ~1-2 days
Typing prompts and commit messages while dyslexic is the other half of the tax. Hold a key, talk, get clean text on the clipboard.
*Build:* `pw-record --rate 16000 --channels 1` while held → `whisper-cli -m ggml-small.en.bin -nt -` (CPU real-time) → `wl-copy` + `notify-send`. Skip auto-typing: ydotool/wtype aren't installed and need a uinput daemon — clipboard + Ctrl+V is the lazy path that works today on GNOME Wayland.

## 10. Karaoke overlay — see and hear the same sentence — ~2 days, biggest wow
A borderless always-on-top window showing only the sentence being spoken, in huge Atkinson Hyperlegible / OpenDyslexic type, advancing with the voice. Dual-channel (audio + one short line, never a wall) is the format dyslexic readers actually retain.
*Build:* GTK4 via the `GirCore.Gtk-4.0` NuGet (works on .NET 10, stays C#), undecorated + always-on-top, one `Label`. Sync off the same SSIP index marks as idea 8. Colour the border by agent (idea 2) so you see who is talking.

## 11. OCR the unselectable — ~1 day
Unity's Inspector, error modals, game view, PDFs and screenshots have no text selection at all. Drag a box, hear its contents.
*Build:* `tesseract` is already installed. Grab the region through the XDG portal — `org.freedesktop.portal.Screenshot` with `interactive:true` gives GNOME's own region picker — then `tesseract shot.png - --psm 6` → daemon. Fall back to reading an image path from the clipboard.

## 12. Spoken diffs — review your agents by ear — ~1 day
"PlayerSystem.cs: two hunks. Removed the null check at line 42. Added a guard in OnUpdate." Review while walking around instead of reading every diff or rubber-stamping.
*Build:* `git diff --stat` for the shape, per-hunk headers + changed lines through the pronouncer (idea 4), optionally one line per hunk from the local model (idea 3). Earcon per file boundary. `readaloud --diff [ref]`.

## 13. Structure before body — ~3 hours
Before committing to a long doc, hear its skeleton: "four sections, twelve hundred words, six minutes. One: netcode seam. Two: combat chain…" then jump straight to the section you need with Super+1..9.
*Build:* markdown/heading regex + word count over the selection or a file path, speak the outline, keep sections in the daemon's queue indexed so number keys seek. Zero new dependencies.

## 14. Read queue + offline export — ~half a day
Let a modifier *append* instead of interrupt, so three agent replies, a diff and a doc pile up and play as one playlist — and dump that playlist to an mp3 for away from the desk.
*Build:* daemon verb `enqueue` vs `speak`, second GNOME shortcut. Export = Piper (idea 2) into `ffmpeg -c:a libmp3lame ~/readaloud/session-$(date +%F).mp3`.

## 15. Urgent lane — "an agent needs you" — ~2 hours
The failure mode of many agents is one silently blocking on a permission prompt. A jump-the-queue spoken cue in that agent's voice ("worktree agent wants to run git push") makes idle agents free to leave unattended.
*Build:* `PermissionRequest` and `TeammateIdle` hooks already fire — send `priority=urgent` to the daemon, which ducks the current utterance, plays a distinct earcon, speaks one line, resumes. `notify-send` fallback when the daemon is down.
