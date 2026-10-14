# RokidDiyStation.Cli — headless testing tool

Companion command-line app for development and CI smoke-tests.

Planned commands:

```
rokid-station-cli devices               # list HID devices (Rokid Max visible?)
rokid-station-cli stream --seconds 10   # dump decoded IMU to stdout
rokid-station-cli fusion --rate 200     # benchmark fusion filter
rokid-station-cli emit freetrack        # spin up FreeTrack UDP emitter
rokid-station-cli dump --pcap out.bin   # record raw USB bytes for RE
```

Kept dependency-light (only `RokidDiyStation.Core`) so it can run in CI containers too.
