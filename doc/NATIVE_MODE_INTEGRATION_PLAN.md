# One-Click Native DualSense Mode — Integration Plan

Status: IMPLEMENTED 2026-07-19 — all five phases coded by Codex (GPT-5.6) via
codex-bridge, each phase reviewed, independently rebuilt/retested, and pushed
by Claude (commits 7af4df3, dc0740b, 9a2ab5e, 5459065, 0830efc; 61 NativeMode
tests + 12 haptics tests green; whole-feature audit: no detach calls, no mic
flags, schtasks via absolute System32 path, only the intended child-tree
kill). LIVE VALIDATION PENDING — checklist at the bottom; also still pending:
one `dotnet publish utils/VirtualDualSenseUsbip -c Release -o
utils/VirtualDualSenseUsbip/obj/native-staging` run (blocked today only
because the live gameplay session locks the emulator's bin output) followed
by a packaged release-shaped build.

Live validation checklist (Claude + Patrick, next session):
1. Publish staging + rebuild app; confirm native\ payload lands in output.
2. First run: Set up native mode (single UAC creates the scheduled task).
3. Start Native Mode: pad releases, child serves, silent attach, status
   Attached; Black Flag full-native check (input/triggers/haptics/speaker).
4. Stop: unplug + reclaim, mapping works again. App-exit-while-active also
   tears down. Rapid Start/Stop clicking stays sane.
5. Pad idle-timeout: auto-stop, pad reclaims on PS press, status hint shown.
6. Elevated-DS4Windows direct-attach bypass path.
7. GUI log stays free of periodic ISO/AUDIO stat lines.

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

## Build & test contract (Codex: follow exactly)

- Main app: `dotnet build .\DS4Windows\DS4WinWPF.csproj -c Release /p:platform=x64`
  from `C:\Users\patri\PS5Haptics\DS4Windows`.
- **VirtualDualSenseUsbip: build ONLY with `-o <temp dir>`** — its default
  bin exe is LOCKED by the live gameplay session right now. Do not kill any
  running VirtualDualSenseUsbip/DS4Windows process; do not run serve/attach;
  do not touch the Bluetooth pad. Offline validation only:
  `<tempout>\VirtualDualSenseUsbip.exe servertest utils\DSCompatProbe\fixtures\dualsense_usb_0ce6`.
- Focused tests: `dotnet test .\DS4WindowsTests\DS4WindowsTests.csproj
  --filter FullyQualifiedName~DualSenseHapticsStreamerTests` must stay green
  (12/12); add NativeModeManager parser tests beside them. The 3 profile-XML
  snapshot failures in the FULL suite are pre-existing upstream — ignore.
- Commit per phase on `feature/bt-audio-haptics`, imperative messages,
  crediting line: `Co-Authored-By: Codex (GPT-5.6) <noreply@openai.com>`.

## Live validation (after Patrick's session — Claude + Patrick)

1. Fresh boot of DS4Windows build → DualSense tab → Set up elevation (one
   UAC) → Start Native Mode → pad releases, child serves, attach lands,
   status Attached.
2. Black Flag: input + triggers + haptics + sail-through-speaker all work.
3. Stop Native Mode → virtual pad unplugs, DS4Windows reclaims the pad,
   mapping works again. App-exit-while-active also tears down.
4. Pad idle-timeout drill: let it power off → status shows pad lost →
   press PS → Start again.
