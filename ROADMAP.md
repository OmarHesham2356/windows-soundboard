# Roadmap

Where the project is going, in the order it is being built. Each item says why it
matters and how we know it is done, so an old decision can be re-read instead of
re-argued.

Shipped: **v0.1.0** — library, WASAPI playback into a virtual cable, global hotkeys
with combinations and control hotkeys, four trigger modes, per-sound play modes,
microphone passthrough, loudness levelling, local monitoring, tray, Options window,
single instance, elevated-app warning, screen-share routing checks.

Legend: **[next]** in progress · **[later]** planned · **[idea]** not committed yet.

---

## v0.2 — Clip shaping **[next]**

The soundboard can only play a file as-is. Most clips people download start with
half a second of silence, end with a tail, or are far too quiet compared to the
rest of the board; today the only fix is to open Audacity, which loses the point of
a soundboard.

- Trim start and end points, shown against a waveform of the file.
- Fade in and fade out.
- Per-sound gain, independent of the board's loudness levelling.
- Everything stored in the config: **the audio file is never modified**, so an edit
  can be reset exactly.

Done when a trimmed clip ends where it was trimmed (not merely stops being
	audible), fades are audible, gain survives a restart, and a reset restores the
	original file's playback byte for byte.

Progress: trim, fades, gain and the waveform dialog are on `main` (right-click a
sound → Edit clip…). A 6 s file trimmed to 1.0–2.5 s plays for 1.509 s, and a
looped clip wraps at the trim start again (a 1.0 s window wrapped at 1.002 s and
was still playing five seconds later). Still to do before this milestone closes:
verify the fades by ear.

## v0.3 — Speed, pitch and cutting **[later]**

- Playback speed / pitch control per sound (the chipmunk and slowed-reverb toys).
- Cut a section out of the middle of a clip.
- Drag the trim handles on the waveform instead of using sliders, plus zoom.

Cutting is the one edit that cannot be non-destructive with a plain offset model:
it needs an exported copy next to the original, with the original left untouched.

## v0.4 — A library that scales **[later]**

- Drag and drop files onto the window: copy into the sounds folder, skip duplicates.
- Search / filter box; sort by name, duration or date.
- Folders or tabs once a board passes a few dozen sounds.
- Multi-select with bulk actions (hotkey, play mode, gain, remove).

A flat grid of cards is fine for twenty sounds and unusable for two hundred; this
is the point where daily use starts to hurt.

## v0.5 — Never lose a moment **[idea]**

- Record from a microphone or from the cable straight into the library.
- A rolling instant-replay buffer: one hotkey saves the last N seconds.

A board that fills itself from what just happened in a call is a different product
from one that must be curated by hand, and it pairs naturally with trimming.

## v0.6 — Contexts and inputs **[later]**

- Profiles: per-game hotkey sets, switched from the tray.
- Mouse button bindings (the five-button mice most players already own).
- A hotkey overview listing every binding, with conflict detection.

## v0.7 — Trust and operations **[later]**

- Reopen automatically when an audio device disappears and comes back.
- Atomic config writes with a `.bak`, plus export/import of settings.
- "Copy diagnostics" for bug reports (log, config, device list — no secrets).
- Update check against GitHub Releases.

## v1.0 — Ready for strangers **[later]**

- Installer (MSI or similar) and code signing, so Windows stops warning.
- First-run setup: pick the cable, pick the monitoring device, verify both.
- Documentation with screenshots and a short video.

---

## Not doing

- An effects rack, EQ, reverb or multitrack. Free DAWs already do this better and
  every knob becomes a permanent support surface.
- Destructive editing of the user's audio files. Edits stay in the config; the only
  exception is an explicitly exported copy when cutting.
- Cloud sync, accounts, mobile clients.

## Known debts

- Loudness is measured over the whole file; a trimmed clip should be measured over
  the part that is actually played.
- First launch still waits on full device enumeration (~4 s) because no output is
  saved yet; later launches open audio in ~150 ms.
- No automated audio tests in CI — loop, trim and mode behaviour are verified with a
  local harness that drives the real app and reads its log.
- The release zip is ~100 MB because the runtime is bundled; a framework-dependent
  build would be ~40 MB if that ever matters.
