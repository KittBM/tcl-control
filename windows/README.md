# TCL Control (Windows)

WPF (.NET 8) port of the Android app. Talks to the TCL S45H soundbar over Bluetooth LE using the WinRT `Windows.Devices.Bluetooth` APIs. Same protocol as the Android app — see the root [PROGRESS.md](../PROGRESS.md) for the reverse-engineered frame format and attribute map.

## Status

Working end-to-end against the real TCL S45H — connect and live control (volume, etc.) confirmed. See [PROGRESS.md](../PROGRESS.md) for the full debugging trail; the short version: connect via a **scanned** address rather than the hardcoded identity address, and the app fetches all GATT services/characteristics unfiltered and matches UUIDs in code, since the WinRT UUID-filtered lookup overloads proved unreliable right after connecting.

## Requirements

- Windows 10 version 1809 (build 17763) or later, with a Bluetooth LE radio
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows targeting components — a plain `dotnet-sdk` install includes these; no separate "Desktop development with C++"/UWP workload needed)

## Building and running

```bash
cd windows/TclControlWin
dotnet build
dotnet run
```

## Project layout

```
TclControlWin/
  TclProtocol.cs        # Frame encode/decode, checksum, attribute constants (ported from Android)
  TclSoundbarBle.cs      # GATT connect/send/notify + BLE advertisement scan (WinRT)
  AppSettings.cs          # Last-connected device persistence (%AppData%\TclControlWin\settings.json)
  MainWindow.xaml(.cs)    # Main UI: volume, mute, source, sound mode, bass/treble, atmos, log
  DeviceListWindow.xaml(.cs) # Scan-results picker dialog
  App.xaml(.cs)
  TclControlWin.csproj
```

## Usage

- **On launch, the app auto-connects in the background**: it repeatedly BLE-scans (15s per attempt) for an advertisement named `tcl_B14S45H0_CCEC` and connects as soon as it's seen, no button press needed. It keeps retrying on disconnect too. This scans by *name* rather than a fixed address on purpose - see Known gaps below for why.
- The address field defaults to the known bonded address (`00:A4:1C:CD:CC:EC`) — click **Connect** for a direct attempt (stops auto-connect while you do this).
- **Scan** does a manual 8-second BLE advertisement scan and lets you pick a discovered device by name/address instead (also stops auto-connect).
- **Reconnect** reuses the last device that connected successfully (persisted across runs).
- Volume, mute, atmos, source, sound mode, bass and treble are all persisted too - the UI starts from the last value the soundbar *confirmed* (via its status echo), not hardcoded defaults, every time the app opens.
- Unlike the Android app's paired-device picker, this version connects directly by Bluetooth address/advertisement rather than enumerating OS-paired devices — simpler to implement, and works whether or not the PC has bonded with the soundbar at the OS level, as long as the GATT service doesn't require encryption.

## Known gaps vs. the Android app

- Auto-connect here works differently from (and doesn't reproduce) the Android version's unreliable `onCreate`-time connect — see Usage above. This side scans for the device by name in the background rather than dialing a fixed address at startup, which sidesteps the address-resolution problem entirely (see next point) instead of racing it.
- No OS-level paired-device list picker (see Usage above for why).
- The default address (`00:A4:1C:CD:CC:EC`, the Android-bonded identity address) may not resolve on a Windows PC that hasn't paired with the soundbar. This is why auto-connect and the background scan match by *advertised name* instead — the address a scan turns up is a rotating random address that isn't stable across sessions, so name matching is the only reliable target.

## Troubleshooting

If `dotnet build`/`dotnet run` reports "No .NET SDKs were found" even after installing the SDK, you may have an older runtime-only `dotnet.exe` earlier on PATH (e.g. under `C:\Program Files (x86)\dotnet\`) shadowing the real SDK install (`C:\Program Files\dotnet\`). Check with `dotnet --list-sdks`, and if empty, invoke the x64 one directly: `& "C:\Program Files\dotnet\dotnet.exe" build`.
