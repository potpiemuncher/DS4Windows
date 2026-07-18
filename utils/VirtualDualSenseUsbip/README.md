# VirtualDualSenseUsbip

User-space USB/IP device emulator for DS4Windows Phase 4. It exposes a
HID-only wired DualSense through usbip-win2's signed VHCI driver. A later
milestone will add the captured UAC1 audio interfaces and relay native haptic
audio to the real Bluetooth controller.

## Status

M2.3-live enumeration is working on the primary Windows 11 PC:

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
  calibration at runtime without logging or persisting hardware values. This
  path passes synthetic CRC/mapping tests but still needs its first live run.
- In neutral-input mode, feature report `0x05` intentionally stalls rather
  than returning invented sensor calibration.

Live physical-input validation, native-title recognition, the five adaptive-
trigger programs, one-hour stability, and repeated clean attach/detach remain
M2.3 acceptance work. UAC1 audio is not exposed yet.

## Build and test

```powershell
dotnet build .\utils\VirtualDualSenseUsbip\VirtualDualSenseUsbip.csproj -c Release
dotnet run --project .\utils\VirtualDualSenseUsbip -- selftest
dotnet run --project .\utils\VirtualDualSenseUsbip -- devicetest
dotnet run --project .\utils\VirtualDualSenseUsbip -- servertest
dotnet run --project .\utils\VirtualDualSenseUsbip -- inputtest 5
```

The tests cover USB/IP golden vectors and fragmentation, byte-exact replay of
the captured composite EP0 fixture, and a loopback live-server session with
management, HID-only EP0, feature, interrupt-IN/OUT, and UNLINK traffic.

## Live attach

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

Protocol source: [Linux kernel USB/IP protocol documentation](https://docs.kernel.org/usb/usbip_protocol.html).
The captured descriptors live in
`../DSCompatProbe/fixtures/dualsense_usb_0ce6`.
