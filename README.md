# Windows Soundboard

A soundboard built for Windows, from the ground up: WASAPI playback into a virtual
audio cable, global hold-to-play hotkeys, and a native Fluent UI that goes quiet
when you do.

> Status: early but usable. Library, hotkeys, playback, trigger and play modes,
> microphone passthrough, loudness levelling, the tray icon, an Options window and
> global control hotkeys all work. Next: the first tagged release.

## Why another soundboard

The original Linux soundboard this project takes its behaviour from is a GTK4 +
libadwaita application, and its Windows port had to drag ~80 MB of GTK runtime
along with it. This is a clean-room rewrite on the Windows stack itself: C#,
WinUI 3 and the Windows App SDK, with NAudio driving WASAPI. No GTK, no Electron,
no bundled browser.

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
5. Cycle the **play mode** button: **Default** stops at the end, **Loop** repeats the
   sound until it is stopped, **Continue** starts the next sound when one finishes.
   **Stop all** (button or its hotkey) cuts everything.

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
| **Playback** | Trigger mode, play mode, output device, output volume, loudness boost (0–30 dB), loudness levelling, mute output |
| **Microphone** | Passthrough on/off, source device, microphone volume, latency profile (400 / 200 / 100 ms buffers) |
| **Hotkeys** | Record/Clear for *Play / Pause*, *Stop All*, *Previous Sound*, *Next Sound*, *Mute Output*, *Mute Real Mic*, *Cycle Trigger Mode*, *Cycle Play Mode*; shared sound hotkeys and what a shared key plays |
| **Tray** | Show a tray icon, close button hides the window |
| **About** | Version, supported formats, paths to the config and the log |

Every change applies immediately. Control hotkeys are global and swallow their
combination, exactly like sound hotkeys.

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
