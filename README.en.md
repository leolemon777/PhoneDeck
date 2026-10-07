# Yandu (言渡)

**Turn a spare Android phone into a wireless microphone and shortcut pad for your computers.**

Speak into the phone and the text appears on your computer. One phone can drive several Windows and Mac computers; swipe to switch between them. You can also share the microphone with several computers at once, each starting and stopping with its own hotkey.

[中文](README.md) · [Download](https://github.com/leolemon777/PhoneDeck/releases) · [Known issues](docs/release/KNOWN_ISSUES.md) · [Contributing](CONTRIBUTING.md)

> **Status: technical preview.** The core path works on the author's devices, but it has not yet been through clean installs by outside users. See [Verification](#verification) for what has been tested on real hardware. Issues are welcome.

The goal is to use a phone you already own instead of buying a separate microphone or a headset for its microphone. Any cost of your chosen dictation app is separate.

| Idle | Dictating | Shared mic |
|---|---|---|
| ![Idle](docs/images/android-home-light.png) | ![Dictating](docs/images/android-recording.png) | ![Shared microphone](docs/images/android-shared.png) |

## How it works

```text
Phone mic ──Wi-Fi / USB──▶ Yandu receiver ──virtual audio device──▶ your dictation app (Typeless, …) ──▶ text
                                  └──────▶ allow-listed shortcuts: copy, paste, /plan, Enter…
```

Yandu **does not do speech recognition**. It carries the phone's audio into the computer and presses your dictation app's hotkeys for you; the dictation app you already use does the recognition.

- **Three ways to talk:** tap (tap to start, tap again to stop), hold (push to talk), and shared (the mic stays on; each computer uses its own hotkey).
- **Three computer modes:** dictate, translate and ask, depending on what the chosen dictation app supports.
- **Several computers:** computers on the same Wi-Fi appear on the phone's home screen. Tap one, check that the four-digit code matches, and click Allow on the computer. No QR codes.
- **Shortcut pad:** copy, paste, screenshot, F1 and so on, plus text commands for AI coding tools such as `/plan`, `/goal` and `/compact`. All of it is editable on the phone.
- **Eight color themes.**

## What you need

| | Requirement |
|---|---|
| Phone | Android 8.0 or later |
| Computer | Windows 10/11 x64, or macOS 14.2 or later (Apple Silicon; build it yourself for Intel) |
| Dictation app | [Typeless](https://www.typeless.com/) by default (paid). Profiles for Doubao and WeChat input methods are built in; other apps can be added with a JSON profile, see the [voice engine guide](docs/guides/VOICE_ENGINES.md) (Chinese) |
| Virtual audio device (free) | Windows: [VB-CABLE](https://vb-audio.com/Cable/); Mac: [BlackHole 2ch](https://github.com/ExistentialAudio/BlackHole) |
| Network | Phone and computer on the same Wi-Fi, or a USB cable |

You install the dictation app and the virtual audio device yourself; this project does not bundle them.

## Install

### 1. Computer

**Windows**

1. Install VB-CABLE and reboot. In your dictation app, set the microphone to `CABLE Output`.
2. Unzip the Windows receiver. Right-click `Enable-PhoneDeckLan.ps1` and choose "Run with PowerShell" to open the LAN ports.
3. Double-click `PhoneDeck.ControlCenter.exe`. A Yandu icon appears in the tray. You're ready when every item in the status window's checklist is ticked.

**Mac**

1. Install BlackHole 2ch. In your dictation app, set the microphone to `BlackHole 2ch`.
2. Drag `PhoneDeck Receiver.app` into Applications and open it. It is not notarized yet, so the first time you need to right-click it and choose Open.
3. In System Settings → Privacy & Security → Accessibility, allow PhoneDeck Receiver. It needs this to press the dictation app's hotkeys. A local status page opens in your browser; you're ready when every item is ticked.

### 2. Phone

1. Install the APK and open Yandu.
2. On the same Wi-Fi, a "Nearby · tap to connect" card appears for each computer. Tap it, check that the phone and the computer show the same four-digit code, then click Allow on the computer.
3. Hold the 按住 说话 (hold to talk) bar near the bottom and speak; release to finish. In tap mode, tap once to start and again to stop.

If the phone and computer are on different subnets (for example behind a second router), use "Add computer → Enter address" and type the computer's LAN IP. USB also works: turn on USB debugging on the phone and install [Android platform-tools](https://developer.android.com/tools/releases/platform-tools) (`adb`) on the computer. The receiver sets up and keeps an `adb reverse` tunnel.

## Verification

"It builds" and "it was tested on a real device" are different things. This table lists only the latter.

| Platform | Tested on real hardware | Not yet tested |
|---|---|---|
| Android | Tap and hold dictation, stop sync, shared mic on/off, dark theme, offline-computer hints, landscape layout and system-bar spacing on a Samsung phone | Other phones, long shared sessions, font scaling and accessibility |
| Windows | The new tray, nearby pairing with Allow, dictation and shortcuts (two PCs) | High-DPI scaling, start at login |
| macOS | The slim native receiver: dictation with Accessibility granted, status page, USB and Wi-Fi, nearby pairing | Translate / ask one by one, Intel |
| Several computers | One phone with a Mac and two Windows PCs, discovered on the same Wi-Fi, switched by swiping | Shared mic feeding several computers at once |
| iPhone | — | No native client yet; there is an experimental web version |

## Privacy

- **Your voice only goes to your own computer.** Audio travels over your LAN or USB to your computer and is handed to the dictation app you chose. Yandu has no cloud recognition, no telemetry and no crash reporting.
- **Nothing is recorded by default.** Neither the phone nor the computer stores audio. The computer keeps only a SHA-256 hash of each phone's credential.
- **The LAN link is encrypted.** Wi-Fi traffic uses HTTPS and pins the computer's certificate on first connection. The only unauthenticated endpoint listens on `127.0.0.1`.
- Your dictation app's own privacy policy applies to whatever it does with the audio.
- Please report security issues privately, see [SECURITY.md](SECURITY.md).

## Build from source

You need the .NET 10 SDK (version pinned in `global.json`), JDK 17 and the Android SDK.

```bash
# Android
cd work/phone-deck/android && ./gradlew :app:assembleDebug :app:testDebugUnitTest :app:lintDebug

# Windows receiver (add -p:EnableWindowsTargeting=true when cross-compiling on Mac/Linux)
dotnet test work/phone-deck/windows/PhoneDeck.Server.Tests/PhoneDeck.Server.Tests.csproj -c Release
dotnet publish work/phone-deck/windows/PhoneDeck.ControlCenter/PhoneDeck.ControlCenter.csproj \
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# macOS receiver (native AOT, about 21 MB)
dotnet test work/phone-deck/macos/PhoneDeck.Receiver.Tests/PhoneDeck.Receiver.Tests.csproj -c Release
zsh scripts/macos/Build-PhoneDeckReceiver.sh
```

Besides the main path (native app + receivers), the repository has two experimental routes: a phone web app and a 2.0 desktop app that runs Whisper locally and needs no dictation app. Both are less mature than the main path.

## Security boundaries

- The phone cannot make the computer run arbitrary shell, PowerShell or CMD commands.
- Keys, key chords (at most four keys), macros and text length are checked on the computer against an allow-list.
- The LAN endpoint uses HTTPS, per-phone credentials, certificate pinning and target computer ID checks. A computer can revoke any phone at any time.

## Contributing

Issues and pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md). AI coding agents working in this repository should read [AGENTS.md](AGENTS.md) first. Most documentation is in Chinese; English contributions are welcome.

## License

[MIT](LICENSE). Shortcut and process names in the voice engine profiles come from public sources. Typeless, Doubao, WeChat, VB-CABLE, BlackHole and other names are trademarks of their respective owners.

## Community

Thanks to the [LINUX DO](https://linux.do/) community for providing a place to share and discuss open-source projects. Feedback and contributions to Yandu are welcome.
