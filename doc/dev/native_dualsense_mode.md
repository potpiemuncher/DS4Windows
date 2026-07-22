# Experimental Native DualSense Mode

Native Mode lets a game use its wired DualSense implementation while the
physical controller remains connected to Windows over Bluetooth. DS4Windows
temporarily releases the selected pad, starts a user-mode USB/IP device server,
attaches the resulting virtual composite device through usbip-win2, and relays
input, triggers, haptic audio, and speaker audio between the two devices.

This work is intentionally separate from the standard Bluetooth audio/haptics
feature. The standard feature does not require virtual USB hardware, elevation,
or a third-party kernel driver.

## Architecture

1. DS4Windows captures and guards the user's default Windows audio endpoints.
2. It records the exact selected DualSense HID path, VID, and PID, then releases
   that controller.
3. `VirtualDualSenseUsbip` opens only that exact Bluetooth device and exposes
   a loopback-only USB/IP server with a virtual wired DualSense composite
   topology.
4. A per-use elevated usbip-win2 attach materializes the virtual device.
5. A shared-mode render keepalive holds the virtual speaker pin open while
   Native Mode is attached.
6. On stop, the helper quiesces isochronous transfers before endpoint teardown;
   DS4Windows waits for the virtual device and keepalive to disappear before it
   restores audio defaults and reacquires the physical controller.

The virtual microphone endpoint currently serves silence. Normal Native Mode
does not enable the physical controller microphone.

## External dependency and safety gate

usbip-win2 is an external BSD-2-Clause dependency and is not bundled. Testing
used its signed 0.9.7.8 release with Secure Boot and Memory Integrity enabled.
That release nevertheless has a confirmed request-lifetime race in
`usbip2_ude.sys` when an audio endpoint is purged while isochronous
completions are still in flight. The reproduced speaker-pin trigger was stable
after adding the render keepalive and transfer-quiescing barriers, including a
controlled game test with repeated Alt-Tab and clean stop.

Those mitigations reduce the reproduced risk; they do not repair or prove the
kernel driver. This draft remains blocked for production use until:

- a signed usbip-win2 release fixes the underlying lifetime race, or maintainers
  explicitly accept a different driver strategy;
- DS4Windows parent-process crash containment keeps the virtual audio pin and
  helper teardown ordered even if the application terminates unexpectedly;
- installer, driver-version/signature validation, repair, and uninstall policy
  are defined; and
- the captured Sony descriptor assets receive maintainer/provenance review.

No automated test or successful hardware session can eliminate a kernel-driver
use-after-free risk. Native Mode must remain clearly experimental until these
gates close.

The attach broker only accepts a canonical executable named `usbip.exe` below
a Windows Program Files root. It does not claim Authenticode verification of
the userspace executable because the current released artifact does not provide
an identity that this integration can reliably assert. Installer-level
usbip-win2 driver package version and signature validation therefore remains a
production blocker, rather than a check approximated in the application.

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
