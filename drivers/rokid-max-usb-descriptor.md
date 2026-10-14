# Rokid Max — USB descriptor template

Use `USB Device Tree Viewer` (Uwe Sieber) or `lsusb -v` (WSL) on a fresh machine that has **never seen** Rokid Station. Fill in the rows we know.

## Device

| Field | Value |
| --- | --- |
| Vendor ID (VID) | `0x____` |
| Product ID (PID) | `0x____` |
| iManufacturer |  |
| iProduct |  |
| iSerialNumber |  |
| bcdDevice (rev) |  |

## Configurations

### Configuration 1

| Interface | Class | Subclass | Protocol | Endpoints |
| --- | --- | --- | --- | --- |
| 0 |  |  |  | EP__: |
| 1 |  |  |  | EP__: |
| 2 |  |  |  | EP__: |

## Endpoints

| EP | Address (in/out) | Type (interrupt/bulk/iso) | MaxPacketSize | Interval (ms) |
| --- | --- | --- | --- | --- |
|  |  |  |  |  |
|  |  |  |  |  |

## HID report descriptor (if interface 0 is HID)

Paste raw bytes in hex here once captured. We will break it down into:
- Input report IDs (0x__ — likely the IMU report ID)
- Output report IDs (0x__ — for send calibration / power on?)
- Feature report IDs (0x__)

## Power-on behavior

- Does the IMU start streaming on plug-in, or only after Rokid Station sends an init report?
- Is a status / heartbeat report sent before the first IMU frame?

## Cross-check

Open the same captures using `Rokid Station` and `Rokid Air` adapters — note any differences (helps locate IMU-only vs display-only endpoints).
