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

    /// <summary>
    /// Apply the magnetometer heading correction.
    /// </summary>
    /// <remarks>
    /// Off by default, and it should stay off.  The Rokid Max's
    /// magnetometer reads about 108 uT indoors — far outside Earth's
    /// 25-65 uT field — because the desk, the monitors and the
    /// glasses' own electronics distort it.  Enabling the correction
    /// makes the panel slew violently (20-90 degree jumps) as the user
    /// moves.  Every production 3DoF runtime — Virtual Desktop,
    /// SteamVR, Bigscreen — disables magnetometers for exactly this
    /// reason.  The user can still turn it on for curiosity, but the
    /// default is the correct one.
    /// </remarks>
    public bool UseMagnetometer { get; init; } = false;

    /// <summary>Low-pass smoothing factor in 0..1; 0 = no smoothing, 1 = frozen.</summary>
    public double Smoothing { get; init; } = 0.0;

    /// <summary>Output FOV in degrees; consumers may scale by this.</summary>
    public double FovDegrees { get; init; } = 60.0;

    /// <summary>Auto-start on Windows login.</summary>
    public bool Autostart { get; init; } = false;

    /// <summary>Start the tracker minimised to the tray.</summary>
    public bool StartMinimised { get; init; } = false;

    /// <summary>
    /// The angular comfort cone of the lazy-follow correction, in
    /// degrees.  Inside the cone the pinned panel is perfectly rigid;
    /// outside it, the panel glides gently back towards the user's
    /// gaze so that 3DoF yaw drift never walks the panel out of view.
    /// 0 disables the lazy follow entirely.
    /// </summary>
    public double LazyFollowDeadzoneDegrees { get; init; } = 15.0;

    /// <summary>
    /// How fast the lazy follow pulls the panel back towards the
    /// gaze, in inverse seconds.  Higher is snappier; anything above
    /// about 8 makes the panel chase the user's eyes.
    /// </summary>
    public double LazyFollowRatePerSecond { get; init; } = 3.0;

    /// <summary>
    /// How much of the kinematic neck model's eye translation to apply,
    /// in 0..1.  0 is pure rotation about the eye (no parallax, no
    /// swim); 1 is the full Cardboard / LibOVR neck offset.  In 3DoF the
    /// neck's true position is unknowable, so the model invents a
    /// translation when the user turns in their chair.  For a flat
    /// reading panel that invented parallax is worse than none.
    /// </summary>
    public double NeckModelScale { get; init; } = 0.3;

    /// <summary>
    /// How far the pinned panel sits from the eye, in metres.  The
    /// glasses' optics have a fixed virtual image distance of roughly
    /// 2-2.5 m, so putting the panel much closer than that creates an
    /// accommodation-ververgence conflict and eye strain.
    /// </summary>
    public double PanelDistanceMetres { get; init; } = 2.0;

    /// <summary>
    /// The fraction of the glasses' horizontal field of view the panel
    /// covers.  0.5 means the panel subtends half the FOV.
    /// </summary>
    public double PanelFovFraction { get; init; } = 0.5;

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
