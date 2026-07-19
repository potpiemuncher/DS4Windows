# DS4Windows Bluetooth Audio/Haptics Project Handoff

Last updated: 2026-07-19 (second session: native speaker audio + microphone)

## Mission

Extend DS4Windows so a physical DualSense connected over Bluetooth can provide
DSX-like listening audio, voice-coil haptics, game-rumble-to-haptics conversion,
and eventually native PC-game DualSense haptics plus adaptive triggers.

Repository state:

- Working copy: `C:\Users\patri\PS5Haptics\DS4Windows`
- Fork: `https://github.com/potpiemuncher/DS4Windows.git`
- Upstream: `https://github.com/ds4windowsapp/DS4Windows.git`
- Active branch: `feature/bt-audio-haptics`
- Latest implementation checkpoint: `1e454b7` (streamer stability); native-game
  haptics end-to-end validation documented 2026-07-19 (see below)

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
- Black Flag Resynced authored adaptive-trigger output and the user felt R2
  resistance while firing.
- A controlled virtual-USB four-channel tone reached native haptic channels 3/4
  and the user felt it at about 7:20 PM on 2026-07-18.
- On 2026-07-19, a controlled three-pulse 120 Hz system-audio test reached the
  physical controller through `System Audio + Rumble` on the stabilized build;
  the user confirmed exactly three pulses.
- **On 2026-07-19 the full native-game path was validated end to end.** Assassin's
  Creed Black Flag Resynced ran against the virtual wired DualSense (usbip
  composite) while the physical pad stayed on Bluetooth; the game's own haptic
  audio on isochronous channels 3/4 was relayed to the physical controller as
  `0x36` reports, and **the user physically felt it in their hands.** This closes
  the last open Phase 4 milestone.

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
- `ebdf780` records the successful M2.1 usbip-win2 driver qualification.
- `1d150e4` attaches the HID-only virtual DualSense through the qualified VHCI.
- `bc21906` bridges authenticated physical Bluetooth input at 250 Hz.
- `860014d` relays native game-authored adaptive-trigger programs over
  Bluetooth without blocking USB output completion.
- `587e458` adds the exact four-interface UAC1 composite configuration and
  healthy Windows speaker/microphone endpoint enumeration.

Latest user-validated fixes:

- `18e05be` switches haptics-only streaming to self-contained `0x36` reports
  and adds `DSHapticsProto test36` plus endpoint metering. This fixed the case
  where the stream looked active but the controller produced nothing.
- `d0ecd89` carries Opus listening audio and haptics together in self-contained
  `0x36` reports, adds 7.1 downmixing, and adds a soft limiter. This fixed
  controller audio and improved sound quality.
- `d5046ed` lets pure rumble synthesis use full PCM amplitude. This fixed weak
  rumble-to-haptics pulses.
- `1e454b7` stabilizes the shared Bluetooth HID output path: all `0x31`, `0x32`,
  and `0x36` writers are serialized; interrupt writes honor their timeout and
  report Win32 errors; startup publishes only one streamer; and replacement
  streams use generation-specific cancellation so an older thread cannot resume.

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

The currently launched exact validated executable is:

`C:\Users\patri\PS5Haptics\stability-final-build\x64\Release\net8.0-windows\DS4Windows.exe`

Its `DS4Windows.dll` SHA-256 is
`E35D61E8E1A45050A07928AED12C5459290C77AE64402C08E201C2CA85F15240`.

Do not rely on an old process ID; check the current process and binary path.

## Bluetooth Streamer Stability Fix (2026-07-19)

The short approximately 0.54-second stream abort was deterministic: 50 failed
writes multiplied by the 10.667 ms haptic slot. Three producers were racing the
same Bluetooth HID output endpoint (normal state `0x31`, amplifier setup `0x32`,
and continuous stream `0x36`), the supplied interrupt-write timeout was ignored,
and startup/settings notifications could create multiple streamer generations.

`1e454b7` fixes all three layers:

- `HidDevice` has one ordered output-report gate per physical HID device.
- Interrupt writes use a bounded, non-alertable overlapped wait. A timed-out
  request is canceled by its exact `OVERLAPPED` pointer and completion is drained
  before its stack/event storage is released.
