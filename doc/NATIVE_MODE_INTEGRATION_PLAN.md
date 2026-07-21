# One-Click Native DualSense Mode — Integration Plan

Status: GUARDED CANDIDATE VALIDATED 2026-07-20. The isolated branch
`feature/native-mode-bsod-mitigation` contains the authorized checkpoint plus
the reviewed BSOD mitigations, the deferred-cleanup UI refresh, and the 50%
native speaker default through app commit
`90e965e7df092b68c7d4c137960c5ceafb304787`. The unapproved Bluetooth-streamer
overhaul `9e9872b` is excluded. The final fresh package is
`C:\Users\patri\PS5Haptics\native-mode-bsod-guarded-build-20260720-r3`.

Three live validation phases passed on 2026-07-20. The first proved exact
virtual attach, render-pin keepalive, unchanged Sonar defaults, ordered
Stop/removal, keepalive release, and Bluetooth-pad reclaim. The second ran
Black Flag Resynced for about four minutes without alt-tab; the user confirmed
controls, triggers, native haptics, and controller-speaker audio were all good.
The server and exact virtual parent remained stable, all six Sonar default
roles stayed unchanged, and no fatal log or system crash event occurred. Stop
then deferred until the endpoint and parent disappeared, reclaimed the normal
X360 mapping, and the UI correctly returned to Start Native Mode.

The third phase used the final r3 package at the persisted 50% volume. The user
repeatedly alt-tabbed between Black Flag and other applications while the game
remained open, then confirmed controls, haptics, and sounds still worked. A
live snapshot after more than six minutes showed the r3 app/server and exact
virtual parent healthy, all six Sonar default roles unchanged, seven recovered
speaker rebuffers, and no fatal native marker or Windows critical/bugcheck
event. This validates the active Alt+Tab scenario that previously crashed.
No forced rapid pin-cycle/stress sequence was performed.

Controlled game-validation checklist:
1. Confirm explicit approval, the package hashes in `BUILD-INFO.txt`, trace
   readiness, the bounded scenario, and the stop procedure.
2. Confirm the existing elevation task. Re-run setup only if it is missing or
   `UsbipExePath` changed.
3. Start Native Mode: pad releases, child serves, silent attach, status
   Attached; confirm Windows render/capture defaults remain on Sonar.
4. Black Flag: input, triggers, native haptics telemetry, speaker effect, and
   the independent native speaker-volume control.
5. Stop Native Mode: virtual child disappears before the render keepalive and
   default-audio protections release; DS4Windows then reclaims the pad.
6. Only after the initial checks pass, run one bounded alt-tab/pin-cycle drill.
7. Exercise app-exit, pad-loss, and rapid Start/Stop only as separately agreed
   follow-ups, not in the first validation run.

## Goal

A DualSense connected over Bluetooth gets a single action inside DS4Windows:
**Start Native Mode**. DS4Windows releases the pad, launches the validated
`VirtualDualSenseUsbip` server as a child process, performs the elevated
`usbip attach` without repeated UAC prompts, and shows live status. **Stop
Native Mode** (or app exit, or pad loss) kills the child — which IS the clean
unplug — and DS4Windows reclaims the pad for normal mapping.

Non-goals for this milestone: microphone (Windows-stack blocked; endpoint
serves silence), in-process hosting of the emulator (child process is the
validated artifact), any change to the validated 0x36/0x31 protocol paths.

## Hard-won invariants (violating these re-fights today's battles)

1. **Teardown is `Process.Kill()` of the server child.** Never call
   `usbip detach` while an audio pin may be open: the vhci livelocks (the CLI
   can print success while URBs keep flowing). Server peer-loss unplugs
   cleanly — proven repeatedly, no stuck devnodes, no bugchecks.
2. `usbip attach` port numbers increment across cycles; nothing may assume
   port 1. (We never detach, so this mostly moots itself.)
