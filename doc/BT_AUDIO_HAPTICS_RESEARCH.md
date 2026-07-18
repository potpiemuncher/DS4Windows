# BT Audio & Haptic Feedback for DualSense — Research and Design

Goal: make DS4Windows drive the DualSense's voice-coil haptic actuators (and eventually
speaker/headphone audio) over **Bluetooth**, comparable to DSX's v3.2 "BT Audio/Haptics"
feature — but open source.

## Background: how DualSense haptics actually work

- The DualSense's "haptic feedback" is **audio**. The two voice-coil actuators (left/right)
  are speakers, driven by an audio signal.
- Over **USB**, the controller enumerates as a quad-channel audio device:
  channels 1/2 = headphone/speaker, channels 3/4 = left/right actuator. Games with native
  support (or Steam Input) render haptics by playing audio on ch 3/4.
- Over **standard Bluetooth on Windows, none of that audio surface exists.** Windows' BT
  stack has no profile for Sony's audio stream, so officially there is no haptics-as-audio
  and no headphone audio wirelessly. Only the classic HID output report `0x31`
  (rumble-emulation bytes, trigger effects, LEDs) is available — which DS4Windows already
  uses today, including "accurate rumble" emulation (fw ≥ 2.24, flag `0x04` in the
  player-LED section byte).

## The discovery that unlocks this: HID report 0x32 haptic streaming

The [SAxense project](https://github.com/egormanga/SAxense) (MPL-2.0, independent RE work
by egormanga — **credit them in any implementation, per their README request**) proved the
DualSense accepts a low-rate haptic PCM stream over plain Bluetooth HID — no driver, no
custom BT profile. This is almost certainly the same mechanism DSX v3.2 uses.

### Protocol summary (verify against SAxense.c verbatim before implementing)

