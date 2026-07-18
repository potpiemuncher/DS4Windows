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

## M2 spike plan (usbip-win2 emulator) — from Codex consult 2026-07-18

Mechanism confirmed: our emulator is a **local usbip protocol server** that
*impersonates* a DualSense (not a forwarder). usbip-win2's signed VHCI driver
attaches it over the usbip TCP protocol and materializes it as a real USB
device; in-box `usbccgp`/`hidusb`/`usbaudio` then bind exactly as to hardware.
Reference impls (behavioral, don't extend): VIIPER, the MIT Rust usbip server.
Build our own C# .NET 8 server for full control over iso descriptors,
outstanding URBs, and completion pacing.

Two hard gates decide if usbip-win2 is viable:
1. Its WHLK x64 driver (try 0.9.7.8, fall back to 0.9.7.5) installs under
   Secure Boot + HVCI with **testsigning OFF** and adds no public test root CA.
2. A paced synthetic UAC1 sink sustains the observed 3840 B / 10 ms stream for
   an hour with no audio glitches and no URB-queue drift.
If gate 2 fails, investigate usbip-win2 iso completion-clock semantics before
abandoning the approach (our bandwidth is modest; failure would be pacing, not
throughput).

Milestones:
- **M2.0 Freeze inputs**: from the descriptor capture, check in binary blobs +
  decoded JSON for device/config/interface/endpoint/HID-report/string/UAC
  descriptors and the observed enumeration control exchanges. Acceptance: a
  descriptor parser round-trips every captured byte exactly.
- **M2.1 Driver qualification**: install usbip-win2 on a disposable Win11,
  Secure Boot + HVCI on, testsigning off; verify .sys/.cat signatures, no test
  CA left behind. Gate 1 above.
- **M2.2 Protocol core**: management ops (OP_REQ_DEVLIST/IMPORT), 48-byte URB
  headers (CMD_SUBMIT/RET_SUBMIT), iso descriptors, exact-read transport, one
  serialized writer, pending-request map, unlink. Golden-test vs kernel docs +
  a localhost session. Acceptance: usbip list/attach over 100 attach/detach
  cycles.
- **M2.3 HID-only DualSense**: EP0 std requests, exact HID report descriptor,
  feature reports, 250 Hz interrupt IN, interrupt OUT capture. Use serial
  DS4WSPKHID001 to avoid polluting cached instance state. usbip-win2 limits the
  serial to 15 alphanumeric ASCII characters. Acceptance: native
  title recognizes it (no Steam), 1 h stable, all 5 trigger programs arrive on
  interrupt OUT, clean detach.
- **M2.4 Composite enumeration**: add exact UAC1 descriptors, no relay yet.
  Acceptance: usbccgp/hidusb/usbaudio bind, 4ch/48k render endpoint appears,
  MMDevice container ID matches the HID child's USB container, Windows selects
  the streaming alt setting.
- **M2.5 ISO timing lab** (the central gate): implement `immediate` and `paced`
  completion modes. Virtual 1 kHz frame clock; honor URB_ISO_ASAP (0x02, start
  frame ignored when set) else startFrame w/ wraparound; complete one response
  per URB at its scheduled end (never Task.Delay per packet; dedicated
  high-prio QPC scheduler + waitable timer). Measure whether Windows/usbaudio
  paces submits or we must pace completions. Acceptance: 1 h stable 4×48k, no
  queue growth, sample count matches elapsed time, 100 stop/start + alt
  transitions.
- **M2.6 Haptic relay**: UAC ch3/4 -> anti-alias -> async 3 kHz -> existing BT
  scheduler; interrupt-OUT trigger relay independent (byte-copy to 0x31
  sections). Never block USB completion on BT WriteFile.

Key protocol traps (Codex):
- Interrupt IN: hold pending submits, complete on real 250 Hz input; never
  synthesize a fake poll rate; log submit/scheduled/complete QPC + seq.
- ISO OUT URB is ~10 packets x 384 B (1/ms) but do NOT hardcode that — it's
  app behavior, not an endpoint invariant; validate packet offsets/lengths
  against wMaxPacketSize each submit.
- TCP concurrency: one reader/parser -> {EP0 exec, int-IN queue, int-OUT,
  iso scheduler, unlink} -> one serialized writer. Multiple URBs outstanding,
  responses may complete out of seqnum order.
- Return RET_SUBMIT for interrupt OUT promptly (status 0, actual len, non-iso
  packet count -1) regardless of BT relay latency.

## Clock model notes (validated)

The Phase 3 listening path (capture → WDL resample to measured ~45 kHz slot
rate → 480-sample frames encoded at Opus's declared 48 kHz → 2 frames per
21.33 ms report) is time- and pitch-correct; the declared-vs-physical rate
mismatch cancels because the controller consumes at the same slot clock.
Long-run verification TODO: confirm 960 decoded samples per slot over
thousands of reports and whether the effective rate drifts per
controller/firmware/temperature; drive a slow ASRC from FIFO occupancy.

## M2.0 result (2026-07-18) -- complete

`DSCompatProbe freezeusb` now produces the byte-exact fixture in
`utils/DSCompatProbe/fixtures/dualsense_usb_0ce6`:

- 18-byte device descriptor, 227-byte composite configuration descriptor,
  289-byte USB HID report descriptor, and language/manufacturer/product string
  descriptors, each stored as binary plus decoded JSON and SHA-256.
- The HID descriptor is read from the physical USB device with
  `IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION`. HidSharp on Windows returns
  a valid but reconstructed 489-byte descriptor; it is retained only as a
  diagnostic and must not be used as the virtual device's wire image.
- `verifyusb` re-parses and concatenates every device/config descriptor segment,
  compares the exact original bytes, checks all hashes, checks the HID length
  advertised by the configuration, and rejects incomplete fixtures.
- `enumeration.json` preserves the control exchanges visible in the source
  USBPcap. `capture-usb.ps1 -RestartDualSense` is available for traces that
  need an explicit device restart, though direct hub reads make it unnecessary
  for freezing the HID descriptor.

This clears M2.0. M2.1 and the M2.2 protocol foundation have since completed;
see the live status below.

## M2.1 driver qualification result (2026-07-18) -- complete

usbip-win2 0.9.7.8 x64 is installed on the primary Windows 11 PC. Its
`usbip2_ude.sys` and `usbip2_filter.sys` drivers are WHQL-signed by Microsoft
Windows Hardware Compatibility Publisher and load with Secure Boot and Memory
Integrity enabled and testsigning disabled. No test root or trusted-publisher
certificate was added. Services remain demand-start. The full qualification
transcript, signature evidence, certificate-store diffs, and security-state
checks are preserved outside the repository at `C:\USBIP-M2.1-Audit`.

## M2.2 protocol core status (2026-07-18) -- foundation complete

`utils/VirtualDualSenseUsbip` now contains the driver-independent framing
foundation: network-order management and 48-byte URB headers, bounded transfer
and ISO descriptor parsing, exact reads that tolerate arbitrary TCP
fragmentation, RET_SUBMIT/RET_UNLINK encoders, fixed-width device records, and
a semaphore-serialized stream writer. Its `selftest` checks the Linux kernel
documentation's interrupt-IN/OUT golden vectors plus a one-byte-fragmented
OP_REQ_IMPORT. The same protocol core now backs the live M2.3 server.

## M2.3 HID-only live status (2026-07-18) -- enumeration core complete

`VirtualDualSenseUsbip serve` now runs a local USB/IP server with
OP_REQ_DEVLIST/IMPORT, EP0, queued interrupt IN, interrupt OUT capture, and
CMD_UNLINK handling. It derives a 41-byte HID-only configuration from the
captured 227-byte composite descriptor while retaining the exact 289-byte HID
report descriptor. The original composite fixture remains unchanged and still
passes byte-exact offline replay.

Live evidence on the qualified primary PC:

- `usbip list` finds Sony `054c:0ce6`; `usbip attach` materializes instance
  `USB\VID_054C&PID_0CE6\DS4WSPKHID001`.
- Windows reports both the USB Input Device and HID-compliant game controller
  nodes as healthy. DSCompatProbe sees the expected product/manufacturer,
  64-byte input, 48-byte output, and 64-byte feature-report limits.
- Windows initialization generated authentic 48-byte interrupt-OUT reports,
  which the server captured successfully.
- Pairing feature report `0x09` uses a stable synthetic locally administered
  address, never the physical controller's address. Firmware report `0x20` is
  served in the captured format. Calibration report `0x05` deliberately stalls
  rather than fabricating hardware-specific sensor calibration.
- A loopback integration test covers management, descriptors, feature reports,
  interrupt IN/OUT, and UNLINK. The live VHCI device sustained 7,504 neutral
  input reports in 30.003 seconds (250.1 Hz) with no read failure. The server
  requests 1 ms Windows timer resolution while running to avoid default timer
  quantization near 64 Hz.
- An opt-in Bluetooth input source now validates each physical `0x31` report's
  CRC and copies its shared payload byte-exact into virtual wired report
  `0x01`. Real feature `0x05` calibration is read and served only at runtime;
  its values and the physical address are never logged or persisted. Synthetic
  conversion/CRC tests pass. Its live run received 3,660 authenticated reports
  in five seconds. The virtual USB path then delivered 3,003 reports in 12.000
  seconds (250.3 Hz), full LX/LY and L2/R2 ranges, and exactly three requested
  Cross-button presses.
- Assassin's Creed Black Flag Resynced recognized the HID-only device as a
  native wired DualSense. The analyzed session captured 30,442 game HID output
  reports with 18 unique full payloads. Of those, 15,455 contained nonzero
  adaptive-trigger blocks: R2 modes `0x05` and `0x22`, and L2 mode `0x05`.
- The Bluetooth bridge now relays only the two 11-byte trigger blocks on a
  latest-value background writer, coalesces repeated states, sets only the R2
  and L2 update flags, and rebuilds the Bluetooth `0x31` CRC. It never blocks
  USB completion or touches rumble/audio/lightbar state. The user confirmed R2
  resistance while firing in Resynced. A verified zero-trigger game report was
  relayed before the test device was detached.

The M2.3 functional path has passed, but durability acceptance is not complete.
Remaining work is to capture the other planned trigger programs, run the
one-hour stability test, add a graceful server shutdown path, and exercise
repeated clean attach/detach cycles. Raw captures and logs remain outside the
repository under `C:\Users\patri\PS5Haptics\m23-*-hid-output.ndjson` and
`m23-*-server.*.log`.

M2.4 UAC1 composite enumeration is next. No cable-like native haptic audio was
expected or heard during the HID-only Resynced test because Windows had no
virtual DualSense audio endpoint to receive the game's four-channel stream.
