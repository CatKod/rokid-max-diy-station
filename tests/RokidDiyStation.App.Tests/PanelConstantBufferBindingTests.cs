// SPDX-License-Identifier: MIT
//
// A regression test for the black-panel bug.
//
// THE BUG
// -------
// The panel's vertex shader computes the clip-space position like this:
//
//     output.position = mul(float4(input.position, 1.0f), mvp);
//
// so it reads the MVP matrix from the constant buffer at b0.  In
// D3D11 the constant buffer slots are PER-STAGE: b0 on the pixel
// stage is a completely different binding from b0 on the vertex stage.
//
// The panel's draw path bound the constant buffer ONLY to the pixel
// stage (for the UV transform) and never to the vertex stage.  The
// vertex shader therefore read an UNBOUND register, which D3D11
// defines as ZERO.  A zero matrix collapses the whole quad into a
// single degenerate point at the origin, so nothing was rasterised and
// the panel came out completely black.
//
// WHY A TEST IS NEEDED
// --------------------
// Every symptom of that bug looked healthy:
//
//   * the capture reported a perfect, live 1920x1080 frame,
//   * the render loop reported 60 fps,
//   * the panel's corners projected inside the clip volume,
//   * and the virtual display itself was attached and correct.
//
// The ONLY symptom was a 0 % coverage reading, in a diagnostic tool,
// on hardware that has to be physically worn to look at.  A unit test
// that only exercised the geometry would have passed throughout.
//
// So this test inspects the renderer's actual IL and asserts that the
// constant buffer is bound to the VERTEX stage on the panel's draw
// path.  That is the one thing which, if it is missing, makes the
// panel invisible - and it is invisible to every other check.
using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace RokidDiyStation.App.Tests;

public class PanelConstantBufferBindingTests
{
    /// <summary>
    /// The names of the methods we care about, in the order the IL
    /// calls them.
    /// </summary>
    private static IReadOnlyList<string> CalledMethodsInOrder(MethodInfo method)
    {
        var body = method.GetMethodBody();
        Assert.NotNull(body);

        var il = body!.GetILAsByteArray();
        Assert.NotNull(il);

        var module = method.Module;
        var names = new List<string>();

        var oneByte = new OpCode[0x100];
        var twoByte = new OpCode[0x100];

        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op) continue;
            var value = (ushort)op.Value;
            if (value < 0x100) oneByte[value] = op;
            else if ((value & 0xFF00) == 0xFE00) twoByte[value & 0xFF] = op;
        }

        var index = 0;
        while (index < il.Length)
        {
            OpCode op;
            var first = il[index];
            if (first == 0xFE)
            {
                op = twoByte[il[index + 1]];
                index += 2;
            }
            else
            {
                op = oneByte[first];
                index += 1;
            }

            switch (op.OperandType)
            {
                case OperandType.InlineMethod:
                {
                    var token = BitConverter.ToInt32(il, index);
                    index += 4;
                    try
                    {
                        var target = module.ResolveMethod(token);
                        names.Add(target.Name);
                    }
                    catch (ArgumentException)
                    {
                        // A token we cannot resolve is not one of ours.
                    }
                    break;
                }

                case OperandType.InlineI:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineR:
                    index += op.OperandType == OperandType.InlineI ? 4
                            : op.OperandType == OperandType.ShortInlineR ? 4
                            : 1;
                    break;

                case OperandType.InlineI8:
                case OperandType.InlineR:
                    index += 8;
                    break;

                case OperandType.InlineString:
                case OperandType.InlineField:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.InlineSig:
                case OperandType.InlineSwitch:
                    if (op.OperandType == OperandType.InlineSwitch)
                    {
                        var count = BitConverter.ToInt32(il, index);
                        index += 4 + (4 * count);
                    }
                    else
                    {
                        index += 4;
                    }
                    break;

                case OperandType.ShortInlineBrTarget:
                case OperandType.InlineBrTarget:
                    index += op.OperandType == OperandType.ShortInlineBrTarget ? 1 : 4;
                    break;

                case OperandType.InlineVar:
                    index += 2;
                    break;

                case OperandType.ShortInlineVar:
                    index += 1;
                    break;

                default:
                    break;
            }
        }

        return names;
    }

    [Fact]
    public void The_Panel_Draw_Path_Binds_The_Constant_Buffer_To_The_Vertex_Stage()
    {
        var renderer = typeof(RokidDiyStation.App.Spatial.PanelRenderer);
        var drawFrame = renderer.GetMethod(
            "DrawFrame",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(drawFrame);

        var calls = CalledMethodsInOrder(drawFrame!);

        Assert.Contains("VSSetConstantBuffer", calls);

        // The constant buffer must be bound to the vertex stage BEFORE
        // the panel is drawn.  Anything else leaves the vertex shader
        // reading zeros, which collapses the quad to a point.
        var boundAt = calls.ToList().IndexOf("VSSetConstantBuffer");
        var drawnAt = calls.ToList().IndexOf("Draw");

        Assert.True(boundAt >= 0, "the constant buffer is never bound to the vertex stage.");
        Assert.True(drawnAt >= 0, "the panel is never drawn.");
        Assert.True(boundAt < drawnAt,
            "the constant buffer must be bound to the vertex stage BEFORE the panel is drawn.");
    }

    [Fact]
    public void The_Panel_Draw_Path_Binds_The_Constant_Buffer_To_The_Pixel_Stage()
    {
        // The pixel stage needs the same buffer, for the UV transform.
        // Dropping this would not blank the panel - it would stretch the
        // texture - but it is the same class of mistake and the same
        // one-line fix.
        var renderer = typeof(RokidDiyStation.App.Spatial.PanelRenderer);
        var drawFrame = renderer.GetMethod(
            "DrawFrame",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(drawFrame);

        var calls = CalledMethodsInOrder(drawFrame!);

        Assert.Contains("PSSetConstantBuffer", calls);
    }

    [Fact]
    public void The_Cursor_Draw_Path_Binds_The_Constant_Buffer_To_Both_Stages()
    {
        // The cursor uses the SAME vertex shader, so it needs the MVP
        // on the vertex stage as well.  The cursor's draw path does
        // bind both - this test exists so that a future refactor that
        // merges the two paths cannot quietly drop one of them.
        var renderer = typeof(RokidDiyStation.App.Spatial.PanelRenderer);
        var drawCursor = renderer.GetMethod(
            "DrawCursor",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(drawCursor);

        var calls = CalledMethodsInOrder(drawCursor!);

        Assert.Contains("VSSetConstantBuffer", calls);
        Assert.Contains("PSSetConstantBuffer", calls);
    }
}