- Write failures now preserve the first/last Win32 error and report ID/size.
- Settings loading cannot start the streamer before `StartUpdate`; initial
  publication is atomic.
- Every stream generation has its own cancellation source. A late old thread
  cannot observe a new generation's `running` state or clear its active state.

Validation on the primary PC:

- Focused `DualSenseHapticsStreamerTests`: 12/12 passed after the final change.
- Startup produced exactly one streamer. Live changes from Rumble-to-Haptics to
  Mix, Mix plus listening audio, and back to Mix each produced one clean
  replacement with no write failure, abort, or stream error.
- A 30-second music/Mix capture produced 26 valid samples: controller and
  DS4Windows present throughout, 37,783.1 B/s average outbound
  (37,439.7-37,937.8), zero ACL flushes, minimum four write credits, and 1.0%
  average / 1.5% maximum DS4Windows CPU.
- The exact final binary then restarted directly into persisted Mix. A 10-second
  check produced 37,835.2 B/s average outbound (37,647.9-38,043.5), zero ACL
  flushes, and no monitor/log errors.
- A final controlled three-pulse 120 Hz test was physically confirmed as exactly
  three controller pulses.

Approximately 37-38 kB/s is the healthy single-writer rate for this continuous
398-byte stream at about 93.75 reports/s. Do not classify it as low merely
because an older mixed-traffic baseline exceeded 40 kB/s; roughly double this
rate would instead indicate duplicate writer generations.

At 10:50:45 the controller logged read failure 1167 and Windows `HidBth` event 2
(out of range or unresponsive). The user confirmed the controller had reached
its idle timeout and powered off, then manually turned it back on. It reconnected
normally; this event is not evidence of a writer or teardown regression.

## Native-Game Haptics Validation (2026-07-19) — MILESTONE MET

