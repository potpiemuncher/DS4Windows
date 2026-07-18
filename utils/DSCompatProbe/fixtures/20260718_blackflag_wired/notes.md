# Fixture: AC Black Flag Resynced, wired DualSense, live gameplay (2026-07-18)

Per-channel peak meters (IAudioMeterInformation) on the controller's 4ch/48k
float render endpoint. Columns: t_seconds, ch1, ch2 (pad listening audio),
ch3, ch4 (haptic actuators). channel_meter.csv = 45 s @ ~30 Hz (light play);
channel_meter_long.csv = ~235 s @ ~40 Hz (action-heavy play).

Long-capture stats: 49 haptic bursts; durations 100 ms - 4.8 s (avg 759 ms);
peak 1.0 full scale; ch3/ch4 stereo-correlated; pad audio ~8% duty.
Game held an audio session on the endpoint (native association).
Wired HID input: report 0x01 @ ~250 Hz throughout.

## USB payload capture (same session, post-reboot, 300 s)

Raw pcaps in utils/DSCompatProbe/usb_captures/20260718_010245 (local only,
~240 MB; hub3.pcap device address 9 = DualSense).

- Output reports: ID 0x02, 48 bytes, ~128/s. **Motor-emulation bytes are 0 in
  every report** - the title vibrates exclusively via haptic audio.
- Flag usage: mostly alternating flags0=0x0C (trigger FFB updates) and
  flags1=0x44 (LED color + motor power level); rare flags0=0x02+b39=0x04
  (UseRumbleNotHaptics + improved-rumble bit, likely init/config moments).
- Distinct trigger-effect programs observed (11-byte FFB blocks, R2/L2):
    off (all 00)                        x20442
    05 00 ... (reset/release mode)      x10715  both triggers
    22 12 00 21 ... on R2               x 6810  weapon-aim resistance
    25 10 01 05 ... both triggers       x  321  sustained dual effect
    25 24 00 07 ... on R2               x  153  weapon variant
  These blocks are byte-compatible with the BT 0x31 trigger sections ->
  Phase 4 trigger relay is a direct copy.
- Iso OUT stream: 3840-byte URBs every 10 ms = 480 samples x 4ch x 16-bit
  48 kHz. Extracted with DSCompatProbe parsepcap over the 288.5 s capture
  (28,848 URBs): ch1/2 (listening audio) rms ~585, peak ~37% FS; ch3/4
  (haptic actuators) rms ~3770, peak **98.6% FS** - the game drives the
  actuators near full scale, far hotter than the audio channels.
  iso_haptics_ch34_excerpt10s.wav = committed 10 s reference; full 52 MB
  WAVs are gitignored (regenerate via
  `DSCompatProbe parsepcap <hub3.pcap> 9 <outdir>`).
