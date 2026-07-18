# DS4Windows Bluetooth Audio/Haptics Project Handoff

Last updated: 2026-07-18

## Mission

Extend DS4Windows so a physical DualSense connected over Bluetooth can provide
DSX-like listening audio, voice-coil haptics, game-rumble-to-haptics conversion,
and eventually native PC-game DualSense haptics plus adaptive triggers.

Repository state:

- Working copy: `C:\Users\patri\PS5Haptics\DS4Windows`
- Fork: `https://github.com/potpiemuncher/DS4Windows.git`
- Upstream: `https://github.com/ds4windowsapp/DS4Windows.git`
- Active branch: `feature/bt-audio-haptics`
- Validated branch tip before this handoff: `d5046ed`

Read `doc/BT_AUDIO_HAPTICS_RESEARCH.md` and
`doc/VIRTUAL_DUALSENSE_DESIGN.md` before changing the protocol or Phase 4
architecture.

## Confirmed Working on the User's PC

The user has confirmed all of the following with a Bluetooth-connected
DualSense:

- Game/system audio can keep playing through the normal headset while a
  loopback copy is streamed to the controller.
- Controller headphone/internal-speaker audio works over Bluetooth.
- System-audio-derived haptics work over Bluetooth.
- XInput game rumble is converted to DualSense voice-coil haptics.
- A controlled three-pulse XInput test produced exactly three pulses: heavy,
  light, then both. After `d5046ed`, those pulses were noticeably stronger.
- The 7.1-to-stereo downmix fix made controller audio sound materially better.

`System Audio + Rumble` means audio-derived haptic PCM and synthesized XInput
rumble are mixed into the same voice-coil haptic stream. It does not itself
enable audible controller audio. `Send audio to controller` independently
controls the Opus listening-audio stream.

While the Bluetooth haptic stream is active, standard DualSense rumble
emulation cannot be sent independently through the same output path. Select
`Rumble To Haptics` or `System Audio + Rumble` to retain game-rumble feedback.

## Protocol Decisions That Must Not Be Regressed

The controller ignored the earlier incremental `0x32`/`0x39` streaming
approach on this Bluetooth link. The proven path is a self-contained `0x36`
HID output report on every approximately 10.667 ms slot:

- Report size: 398 bytes.
- Repeat the known-good 63-byte advanced-haptics SetStateData state in every
  report. `UseRumbleNotHaptics` remains clear.
- Haptic packet PID is `0x12` (`0x92` with the sized flag).
- Haptic payload is 32 interleaved stereo frames, signed 8-bit PCM at 3 kHz
  per channel (64 bytes per report). Internal rings use unsigned offset-binary;
  convert to signed bytes when building the report.
- When listening audio is enabled, the same `0x36` contains one 200-byte Opus
  frame: stereo, 48 kHz, 480 samples/channel, 10 ms, 160 kbps CBR.
- Feed Opus at an effective 45 kHz delivery rate because one 480-sample frame
  is consumed per approximately 10.667 ms haptic slot. Feeding 48 kHz causes
  backlog and audible frame drops.
- A packetized SetStateData setup report is still used to initialize/unmute the
  listening-audio amplifier before streaming.
- Preserve the multichannel downmix and soft limiter. The user's SteelSeries
  Sonar Gaming endpoint is 96 kHz, 8-channel float; taking only channels 1/2
  discarded active center/surround content and sounded thin.
- Pure rumble synthesis must use the full PCM range after its deadzone and
  rescaling. Do not run pure-rumble output through the audio soft clipper; that
  reduced full-scale haptics to roughly half amplitude.

The primary implementation is
`DS4Windows/DS4Library/InputDevices/DualSenseHapticsStreamer.cs`. Focused tests
are in `DS4WindowsTests/DualSenseHapticsStreamerTests.cs`.

Protocol research credit already recorded in source: egormanga/SAxense and
awalol/DS5Dongle.

## Important Commits and Why

Bluetooth foundation:

- `effdebf` adds the BT audio/haptics research and initial design.
- `7cfbd64` through `6fedfaa` build the prototype, DS4Windows integration,
  controller-audio amplifier setup, 45 kHz delivery correction, jitter
  hardening, and selectable latency profiles.
- `45bdd5a` adds the rumble-synthesis deadzone, idle suppression, and tests.

Phase 4/native-game foundation:

- `8fbed1e` through `d204bb8` add the virtual-DualSense plan, compatibility
  probe, wired captures, adaptive-trigger fixtures, and haptic waveform data.
- `6c0d39a` completes M2.0 byte-exact wired DualSense descriptor fixtures and
  starts the user-space USB/IP protocol core (M2.2).
- `558ae95` prevents DSCompatProbe failures from showing CLR crash dialogs.

Latest user-validated fixes:

- `18e05be` switches haptics-only streaming to self-contained `0x36` reports
  and adds `DSHapticsProto test36` plus endpoint metering. This fixed the case
  where the stream looked active but the controller produced nothing.
- `d0ecd89` carries Opus listening audio and haptics together in self-contained
  `0x36` reports, adds 7.1 downmixing, and adds a soft limiter. This fixed
  controller audio and improved sound quality.
