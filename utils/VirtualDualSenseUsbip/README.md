# VirtualDualSenseUsbip

User-space USB/IP device-emulation spike for DS4Windows Phase 4. The intended
device is a virtual wired DualSense composite device: UAC1 audio plus HID,
materialized by usbip-win2's signed VHCI driver.

Current status: M2.2 protocol foundation only. It does **not** install or attach
a driver. M2.1 must first prove that a production-signed usbip-win2 package runs
with Secure Boot and Memory Integrity enabled and without adding a test root.

Implemented:

- USB/IP v1.1.1 network-order operation headers.
- OP_REQ_IMPORT and OP_REQ_DEVLIST framing support.
- 48-byte CMD_SUBMIT/CMD_UNLINK parser and RET_SUBMIT/RET_UNLINK encoder.
- Bounded transfer lengths and ISO packet ranges.
- Exact reads across arbitrary TCP fragmentation.
- Serialized stream writer for out-of-order URB completion without interleaved
  response bytes.
- Linux-kernel-documentation golden-vector self-test.

Run:

```powershell
dotnet run --project .\utils\VirtualDualSenseUsbip -- selftest
```

Protocol source: [Linux kernel USB/IP protocol documentation](https://docs.kernel.org/usb/usbip_protocol.html).
The byte-exact DualSense descriptors consumed by the future device layer live
in `../DSCompatProbe/fixtures/dualsense_usb_0ce6` and are verified with:

```powershell
DSCompatProbe.exe verifyusb ..\DSCompatProbe\fixtures\dualsense_usb_0ce6
```