- HID **output report ID `0x32`** (the BT descriptor declares `0x32`–`0x39` as
  progressively larger containers: 95/141/205/269… data bytes — see
  [nondebug/dualsense](https://github.com/nondebug/dualsense)).
- Report layout (0x32):
  - byte 0: `0x32` (report ID)
  - byte 1: 4-bit tag + 4-bit rolling sequence number
  - payload area: nested **sized packets**, each `PID + length` header:
    - packet `0x11` (config, 7 data bytes): `FE 00 00 00 00 FF 00`, last byte is an
      incrementing counter. The `0xFE` is presumably the haptics-mode selector
      (cf. the known `UseRumbleNotHaptics` values `0xFC`/`0xFE`).
    - packet `0x12` (audio, 64 data bytes): raw PCM samples.
  - last 4 bytes: CRC-32 over `0xA2 ‖ report bytes` — **identical scheme to the 0x31
    report DS4Windows already signs** (`Crc32Algorithm` with the `outputBTCrc32Head = A2`
    prefix; SAxense's magic constant `~0xEADA2D49` is just the precomputed state after
    `0xA2`).
- Audio format: **PCM unsigned 8-bit, stereo (L actuator / R actuator), 3000 Hz.**
  64 bytes per report = 32 stereo frames = one report every ~10.67 ms.
- Works on DualSense (PID 0x0CE6) and DualSense Edge (PID 0x0DF2).

### Why this fits DS4Windows cleanly

- `DualSenseDevice` already builds/signs BT reports with the exact same CRC scheme
  (`SendInitialBTOutputReport`, `PrepareOutReport`).
- The write path (`HidDevice.WriteOutputReportViaInterrupt`) is a plain overlapped
  `WriteFile` that takes any buffer length — a 142-byte 0x32 report goes through the same
  code as today's 78-byte 0x31 report.
- Open question to test on hardware: interleaving cadence of 0x32 (haptics stream) with
  0x31 (LED/trigger state) writes, and whether streaming 0x32 changes how rumble-emulation
  bytes in 0x31 are honored.

## Plan

### Phase 0 — workspace
- .NET 8 SDK (`net8.0-windows` target), fork on the user's GitHub, full (unshallowed) clone.

### Phase 1 — prototype haptics streamer (standalone console tool in `extras/` or `utils/`)
1. WASAPI **loopback capture** of system audio (NAudio, or CsWin32 raw WASAPI — the
   project already uses CsWin32).
2. DSP: low-pass / band-split + envelope shaping (DS5Dongle's approach is a good
   reference: LPF 80–250 Hz, ~1 ms attack / ~80 ms release envelope, soft-clip
   `x/(1+|x|)`), then decimate to 3 kHz and quantize to u8 stereo.
3. Chunk into 64-byte packets → build 0x32 reports (seq counter, config packet, CRC) →
   write to the controller's HID handle every ~10.67 ms.
4. Validate on real hardware over BT (feel test, latency test, battery drain, coexistence
   with 0x31 writes).

### Phase 2 — integrate into DS4Windows
- New per-profile "Audio Haptics" settings: enable, intensity, low-pass cutoff,
  mix-with-rumble vs replace, source selection.
- Source selection must include a **render-endpoint picker**, not just the default
  device: virtual audio routers (SteelSeries Sonar, Voicemeeter, …) split game audio
  across multiple endpoints, and loopback of the wrong one captures silence.
- A `DualSenseHapticsStreamer` owned by `DualSenseDevice` (BT mode): dedicated output
  thread pacing 0x32 reports; coordinate with the existing input-thread-driven 0x31 writes.
- UI page + profile persistence, following existing options-store patterns
  (`DualSenseControllerOptions`).

### Phase 3 — BT speaker/headphone audio — DONE (3a, output side), verified on hardware
The protocol came from [awalol/DS5Dongle](https://github.com/awalol/DS5Dongle) (MIT),
whose Pico 2 W firmware implements the full audio path. Working parameters as
implemented in `DualSenseHapticsStreamer`:

- **Codec: Opus** (not SBC) — 48 kHz stereo, 10 ms frames, 160 kbps CBR = exactly
  200 bytes per frame. Complexity is free on PC (DS5Dongle uses 0 for the Pico).
- **Container: report 0x39** (547 bytes) every ~21.33 ms:
  `[0]=0x39, [1]=seq<<4, [2]=0x91, [3]=6, [4]=0x7E (0x7F w/ mic; bit6 mandatory,
  bits = field presence), [5..8]=dejitter buffer len, [9]=frame counter (+2/report),
  [10]=0xD2, [11]=64, [12..139]=2×64 B haptic PCM (signed s8 here),
  [140]=route|0xC0 (PID 0x13 speaker / 0x16 headphone), [141]=200,
  [142..341]+[342..541]=two Opus frames, CRC-32(0xA2‖first 543) at [543..546]`.
- **The amp boots muted**: audio stays silent until a SetStateData container packet
  (PID `0x10`, len `0x3F`, inside a 0x32 report) sets `AllowHeadphoneVolume|
  AllowSpeakerVolume|AllowAudioControl` (byte0=0xB0), `AllowAudioControl2`
  (byte1=0x80), volumes ≈ 0x64, and `SpeakerCompPreGain=2` (byte37).
- **Audio is slaved to the haptics clock**: one 480-sample frame per ~10.667 ms slot
  ⇒ deliver at **45 000 samples/s**, not real-time 48 kHz, or the stream drops a
  frame every few reports (constant chop). This is why DS5Dongle resamples 512→480.
- **Jitter defenses that made it clean on a congested link**: controller dejitter
  buffer 120 (max 127), ~213 ms local Opus backlog, 4-frame prebuffer with
  rebuffer-on-dry-out, burst catch-up ≤250 ms instead of clock resync.
- Mic input (Phase 3b, not yet implemented): enable via 0x32 config packet len 1
  data `0x03` (off: `0x02`); controller then interleaves 78-byte 0x31 input reports
  with bit 1 of byte[1] set carrying 71-byte Opus mono (48 kHz) frames at [3..73].
  These must be filtered out of gamepad parsing; exposing them as a Windows mic
  needs a virtual audio driver.
- Headset-plug detection: input status byte (`inputReport[54+reportOffset]`) bit 0.

### Phase 4 (stretch, likely out of scope) — virtual wired DualSense
- DSX's trick for **native game haptics over BT**: a proprietary virtual "wired" DualSense
  (games render haptic audio to it; DSX relays to the real pad). An open equivalent needs
  a Windows audio+HID emulation driver — a large separate project. Not planned; Phase 1–3
  system-audio capture covers the practical use cases (as DSX's "System Audio Capture"
  mode does).

## Sources
- SAxense (protocol RE, MPL-2.0): https://github.com/egormanga/SAxense
- DualSense HID reference: https://github.com/nondebug/dualsense
- DS5Dongle audio→haptics DSP reference: https://github.com/loteran/DS5Dongle
- DS4 BT audio RE (SBC) breadcrumbs: https://github.com/nefarius/ViGEmBus/issues/61,
  https://github.com/Ohjurot/DualSense-Windows/issues/7
- DSX v3.2 feature description: https://www.tweaktown.com/news/112219/
