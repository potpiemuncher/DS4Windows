# VirtualDualSenseUsbip

User-space USB/IP device emulator for DS4Windows Phase 4. It exposes a wired
DualSense through usbip-win2's signed VHCI driver. The exact composite mode now
materializes Windows audio endpoints, accepts paced isochronous playback, and
relays native haptic channels 3/4 to the real Bluetooth controller. Controlled
M2.6 haptics and the corrected teardown path are live-validated on the primary
PC under USB/IP WPP tracing.

## Status

M2.3 HID/input/trigger relay and M2.4 composite enumeration are working on the
primary Windows 11 PC:

- usbip-win2 0.9.7.8 is qualified under Secure Boot and Memory Integrity.
- `usbip list` and `usbip attach` discover and materialize the virtual device.
- Windows binds `hidusb` and exposes a HID-compliant game controller.
- The device uses the captured 289-byte DualSense report descriptor and a
  derived 41-byte HID-only configuration descriptor.
- EP0 standard requests, pairing report `0x09`, firmware report `0x20`,
  interrupt IN, interrupt OUT capture, and UNLINK are live.
- Neutral interrupt-IN reports sustain 250 Hz; a 30-second run delivered 7,504
  reports at 250.1 Hz without a read failure.
- `--input bluetooth` reads authenticated report `0x31` frames from the real
  pad, maps their shared payload to wired report `0x01`, and supplies real
  calibration at runtime without logging or persisting hardware values. The
  live path has passed at 250.3 virtual USB reports/s with full stick/trigger
  ranges and exact button transitions.
- Native game output is relayed through a non-blocking latest-value worker that
  copies only the two 11-byte adaptive-trigger blocks into authenticated
  Bluetooth `0x31` reports. Black Flag Resynced recognized the virtual wired
  device, emitted R2 modes `0x05`/`0x22` and L2 mode `0x05`, and the user
  confirmed physical R2 resistance while firing.
- `--configuration composite` serves the exact captured 227-byte UAC1 + HID
  configuration. Windows starts the MEDIA child and creates healthy Speakers
  and Headset Microphone endpoints. The audio engine reaches playback
  interface 1 alt 1 and submits ISO OUT traffic.
- ISO completion is paced at ten 384-byte packets every 10 ms. A 105-second
  live run held 100.0 URBs/s and 375.1 KiB/s without disconnecting.
- Four-channel 48 kHz signed 16-bit playback is converted from channels 3/4 to
  3 kHz signed 8-bit stereo and sent in authenticated Bluetooth report `0x36`
  frames. The user felt the controlled haptic tone at about 7:20 PM.
- In neutral-input mode, feature report `0x05` intentionally stalls rather
  than returning invented sensor calibration.

Real game-authored channels 3/4 and one-hour stability still need validation.
Audible USB channels 1/2 are not forwarded by this spike.

**Teardown fix live-validated:** a composite removal at 7:24 PM on 2026-07-18
produced bugcheck `0xA` while Windows freed an IRP. An emulator race could let
successful UNLINK precede a late RET_SUBMIT. The atomic transfer-state fix was
subsequently exercised by two primary-PC active-ISO detaches with Active Memory
Dump and both USB/IP WPP providers armed. The traces reconciled 16,093/1,171
submits into normal completions plus exactly four once-canceled outstanding
requests per unplug. They contained no failed, duplicate, orphaned, or late
completion, no ISO error, no post-plug-out protocol traffic, and no trace loss.
The user accepted this evidence as fixed and lifted the primary-PC safety hold.

## Build and test

```powershell
dotnet build .\utils\VirtualDualSenseUsbip\VirtualDualSenseUsbip.csproj -c Release
dotnet run --project .\utils\VirtualDualSenseUsbip -- selftest
dotnet run --project .\utils\VirtualDualSenseUsbip -- devicetest
dotnet run --project .\utils\VirtualDualSenseUsbip -- servertest
dotnet run --project .\utils\VirtualDualSenseUsbip -- inputtest 5
dotnet run --project .\utils\DSHapticsProto -- watchusb 12
dotnet run --project .\utils\DSHapticsProto -- audiotest 5 120 0.15
```

The tests cover USB/IP golden vectors and fragmentation, byte-exact replay of
the captured composite EP0 fixture, UAC1 mute/volume state and range controls,
exact composite topology, and a loopback live-server session with management,
HID-only EP0, feature, interrupt-IN/OUT, packet-preserving paced ISO, and
UNLINK traffic. The teardown regression unlinks a synthetic 100-packet ISO transfer
during its pacing window and verifies that no late RET_SUBMIT follows. The
complete loopback suite passed 50 consecutive runs after the fix.

## Live attach

Configure USB/IP WPP tracing and kernel dumps before testing changes to
teardown, ISO pacing, or USB/IP protocol behavior.

Close DS4Windows for the first clean enumeration. Start the server from a
normal terminal:

```powershell
dotnet run --project .\utils\VirtualDualSenseUsbip -- serve --input bluetooth --capture .\m23-hid-output.ndjson
```

Then attach from an elevated terminal:

```powershell
& 'C:\Program Files\USBip\usbip.exe' attach -r 127.0.0.1 -b 1-1 --serial DS4WSPKHID001 --once
```

The serial must contain at most 15 alphanumeric ASCII characters; hyphenated
`DS4WSPK-HID-001` is rejected by usbip-win2. Inspect or detach with:

```powershell
& 'C:\Program Files\USBip\usbip.exe' port
& 'C:\Program Files\USBip\usbip.exe' detach -p 1
```

The port number can differ; use the value printed by `usbip port`.
The physical controller must be awake and connected over Bluetooth before the
server starts. Keep DS4Windows, DSX, and Steam Input closed for the first live
bridge test. Use `--input neutral` when only enumeration/output capture is
needed.

For composite enumeration, add `--configuration composite` and attach with an
alphanumeric serial such as `DS4WSPKCOMP001`. Change the serial while iterating
Windows driver startup so a previous failed device instance is not reused.

Protocol source: [Linux kernel USB/IP protocol documentation](https://docs.kernel.org/usb/usbip_protocol.html).
The captured descriptors live in
`../DSCompatProbe/fixtures/dualsense_usb_0ce6`.