3. The pad idle-times-out on **user-input silence** regardless of host
   traffic. Pad loss mid-session is NORMAL: the server logs
   `The physical pad is gone; restart serve after it reconnects.` — the
   manager must detect this line, kill the child, and surface "pad lost —
   press PS and start again" (auto-restart optional, see P5).
4. A pad power-cycle wedges synchronous writes on dead handles; the server
   already hardens this (fail-fast + CancelIoEx) — the manager only needs to
   watch for exit/log markers, never to "rescue" a wedged child except by kill.
5. `--mic` stays off. `--speaker-audio on --route auto` is the validated
   default for native mode.
6. VirtualDualSenseUsbip keeps `TreatWarningsAsErrors`; do not regress it.

## Architecture

```
DS4Windows (WPF, may be non-elevated)
  └─ NativeModeManager (new, DS4WinWPF)
       ├─ releases the DS4Device (stop mapping, close HID handle)
       ├─ spawns child:  VirtualDualSenseUsbip.exe serve
       │     --configuration composite --input bluetooth
       │     --speaker-audio on --route auto [--capture <log>]
       │     (stdout piped → readiness, stats, pad-loss markers)
       ├─ requests attach via the elevation broker (below)
       ├─ polls PnP for USB\VID_054C&PID_0CE6 arrival (attached = success)
       └─ Stop/exit/pad-loss → child.Kill() → poll device removal → reclaim pad
```

### Elevation broker: pre-authorized scheduled task (one UAC, ever)

Current lifecycle guarantees implemented around that architecture:

- Before any protected startup action, DS4Windows must capture a non-null
  render/capture default-endpoint snapshot and register the Windows audio
  notification monitor. Failure of either prerequisite aborts Native Mode.
- Arrival and removal use `SetupDiGetClassDevs` with
  `DIGCF_PRESENT | DIGCF_ALLCLASSES`, then match only the fixed parent instance
  `USB\VID_054C&PID_0CE6\DS4WSPKCOMP001`; cached/phantom devnodes do not count.
- Teardown keeps the render stream and other protections when a removal probe
  fails or either the endpoint or exact present parent remains. A deliberately
  retained monitor handles notifications and a slower one-second removal poll;
  probe failures log once until a successful probe recovers.

`usbip attach` requires admin. To avoid a UAC prompt per session:

- First Start Native Mode run (or an explicit "Set up native mode" button)
  creates — with ONE UAC prompt — a Windows scheduled task
  `DS4Windows\NativeDualSenseAttach`, RunLevel=Highest, no triggers,
  executing the **fixed, literal** command line:
  `"C:\Program Files\USBip\usbip.exe" attach -r 127.0.0.1 -b 1-1 --serial DS4WSPKCOMP001 --once`
  The task definition embeds the full command; the on-demand trigger passes
  NO arguments, so there is no elevation-of-argument injection surface.
- Subsequent sessions call `schtasks /run /tn "DS4Windows\NativeDualSenseAttach"`
  from user context — runs elevated silently.
- Success is confirmed by PnP device arrival (poll ≤10 s), not by schtasks
  exit code. Failure surfaces a readable error (usbip not installed → point
  at doc/M2 runbook; task missing → offer setup; timeout → check server log).
- usbip.exe path: default `C:\Program Files\USBip\usbip.exe`, overridable in
  settings; validate existence before anything else.

If DS4Windows itself is elevated, skip the task and invoke usbip directly.

## Phases (each ends: builds clean + tests pass + committed)

### P1 — Server child lifecycle (no UI)
`DS4WinWPF/DS4Control/NativeModeManager.cs` (or fitting namespace):
- Locate server exe: output dir first (see P4 packaging), then
  `utils/VirtualDualSenseUsbip/bin/Release/net8.0/` for dev runs.
- Start(args) → Process with redirected stdout/stderr; parse readiness
  (`USB/IP server listening`), pad-open failure (`No physical Bluetooth
  DualSense`), pad-loss marker, and the periodic stats lines (expose latest
  stats snapshot). All lines forwarded to AppLogger with a `[native]` prefix.
