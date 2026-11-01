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
    /// Low-pass smoothing factor in 0..1; 0 = no smoothing, 1 = frozen.
    /// </summary>
    public double Smoothing { get; init; } = 0.0;

    /// <summary>Output FOV in degrees; consumers may scale by this.</summary>
    public double FovDegrees { get; init; } = 60.0;

    /// <summary>Auto-start on Windows login.</summary>
    public bool Autostart { get; init; } = false;

    /// <summary>Start the tracker minimised to the tray.</summary>
    public bool StartMinimised { get; init; } = false;

    /// <summary>
    /// How the pinned panel reacts when the user turns their head away
    /// from it.
    /// </summary>
    /// <remarks>
    /// <c>Strict</c> (the default) makes the panel behave like a screen
    /// hanging in the room: its position in the world NEVER changes
    /// because of head motion.  It slides across the display as the user
    /// turns and then off the edge, exactly like a poster on a wall.
    /// <para/>
    /// <c>ComfortCone</c> is the classic AR "Lazy Follow" / "Tag-Along"
    /// that Microsoft HoloLens and Virtual Desktop use for transient
    /// PALETTES: inside a comfort cone the panel is rigid, outside it
    /// the panel is dragged back towards the gaze.  It is offered for
    /// users who want the standard XR idiom, but it is NOT the default
    /// for a reading surface — during a normal head turn it drags the
    /// panel along with the skull, and the user perceives the spatial
    /// anchor as lost.
    /// </remarks>
    public string WorldLockMode { get; init; } = "Strict";

    /// <summary>
    /// The angular comfort cone, in degrees.  Only used when
    /// <see cref="WorldLockMode"/> is <c>ComfortCone</c>.
    /// </summary>
    public double ComfortConeDegrees { get; init; } = 15.0;

    /// <summary>
    /// How fast the comfort cone pulls the panel back towards the gaze,
    /// in inverse seconds.  Only used when <see cref="WorldLockMode"/>
    /// is <c>ComfortCone</c>.
    /// </summary>
    public double ComfortConeRatePerSecond { get; init; } = 3.0;

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
