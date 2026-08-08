# TCL Control

Custom Android app that talks directly to a **TCL S45H** soundbar over Bluetooth LE, replacing the official TCL Home app. Built by reverse-engineering the BLE protocol from `btsnoop_hci.log` captures.

## Status

Android app works when connecting manually via the device picker. Auto-connect on cold app launch is currently unreliable — see [PROGRESS.md](PROGRESS.md) for the full investigation log and current blocker.

## Project structure

```
app/                      # Android app (Kotlin, Gradle)
  src/main/java/com/tclcontrol/app/
    TclProtocol.kt      # Frame encode/decode, checksum, attribute constants
    TclSoundbarBle.kt   # GATT connection manager (connect/disconnect/send/notify parsing)
    MainActivity.kt      # UI wiring, permissions, device picker, auto-connect/reconnect logic
  src/main/res/          # Layout, drawables, strings, theme
build.gradle.kts
settings.gradle.kts
gradle.properties

windows/TclControlWin/    # Windows app (C#, WPF, .NET 8) — see windows/README.md
```

## Requirements

- Android Studio (or Gradle CLI) with Android SDK, `compileSdk 35`
- JDK 21 (set via `org.gradle.java.home` in [gradle.properties](gradle.properties) — adjust the path for your machine)
- A device running Android 8.0+ (`minSdk 26`) with Bluetooth LE support
- A TCL S45H soundbar, previously paired/bonded with the device

## Building

```bash
./gradlew assembleDebug
```

Install to a connected device/emulator:

```bash
./gradlew installDebug
```

## BLE protocol summary

- Device name: `tclB14S45H0_CCEC`
- Bonded/identity address: `00:A4:1C:CD:CC:EC` (use the bonded address, not the address seen during a scan — see gotchas in PROGRESS.md)
- GATT service: `0000f500-0000-1000-8000-00805f9b34fb`
- Write characteristic (Write-Without-Response): `e49a25e0-f69a-11e8-8eb2-f2801f1b9fd1`
- Notify characteristic: `e49a28e1-f69a-11e8-8eb2-f2801f1b9fd1`

Full frame format, attribute map, and debugging gotchas (address types, transport selection, GATT client leaks) are documented in [PROGRESS.md](PROGRESS.md).

## Windows app

A WPF port lives in [windows/](windows/README.md), using the same protocol over WinRT's `Windows.Devices.Bluetooth`. It's untested — written on a machine without the .NET SDK installed, so it hasn't been built yet. See [windows/README.md](windows/README.md) for build steps and known gaps.

## Roadmap

- Fix unreliable auto-connect on cold launch (Android)
- Build-verify and test the Windows app on real hardware
- Power on/off control (not yet reverse-engineered)
