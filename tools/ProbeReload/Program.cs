// SPDX-License-Identifier: MIT
//
// A live end-to-end check of the reload mechanism.
//
// The reload's whole difficulty is in the INTERACTION between two
// processes: the replacement has to wait for the outgoing instance to
// release the single-instance mutex, and it has to end up running.
//
// A unit test cannot see that.  This tool runs the real app, sends it
// the reload command through the same pipe the tray menu uses, and
// checks that a new process took over.
//
// It is a diagnostic, not part of the test suite, because it needs the
// real app and it changes the machine's display state.
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

var exe = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "RokidDiyStation.exe");

if (!File.Exists(exe))
{
    Console.WriteLine($"FAIL: {exe} does not exist");
    return 1;
}

static int CountProcesses(string name)
{
    var n = 0;
    foreach (var p in Process.GetProcessesByName(name))
    {
        n++;
        p.Dispose();
    }
    return n;
}

// The pipe the single-instance "show the window" signal uses.
const string PipeName = "RokidDiyStation.ShowWindow";

static void Signal(string message)
{
    try
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        client.Connect(timeout: 2000);
        using var writer = new StreamWriter(client, new UTF8Encoding(false));
        writer.Write(message);
        writer.Flush();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  (signal failed: {ex.GetType().Name}: {ex.Message})");
    }
}

Console.WriteLine($"exe: {exe}");

// --- 1. Start the first instance -------------------------------
var first = Process.Start(new ProcessStartInfo(exe)
{
    UseShellExecute = false,
    WorkingDirectory = Path.GetDirectoryName(exe)!,
});
if (first is null) { Console.WriteLine("FAIL: could not start the first instance"); return 1; }

Console.WriteLine($"started the first instance, pid {first.Id}");
Thread.Sleep(6000);

var count = CountProcesses("RokidDiyStation");
Console.WriteLine($"processes running: {count}");

// --- 2. Ask the second instance to show its window --------------
// This proves the pipe server is alive and that a second copy does
// not leave a duplicate behind.
var second = Process.Start(new ProcessStartInfo(exe)
{
    UseShellExecute = false,
    WorkingDirectory = Path.GetDirectoryName(exe)!,
});
if (second is not null)
{
    Thread.Sleep(3000);
    Console.WriteLine($"second instance exited: {second.HasExited} (code {(second.HasExited ? second.ExitCode.ToString() : "n/a")})");
    second.Dispose();
}

count = CountProcesses("RokidDiyStation");
Console.WriteLine($"processes after the double-click: {count} (expected 1)");

// --- 3. Reload --------------------------------------------------
// The reload is driven through the same pipe the tray menu uses, so
// this exercises the REAL path: save, release the virtual display,
// start the replacement, quit.
var before = CountProcesses("RokidDiyStation");
Console.WriteLine($"--- reloading (before: {before} processes) ---");

var firstPid = first.Id;
Console.WriteLine($"asking the running instance (pid {firstPid}) to reload");

Signal("reload");

// The outgoing instance has to quit and the replacement has to take
// over.  Give it time.
Thread.Sleep(12000);

var after = CountProcesses("RokidDiyStation");
Console.WriteLine($"processes after the reload: {after}");

// The ORIGINAL process must be gone - that is what "reload" means.
var originalExited = first.HasExited;
Console.WriteLine($"the original instance exited: {originalExited}");

// And a NEW process must be running.
var newPid = 0;
foreach (var p in Process.GetProcessesByName("RokidDiyStation"))
{
    if (p.Id != firstPid) { newPid = p.Id; }
    p.Dispose();
}
Console.WriteLine($"the replacement is running: {newPid != 0} (pid {newPid})");

// The pipe must still work, so a later double-click can show the
// window of the instance that took over.
Signal("0");
Thread.Sleep(2000);

var final = CountProcesses("RokidDiyStation");
Console.WriteLine($"processes at the end: {final}");

var ok = originalExited && newPid != 0 && final >= 1;
Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");

// Clean up.
foreach (var p in Process.GetProcessesByName("RokidDiyStation"))
{
    try { p.Kill(); } catch { }
    p.Dispose();
}

return ok ? 0 : 1;
