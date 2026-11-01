// SPDX-License-Identifier: MIT
//
// Probe: how does the DevTools protocol deliver a keystroke?
//
// The keyboard is the last missing input channel.  The mouse works -
// we proved that against a real Electron app - but for the keyboard
// there is a genuine protocol subtlety that has to be settled
// EMPIRICALLY, because getting it wrong produces a very confusing
// bug: the app types the character TWICE.
//
// The subtlety
// ------------
// Input.dispatchKeyEvent has four types:
//
//   keyDown      - a key event that may also carry text
//   rawKeyDown   - a key event with no text at all
//   keyUp        - the release
//   char         - a text-only event, no key
//
// Chromium's rule is roughly: for a printable key, `keyDown` WITH a
// text field generates the character; `keyDown` WITHOUT text (or
// `rawKeyDown`) does not; and a separate `char` event also generates
// it.  So sending `keyDown` with text AND a `char` would type the
// character twice.
//
// Which combination an APPLICATION wants is the open question:
//   * a plain web page's text input wants the character once;
//   * an Electron app's workbench has its own keybinding service, and
//     a shortcut like Ctrl+S must arrive as a key event WITHOUT text,
//     while a character typed into the editor must arrive WITH text.
//
// So the probe measures, for each candidate strategy, exactly what the
// app received - the number of characters, the event types, the
// `key` values, whether the events were trusted, and whether the
// OS mouse/keyboard state was disturbed.
//
// The strategies tested:
//
//   S1  keyDown WITH text, then keyUp        - the obvious one
//   S2  rawKeyDown (no text), then keyUp     - the key-only one
//   S3  keyDown (no text), then char, then keyUp
//   S4  rawKeyDown, then char, then keyUp
//
// The probe installs a recorder in the page, types through each
// strategy, and reports what arrived.
//
// Usage:
//   dotnet run --project tools/ProbeKeyboardInput
using System.Diagnostics;
using System.Text.Json.Nodes;
using RokidDiyStation.App.Input;
using RokidDiyStation.App.Spatial;

internal static class Program
{
    private const int ProbePort = 9353;

    private static async Task Main()
    {
        Console.WriteLine("=== CONTROL: Chromium page ===");
        await RunAsync(BrowserLauncher.FindExecutable(), ChromeArgs(), "chrome").ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("=== SUBJECT: Electron (VS Code) ===");
        await RunAsync(DefaultElectronExecutable(), Array.Empty<string>(), "electron").ConfigureAwait(false);
    }

    private static string[] ChromeArgs() => new[]
    {
        "--app=data:text/html,<body style='margin:0'><input id=t style='font-size:20px;width:600px'><div id=out></div></body>",
        "--no-first-run",
    };

