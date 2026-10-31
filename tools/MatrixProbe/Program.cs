// Temporary numeric probe: emulate the EXACT shader path.
using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.Protocol;
using RokidDiyStation.Core.Spatial;

// UpdateConstants uploads row-major(Transpose(mvp)).
static float[] Upload(Matrix4x4 mvp)
{
    var t = Matrix4x4.Transpose(mvp);
    return new[]
    {
        t.M11, t.M12, t.M13, t.M14,
        t.M21, t.M22, t.M23, t.M24,
        t.M31, t.M32, t.M33, t.M34,
        t.M41, t.M42, t.M43, t.M44,
    };
}

// HLSL mul(v, M): result[i] = sum_j v[j] * M[j][i], and a cbuffer
// float4x4 is column-major, so M[row][col] = data[4*col + row].
static Vector4 HlslMul(float[] data, Vector3 pos)
{
    var v = new[] { pos.X, pos.Y, pos.Z, 1f };
    var r = new float[4];
    for (var i = 0; i < 4; i++)
        for (var j = 0; j < 4; j++)
            r[i] += v[j] * data[4 * i + j];
    return new Vector4(r[0], r[1], r[2], r[3]);
}

var service = new SpatialAnchorService(new SpatialAnchorOptions { PitchBiasDegrees = 0f });
var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -29.5f * MathF.PI / 180f);
var a = Vector3.Transform(Vector3.UnitY, q);
ulong t = 0;
for (var i = 0; i < 3000; i++) { t += 2_288_333UL; service.Update(new ImuSample(t, a, Vector3.Zero, Vector3.Zero, true, t)); }
service.AnchorHere();

var view = service.GetView();
var proj = service.GetProjection();
var mvp = view.View * proj;
var data = Upload(mvp);

Console.WriteLine("=== shader-path NDC (correct HLSL mul(v,M) semantics) ===");
foreach (var (name, c) in new[]
{
    ("c0", view.PanelCorners[0]),
    ("c1", view.PanelCorners[1]),
    ("c2", view.PanelCorners[2]),
    ("c3", view.PanelCorners[3]),
})
{
    var clip = HlslMul(data, c);
    Console.WriteLine($"  {name}: clip = {clip}  ndc = ({clip.X / clip.W:F4}, {clip.Y / clip.W:F4}, {clip.Z / clip.W:F4})");
}

Console.WriteLine();
Console.WriteLine("=== System.Numerics row-vector ground truth (v * mvp) ===");
foreach (var (name, c) in new[]
{
    ("c0", view.PanelCorners[0]),
    ("c1", view.PanelCorners[1]),
    ("c2", view.PanelCorners[2]),
    ("c3", view.PanelCorners[3]),
})
{
    var clip = Vector4.Transform(new Vector4(c, 1f), mvp);
    Console.WriteLine($"  {name}: clip = {clip}  ndc = ({clip.X / clip.W:F4}, {clip.Y / clip.W:F4}, {clip.Z / clip.W:F4})");
}
