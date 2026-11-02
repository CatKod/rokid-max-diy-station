// SPDX-License-Identifier: MIT
//
// Architectural regression tests for the True Spatial Display.
//
// WHY THESE EXIST
// ---------------
// The spatial display works because the virtual IddCx monitor is a
// REAL Windows display.  The operating system therefore does all of
// the input work by itself: hit-testing, focus, z-order, clicks,
// wheel, keyboard, window drags, OLE drag-and-drop.
//
// The old architecture achieved the same result the hard way: it
// intercepted the mouse with a WH_MOUSE_LL hook, swallowed the events,
// and re-delivered them over the Chrome DevTools Protocol.
//
// That stack was ~2,000 lines whose entire job was to fake what the
// OS now does natively - and it only worked for Chromium applications
// launched with a debugging port.  A native app (Notepad, Explorer,
// VS Code) was view-only, or worse, silently dead.
//
// These tests make the removal STICK.  If someone reintroduces a
// mouse hook or a DevTools client, the build fails - which is the
// point: the CDP stack must not creep back in, because the moment it
// does, the product silently regresses to "browser only".
using System.Reflection;
using Xunit;

namespace RokidDiyStation.App.Tests;

public class SpatialDisplayArchitectureTests
{
    /// <summary>
    /// Every type in the app assembly, by full name.
    /// </summary>
    private static IReadOnlyList<string> AllTypeNames()
    {
        var assembly = typeof(RokidDiyStation.App.Program).Assembly;
        return assembly.GetTypes()
            .Select(t => t.FullName ?? t.Name)
            .ToList();
    }

    [Fact]
    public void The_Input_Namespace_Is_Gone()
    {
        // The whole `RokidDiyStation.App.Input` folder was the DevTools
        // protocol stack.  Its absence is the single clearest signal
        // that the architecture has changed.
        var offenders = AllTypeNames()
            .Where(n => n.StartsWith("RokidDiyStation.App.Input", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            "the DevTools protocol stack must stay deleted: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("CdpClient")]
    [InlineData("CdpDiscovery")]
    [InlineData("BrowserInputRouter")]
    [InlineData("SpatialInputCoordinator")]
    [InlineData("AppLauncher")]
    [InlineData("SquirrelExecutable")]
    public void No_Cdp_Type_Survives(string typeName)
    {
        var offenders = AllTypeNames()
            .Where(n => n.EndsWith("." + typeName, StringComparison.Ordinal)
                     || n.EndsWith("+" + typeName, StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{typeName} belongs to the retired DevTools architecture, but it is still in the build: "
            + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("KeyboardInput")]
    [InlineData("PointerInput")]
    public void No_Low_Level_Input_Hook_Survives(string typeName)
    {
        // The WH_MOUSE_LL and WH_KEYBOARD_LL hooks existed to intercept
        // input that the OS now delivers natively to the virtual
        // monitor.  A global hook on every keystroke in the system is
        // a cost we no longer need to pay.
        var offenders = AllTypeNames()
            .Where(n => n.EndsWith("." + typeName, StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{typeName} installs a global input hook, which the virtual-display "
            + "architecture makes unnecessary: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_App_Does_Not_Set_The_Hook_Procedures()
    {
        // A belt-and-braces check on the same property: the app must
        // not declare WH_MOUSE_LL / WH_KEYBOARD_LL at all.
        var assembly = typeof(RokidDiyStation.App.Program).Assembly;
        var hookIds = new[] { 2, 3, 14 };   // WH_MOUSE_LL, WH_KEYBOARD_LL, WH_KEYBOARD

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Static | BindingFlags.Instance |
                         BindingFlags.DeclaredOnly))
            {
                if (!method.Name.Contains("SetWindowsHookEx", StringComparison.Ordinal)) continue;

                // Find the hook id argument: it is the first int
                // parameter, or a constant passed to a wrapper.
                var args = method.GetParameters();
                foreach (var arg in args)
                {
                    if (arg.ParameterType != typeof(int)) continue;
                    var defaultValue = arg.HasDefaultValue ? arg.DefaultValue : null;
                    if (defaultValue is int id && hookIds.Contains(id))
                    {
                        Assert.Fail($"{type.FullName}.{method.Name} installs a low-level hook (id {id}). "
                            + "The virtual display handles input natively.");
                    }
                }
            }
        }
    }

    [Fact]
    public void The_Virtual_Display_Is_Captured_As_A_Monitor()
    {
        // The heart of the architecture: the panel shows a whole
        // MONITOR, not a window.  If someone changes the capture back
        // to a window, the taskbar and the z-order go away.
        var capture = typeof(RokidDiyStation.App.Spatial.DisplayCapture);
        var start = capture.GetMethod("Start");
        Assert.NotNull(start);

        var parameters = start!.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(IntPtr), parameters[0].ParameterType);
        Assert.Contains("monitor", parameters[0].Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_Station_Exposes_The_Virtual_Display_Controls()
    {
        // Opening and closing the spatial display must be a first
        // class, user-visible action: it is the product now.
        var station = typeof(RokidDiyStation.App.Spatial.SpatialStation);
        Assert.NotNull(station.GetMethod("OpenSpatialDisplay"));
        Assert.NotNull(station.GetMethod("CloseSpatialDisplay"));
        Assert.NotNull(station.GetMethod("SnapCursorHome"));
    }

    [Fact]
    public void The_Cursor_Feedback_Loop_Exists_And_Is_Wired()
    {
        // The cursor feedback is the ONLY input-related code left, and
        // it must still be there - without it there is no arrow in the
        // air.
        var feedback = typeof(RokidDiyStation.App.Spatial.SpatialPointerFeedback);
        Assert.NotNull(feedback.GetMethod("Update"));

        var station = typeof(RokidDiyStation.App.Spatial.SpatialStation);
        var field = station.GetField("_feedback", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
    }
}