    private static async Task RunAsync(string? exe, string[] extraArgs, string label)
    {
        if (exe is null || !File.Exists(exe))
        {
            Console.WriteLine($"{label}: not installed, skipping.");
            return;
        }

        var profile = Path.Combine(Path.GetTempPath(), "rokid-kb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(profile);

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in extraArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add($"--remote-debugging-port={ProbePort}");
        psi.ArgumentList.Add($"--user-data-dir={profile}");

        Process? process;
        try { process = Process.Start(psi); }
        catch (Exception ex) { Console.WriteLine($"{label}: launch failed: {ex.Message}"); return; }

        try
        {
            CdpTarget? target = null;
            var deadline = Environment.TickCount64 + 60_000;
            while (Environment.TickCount64 < deadline)
            {
                var pages = await CdpDiscovery.ListPagesAsync(ProbePort).ConfigureAwait(false);
                target = pages.FirstOrDefault(p => !p.Url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase));
                if (target is not null) break;
                await Task.Delay(400).ConfigureAwait(false);
            }
            if (target is null) { Console.WriteLine($"{label}: no page target."); return; }

            var router = new BrowserInputRouter();
            if (!await router.ConnectAsync(target.WebSocketDebuggerUrl).ConfigureAwait(false))
            {
                Console.WriteLine($"{label}: websocket failed."); return;
            }
            await router.EnsureActiveAsync().ConfigureAwait(false);

            // Wait for a real document.
            var ready = false;
            deadline = Environment.TickCount64 + 60_000;
            while (Environment.TickCount64 < deadline)
            {
                var raw = await router.EvaluateAsync(
                    "JSON.stringify({href:location.href, elems:document.querySelectorAll('*').length})").ConfigureAwait(false);
                try
                {
                    var n = JsonNode.Parse(raw ?? "{}");
                    var href = (string?)n?["href"] ?? "";
                    var elems = n?["elems"]?.GetValue<int>() ?? 0;
                    if (elems > 20 && !href.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) { ready = true; break; }
                }
                catch { }
                await Task.Delay(400).ConfigureAwait(false);
            }
            if (!ready) { Console.WriteLine($"{label}: document never loaded."); return; }

            // Install the recorder.  It captures every key event, in
            // order, with the fields that matter.
            await router.EvaluateAsync(
                "(() => { window.__kb = []; " +
                "for (const t of ['keydown','keypress','keyup','beforeinput','input']) " +
                "  window.addEventListener(t, (e) => window.__kb.push({t: t, key: e.key, code: e.code, " +
                "keyCode: e.keyCode, trusted: e.isTrusted, data: e.data === undefined ? null : e.data, " +
                "target: (e.target && (e.target.id || e.target.tagName)) || '?'}), true); " +
                "return 1; })()").ConfigureAwait(false);

            Console.WriteLine($"{label}: recorder installed.");

            // The key we will type: 'A'.  The DOM names are what the
            // protocol expects.
            const string domKey = "a";
            const string domCode = "KeyA";
            const int vk = 0x41; // 'A'

            await RunStrategyAsync(router, label, "S1 keyDown WITH text + keyUp",
                async () =>
                {
                    await router.SendRawAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = "keyDown", ["key"] = domKey, ["code"] = domCode,
                        ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk,
                        ["text"] = "a", ["unmodifiedText"] = "a", ["modifiers"] = 0,
                    });
                    await router.SendRawAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = "keyUp", ["key"] = domKey, ["code"] = domCode,
                        ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk,
                        ["modifiers"] = 0,
                    });
                }).ConfigureAwait(false);

            await RunStrategyAsync(router, label, "S2 rawKeyDown (no text) + keyUp",
                async () =>
                {
                    await router.SendRawAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = "rawKeyDown", ["key"] = domKey, ["code"] = domCode,
                        ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk,
                        ["modifiers"] = 0,
                    });
                    await router.SendRawAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = "keyUp", ["key"] = domKey, ["code"] = domCode,
                        ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk,
                        ["modifiers"] = 0,
                    });
                }).ConfigureAwait(false);

            await RunStrategyAsync(router, label, "S3 keyDown (no text) + char + keyUp",
                async () =>
                {
                    await router.SendRawAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = "keyDown", ["key"] = domKey, ["code"] = domCode,
                        ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk,
                        ["modifiers"] = 0,
                    });
                    await router.SendRawAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = "char", ["key"] = "a", ["text"] = "a", ["unmodifiedText"] = "a",
                        ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk, ["modifiers"] = 0,
                    });
                    await router.SendRawAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = "keyUp", ["key"] = domKey, ["code"] = domCode,
                        ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk,
                        ["modifiers"] = 0,
                    });
                }).ConfigureAwait(false);

            await RunStrategyAsync(router, label, "S4 rawKeyDown + char + keyUp",
                async () =>
                {
                    await router.SendRawAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = "rawKeyDown", ["key"] = domKey, ["code"] = domCode,
                        ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk,
                        ["modifiers"] = 0,
                    });
                    await router.SendRawAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = "char", ["key"] = "a", ["text"] = "a", ["unmodifiedText"] = "a",
                        ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk, ["modifiers"] = 0,
                    });
                    await router.SendRawAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = "keyUp", ["key"] = domKey, ["code"] = domCode,
                        ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk,
                        ["modifiers"] = 0,
                    });
                }).ConfigureAwait(false);

            // And a MODIFIER, to check that the bitmask works.
            await RunStrategyAsync(router, label, "S5 Ctrl+S (modifier bitmask)",
                async () =>
                {
                    await router.SendRawAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = "rawKeyDown", ["key"] = "Control", ["code"] = "ControlLeft",
                        ["windowsVirtualKeyCode"] = 0x11, ["nativeVirtualKeyCode"] = 0x11,
                        ["modifiers"] = 2,
                    });
                    await router.SendRawAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = "rawKeyDown", ["key"] = "s", ["code"] = "KeyS",
                        ["windowsVirtualKeyCode"] = 0x53, ["nativeVirtualKeyCode"] = 0x53,
                        ["modifiers"] = 2,
                    });
                    await router.SendRawAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = "keyUp", ["key"] = "s", ["code"] = "KeyS",
                        ["windowsVirtualKeyCode"] = 0x53, ["nativeVirtualKeyCode"] = 0x53,
                        ["modifiers"] = 2,
                    });
                    await router.SendRawAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = "keyUp", ["key"] = "Control", ["code"] = "ControlLeft",
                        ["windowsVirtualKeyCode"] = 0x11, ["nativeVirtualKeyCode"] = 0x11,
                        ["modifiers"] = 0,
                    });
                }).ConfigureAwait(false);

            router.Dispose();
        }
        finally
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(profile, true); } catch { }
            await Task.Delay(1500).ConfigureAwait(false);
        }
    }

    private static async Task RunStrategyAsync(
        BrowserInputRouter router, string label, string name, Func<Task> strategy)
    {
        // Clear the recorder so we only see this strategy's events.
        await router.EvaluateAsync("window.__kb = []").ConfigureAwait(false);

        await strategy().ConfigureAwait(false);
        await Task.Delay(350).ConfigureAwait(false);

        var raw = await router.EvaluateAsync("JSON.stringify(window.__kb || [])").ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"{label}: {name}");

        var events = new List<JsonNode>();
        if (raw is not null)
        {
            try
            {
                if (JsonNode.Parse(raw) is JsonArray arr)
                    foreach (var i in arr) if (i is not null) events.Add(i);
            }
            catch { }
        }

        if (events.Count == 0)
        {
            Console.WriteLine("    (nothing arrived)");
            return;
        }

        var charEvents = 0;
        var trusted = 0;
        foreach (var e in events)
        {
            Console.WriteLine($"    {e}");
            if ((string?)e?["t"] == "input") charEvents++;
            if ((bool?)e?["trusted"] == true) trusted++;
        }

        // The verdict: did a CHARACTER get produced, and how many?
        var inputs = events.Count(e => (string?)e?["t"] == "input");
        var keydowns = events.Count(e => (string?)e?["t"] == "keydown");
        var keyups = events.Count(e => (string?)e?["t"] == "keyup");
        var keypresses = events.Count(e => (string?)e?["t"] == "keypress");

        Console.WriteLine($"    -> keydown={keydowns} keypress={keypresses} keyup={keyups} input={inputs} "
                        + $"trusted={trusted}/{events.Count}");
    }

    private static string? DefaultElectronExecutable()
    {
        var candidates = new[]
        {
            @"C:\Users\kimvi\AppData\Local\Programs\Microsoft VS Code\Code.exe",
            @"C:\Program Files\Microsoft VS Code\Code.exe",
            @"C:\Users\kimvi\AppData\Local\Programs\Notion\Notion.exe",
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }
}
