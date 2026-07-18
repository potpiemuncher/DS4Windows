# Phase 4 — Virtual Wired DualSense: Design

Goal: games with native DualSense support (SDL3, libScePad titles, Steam-free
builds) detect a **wired** DualSense — HID device plus 4-channel audio device —
render haptic audio and trigger effects to it, and we relay everything to the
real Bluetooth pad using the streaming stack built in Phases 1–3.

Design reviewed in consultation with OpenAI Codex (2026-07-17, full transcript
in `codex-bridge/transcript.md`). Key conclusions adopted below.

## Architecture decision

**Primary candidate: user-space USB composite emulation via usbip-win2 (UDE
virtual host controller).** We emulate the complete wired DualSense USB device
(descriptors, HID interface, UAC1 4-channel audio) in user space; the generic
signed VHCI driver supplies the virtual USB bus. Rationale:

- Authentic USB topology: in-box `hidusb.sys`/`usbaudio.sys` bind exactly as
  they do to real hardware; HID↔audio association falls out naturally from the
  shared composite parent/container.
- No kernel code of our own → no HLK/attestation burden for the device logic,
  as long as we ship an unmodified, production-signed VHCI driver whose
  license permits redistribution.
- Descriptor fixes are user-space changes, not driver releases.

**Gate (Milestone 2 spike) before committing:** sustained isochronous 4ch
48 kHz traffic through usbip-win2 must be stable for hours, survive surprise
removal/suspend, avoid TCP buffering latency, and have a defensible signed
redistribution story. **If the spike fails**, fall back to: custom KMDF virtual
bus + VHF-backed HID child + WaveRT/ACX audio children sharing one forged
`DEVPKEY_Device_ContainerId` (honest label: API-level emulation, not USB
equivalence). **Rejected:** extending ViGEmBus (wrong abstraction; audio does
not fit; inherits a legacy kernel product).

## The association question (architectural hinge)

How do titles match the DualSense HID device to its audio endpoint?
- Windows' model: shared **device container ID** from the composite parent.
- SDL (as of the SDL#9197 discussion): **friendly-name matching** ("DualSense")
  — fragile with multiple pads, but composite emulation satisfies it anyway.
- libScePad/individual titles: undocumented; must be traced empirically.

Hence **Milestone 0: compatibility laboratory** before any driver work — see
`M0_COMPAT_LAB_RUNBOOK.md` and the `utils/DSCompatProbe` tool. Acceptance:
for each target title we can state exactly which HID path it opens, which
feature reports it requests, which audio endpoint/format it activates, and
which property (container/parent/VID-PID/name) it used to associate them.

## Signing / distribution reality (2026)

- Attestation-signed drivers: testing path, not Windows Update retail.
- Public release of any custom kernel driver ⇒ HLK/WHCP via a Partner Center
  entity + EV credential — the project needs a legal entity or sponsor for
  that; flag early to the DS4Windows maintainers.
- The usbip-win2 path avoids this only if we ship their driver unmodified.

## Milestones

- **M0 — Compat lab**: probe fixtures (real wired + BT DualSense), title trace
  matrix (SDL3 sample, 2 libScePad titles, 2 Steam-free native titles), two
  controllers connected to expose name-matching bugs.
- **M1 — Phase 3b mic**: minimal WaveRT capture-only driver (SimpleAudioSample
  derivative); Opus/BT decode stays in the user-mode service; shared PCM ring;
  silence-on-starvation with clock continuity. Exit: stable capture under
  Secure Boot/HVCI, 30-min drift test, conferencing app test.
- **M2 — usbip-win2 spike**: descriptors + HID interface only → title matrix
  enumeration test; then synthetic 4ch UAC1 render endpoint (samples counted,
  not used). Exit criteria above; failure ⇒ switch to KMDF/VHF fallback.
- **M3 — Haptic relay**: game 4ch 48k render stream → split ch3/4 → proper
  anti-aliased 48k→3k resample (polyphase FIR, not the current biquad+decimate)
  → FIFO-occupancy-driven slow ASRC → 0x39 stream. Trigger/output report relay
  + arbitration vs DS4Windows profiles.
- **M4 — Hardening + single-controller release**: HVCI/Secure Boot/sleep
  matrix, installer, regression fixtures.
- **M5 — Integration extras**: mic under the same composite device, multiple
  controllers, upstream SDL association improvement.

## Clock model notes (validated)

The Phase 3 listening path (capture → WDL resample to measured ~45 kHz slot
rate → 480-sample frames encoded at Opus's declared 48 kHz → 2 frames per
21.33 ms report) is time- and pitch-correct; the declared-vs-physical rate
mismatch cancels because the controller consumes at the same slot clock.
Long-run verification TODO: confirm 960 decoded samples per slot over
thousands of reports and whether the effective rate drifts per
controller/firmware/temperature; drive a slow ASRC from FIFO occupancy.
