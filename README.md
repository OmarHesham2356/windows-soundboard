# Windows Soundboard

A soundboard built for Windows, from the ground up: WASAPI playback into a virtual
audio cable, global hold-to-play hotkeys, and a native Fluent UI that goes quiet
when you do.

> Status: early. The core loop works (library, hotkeys, playback, trigger modes)
> and so does microphone passthrough. Loudness normalisation and a tray icon are next.

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
3. Click a sound to play it, or give it a key with the keyboard button — press the
   key you want, Escape cancels.
4. Cycle the **trigger mode** button:
   - **Overlap** — presses layer another copy on top
   - **Stop & restart** — a press cuts and restarts the sound
   - **Play & hold** — the sound plays while the key is held and fades out on release

Bound keys are swallowed system-wide, so pressing a sound key does not type into
the game or chat you are talking in.

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
| Config | `Services/ConfigStore.cs` | Small JSON file, enum stored by name. |
| UI | `MainWindow.xaml` | WinUI 3, Mica backdrop, GridView entrance/reposition transitions, no render loop of our own. |

## Licence

MIT. This is a clean-room implementation that follows the *behaviour* of
[Linux-SoundBoard](https://github.com/germanua/Linux-SoundBoard) (a Linux GTK
soundboard) — no code is copied from it. VB-CABLE is third-party donationware by
VB-Audio and is downloaded from its own site, never redistributed here.
