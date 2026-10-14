# Rokid Max — USB / IMU reverse engineering notes

Goal: figure out how Rokid Station reads the embedded IMU, then match it without Station.

## Known starting points

| Thing | Where to look |
| --- | --- |
| USB descriptors | `drivers/rokid-max-usb-descriptor.md` |
| HID report layout | `docs/hid-report-layout.md` (TBD) |
| Sample captures | GitHub Releases / linked issues |
| Driver behavior on Windows | USBlyzer / Wireshark + USBPcap during Rokid Station startup |

## Tools we already use

- **USBPcap** + Wireshark — capture the bulk / interrupt traffic when Station opens the device.
- **USB Device Tree Viewer** — read descriptors without Station interfering.
- **HID descriptor parser** — confirm report IDs and lengths.

## Open questions

- Does Station use **HID over USB** (likely) or a **vendor bulk** endpoint?
- Is the IMU rate fixed (e.g. 200 Hz) or negotiated?
- Does the device expose its own calibration blob, or does Station apply it in software?
- Are there any buttons / brightness controls on the same endpoint that share the bus time?
- Is there a power-on handshake so the IMU actually starts streaming?

## Want to help?

1. Plug your Rokid Max in.
2. Run `tools/capture-usbpcap.ps1` (TBD) with a fresh USBPcap session, BEFORE Station opens the device.
3. Open Rokid Station. Then close it.
4. Attach the `.pcap` here as a new issue using the **protocol_capture** template.

We need ≥3 captures from different firmware versions to spot-check the parser.
