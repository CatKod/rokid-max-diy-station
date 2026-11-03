// SPDX-License-Identifier: MIT
//
// Enforce single-instance behaviour for the tray app.
//
// We use a named Mutex (HKCU-scope) so two copies of the same exe
// can't run at once and fight over the HID device.  When the second
// instance starts it sends a "show" message to the first via a named
// pipe and exits; the first brings the settings window to the front.
//
// THE RELOAD PATH
// ---------------
// The app has a "reload" action that restarts it, and the reload
// breaks the assumption above.  The new instance is started BEFORE the
// old one has finished exiting, so at the moment the new one looks at
// the mutex the old one still holds it.
//
// The new instance would therefore conclude "another copy is running",
// signal the old one to show its window, and exit - leaving the user
// with no app at all, which is the exact opposite of what they asked
// for.
//
// So the reload starts the replacement with a "--reload" flag, and an
// instance started with that flag does not signal and exit: it WAITS
// for the mutex to become free, takes it over, and carries on.  That
// is the only difference between the two modes, and it is what makes
// the reload reliable.
//
// The wait has a deadline.  If the old instance is wedged and never
// releases the mutex, the replacement must not hang forever: it
// falls back to the normal "signal and exit" behaviour, which at
// least brings the old instance's window to the front.
//
// THE PIPE PROTOCOL
// -----------------
// The pipe carries a single line, and the line is a command:
//
//   "<hwnd>"     bring the settings window to the front
//   "reload"     restart the app in place
//   "quit"       close the spatial display and exit
//   "close-spatial"  close the spatial display, but keep running
//   "open-spatial"   open the spatial display, but keep running
//
// The reload command exists so that the reload can be driven from the
// outside - by a script, by a diagnostic tool, or by a test - without
// clicking the tray menu.  That matters because the reload's whole
// difficulty is in the INTERACTION between two processes, and an
// interaction that can only be triggered by a mouse click cannot be
// verified automatically.
//
// The quit command is here for the same reason, and for a specific
// reason: the app's EXIT path has to take the virtual display off the
// desktop, and that is the behaviour the user complained about.  A
// kill - a Stop-Process, a crash - does NOT run the exit path, so a
// test that kills the app is not testing the thing that has to work.
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace RokidDiyStation.App;

