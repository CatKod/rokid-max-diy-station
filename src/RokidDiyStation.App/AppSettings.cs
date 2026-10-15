// SPDX-License-Identifier: MIT
//
// User-facing settings for the Rokid DiyStation tray app.
//
// Persisted to %APPDATA%\RokidDiyStation\settings.json.  Field defaults
// are conservative: the same M5 CLI behaviour, plus a smoothing
// factor (one-Euro-style) and FOV curve that the renderer is free to
// ignore if it doesn't care.
using System.IO;
using System.Text.Json.Serialization;

namespace RokidDiyStation.App;

public sealed record AppSettings
{
    /// <summary>UDP target host for the FreePIE-compatible stream.</summary>
    public string UdpHost { get; init; } = "127.0.0.1";

    /// <summary>UDP target port.</summary>
    public int UdpPort { get; init; } = 5555;

    /// <summary>Emission rate cap (packets per second).</summary>
    public int MaxPacketsPerSecond { get; init; } = 250;

    /// <summary>Apply the magnetometer heading correction (M5).</summary>
    public bool UseMagnetometer { get; init; } = true;

    /// <summary>Low-pass smoothing factor in 0..1; 0 = no smoothing, 1 = frozen.</summary>
    public double Smoothing { get; init; } = 0.0;

    /// <summary>Output FOV in degrees; consumers may scale by this.</summary>
    public double FovDegrees { get; init; } = 60.0;

    /// <summary>Auto-start on Windows login.</summary>
    public bool Autostart { get; init; } = false;

    /// <summary>Start the tracker minimised to the tray.</summary>
    public bool StartMinimised { get; init; } = true;

    /// <summary>Hotkey to set the anchor.  Format: "Space" / "F1" / "Ctrl+Space".</summary>
    public string AnchorHotkey { get; init; } = "Space";

    /// <summary>Hotkey to reset the anchor.</summary>
    public string ResetHotkey { get; init; } = "R";

    /// <summary>Hotkey to recenter the view (send a single neutral orientation).</summary>
    public string RecenterHotkey { get; init; } = "C";

    /// <summary>Axis remap: device X / Y / Z -> world X / Y / Z.  Each value is
    /// 0/1/2 selecting which device axis to use.  Default = identity.</summary>
    public AxisRemap AxisRemap { get; init; } = AxisRemap.Identity;

    [JsonIgnore]
    public static AppSettings Default { get; } = new();
}

public sealed record AxisRemap(int X, int Y, int Z)
{
    [JsonIgnore]
    public static readonly AxisRemap Identity = new(0, 1, 2);

    public static AxisRemap Parse(string? s) => s switch
    {
        "XZY" => new(0, 2, 1),
        "YXZ" => new(1, 0, 2),
        "YZX" => new(1, 2, 0),
        "ZXY" => new(2, 0, 1),
        "ZYX" => new(2, 1, 0),
        _     => Identity,
    };

    public override string ToString() => X == 0 && Y == 1 && Z == 2 ? "XYZ" :
        X == 0 && Y == 2 && Z == 1 ? "XZY" :
        X == 1 && Y == 0 && Z == 2 ? "YXZ" :
        X == 1 && Y == 2 && Z == 0 ? "YZX" :
        X == 2 && Y == 0 && Z == 1 ? "ZXY" :
        "ZYX";
}