The previously-open gap ("a native game has not yet been shown to emit nonzero
channels 3/4 through this path") is now closed. Assassin's Creed Black Flag
Resynced was run against the attached composite virtual DualSense
(`serve --configuration composite --input bluetooth --capture ...`, then
`usbip attach ... --serial DS4WSPKCOMP001 --once`). DS4Windows was closed for the
run so the emulator's `--input bluetooth` bridge was the sole owner of the
physical pad; the native game does not need DS4Windows or XInput.

Evidence (server ISO meter, `artifacts/m2-native/blackflag-20260719-111224.*`):

- Clean pre-game baseline: audio streaming interfaces at alt 0, no ISO ch3/4
  activity, `bt36=0`.
- ISO stream opened at the correct format: ten 384-byte packets per URB,
  ~99 URBs/s (~370 KiB/s), interval 4.
- Multiple game-authored haptic bursts on channels 3/4 with channels 1/2 either
  silent (haptic-only) or co-active (explosions driving speaker + actuators):
  observed ch3/ch4 peaks of 19.1/23.4%, 11.1/9.9%, and **50.4/24.2%**; ch3/ch4
  RMS up to ~4.4%.
- Return to silent baseline (ch3/ch4 → 0.00, relay counter frozen) whenever
  gameplay paused — confirming the bursts were game-driven, not an artifact.
- **461 haptic `0x36` reports relayed to the physical pad, zero Bluetooth write
  errors across the whole session (~285k ISO frames, 0 `bt-errors`).**
- No synth exists in this path, so all channel-3/4 energy is game-authored.
- The user physically confirmed feeling it.
- Graceful teardown: game closed, `usbip detach -p 1`, server stopped, DS4Windows
  restarted. Virtual device removed cleanly, no stuck devnode, no bugcheck; the
  UNLINK/teardown fix held.

Known follow-up (not a blocker): `bt-underrun` rose to ~90 because the game's
haptics are burstier than the earlier steady-tone test. Add a small prebuffer /
deeper queue to `BluetoothDualSenseInputSource`'s haptic relay so bursty content
does not momentarily drain the relay queue. Zero write errors throughout, so this
is a smoothing refinement, not a correctness fix.

## Native Speaker Audio + Microphone (2026-07-19, commits 60ee5c6 + ee88e73)

Both remaining audio paths are IMPLEMENTED and offline-validated; live
validation is pending one interrupted run (details below).

- **Speaker relay** (`serve --speaker-audio on`): ISO OUT channels 1/2 are
  resampled 48->45 kHz (16:15 linear, `StereoLinearResampler`), Opus-encoded
  (Concentus, 200-byte CBR frames, complexity 5), and carried in the existing
  0x36 reports — route byte `[142]` = (0x16 headphone / 0x13 speaker) | 0x80
  from the debounced headset-plug bit (BT report byte 55 bit 0), Opus at
  `[144..343]`. The amplifier SetStateData init (fixed `[1]=0x10`) is sent once
  per link before the first audio report or mic enable. A silence gate
  (-46 dBFS, 2 s) keeps the stream off while Windows primes an open pin with
  silence; the stream aborts after 50 consecutive write failures or when the
  BT link dies.
- **Microphone** (`serve --mic on`): opening capture interface 2 alt 1 sends
  the pad the mic-enable 0x32 (config packet 0x91 len 1, payload 0x03 on /
  0x02 off, rolling sequence starting at 2 to avoid colliding with the fixed
  0x10 amp init). Mic input reports are 0x31 with byte 1 **bit 1** set (this
  pad's normal gamepad reports carry byte1 low-nibble 0x1, e.g. 0xA1 — bit 0
  set is NORMAL; only bit 1 marks mic). They are CRC-checked, filtered out of
  the gamepad parser, Opus-decoded (71-byte mono 48 kHz frames at [3..73]),
  duplicated to stereo, and served on ISO IN EP 0x82 as paced compact
  RET_SUBMITs (silence when the ring is empty; 47/48/49-frame hysteresis; the
  IN pump has its own monotonic timeline, pending cap 32, shared ceiling 96,
  full UNLINK integration). The mic source frame rate is measured for 1 s
  after enable; ~93.75/s (45 kHz-effective) sources are resampled to 48 kHz.
- UAC1 feature-unit mute/volume SETs (FU2 playback, FU5 capture) now scale
  the respective PCM paths (`ControlEndpoint.AudioScaleChanged`).
- Windows enumerated healthy `Speakers (10- DualSense Wireless Controller)`
  and `Headset Microphone (10- DualSense Wireless Controller)` endpoints, and
  a 1 s WASAPI capture from the virtual mic delivered 92,160 samples of paced
  silence through the new ISO IN path — the USB side is proven.
- The first live run ended when the physical pad hit its idle timeout
  (error 21 then 1167) seconds after the audio interfaces opened — the same
  pad power-off seen at 10:50 on the stability day. Keep the pad awake with
  occasional input during audio tests.
- `DSCompatProbe playtone <endpointNameSubstring|id> [s] [hz] [amp]` renders a
  sine to a specific render endpoint (never the default device);
  `DSCompatProbe recordmic <endpointNameSubstring|id> [s] [out.wav]` captures
  a specific mic endpoint with RMS/peak and optional WAV.
- Codex (GPT-5.6) reviewed both commits over the codex-bridge; all High and
  Medium findings are fixed in ee88e73 (unconditional relay teardown in the
  session finally, first-audio preservation via TrimToNewest instead of a
  reset, bounded per-tick encoder work, pristine silence-packet fallback for
  any non-200-byte encode, sequence-collision fix, capture-reopen ASRC and
  decoder resets, gapped ISO IN descriptor tolerance, clamped packet fills).

Live validation recipe (flags are opt-in; both default OFF):

```powershell
# close DS4Windows first; pad awake on BT
dotnet run --project utils/VirtualDualSenseUsbip -c Release -- serve `
  --configuration composite --input bluetooth --speaker-audio on --mic on `
  --route auto --capture artifacts/m3-audio/<date>.jsonl
# elevated: usbip attach -r 127.0.0.1 -b 1-1 --serial DS4WSPKCOMP001 --once
# speaker: utils/DSCompatProbe/... playtone "DualSense" 6 440   (user listens)
# mic:     utils/DSCompatProbe/... recordmic "DualSense" 8 mic.wav (user speaks)
# teardown: elevated usbip detach -p 1, stop serve, restart DS4Windows
```

## Live Validation Results (2026-07-19 afternoon, commit c372e4d)

**Speaker relay: USER-VALIDATED at "99%" quality — final state ZERO
rebuffers/underruns/BT errors over multi-minute music sessions.** The
audible-artifact hunt went through five real causes; keep all of these fixes:

1. Prebuffer 2→4 frames, queue depth 8 (crackle from 17 ms arrival jitter).
2. Stall-skip: with listening audio, missed slots beyond two are skipped
   instead of burst-caught-up (a GC-pause catch-up burst dequeued that many
   frames instantly and drained the queue; haptics-only keeps the 100 ms
   catch-up for rumble timing).
3. Silence gate + idle re-arm must both honor the energy condition (Windows
   keeps an open pin primed with silence indefinitely).
4. **THE BIG ONE: the usbip-win2 vhci's virtual frame clock delivers ISO OUT
   at 98.7 URBs/s, not 100 — a permanent ~1.3 % deficit** (visible as
   `urbs/s=98.7` in every session log). Completion pacing cannot fix it
   (submissions themselves arrive at 98.7/s; the ideal-timeline completion
   change in 9e0ff25 is still correct and keeps average completion pacing
   exactly real-time). The fix is an adaptive resampler ratio
   (`StereoLinearResampler.SetRateTrim`, integral servo on the encoded-queue
   level, gain 3e-5/frame-error, authority ±2.5 %): production locks to the
   driver's real rate within seconds. The 0x36 cadence stays fixed at
   93.75/s (the pad's clock). Result: queue sits exactly on target (5) with
   zero dry-outs.
5. Rebuffer events log ring depth/ISO age/spacing (`Speaker rebuffer #N`)
   so any future artifact names itself.

**Microphone over Bluetooth: BLOCKED BY THE WINDOWS STACK — do not re-litigate
without new evidence.** Enabling the pad mic (0x32 config packet 0x03) makes
Windows itself terminate the entire Bluetooth link. Proof
(artifacts/m3-audio/bt-micdrop.etl, BTHPORT HCIRAW trace): host-initiated
`HCI_Disconnect` (opcode 0x0406, handle, reason 0x13) with completion reason
0x16 ("terminated by local host"), 0.25–1.3 s after mic streaming starts;
HidBth logs event 2 ("unresponsive") at the same instant. No L2CAP signaling,
no config/QoS/mode-change, no eSCO attempt, no protocol anomaly precedes it.
The pad behaves perfectly: valid CRC'd 71-byte Opus mono frames at 100.2/s
(true 48 kHz — no 45 kHz slot slaving on input; ASRC unnecessary), decoded
speech RMS confirmed in userspace right up to the drop. Died identically
under every variant tried: bare enable with zero outbound traffic; 0x36
stream carrying the mic-presence config bit (0xFE→0xFF, mirroring the
dongle's 0x7E→0x7F); SetStateData audio-allow bits masked while mic active;
dongle-exact init order (feature reads 0x09/0x20/0x22/0x05/0x70 +
MicSelect=1 SetStateData, 150 ms, then enable); HidD_SetNumInputBuffers(512).
More outbound traffic correlates with a faster kill (~0.3 s vs ~1.3 s bare).
Consistent with the ecosystem: no shipped tool captures the DualSense mic
over stock Windows Bluetooth (Codex could find none), and awalol/DS5Dongle
exists precisely to be its own Bluetooth host hardware.

`serve --mic on` now refuses with an explanatory error (`--mic force` for
protocol experiments only). The virtual mic endpoint still enumerates and
serves paced silence — the full ISO IN pipeline is implemented, tested, and
proven live (92,160 samples captured via WASAPI), so a future mic SOURCE
(wired-pad passthrough, dedicated BT-host dongle, or a Windows-stack
workaround) plugs straight in.

Diagnostics added: `VirtualDualSenseUsbip mictest [s] [--state masked|full]
[--no-amp] [--no-stream] [--dongle-init]` (standalone BT mic probe, no
USB/IP/elevation, per-second frame counts + decoded RMS + link verdict) and
`artifacts/m3-audio/trace-bt.ps1` (elevated 40 s BTHPORT/BTHUSB HCI trace;
parse with `tracerpt <etl> -o out.xml -of XML -lr -y`, then look for
`BIP_Data` `0x0604..` host Disconnect commands and `0x0504..` completion
events — BIP_Type 1=HCI cmd, 2=HCI event, 3=ACL in, 4=ACL out).

Mic-path options going forward: (a) wired passthrough — when the pad is on
USB the real mic works natively, zero work; (b) a dedicated BT-host dongle
(awalol/DS5Dongle, RP2350-based, open source) — mic works there today;
(c) deep Windows work (filter driver / profile driver on HidBth's territory)
— research-grade effort, unbounded.

## FULL NATIVE EXPERIENCE VALIDATED IN-GAME (2026-07-19 ~14:10, build 7259568)

Assassin's Creed Black Flag Resynced, physical pad on Bluetooth, main audio
on the user's headset: raising the sail produced the sail sound FROM THE
CONTROLLER SPEAKER simultaneously with game haptics and adaptive triggers —
user verdict "its all working". Telemetry: game-authored ch1/2 speaker cues
(peaks ~31%) co-active with ch3/4 haptics (peak 50.8%) relayed in shared
0x36 reports, zero Bluetooth errors, zero speaker underruns during streams;
the game engine cycles the playback pin per cue cluster and the
silence-gated stream follows cleanly. Music listening on the same build:
0 rebuffers / 0 drops / 0 errors, user "99%".

Operational notes that MUST survive into the ergonomics work:
- **Teardown = stop the server process.** `usbip detach` livelocks whenever
  the audio engine still holds the playback pin (CLI may even print success
  while URBs keep flowing); server stop (peer loss) unplugs cleanly — proven
  ~8 times today, no stuck devnodes, no bugchecks.
- Port numbers from `usbip attach` increment across cycles; never hardcode
  `-p 1` (use `usbip port` first) if detach is ever needed.
- A pad power-cycle wedges synchronous writes on the dead handle; the
  fail-fast + CancelIoEx hardening (90c916c) keeps the session and any
  detach from freezing. Serve must be restarted after the pad reconnects.
- The pad idle-times-out on no USER input regardless of host traffic.

## Current Limitation

Native input, adaptive triggers, haptics, and controller-speaker audio are
all validated end to end over Bluetooth in a real game. The microphone leg
is blocked by Windows Bluetooth stack behavior (see above) — the virtual mic
endpoint serves silence until a viable mic source exists (wired passthrough
or BT-host dongle hardware). The DS4Windows in-app path still lacks mic for
the same reason. Next milestone: fold serve+attach into DS4Windows as a
one-click "native DualSense mode" with auto-teardown (= stop the server).

The primary PC previously bugchecked at 7:24 PM during removal of
`USB\VID_054C&PID_0CE6\DS4WSPKM26001`. The minidump reports
`0xA IRQL_NOT_LESS_OR_EQUAL`, `AV_nt!RtlpHpVsChunkFree`, and
`IoFreeIrp -> IopUserCompletion`; PnP black-box data names that exact virtual
device with problem code 24. The small dump cannot prove the responsible
driver, but an emulator race was found where paced ISO work left the pending
table before RET_SUBMIT, allowing UNLINK status 0 to precede a late RET_SUBMIT.
The race is fixed, covered offline, and live-revalidated twice on the primary
PC under USB/IP WPP tracing. Both active-ISO detach traces reconciled every
request with no failed, duplicate, orphaned, or late completion. The user
accepted that evidence and declared the teardown issue fixed; the primary-PC
safety hold is lifted.

## Phase 4: Native DualSense Game Compatibility — ACHIEVED (2026-07-19)

The core goal is met end to end. The architecture is a user-space virtual wired
DualSense composite USB device (HID plus UAC1 audio) exposed through usbip-win2's
signed VHCI driver. The real controller stays on Bluetooth; native game HID output
(adaptive triggers) and isochronous haptic audio (channels 3/4) are translated and
forwarded through the proven Bluetooth `0x36`/`0x31` streamers, and a real native
game (Black Flag Resynced) was felt on the physical pad.

### Remaining work (polish / optional, none blocking the core result)

1. **Relay smoothing**: add a small prebuffer / deeper queue to the haptic relay
   in `BluetoothDualSenseInputSource` so bursty game haptics stop draining the
   queue (observed `bt-underrun` ~90; zero write errors — this is feel polish).
2. **Endurance**: a one-hour continuous native-game session with underrun/error
   counters and a clean detach at the end.
3. **Ergonomics**: the run currently requires closing DS4Windows, launching the
   emulator, and an elevated `usbip attach`. Fold the attach/serve lifecycle into
   DS4Windows (or a helper) so it is one action, and auto-detach on exit.
4. **Trigger coverage**: capture the remaining trigger programs beyond the two
   Black Flag modes; test more native titles (SDL3, other libScePad games).
5. **Optional features — DONE 2026-07-19 (offline)**: native-game speaker audio
   relay (channels 1/2) and the microphone path are implemented in
   `VirtualDualSenseUsbip` (no separate capture driver needed — the usbip
   composite's own mic endpoint serves real pad audio). Live pad validation
   pending one clean run.
6. **Upstream**: when the user is ready, prepare the PR to ds4windowsapp/DS4Windows
   with credits (egormanga/SAxense, awalol/DS5Dongle) and the usbip-win2 BSD notice.

### Milestone status and order (all core milestones PASSED):

1. **M2.0 complete**: exact wired DualSense descriptors/fixtures live under
   `utils/DSCompatProbe/fixtures/dualsense_usb_0ce6`.
2. **M2.1 PASSED (2026-07-18)**: usbip-win2 0.9.7.8 x64 installed on the primary
   PC. Both `usbip2_ude.sys` and `usbip2_filter.sys` are signed by "Microsoft
   Windows Hardware Compatibility Publisher" (WHQL, EKU 1.3.6.1.4.1.311.10.3.5)
   and load/run under Secure Boot + HVCI with testsigning OFF. Services are
   demand-start and Running; emulated host controller present as
   `ROOT\USB\0002` ("USBip 3.X Emulated Host Controller", status OK). No certs
   added to Root/TrustedPublisher (VIIPER test-CA warning is stale for 0.9.7.8),
   no Code Integrity blocks, security posture unchanged. Audit trail in
   `C:\USBIP-M2.1-Audit`. CLI at `C:\Program Files\USBip\usbip.exe`; uninstall
   via `C:\Program Files\USBip\unins000.exe`.
3. **M2.2 protocol foundation complete**: `utils/VirtualDualSenseUsbip`
   implements USB/IP management framing, 48-byte URB headers, fragmented reads,
   bounded transfer parsing, serialized responses, pending requests, and
   UNLINK. Its protocol and fragmentation self-tests pass.
4. **M2.3 functional path PASSED; durability acceptance remains**: the signed
   VHCI driver attaches `054c:0ce6` as `DS4WSPKHID001`; Windows binds healthy
   USB Input Device and HID game-controller nodes. The 41-byte HID-only
   configuration retains the exact 289-byte captured report descriptor. EP0,
   pairing report `0x09`, firmware report `0x20`, 250 Hz interrupt IN,
   interrupt OUT capture, and UNLINK work. A 30-second live read delivered
   7,504 reports at 250.1 Hz without failure. In neutral-input mode,
   calibration report `0x05` intentionally stalls instead of returning
   fabricated sensor data.
   The opt-in `--input bluetooth` bridge is live-validated:
   it validates physical `0x31` CRCs, maps bytes 2..64 to wired `0x01` bytes
   1..63, and forwards real calibration at runtime without persisting hardware
   values. `inputtest 5` received 3,660 valid authenticated reports; an
   end-to-end USB watch delivered 3,003 reports in 12.000 s (250.3 Hz), full
   0..255 LX/LY and L2/R2 ranges, and exactly three requested Cross presses.
   Assassin's Creed Black Flag Resynced recognized the virtual wired pad and
   generated 30,442 captured HID outputs in the analyzed session. Two distinct
   trigger blocks were observed (R2 modes `0x05` and `0x22`, L2 `0x05`); 15,455
   reports carried nonzero trigger data. A non-blocking, coalescing,
   trigger-only Bluetooth relay rebuilt the `0x31` CRC, and the user confirmed
   physical R2 resistance while firing. Remaining M2.3 work: capture the other
   planned trigger programs, run one-hour stability, add graceful server
   shutdown, and repeat clean attach/detach cycles.
5. **M2.4 PASSED (2026-07-18)**: the exact captured 227-byte composite
   configuration is live through VHCI with Audio Control, playback Audio
   Streaming, capture Audio Streaming, and HID interfaces. Windows starts the
   MEDIA child and creates healthy `Speakers` and `Headset Microphone`
   endpoints. The audio engine selects playback interface 1 alt 1 and submits
   isochronous OUT traffic. Live iteration identified and implemented the UAC1
   speaker mute/volume GET/SET controls and volume range queries required by
   Windows. The provisional -100..0 dB / 1 dB range must be replaced if a
   future wired control capture proves Sony uses different values.
6. **M2.5 controlled timing PASSED (2026-07-18)**: playback uses ten 384-byte
   packets per URB and paced asynchronous completion at 100.0 URBs/s
   (375.1 KiB/s). A 105-second run stayed connected at the exact long-term rate.
   The first immediate-completion experiment produced an invalid approximately
   11,000 URBs/s / 41 MiB/s loop; never restore immediate ISO completion.
7. **M2.6 relay PASSED, including native-game validation (2026-07-18/19)**:
   48 kHz signed 16-bit channels 3/4 are reduced to 3 kHz signed 8-bit stereo
   and sent in authenticated 398-byte Bluetooth report `0x36` frames. The
   controlled tone measured 9.56-10.61% RMS and 15% peaks on channels 3/4,
   produced zero Bluetooth write errors, and the user felt it at about 7:20 PM.
   A separate 120 Hz / 25% run sent 503 haptic reports with zero write errors.
   Silence suppression drains a six-report tail and then idles. The native
   11-byte adaptive-trigger relay remains proven in M2.3.
   **On 2026-07-19 a real native game (Black Flag Resynced) drove this path**:
   game-authored channel-3/4 haptics (peaks to 50.4%) relayed to the physical
   pad as 461 `0x36` reports with zero write errors, correlated with in-game
   events, and the user felt it. See "Native-Game Haptics Validation" above.
   Follow-up: add a relay-queue prebuffer to smooth bursty-content underruns.
8. **Live teardown fix PASSED (2026-07-18)**: the 7:24 PM bugcheck happened
   while this composite instance was being removed. The emulator's
   UNLINK/completion race is fixed with an atomic
   pending/completing/canceled state machine and a regression test that
   forbids late RET_SUBMIT after successful UNLINK. All offline builds and
   selftest/devicetest/servertest pass; 50 consecutive servertest runs also
   passed. Active Memory Dump, verbose KMDF logging, and both USB/IP WPP
   providers were then armed on the primary PC. The first live active-ISO
   detach reconciled 16,093 submits as 16,089 normal RET_SUBMITs plus exactly
   four once-canceled outstanding requests. A second independent cycle
   reconciled 1,171 submits as 1,167 normal returns plus the same expected
   four cancellations. Both traces had zero nonzero completion statuses, ISO
   errors, orphan/duplicate sequences, protocol traffic after plug-out, trace
   loss, or `force delete`; composite unregister and port cleanup succeeded.
   Evidence is under
   `C:\Users\patri\PS5Haptics\USBIP-M2.6-Safety-Audit`. The second batch
   harness stopped after that pass because it treated an unavailable process
   exit-code property as failure; its audio test actually completed normally.
   The user elected not to continue Driver Verifier/server-loss stress and
   declared the teardown issue fixed.

Installing the VHCI kernel driver is a material system change and briefly
restarts USB 3.0 hubs/devices. Before doing it, get explicit user approval,
have the user save work and leave active games/calls/transfers, record Secure
Boot/HVCI/testsigning state, create a restore point if available, and verify the
downloaded package/signatures. Do not weaken Windows security to make the
driver load.

The primary PC has completed installation, qualification, and traced teardown
revalidation. Do not reinstall or cycle USB hubs without a new reason and the
same system-change precautions. Normal attach/detach testing is permitted, but
re-arm WPP tracing and dump capture before testing any new teardown, pacing, or
USB/IP protocol change.

## Working Rules for the Next Agent

- Inspect `git status` before editing and preserve unrelated user work.
- Keep USB/IP detached when it is not being tested. The current UNLINK fix is
  live-validated; re-arm WPP tracing and dumps before changing teardown or ISO
  completion behavior.
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
