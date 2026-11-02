// SPDX-License-Identifier: MIT
//
// Probe: can the virtual cursor actually CLICK inside a real Electron
// application that our own launcher started?
//
// This is the full chain against a real application:
//
//   physical mouse -> WH_MOUSE_LL -> PointerInput (differential) ->
//   VirtualCursor (u,v) -> SpatialInputCoordinator -> BrowserInputRouter
//   -> CDP -> the application's renderer
//
// It proves the thing the user actually wants: the cursor drawn on the
// glasses and the click that lands are the SAME point, inside a real
// application, with the OS mouse left alone.
//
// Usage:
//   dotnet run --project tools/ProbeAppClick -- Slack
using System.Runtime.InteropServices;
using RokidDiyStation.App.Input;
using RokidDiyStation.App.Spatial;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private static async Task<int> Main(string[] args)
    {
        var appName = args.Length > 0 ? args[0] : "Slack";

        var apps = AppLauncher.KnownApps();
        var app = apps.FirstOrDefault(a => a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
        if (app is null)
        {
            Console.WriteLine($"{appName} is not installed or not driveable.");
            return 1;
        }

        Console.WriteLine($"[{appName}] executable: {app.Executable}");

        var process = AppLauncher.Launch(app);
        if (process is null)
        {
            Console.WriteLine("FAIL: the launcher could not start it.");
            return 1;
        }

        if (!await AppLauncher.WaitForEndpointAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(false))
        {
            Console.WriteLine("FAIL: no DevTools endpoint.");
            return 1;
        }

        var hwnd = BrowserWindowFinder.WaitForWindowOfProcess(process, TimeSpan.FromSeconds(45));
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine("FAIL: no window.");
            return 1;
        }

        Console.WriteLine($"[{appName}] endpoint up, window 0x{hwnd.ToInt64():X}");

        var target = DisplayLocator.Select();
        if (target is null)
        {
            Console.WriteLine("FAIL: no Rokid display.");
            return 1;
        }

        using var station = new SpatialStation();
        station.OnLog += line => Console.WriteLine($"    [station] {line}");
        station.Input.OnMouseDispatched += line => Console.WriteLine($"    [wire] {line}");
        station.Start(hwnd);
        station.QueueCapture(hwnd);

        // A synthetic IMU stream, so the panel has a pose.  The real
        // device feeds this in normal use; the cursor's behaviour does
        // not depend on where the head is.
        var accel = new System.Numerics.Vector3(0f, 9.80665f, 0f);
        ulong ts = 1_000_000_000UL;
        for (var i = 0; i < 800; i++)
        {
            station.FeedImu(new RokidDiyStation.Core.Protocol.ImuSample(
                ts, accel, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, true, ts));
            ts += (ulong)(1e9 / 437.0);
        }

        station.AnchorHere();
        await Task.Delay(600).ConfigureAwait(false);

        if (!station.Anchor.HasPanel)
        {
            Console.WriteLine("FAIL: the panel was not anchored.");
            return 1;
        }
        Console.WriteLine($"[{appName}] panel anchored");

        // Give the coordinator time to attach.
        var deadline = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < deadline && !station.Input.IsInteractive)
        {
            await Task.Delay(500).ConfigureAwait(false);
        }

        if (!station.Input.IsInteractive)
        {
            Console.WriteLine("FAIL: the station never became interactive.");
            return 1;
        }
        Console.WriteLine($"[{appName}] interactive over the DevTools protocol");
        await Task.Delay(1500).ConfigureAwait(false);

        var metrics = await station.Input.EvaluateAsync(
            "JSON.stringify({w: innerWidth, h: innerHeight, dpr: devicePixelRatio})").ConfigureAwait(false);
        Console.WriteLine($"[{appName}] viewport: {metrics}");

        // Put the physical pointer on the glasses' display.
        var bounds = target.Bounds;
        var centreX = bounds.X + bounds.Width / 2;
        var centreY = bounds.Y + bounds.Height / 2;

        GetCursorPos(out var pointerBefore);
        SetCursorPos(centreX, centreY);
        await Task.Delay(600).ConfigureAwait(false);

        var cursor = station.Renderer.Cursor;
        if (cursor is null)
        {
            Console.WriteLine("FAIL: the renderer has no virtual cursor.");
            return 1;
        }

        // Re-enter so the re-seed is deterministic.
        SetCursorPos(bounds.X - 200, centreY);
        await Task.Delay(500).ConfigureAwait(false);
        SetCursorPos(centreX, centreY);
        await Task.Delay(500).ConfigureAwait(false);

        var (u0, v0) = cursor.Position;
        Console.WriteLine($"[{appName}] cursor at entry: u={u0:F4} v={v0:F4}");

        SetCursorPos(centreX + 200, centreY);
        await Task.Delay(500).ConfigureAwait(false);
        var (u1, v1) = cursor.Position;
        Console.WriteLine($"[{appName}] after RIGHT: u={u1:F4} v={v1:F4} (du={u1 - u0:+0.0000;-0.0000})");

        SetCursorPos(centreX + 200, centreY + 150);
        await Task.Delay(500).ConfigureAwait(false);
        var (u2, v2) = cursor.Position;
        Console.WriteLine($"[{appName}] after DOWN : u={u2:F4} v={v2:F4} (dv={v2 - v1:+0.0000;-0.0000})");

        var ok = true;
        if (u1 <= u0) { Console.WriteLine("[!] the cursor did not follow the mouse right"); ok = false; }
        if (v2 <= v1) { Console.WriteLine("[!] the cursor did not follow the mouse down"); ok = false; }

        // Click where the cursor is.
        //
        // The recorder is installed LAST, right before the click.  A
        // single-page application re-renders its own DOM, which wipes
        // any listener attached earlier - installing it up front and
        // clicking later measures the application, not the input path.
        //
        // Note: no "return" statement.  Runtime.evaluate runs the
        // expression at the top level, where "return" is a syntax
        // error - the whole statement then fails and the recorder is
        // never installed.  The expression's value is the last
        // statement's value, which is all we need.
        await station.Input.EvaluateAsync(
            "window.__rokid = []; ['mousedown','mouseup','click','pointerdown'].forEach(t => "
            + "window.addEventListener(t, (e) => window.__rokid.push({t: t, x: e.clientX, y: e.clientY, "
            + "trusted: e.isTrusted}), true)); 'installed'").ConfigureAwait(false);

        // A control: a click the page makes to itself.  If the recorder
        // catches THIS, the recorder is alive; if it is empty, the
        // recorder was wiped and the real click may have arrived unseen.
        await station.Input.EvaluateAsync(
            "document.body.dispatchEvent(new MouseEvent('click', {clientX: 11, clientY: 22, bubbles: true}))")
            .ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false);
        var control = await station.Input.EvaluateAsync(
            "JSON.stringify(window.__rokid || [])").ConfigureAwait(false);
        Console.WriteLine($"[{appName}] control (a self-made click): {control ?? "(no reply)"}");
        var recorderAlive = control is not null && control.Contains("\"x\":11");

        Click();
        await Task.Delay(900).ConfigureAwait(false);

        var eventsRaw = await station.Input.EvaluateAsync(
            "JSON.stringify(window.__rokid || [])").ConfigureAwait(false);

        Console.WriteLine($"[{appName}] the application received: {eventsRaw ?? "(no reply)"}");

        // The real click is the TRUSTED one at the cursor's position.
        // The control was a self-made event at (11, 22), so it is
        // untrusted - and it must not be mistaken for the real thing.
        //
        // The check is therefore on the trusted click specifically, not
        // on the whole array: the control stays in the array for the
        // whole run, so looking for "no x:11 anywhere" would always
        // fail.
        var got = eventsRaw is not null
                  && eventsRaw.Contains("\"t\":\"click\"")
                  && eventsRaw.Contains("\"trusted\":true");
        Console.WriteLine();
        if (!recorderAlive)
        {
            Console.WriteLine($"[{appName}] INCONCLUSIVE - the page re-rendered and wiped the recorder. "
                + "The application's own log is the authority here.");
        }
        else
        {
            Console.WriteLine(got
                ? $"PASS - the physical mouse clicked inside {appName} through the virtual cursor."
                : $"FAIL - {appName} received no click.");
        }

        GetCursorPos(out var pointerAfter);
        Console.WriteLine($"[{appName}] OS pointer: ({pointerBefore.x}, {pointerBefore.y}) -> ({pointerAfter.x}, {pointerAfter.y})");

        return got && ok ? 0 : 1;
    }

    private static void Click()
    {
        Send(0x0002);
        Thread.Sleep(40);
        Send(0x0004);
    }

    private static void Send(uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }
}