- Stop() → Kill(entireProcessTree) + await exit + poll PnP removal (≤5 s).
- Events: StateChanged(Starting/Serving/Attached/PadLost/Stopped/Faulted).
- Unit-testable line-parser (pure static): marker classification from
  sample log lines (copy real lines from artifacts/m3-audio logs).

### P2 — Pad release & reclaim inside DS4Windows
- On Start: identify the target DS4Device (DualSense on BT), stop its
  mapping/output cleanly (existing hot-plug/removal machinery — find how
  DS4Windows handles device removal and reuse it), ensure its HID handle is
  CLOSED before the child spawns (the child needs sole ownership).
- Suppress auto-reconnect/re-open of that pad while native mode is active
  (a guard in the hot-plug path keyed by MAC or by "native mode active").
- On Stop: re-trigger enumeration (existing hotplug rescan) to reclaim.
- Careful: DS4Windows sometimes runs elevated; nothing here needs elevation.

### P3 — UI + settings
- Device Options → DualSense tab (where the Phase-2 haptics settings live):
  a "Native passthrough" group: Start/Stop button, status text bound to
  NativeModeManager state, speaker-audio checkbox, route combo (reuse
  existing AudioOutputRoute enum), and a first-run "Set up elevation" flow.
- Persist per-controller: NativeModeSpeakerAudio (default true), route,
  plus a global UsbipExePath — follow the existing
  DualSenseControllerOptions/DTO persistence pattern from Phase 2 exactly.
- Status must show the pad-lost case with the "press PS, then Start" hint.

### P4 — Packaging + elevation broker
- DS4WinWPF.csproj: copy the VirtualDualSenseUsbip publish output into the
  DS4Windows output (`native\VirtualDualSenseUsbip.exe` + deps) via a
  build target or ProjectReference with SetTargetFramework — the app must
  find it beside itself in release builds. Keep `dotnet build
  .\DS4Windows\DS4WinWPF.csproj -c Release /p:platform=x64` working from
  repo root (the vendored-lib HintPath quirk means plain `dotnet build`
  fails — do not break the documented invocation).
- Elevation broker class: EnsureAttachTask() (create via one elevated
  schtasks /create), RunAttach() (schtasks /run + PnP arrival poll),
  IsTaskPresent(). Fixed literal command line as specified above.

### P5 — Lifecycle safety net
- App exit / service stop with native active → Stop() synchronously first.
- Child unexpected exit → state Faulted + reclaim pad + log tail surfaced.
- Pad-loss marker → auto Stop() + status "pad lost — press PS, then Start
  Native Mode again". (Optional stretch: auto-restart when the pad
  reappears within 60 s — only if clean to implement.)
- Only ONE native session at a time; Start disabled while a session exists.

## Build & test contract

- Always publish the server to a new commit-keyed staging directory and give
  it a separate `BaseOutputPath`; `dotnet publish -o` reuses stale files.
- Build the app to a new commit-keyed output and pass
  `NativeDualSensePublishDir` explicitly. Refuse to reuse an existing package
  directory.
- Required package payloads are `native\VirtualDualSenseUsbip.exe`,
  `native\VirtualDualSenseUsbip.dll`, and
  `native\fixtures\dualsense_usb_0ce6\configuration.bin`.
- Run server `selftest`, `devicetest`, and repeated `servertest` from the
  fresh publish output. Do not validate a stale default `bin` output.
- Run the focused filter
  `FullyQualifiedName~NativeMode|FullyQualifiedName~Haptics` and the complete
  DS4WindowsTests assembly. At the 2026-07-20 checkpoint these were 143/143
  focused and 150/153 full; the three full-suite failures are the pre-existing
  XML snapshots.
- Offline builds and tests do not require or access a connected controller.
  Never run `serve`, attach, a scheduled attach task, or a hardware/game test
  without explicit approval.

## Live validation (explicit approval required)

Use the controlled checklist at the top of this document. The first run is
intentionally bounded; later lifecycle drills are separate validation steps.
