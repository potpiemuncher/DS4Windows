# Experimental Native DualSense Mode

Native Mode lets a game use its wired DualSense implementation while the
physical controller remains connected to Windows over Bluetooth. DS4Windows
temporarily releases the selected pad, starts a user-mode USB/IP device server,
attaches the resulting virtual composite device through usbip-win2, and relays
input, triggers, haptic audio, and speaker audio between the two devices.

This work is intentionally separate from the standard Bluetooth audio/haptics
feature. That independently user-validated feature does not require virtual USB
hardware, elevation, or a third-party kernel driver, and none of the Native Mode
production gates below apply to it.

## Architecture

1. DS4Windows captures and guards the user's default Windows audio endpoints.
2. It records the exact selected DualSense HID path, VID, and PID, then releases
   that controller.
3. `VirtualDualSenseUsbip` opens only that exact Bluetooth device and exposes
   a loopback-only USB/IP server with a virtual wired DualSense composite
   topology.
4. A per-use elevated usbip-win2 attach materializes the virtual device.
   If the UAC launch outlives startup cancellation or timeout, it becomes a
   fail-closed lifetime barrier; helper and audio protections remain until the
   command is terminal and ordered teardown confirms device removal.
5. DS4Windows and the helper each hold an independent shared-mode render
   keepalive on the exact virtual endpoint; Attached is not published until both
   are ready.
6. A protocol-v2 redirected-input lease carries normal Stop and reports parent
   death as pipe EOF. Both enter the same cooperative helper cancellation path.
7. In-band audio interface changes retire ISO generations without sending late
   completions. On Stop/EOF, the helper closes its USB/IP session while at least
   one keepalive remains, and both sides retain protection until the exact
   endpoint and virtual parent are absent.

The redundant leases and cooperative control path are implemented on the
working branch and have offline validation only. Startup reconciliation and
live parent/helper failure drills remain incomplete.

The virtual microphone endpoint currently serves silence. Normal Native Mode
does not enable the physical controller microphone.

## External dependency and safety gate

usbip-win2 is an external BSD-2-Clause dependency and is not bundled. Testing
used the Microsoft-attestation-signed packages installed by its upstream
0.9.7.8 release with Secure Boot and Memory Integrity enabled. That release
nevertheless has a confirmed request-lifetime race in
`usbip2_ude.sys` when an audio endpoint is purged while isochronous
completions are still in flight. The reproduced speaker-pin trigger was stable
after adding the render keepalive and transfer-quiescing barriers, including a
controlled game test with repeated Alt-Tab and clean stop.

Those mitigations reduce the reproduced risk; they do not repair or prove the
kernel driver. This draft remains blocked for production use until:

- a signed usbip-win2 release fixes the underlying lifetime race, or maintainers
  explicitly accept a different driver strategy;
- redundant parent/helper containment passes approved live process-failure
  drills and startup reconciliation can recover an incomplete prior session;
- installer, driver-version/signature validation, repair, and uninstall policy
  are defined; and
- the captured Sony descriptor assets receive maintainer/provenance review.

Implementation and review work toward these gates:
[parent/helper crash containment](native_mode_crash_containment.md) (gate 2);
[driver risk, validation, repair, and uninstall policy](native_mode_driver_policy.md)
(gates 1 and 3); and
[descriptor provenance review](native_mode_descriptor_provenance.md) (gate 4).
These are an offline-only containment implementation plus policy/review dossiers
for maintainer sign-off; they map each gate to concrete work but do not by
themselves close it.

No automated test or successful hardware session can eliminate a kernel-driver
use-after-free risk. Native Mode must remain clearly experimental until these
gates close.

The attach broker currently accepts only a canonical executable named
`usbip.exe` below a Windows Program Files root. The tested executable has a
valid Authenticode signature, but the broker does not verify that signature or
associate the executable with both installed driver packages. A future
supported-release manifest and driver-store trust check therefore remain a
production blocker rather than being approximated by a path check.

## Security model

- The server binds only to loopback.
- The child receives one exact controller identity and fails closed on any
  path, VID, PID, transport, or report-shape mismatch.
- Release builds publish the helper deterministically for the application's
  x64 or x86 runtime and exclude PDB files.
- usbip.exe must resolve canonically beneath Windows Program Files or Program
  Files (x86); arbitrary user-writable executable paths are rejected.
- Elevated attach is per-use and limited to the fixed loopback host, bus ID,
  and virtual serial. No reusable privileged attach task is created or run.
- A timed-out UAC launch is not treated as canceled cleanup. The helper and
  protections remain active until the dispatched process is confirmed gone,
  then exact-device removal is rechecked.
- If an older experimental build left the exact
  `\DS4Windows\NativeDualSenseAttach` task, requirements checking requests
  one-time approval to delete it and fails closed until deletion is confirmed.

## Validation

Offline checks include USB/IP codec vectors, descriptor and EP0 behavior,
loopback HID/ISO/UNLINK behavior, transfer-quiescing races, controller identity
matching, Native Mode lifecycle policy, default-audio restoration, keepalive
behavior, and Release x64/x86 packaging.

The controlled hardware validation covered native controls, adaptive triggers,
haptics, controller speaker audio at a 50% default, repeated Alt-Tab, default
Windows audio preservation, and clean teardown. It does not waive the
production blockers above.