- `d5046ed` lets pure rumble synthesis use full PCM amplitude. This fixed weak
  rumble-to-haptics pulses.

Use `git log --oneline upstream/main..HEAD` for the complete ordered commit
history.

## Test Evidence and Reusable Tools

Focused unit tests:

```powershell
dotnet test .\DS4WindowsTests\DS4WindowsTests.csproj --filter FullyQualifiedName~DualSenseHapticsStreamerTests
```

Most recent result: 12 focused tests passed. The main build succeeds with 20
pre-existing warnings. A full test run had 13 passing and 3 unrelated XML
snapshot failures because the branch adds profile/settings fields; do not
misdiagnose those snapshots as a streaming regression.

USB/IP protocol self-test:

```powershell
dotnet run --project .\utils\VirtualDualSenseUsbip -- selftest
```

External reusable diagnostics (not tracked in this repository):

- `C:\Users\patri\PS5Haptics\xinput_rumble_to_haptics_test.ps1` sends heavy,
  light, and combined three-second XInput pulses to the virtual controller.
- `C:\Users\patri\PS5Haptics\bluetooth_link_monitor.ps1` samples Bluetooth and
  DS4Windows process/link counters.
- `C:\Users\patri\PS5Haptics\bluetooth_audio36_5min.csv` is the five-minute
  report-`0x36` baseline.

Five-minute baseline: 300.2 seconds / 292 samples; outbound controller traffic
averaged 47,665.81 B/s (42,805.74 minimum, 58,027.53 maximum), no sample fell
below 40 kB/s, no ACL flushes were observed, and DS4Windows CPU averaged 1.23%
with a 2.1% maximum. Two isolated zero-credit samples occurred without a
dropout. The monitor's stored PnP-presence identifier was stale after a
reconnect; live bidirectional traffic proved the connection was present.

The last manually launched validated executable was:

`C:\Users\patri\PS5Haptics\rumblefull-build\x64\Release\net8.0-windows\DS4Windows.exe`

Do not rely on an old process ID; check the current process and binary path.

## Current Limitation

Today, games normally see the existing virtual XInput controller. Therefore
ordinary game rumble can be synthesized into haptics, but native DualSense PC
game haptic audio and game-authored adaptive-trigger output are not yet being
captured. That is the purpose of Phase 4. Do not claim native game support until
a game recognizes the virtual device as a wired DualSense and its HID/UAC
output has been relayed successfully.

## Next Milestone: Native DualSense Game Compatibility

The architecture is a user-space virtual wired DualSense composite USB device
(HID plus UAC1 audio) exposed through usbip-win2's signed VHCI driver. The real
controller remains connected over Bluetooth; native game output is translated
and forwarded through the proven Bluetooth `0x36` streamer.

Status and order:

1. **M2.0 complete**: exact wired DualSense descriptors/fixtures live under
   `utils/DSCompatProbe/fixtures/dualsense_usb_0ce6`.
2. **M2.2 protocol foundation complete**: `utils/VirtualDualSenseUsbip`
   implements USB/IP management framing, 48-byte URB headers, fragmented reads,
   bounded transfer parsing, and serialized responses. It does not attach a
   device yet.
3. **M2.1 is the immediate gate**: qualify the production-signed usbip-win2
   0.9.7.8 VHCI driver with Secure Boot and Memory Integrity/HVCI enabled,
   testsigning disabled, and no test root CA. No matching USB/IP/VHCI service or
   driver was installed when last checked.
4. **M2.3**: implement HID-only virtual DualSense enumeration: EP0 standard
   requests, exact HID report descriptor, feature reports, 250 Hz interrupt IN,
   and interrupt OUT capture. First prove a native title recognizes it and
   capture adaptive-trigger blocks.
5. **M2.4**: add exact UAC1 composite descriptors.
6. **M2.5**: qualify isochronous timing and stability.
7. **M2.6**: relay UAC haptic channels 3/4 to 3 kHz Bluetooth haptic PCM and
   forward native 11-byte adaptive-trigger blocks into Bluetooth controller
   state.

Installing the VHCI kernel driver is a material system change and briefly
restarts USB 3.0 hubs/devices. Before doing it, get explicit user approval,
have the user save work and leave active games/calls/transfers, record Secure
Boot/HVCI/testsigning state, create a restore point if available, and verify the
downloaded package/signatures. Do not weaken Windows security to make the
driver load.

## Working Rules for the Next Agent

- Inspect `git status` before editing and preserve unrelated user work.
- Keep report `0x36` as the known-good Bluetooth transport unless new hardware
  evidence proves another report works.
- Keep listening audio and audible system output independent via WASAPI
  loopback; do not switch the user's default output device.
- Validate haptics separately from audible audio. Hearing the headset proves
  capture source activity, not controller haptic output.
- Prefer controlled fixtures/tests over subjective game testing when isolating
  a layer, then finish with a real native-DualSense game test.
- Commit protocol discoveries with the reasoning and captured evidence so the
  next handoff does not repeat the failed `0x32`/`0x39` experiments.
