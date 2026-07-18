# Milestone 0 — Compatibility Lab Runbook

Purpose: produce fixture data proving how Windows and each target title see a
real DualSense, so the Phase 4 virtual device can be built to match. Collect
first, filter later — dump every property, tag DualSense-related nodes.

## 1. Automated probe (`utils/DSCompatProbe`)

Run once per configuration and keep the output folders:

```
DSCompatProbe.exe            # writes probe_runs/<timestamp>/*.json
```

Configurations to capture:
1. DualSense over **Bluetooth** (done first — no cabling needed).
2. DualSense over **USB cable** (the critical fixture: this is what we clone).
3. **Two** DualSense controllers wired simultaneously (exposes friendly-name
   ambiguity in SDL-style matching).
4. DualSense Edge over USB, if available.

The probe collects: all devnodes for HID/audio/USB device-interface classes
with every CM property (raw + decoded), full ancestor chains to the root,
container IDs, all MMDevice render/capture endpoints with complete property
stores, IAudioClient mix formats/periods, IsFormatSupported for the 4ch
haptics formats, and HID identity/report-descriptor capabilities.

Key comparison for association:

```
HID TLC → HID transport → USB composite parent
Audio endpoint → KS filter → USB audio interface → same USB composite parent
```

## 2. Title trace matrix (manual, per title)

Targets: an SDL3 sample/testcontroller build, two libScePad titles, two
Steam-free native-DualSense titles. Per title, wired controller:

1. Start **ProcMon** (elevated, backing file). Filters — processes: title exe,
   launcher, `audiodg.exe`, `svchost.exe`; operations: Process Create, Load
   Image, CreateFile, QueryOpen, DeviceIoControl, Read/WriteFile, Reg*; path
   contains: `HID#`, `USB#`, `BTH`, `SWD#MMDEVAPI`, `MMDevices\Audio`,
   `DeviceClasses`, `CurrentControlSet\Enum`, `VID_054C`.
2. Start **WPR** (use names from `wpr -profiles`; archive `wpr -providers`):
   `wpr -start GeneralProfile.Verbose -start Audio.Verbose`, markers at:
   launch, main menu, controller active, haptics observed, exit.
3. Sequence: launch → menu → activate pad → gameplay → trigger rumble,
   haptic audio, adaptive triggers separately → open controller/audio
   settings → clean exit → stop traces.
4. Snapshot process tree + loaded modules (SDL*.dll, libScePad, GameInput,
   audio middleware) with versions/hashes, and active audio sessions
   (IAudioSessionManager2: PID ↔ endpoint mapping — often the most direct
   association evidence).

Do NOT brute-force feature report IDs against real hardware; record only IDs
already known or observed from real software.

## 3. Fixture acceptance (per title)

Answerable from the data:
- Which HID path was opened; VID/PID via API or path parsing?
- Which feature report IDs requested?
- Which render endpoint activated, exact format + share mode?
- Association mechanism: container ID / parent traversal / VID-PID / name?
- Behavior deltas BT vs USB, one pad vs two?

If ProcMon+ETW leave the association mechanism ambiguous, escalate to
API-level tracing (Detours-style) of SetupDi*/CM_*/IMMDevice*/HidD_* calls
with arguments — separate run.

## Findings so far (2026-07-18, wired DualSense + AC Black Flag Resynced live)

- Wired render endpoint: "Speakers (DualSense Wireless Controller)", mix
  format **48 kHz / 4ch / 32-bit float**; capture endpoint "Headset
  Microphone" 48 kHz / 2ch. 16-bit shared/exclusive 4ch rejected — games
  render via the float engine format.
- **Native association confirmed live**: `ACBlackFlag_Plus` holds an audio
  session on the controller's render endpoint (session PID↔endpoint mapping
  from the probe). Module list is blocked by anti-tamper, so the SDK in use
  is still unknown — needs ProcMon Load Image trace.
- Per-channel metering (IAudioMeterInformation, 30 Hz sampling): ch1/2 carry
  occasional game audio to the pad (~19% duty in sample), **ch3/4 carry
  haptic bursts** (peak 0.50, sub-1% duty, short punchy envelopes) —
  waveform logs in `probe_runs/<ts>/channel_meter*.csv`. These are reference
  targets for the Phase 4 BT relay quality.
- Wired HID input: report 0x01 @ ~250 Hz steady while the title plays.
- Still needed: USB payload capture (game's trigger-effect output reports +
  iso audio frames) via USBPcap/Wireshark; two-controller fixture; per-title
  ProcMon/WPR traces per section 2.