public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\RokidDiyStation.SingleInstance";
    private const string PipeName = "RokidDiyStation.ShowWindow";

    /// <summary>
    /// How long a replacement instance waits for the outgoing one to
    /// release the mutex.
    /// </summary>
    /// <remarks>
    /// The outgoing instance releases the mutex as the very last thing
    /// it does, so this only has to cover a normal shutdown - a
    /// fraction of a second.  The generous deadline is there for the
    /// case where the outgoing instance is busy tearing the virtual
    /// display down, which can take a moment.
    /// </remarks>
    private const int TakeoverTimeoutMs = 15000;

    /// <summary>How long to wait between attempts to take the mutex.</summary>
    private const int TakeoverPollMs = 100;

    private readonly Mutex _mutex;
    private CancellationTokenSource? _listenerCts;
    private bool _released;

    public bool IsOwner { get; private set; }

    /// <summary>
    /// True when this instance is a replacement that waited for the
    /// outgoing one to go away.
    /// </summary>
    public bool TookOver { get; private set; }

    /// <summary>
    /// Raised when the pipe asks for a reload.
    /// </summary>
    /// <remarks>
    /// The reload is driven from here rather than from the pipe's
    /// listener thread, because it has to touch WPF and the GPU.
    /// </remarks>
    public event Action? OnReloadRequested;

    /// <summary>
    /// Raised when the pipe asks the app to quit.
    /// </summary>
    /// <remarks>
    /// The quit has to go through the same route as the tray menu's
    /// "Quit", because that is the route that closes the spatial
    /// display and gives the virtual display back.
    /// </remarks>
    public event Action? OnQuitRequested;

    /// <summary>
    /// Raised when the pipe asks the app to close the spatial display.
    /// </summary>
    /// <remarks>
    /// This is the "Close the spatial display" tray command, exposed
    /// on the pipe for the same reason as the quit: the user's report
    /// is about the virtual display NOT going away when the spatial
    /// display is closed, and that has to be verifiable without a
    /// mouse click.
    ///
    /// It is a DIFFERENT path from the quit, and it has to be tested
    /// separately, because the quit also tears the process down - and
    /// a process that is exiting cannot be observed reconciling a
    /// topology change.
    /// </remarks>
    public event Action? OnCloseSpatialRequested;

    /// <summary>
    /// Raised when the pipe asks the app to open the spatial display.
    /// </summary>
    /// <remarks>
    /// The open side of the round trip.  A close that works but
    /// cannot be undone is not a fix, so both halves have to be
    /// drivable from outside the process.
    /// </remarks>
    public event Action? OnOpenSpatialRequested;

    public event Action<int>? OnShowRequested;

    /// <summary>
    /// Create the single-instance guard.
    /// </summary>
    /// <param name="takeOver">
    /// True when this instance is a RELOAD replacement.  It waits for
    /// the outgoing instance to release the mutex instead of signalling
    /// it and exiting.
    /// </param>
    public SingleInstance(bool takeOver = false)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

        if (createdNew)
        {
            IsOwner = true;
            StartPipeServer();
            return;
        }

        if (takeOver && TryTakeOver())
        {
            IsOwner = true;
            TookOver = true;
            StartPipeServer();
            return;
        }

        // The normal path: another copy is already running, so hand the
        // "show the window" signal to it and exit.
    }

    /// <summary>
    /// Wait for the outgoing instance to release the mutex and take it.
    /// </summary>
    /// <returns>True when the mutex was taken within the deadline.</returns>
    private bool TryTakeOver()
    {
        var deadline = Environment.TickCount64 + TakeoverTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                // WaitOne(0) would spin the CPU; a short wait blocks
                // efficiently and still returns promptly.
                if (_mutex.WaitOne(TakeoverPollMs))
                    return true;
            }
            catch (AbandonedMutexException)
            {
                // The outgoing instance died without releasing the
                // mutex.  We own it now - this is exactly the state we
                // want, and the exception is the successful outcome.
                return true;
            }
            catch (Exception)
            {
                // Anything else (e.g. the mutex is not in a waitable
                // state) is not worth retrying forever.
                return false;
            }
        }
        return false;
    }

    private void StartPipeServer()
    {
        _listenerCts = new CancellationTokenSource();
        var token = _listenerCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    // The pipe may still be held by the outgoing
                    // instance in the reload path - the new instance
                    // takes over the mutex a moment before the old
                    // process has actually exited.  Creating a second
                    // server on the same name fails while the first one
                    // is alive, so the creation is retried rather than
                    // giving up: without it, a later double-click on
                    // the exe would find no listener and the window
                    // would never come to the front.
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            server = new NamedPipeServerStream(
                                PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                            break;
                        }
                        catch (IOException)
                        {
                            server = null;
                            try { await Task.Delay(250, token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { break; }
                        }
                        catch (UnauthorizedAccessException)
                        {
                            server = null;
                            try { await Task.Delay(250, token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { break; }
                        }
                    }

                    if (server is null) break;

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var payload = await reader.ReadToEndAsync().ConfigureAwait(false);
                    Dispatch(payload.Trim());
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { /* client died, try again */ }
                catch (UnauthorizedAccessException) { /* pipe taken, try again */ }
                finally
                {
                    try { server?.Dispose(); } catch { }
                }
            }
        }, token);
    }

    /// <summary>
    /// Act on a command from the pipe.
    /// </summary>
    /// <param name="payload">The command line.</param>
    private void Dispatch(string payload)
    {
        // The BOM matters here.
        //
        // `new StreamWriter(stream, Encoding.UTF8)` writes a BOM
        // preamble, because Encoding.UTF8 carries one.  The payload
        // therefore arrives as "\uFEFFreload", and the exact
        // comparison against the command would fail.
        //
        // This did not show up for the "show" command, because
        // int.TryParse simply rejects the BOM and the code falls
        // through to "show hwnd 0" - which happens to be the right
        // answer anyway.  So the show path worked BY ACCIDENT, and the
        // reload path, which needs an exact match, did not work at
        // all.
        //
        // Both ends now write without a BOM, and the BOM is stripped
        // here as well so that a client written against the old
        // behaviour still works.
        var command = payload.Trim().TrimStart('\uFEFF').Trim();

        if (string.Equals(command, ReloadCommand, StringComparison.OrdinalIgnoreCase))
        {
            OnReloadRequested?.Invoke();
            return;
        }

        if (string.Equals(command, QuitCommand, StringComparison.OrdinalIgnoreCase))
        {
            OnQuitRequested?.Invoke();
            return;
        }

        if (string.Equals(command, CloseSpatialCommand, StringComparison.OrdinalIgnoreCase))
        {
            OnCloseSpatialRequested?.Invoke();
            return;
        }

        if (string.Equals(command, OpenSpatialCommand, StringComparison.OrdinalIgnoreCase))
        {
            OnOpenSpatialRequested?.Invoke();
            return;
        }

        if (int.TryParse(command, out var hwnd))
        {
            OnShowRequested?.Invoke(hwnd);
        }
        else
        {
            OnShowRequested?.Invoke(0);
        }
    }

    /// <summary>
    /// The pipe command that asks the running instance to reload.
    /// </summary>
    public const string ReloadCommand = "reload";

    /// <summary>
    /// The pipe command that asks the running instance to quit.
    /// </summary>
    public const string QuitCommand = "quit";

    /// <summary>
    /// The pipe command that asks the running instance to close the
    /// spatial display, without quitting.
    /// </summary>
    public const string CloseSpatialCommand = "close-spatial";

    /// <summary>
    /// The pipe command that asks the running instance to open the
    /// spatial display, without quitting.
    /// </summary>
    public const string OpenSpatialCommand = "open-spatial";

    /// <summary>
    /// Ask the running instance to close the spatial display.
    /// </summary>
    /// <returns>True when the command was delivered.</returns>
    public static bool SignalCloseSpatial()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            // UTF8Encoding(false) - no BOM.  See Dispatch().
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(CloseSpatialCommand);
            writer.Flush();
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>Ask the running instance to show its window.  Returns true
    /// if a signal was delivered (i.e. another instance was already running).</summary>
    public static bool SignalShow(int hwnd = 0)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 500);
            // UTF8Encoding(false) - no BOM.  See Dispatch().
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(hwnd.ToString());
            writer.Flush();
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// Ask the running instance to reload itself.
    /// </summary>
    /// <returns>True when the command was delivered.</returns>
    public static bool SignalReload()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            // UTF8Encoding(false) - no BOM.  See Dispatch().
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(ReloadCommand);
            writer.Flush();
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// Release the mutex, so another instance can take over.
    /// </summary>
    /// <remarks>
    /// The outgoing instance calls this on its way out.  It has to be
    /// explicit: relying on the finalizer would mean the mutex was
    /// released at an unpredictable moment, and the replacement
    /// instance would be racing a garbage collector.
    /// </remarks>
    public void ReleaseOwnership()
    {
        if (!IsOwner || _released) return;
        _released = true;
        try { _mutex.ReleaseMutex(); } catch { }
    }

    public void Dispose()
    {
        try { _listenerCts?.Cancel(); } catch { }
        ReleaseOwnership();
        _mutex.Dispose();
    }
}
