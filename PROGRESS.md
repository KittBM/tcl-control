# TCL S45H BLE Control — Progress Notes

## Goal
Replace TCL Home app with a custom Android (and later Windows) app that talks
directly to the TCL S45H soundbar over BLE.

## Status: Android app works manually. Auto-connect on cold launch is unreliable — investigation paused here.

---

## What's done

### 1. Protocol reverse-engineered (from Android `btsnoop_hci.log` captures)
- Soundbar: **TCL S45H**, BLE name `tclB14S45H0_CCEC`
- Bonded/identity address: `00:A4:1C:CD:CC:EC` (use this one — see gotcha below)
- BLE GATT:
  - Service: `0000f500-0000-1000-8000-00805f9b34fb`
  - Write characteristic (Write-Without-Response): `e49a25e0-f69a-11e8-8eb2-f2801f1b9fd1`
  - Notify characteristic: `e49a28e1-f69a-11e8-8eb2-f2801f1b9fd1`
- Frame format: `52 43` ("RC" magic) + `type`(1: req=0x01/resp=0x02) + `seq`(u16 LE, always 1 for control commands) + `checksum`(u16 LE) + `06 00` + `attrId`(u16 LE) + `value`(u16 LE, low byte signed, high byte 0x00) + `00`
  - **`checksum` field is NOT a sequence counter** — it's `sum(06,00,attrIdLo,attrIdHi,valueLo,valueHi,00) mod 256`. Sending an arbitrary/incrementing value here gets silently NACKed by the device (ack payload `01 ff` instead of `01 aa`). This was the first big bug — cost the most debugging time.
  - Status notifications echo the same shape with `attrId = setAttrId + 0x82`.
- Confirmed attribute map:
  | attr | function | notes |
  |---|---|---|
  | 0x02 | Volume | absolute level |
  | 0x0e | Mute | 0/1 |
  | 0x0f | Source | 1=HDMI, 2=Optical, 3=AUX (user-confirmed) |
  | 0x11 | Sound Mode | 1=Music, 2=Voice, 3=Movie (user-confirmed) |
  | 0x04 | Bass | signed, -6..+6 |
  | 0x05 | Treble | signed, -6..+6 |
  | 0x12 | Dolby Atmos / DTS Virtual:X toggle | 0/1 |
  | 0x01 | Get status (poll) | value always 0 |
  | 0x18 | Get device info | returns ASCII firmware string |
  - **Power on/off**: not captured yet — no distinct command found in the session. Toggling power may just drop the BLE link rather than being a GATT attribute. Not implemented in the app.

### 2. Android app (`/Volumes/ExSSD/tcl-control`, Kotlin, Gradle)
- `TclProtocol.kt` — frame encode/decode, checksum, attribute constants.
- `TclSoundbarBle.kt` — GATT connection manager (connect/disconnect/send/notify parsing).
- `MainActivity.kt` — UI wiring, permissions, device picker (bonded list + BLE scan), auto-connect/reconnect logic.
- UI redesigned as dark card-based layout (Material Components): Volume (+/- and big number), Mute switch, Source/Sound Mode as segmented toggle groups, Bass/Treble sliders, Atmos switch, connection status dot, scrollable debug log panel.
- Verified working end-to-end manually: connect via "Paired" picker → Volume/Mute/Source/Sound Mode/Bass/Treble/Atmos all send commands and the soundbar echoes correct status back (confirmed via live btsnoop capture, ack byte `01 aa` = accepted).

### 3. Gotchas hit and fixed along the way
- **Gradle/JDK mismatch**: system default JDK was 26 (too new for AGP 8.7.3's jlink step). Fixed by pinning `org.gradle.java.home` to a JDK 21 install in `gradle.properties`.
- **BLE address type**: the address seen during a BLE *scan* (`FF:A4:1C:CD:CC:EC`) is a random address and is NOT the same as the bonded identity address (`00:A4:1C:CD:CC:EC`). `BluetoothAdapter.getRemoteDevice()` assumes a public address unless resolved through bonding, so connecting with the scanned address silently fails. Always use the bonded address for direct connect.
- **Transport selection**: the soundbar also exposes classic Bluetooth (A2DP, for audio streaming) under the same address. `connectGatt()` without an explicit transport can pick BR/EDR and never find the GATT service. Fixed by passing `BluetoothDevice.TRANSPORT_LE` explicitly.
- **GATT client leak**: never closing the previous `BluetoothGatt` object before reconnecting leaks a client interface in the BT stack; enough leaked interfaces make *all* subsequent connection attempts fail at the radio level (`GATT_Status 255` / `reason=0x00ff`), even to a device that isn't the problem. Fixed by always `gatt.close()`-ing before a new `connectGatt()` and on every disconnect.

---

## Current blocker (where we stopped)

**Auto-connect on cold app launch is unreliable.** Manual connect via the "Paired" device
picker consistently works within a couple seconds. But automatically calling
`ble.connect()` right at `onCreate()` (before the user taps anything) has failed
repeatedly in testing, even after the transport fix, the address fix, and the
GATT-leak fix + 2x retry with 600ms backoff.

Last observed failure mode (via `adb logcat`): `bta_gattc_conn_cback ... reason=0x00ff`
/ `bta_gattc_open_fail: Cannot establish Connection. Return GATT_Status(255)` —
this is a radio-level failure, not an app-level one. It's not yet confirmed whether:
- the soundbar simply isn't advertising/connectable at the moment the app launches
  (e.g. still settling from a previous session, or briefly unavailable), or
- there's a timing race between the Android BT stack finishing init after
  `force-stop`/relaunch and our connect attempt, or
- something OS/OEM-specific (this was tested on a Xiaomi/MIUI tablet, which is
  known for being idiosyncratic about background BLE auto-connect).

**Not yet tried:** waiting longer before the first attempt, checking
`BluetoothAdapter.getBondedDevices()`/adapter state before connecting, or comparing
against a plain manual connect issued immediately after a fresh app launch (to see
if the race is specific to *auto*-triggering at `onCreate` vs. a user-initiated tap
a few seconds later).

## Not started yet
- Windows app (user said "Android first"; protocol docs above should carry over directly — Windows BLE via WinRT `Windows.Devices.Bluetooth`, same UUIDs/frame format).
- Power on/off control (needs a dedicated capture: toggle power via TCL Home while logging, see if anything shows up before the BLE link drops).
