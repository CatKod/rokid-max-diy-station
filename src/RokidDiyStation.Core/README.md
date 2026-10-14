# RokidDiyStation.Core — protocol & fusion engine

Pure (UI-free) engine. Implemented as a `System.Threading.Channels`-based pipeline:

```
UsbCapture ──▶ PacketParser ──▶ SensorFusion ──▶ OrientationStream ──▶ ProtocolMultiplexer
                                                      ├─ FreeTrackUdpEmitter
                                                      ├─ VirtualMouseEmitter
                                                      └─ WebSocketEmitter
```

Planned namespaces:

- `RokidDiyStation.Core.Usb` — HID enumeration + capture (`IHidCapture`)
- `RokidDiyStation.Core.Parsing` — packet framing & calibration
- `RokidDiyStation.Core.Fusion` — Madgwick / Mahony / complementary filters
- `RokidDiyStation.Core.Orientation` — Quaternion, Euler, FOV curve
- `RokidDiyStation.Core.Protocols` — FreeTrack, VSeeFace, Mouse, WebSocket

Target: cross-platform friendly (no Windows-only deps in this assembly). Only the tray app pulls in WPF.
