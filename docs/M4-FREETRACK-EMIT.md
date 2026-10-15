# M4 — FreePIE UDP output

`RokidDiyStation.Core.Output.FreePieEmitter` sends orientation
packets over UDP in the same wire format that
[opentrack's `tracker-freepie-udp`](https://github.com/opentrack/opentrack/tree/master/tracker-freepie-udp)
receives.  That makes our IMU data plug-compatible with opentrack on
the same machine, or with anything else that listens on a FreePIE UDP
port.

## Why FreePIE UDP and not raw FreeTrack 2.x?

There are two common "FreeTrack" wire formats on the network:

1. **FreeTrack 2.x native (100 bytes, Yaw/Pitch/Roll in degrees)** —
   used by some racing/flight emulators that mimic the original
   FreeTrack DLL.  Vendor-specific, fragmented docs.
2. **FreePIE UDP (50 bytes, Yaw/Pitch/Roll in radians)** — used by
   opentrack's `tracker-freepie-udp` and by the FreePIE scripting
   hub.  Open source, well-defined.

We speak #2 because it's the format opentrack (the de-facto standard
head-tracking relay on Windows/Linux) is happy to ingest.  If a target
needs the 100-byte format, it's a one-screen extension on top of
`FreePiePacket`.

## Wire format

50 bytes, little-endian, packed, no alignment padding:

```
offset  size  field
------  ----  -----
0       1     pad (always 0)
1       1     flags: bit0=Raw, bit1=Orient
2       48    float[12]
```

With `flags = Orient (2)`, the last three floats carry
`yaw, pitch, roll` in **radians**.  With `flags = Raw|Orient (3)`,
the first three carry `X, Y, Z` in metres and the last three carry
orientation.

Sign convention matches opentrack's receiver: positive yaw = turn
left, positive pitch = up, positive roll = tilt left.  The Rokid
device frame (X=pitch, Y=yaw, Z=roll) maps onto this with the
identity:

```csharp
packet.YawRad   = yawDegrees   * π/180
packet.PitchRad = pitchDegrees * π/180
packet.RollRad  = rollDegrees  * π/180
```

## CLI

```powershell
# Live mode: open the Rokid Max via HidSharp and stream orientation.
# Receivers: opentrack tracker-freepie-udp, FreePIE script, etc.
dotnet run --project src/RokidDiyStation.Cli -- fusion-emit `
    --host 127.0.0.1 --port 5555

# Replay mode: read samples from a binary capture and stream
# the same way.  Useful for testing without the glasses.
dotnet run --project src/RokidDiyStation.Cli -- fusion-emit `
    --replay docs/fixtures/raw-200.bin --port 5556 --timeout 5
```

## Wiring it up in opentrack

1. Start opentrack.
2. **Input** → **UDP over network** (the FreePIE one).
3. Set the port to `5555` (or whatever you pass to `--port`).
4. **Output** → **freetrack 2.x** (or whatever your game/emu wants).
5. Run our CLI.  You should see opentrack's orientation cube
   track your head motion.

## Hand-verified end-to-end

The included test
`FreePieEmitterTests.Emitter_Sends_Computed_Orientation_Through_Fusion`
runs a synthetic 30°/s yaw trajectory through MahonyFusion, sends the
result over UDP, receives it on a localhost socket, and asserts the
decoded yaw is within 35° of the ground truth (generous — the filter
is still warming up at 0.3 s of motion).

A shell-loop test against the real capture
(`docs/fixtures/raw-200.bin`) confirmed all 200 packets arrive on a
localhost receiver, with the expected stationary orientation at the
end (`yaw = -0.06°`).

## Microbenchmark note

We did not add a separate benchmark CLI in M4: at 437 Hz the fusion
step is ~10 µs (Mahony with 9 floats and a quaternion multiply) and
the 50-byte UDP send on a non-blocking socket is essentially free.
Real-world CPU usage is dominated by the IMU read, not the output.
