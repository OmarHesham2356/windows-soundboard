# Windows Soundboard

A soundboard built for Windows, from the ground up: WASAPI playback into a virtual
audio cable, global hold-to-play hotkeys, and a native Fluent UI that goes quiet
when you do.

> Status: **v0.1.0**, the first tagged release. Library, hotkeys, playback, trigger
> and per-sound play modes, microphone passthrough, loudness levelling, local
> monitoring, the tray icon, an Options window and global control hotkeys all work.
> Editing clips, recording, profiles and faster starts are planned for later
> releases.

## Why another soundboard

The original Linux soundboard this project takes its behaviour from is a GTK4 +
libadwaita application, and its Windows port had to drag ~80 MB of GTK runtime
along with it. This is a clean-room rewrite on the Windows stack itself: C#,
WinUI 3 and the Windows App SDK, with NAudio driving WASAPI. No GTK, no Electron,
no bundled browser.

## Download

Grab `WindowsSoundboard-win-x64.zip` from the [latest release](https://github.com/OmarHesham2356/windows-soundboard/releases/latest), unzip it anywhere and run `WindowsSoundboard.exe`. It is self-contained: no
.NET install and no SDK are needed. `SHA256SUMS.txt` is attached to the same release
if you want to verify the download.

You still need VB-CABLE (below) for other apps to hear the board.

## Requirements

- Windows 10 1809 or newer (Windows 11 recommended)
- [VB-CABLE](https://vb-audio.com/Cable/) — Windows has no built-in virtual
  microphone, so the soundboard plays into a virtual cable that other apps can
  listen to. Install it, reboot, then pick `CABLE Input (VB-Audio Virtual Cable)`
  as the output in the app.
- .NET SDK 10 to build from source

## Using it

1. Put audio files (mp3, wav, ogg, flac, m4a, aac, wma, aiff) in your sounds
   folder. Default: `%USERPROFILE%\Music\Windows Soundboard`; change it with
   **Sounds folder**.
2. Pick your output. The app prefers a virtual cable automatically and marks it
   in the list.
3. Click a sound to play it, or give it a key: click the keyboard button on the
   card, or right-click the card and pick **Set hotkey…**. The dialog records the
   next combination you press — `Ctrl+Shift+F1` works, not just single keys —
   **Save** assigns it, **Clear** removes it, Escape cancels recording.
4. Cycle the **trigger mode** button:
   - **Play & Overlap** — presses layer another copy on top
   - **Play & Stop** — a press cuts and restarts the sound
   - **Push to Loop** — the sound loops while the key is held
   - **Play & Hold** — the sound plays once while the key is held and fades out on release
5. Play modes are **per sound**: right-click a card → **Play mode** → *Play once*,
   *Loop* or *Continue*. A loop repeats until it is stopped; *Continue* starts the
   next sound in the board when this one ends. Sounds without an override follow
   the board default in **Options → Playback**, and the card shows a `loop` /
   `continue` badge. **Stop all** (button or its hotkey) cuts everything.

Bound keys are swallowed system-wide, so pressing a sound key does not type into
the game or chat you are talking in.

Closing the window **hides it to the tray** — hotkeys keep firing while the board
is out of the way. The tray icon menu has *Show/Hide*, *Mute* and *Exit*; muting
silences the cable and the microphone passthrough without losing the volume setting.

## Options

The gear button opens **Options**, which mirrors the settings of the Linux
soundboard this project follows:

| Page | What is there |
| --- | --- |
| **General** | Sounds folder (change, rescan, open), theme (follow Windows / dark / light), card or compact list layout |
| **Playback** | Trigger mode, board default play mode, output device, output volume, loudness boost (0–30 dB), loudness levelling, mute output, local monitoring device |
| **Microphone** | Passthrough on/off, source device, microphone volume, latency profile (400 / 200 / 100 ms buffers) |
| **Hotkeys** | Record/Clear for *Play / Pause*, *Stop All*, *Previous Sound*, *Next Sound*, *Mute Output*, *Mute Real Mic*, *Cycle Trigger Mode*, *Cycle Play Mode*; shared sound hotkeys and what a shared key plays |
| **Tray** | Show a tray icon, close button hides the window |
| **About** | Version, supported formats, paths to the config and the log |

Every change applies immediately. Control hotkeys are global and swallow their
combination, exactly like sound hotkeys.

## Screen shares, Discord and the virtual cable

Discord's **Share Audio** is not your microphone: it records what Windows plays
through the **default playback device** (a loopback), and it is a separate stream
from your voice. That has two consequences worth knowing:

- **Muting your microphone will not silence the soundboard in a screen share.**
  The board reaches the share because it is audible on the default playback
  device, not because of the mic.
- The board only stays out of a share when it does not play through the device
  Discord records. Keep the output on the virtual cable (as the app does by
  default) and leave Windows' **Listen to this device** off for `CABLE Output`;
  then the board is heard by the people in your call through the virtual mic and
  not by everyone watching the share. If you do want the board audible locally,
  Windows' *Listen* setting is what puts it — and therefore the share — back in
  the loop.

The app checks this for you: if its output **is** the Windows default playback
device it shows a warning bar and logs `screenShareLeak=yes`, because in that setup
Discord shares everything the board plays. **Mute Output** (tray menu or its hotkey)
silences the board wherever it is going.

### Keeping your own voice out of the share

Two things in this app could put *your voice* on a device a screen share records,
and both are now prevented:

1. **Mic passthrough is only applied when the output is a virtual cable.** That is
the only path other apps can hear. Pointed at a headset or speakers, mixing the
microphone in would simply play your voice back to you — and to anything recording
that device, such as a screen share. The app skips it and logs
`microphone not mixed in: output '…' is not a virtual cable`.
2. **Local monitoring never contains the microphone.** Turn on **Options →
   Playback → Local monitoring** and pick your headphones: sounds are played to
   the cable (so the call hears them) *and* to your headphones (so you do), while
   the microphone is only ever sent to the cable. Because of that you can turn
   Windows' *Listen to this device* off for `CABLE Output`, which is the other
   classic way a voice ends up on a device a share records.

With the board on the cable, monitoring on your headphones and Windows *Listen*
off, a screen share carries the game and the board but never your microphone.

### Discord's own screen-share audio device

Discord's share picker has an **Audio Device** option under Capture Devices, and
it defaults to your microphone, which sends the microphone through the share *as
well as* the call (it is not affected by the mute button). If you share a whole
screen, that setting can therefore add your voice to the stream. Setting it to a
silent or unused input, or sharing the **application window** instead of the
monitor, keeps the voice out of the share entirely.

The board is only in the share if it is audible on the device being recorded;
with monitoring on, that is your headphones, which is the same path your game
audio takes.

## Build

```powershell
dotnet build src/WindowsSoundboard/WindowsSoundboard.csproj -c Debug -p:Platform=x64
dotnet run --project src/WindowsSoundboard/WindowsSoundboard.csproj -p:Platform=x64
```

Self-contained release build (no .NET install needed on the target machine):

```powershell
dotnet publish src/WindowsSoundboard/WindowsSoundboard.csproj -c Release -p:Platform=x64 `
  -r win-x64 --self-contained true -o dist
```

## Where things live

- `%LOCALAPPDATA%\WindowsSoundboard\config.json` — folder, output device, volume, mode, hotkeys
- `%LOCALAPPDATA%\WindowsSoundboard\app.log` — what the app did last run

## Architecture

| Part | File | Notes |
| --- | --- | --- |
| Playback | `Services/AudioEngine.cs` | One WASAPI stream per run, a float mixer, per-sound fades. Overlap comes from many inputs on that mixer. |
| Microphone | `Services/MicPassthrough.cs` | WASAPI capture into a float buffer, mono→stereo, resampled only when the rates differ, mixed into the same output. |
| Hotkeys | `Services/HotkeyService.cs` | `WH_KEYBOARD_LL` hook on the UI thread; bound keys are swallowed; releases drive Play & Hold. |
| Library | `Services/SoundLibrary.cs` | Flat scan of the sounds folder; ids are relative paths so bindings survive moves of the folder. |
| Tray | `Services/TrayIcon.cs` | `Shell_NotifyIcon` on a message-only window: show/hide, mute, exit. No extra package. |
| Play modes | `Models/SoundItem.cs`, `Services/ConfigStore.cs` | Each sound carries its own mode; `PlayModes` overrides the board default per id. |
| Loudness | `Services/LoudnessAnalyzer.cs` | One decode pass per file, RMS against a target, cached by size and write time. |
| Config | `Services/ConfigStore.cs` | Small JSON file, enum stored by name. |
| UI | `MainWindow.xaml` | WinUI 3, Mica backdrop, GridView entrance/reposition transitions, no render loop of our own. |
| Options | `Views/SettingsWindow.xaml` | Six pages over one config; every edit is re-applied by the main window straight away. |
| Hotkey dialog | `Views/HotkeyDialog.xaml` | Records combinations through the same hook that fires them. |

## Licence

MIT. This is a clean-room implementation that follows the *behaviour* of
[Linux-SoundBoard](https://github.com/germanua/Linux-SoundBoard) (a Linux GTK
soundboard) — no code is copied from it. VB-CABLE is third-party donationware by
VB-Audio and is downloaded from its own site, never redistributed here.
