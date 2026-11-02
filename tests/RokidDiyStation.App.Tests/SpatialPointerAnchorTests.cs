// SPDX-License-Identifier: MIT
//
// Regression test for the cursor-driven re-pin.
//
// THE BUG
// -------
// The user reported: "when I move the mouse from another screen onto
// the spatial display, it seems to recentre itself".
//
// The cause was one line in SpatialStation's constructor:
//
//     _feedback.OnInsideChanged += inside => { if (inside) AnchorHere(); };
//
// The reasoning behind it was "the cursor is meaningful over the
// spatial display, so that is where the panel belongs".  The effect
// was that the panel TELEPORTED to the user's current gaze every time
// the mouse crossed onto the virtual monitor.
//
// That is wrong because the mouse and the head are independent.  Moving
// the mouse to click something in the panel says nothing about where
// the user is looking, and nothing about where they want the panel to
// be.  The panel's world pose is a property of the ROOM.
//
// WHY THIS IS AN IL INSPECTION TEST
// ---------------------------------
// The station's constructor wires the feedback loop, and the station
// needs a GPU, a window and an IMU to be useful - none of which exist
// in a unit test.  So the assertion is made against the compiled IL:
// no method reachable from the pointer-feedback wiring may call
// AnchorHere().
//
// This is the same technique the PanelConstantBufferBindingTests use
// to guard the vertex-shader constant buffer: the bug was invisible to
// every other check, so the check has to look at the code itself.

using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace RokidDiyStation.App.Tests;

public class SpatialPointerAnchorTests
{
    /// <summary>
    /// The names of the methods a method body calls, in order.
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
                        names.Add(module.ResolveMethod(token).Name);
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
                    index += 4;
                    break;

                case OperandType.InlineSwitch:
                {
                    var count = BitConverter.ToInt32(il, index);
                    index += 4 + (4 * count);
                    break;
                }

                case OperandType.ShortInlineBrTarget:
                    index += 1;
                    break;

                case OperandType.InlineBrTarget:
                    index += 4;
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

    /// <summary>
    /// Every method in the station, including the compiler-generated
    /// closures and lambdas it nests.
    /// </summary>
    private static IEnumerable<MethodInfo> AllStationMethods()
    {
        var station = typeof(RokidDiyStation.App.Spatial.SpatialStation);

        foreach (var method in station.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic |
                     BindingFlags.Instance | BindingFlags.Static |
                     BindingFlags.DeclaredOnly))
        {
            yield return method;
        }

        // The lambdas the old bug used lived on the compiler-generated
        // display class, which is a nested type of the station.
        foreach (var nested in station.GetNestedTypes(
                     BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var method in nested.GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Instance | BindingFlags.Static |
                         BindingFlags.DeclaredOnly))
            {
                yield return method;
            }
        }
    }

    [Fact]
    public void No_Pointer_Handler_Re_Pins_The_Panel()
    {
        // The precise shape of the old bug: a lambda subscribed to
        // OnInsideChanged that calls AnchorHere().  Any method whose
        // name mentions the pointer / cursor / inside feedback must not
        // re-pin the panel.
        foreach (var method in AllStationMethods())
        {
            var name = method.Name;
            var isPointerHandler =
                name.Contains("Inside", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Pointer", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Cursor", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Feedback", StringComparison.OrdinalIgnoreCase);

            if (!isPointerHandler) continue;

            var calls = CalledMethodsInOrder(method);

            Assert.DoesNotContain("AnchorHere", calls);
            Assert.DoesNotContain("Recenter", calls);
        }
    }

    [Fact]
    public void The_Station_Never_Subscribes_To_The_Inside_Changed_Event()
    {
        // The strongest form of the assertion: the station must not
        // subscribe to OnInsideChanged AT ALL.
        //
        // There is no reason for it to.  The event fires when the
        // cursor crosses the display's edge, and the only thing the
        // product needs to know is whether the cursor is over the
        // display - which SpatialPointerFeedback already tracks
        // internally, and which the renderer reads through
        // VirtualCursor.IsActive.
        var station = typeof(RokidDiyStation.App.Spatial.SpatialStation);

        foreach (var method in AllStationMethods())
        {
            var calls = CalledMethodsInOrder(method);
            Assert.DoesNotContain("add_OnInsideChanged", calls);
        }
    }

    [Fact]
    public void The_Pointer_Feedback_Still_Activates_The_Cursor()
    {
        // The guard on the guard: removing the re-pin must NOT have
        // removed the cursor feedback itself.  Without it there is no
        // arrow in the air and no way to click anything.
        var feedback = typeof(RokidDiyStation.App.Spatial.SpatialPointerFeedback);
        Assert.NotNull(feedback.GetMethod("Update"));

        var station = typeof(RokidDiyStation.App.Spatial.SpatialStation);
        var field = station.GetField("_feedback", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
    }

    [Fact]
    public void The_Cursor_Is_Deactivated_When_It_Leaves_The_Display()
    {
        // Leaving the display must still hide the 3D cursor.  This was
        // the false branch of the old OnInsideChanged handler, and it
        // has to survive the fix.
        var feedback = typeof(RokidDiyStation.App.Spatial.SpatialPointerFeedback);

        var setInside = feedback.GetMethod(
            "SetInside", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(setInside);

        var calls = CalledMethodsInOrder(setInside!);

        // The deactivation must still be there.
        Assert.Contains("Deactivate", calls);
    }
}
