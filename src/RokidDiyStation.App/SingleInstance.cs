// SPDX-License-Identifier: MIT
//
// Enforce single-instance behaviour for the tray app.
//
// We use a named Mutex (HKCU-scope) so two copies of the same exe
// can't run at once and fight over the HID device.  When the second
// instance starts it sends a "show" message to the first via a named
// pipe and exits; the first brings the settings window to the front.
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace RokidDiyStation.App;

public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\RokidDiyStation.SingleInstance";
    private const string PipeName = "RokidDiyStation.ShowWindow";
    private readonly Mutex _mutex;
    private CancellationTokenSource? _listenerCts;

    public bool IsOwner { get; }
    public event Action<int>? OnShowRequested;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsOwner = createdNew;
        if (IsOwner) StartPipeServer();
    }

    private void StartPipeServer()
    {
        _listenerCts = new CancellationTokenSource();
        var token = _listenerCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var payload = await reader.ReadToEndAsync().ConfigureAwait(false);
                    if (int.TryParse(payload.Trim(), out var hwnd))
                    {
                        OnShowRequested?.Invoke(hwnd);
                    }
                    else
                    {
                        OnShowRequested?.Invoke(0);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { /* client died, try again */ }
                catch (UnauthorizedAccessException) { /* already a server, bail */ break; }
            }
        }, token);
    }

    /// <summary>Ask the running instance to show its window.  Returns true
    /// if a signal was delivered (i.e. another instance was already running).</summary>
    public static bool SignalShow(int hwnd = 0)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 500);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(hwnd.ToString());
            writer.Flush();
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
    }

    public void Dispose()
    {
        try { _listenerCts?.Cancel(); } catch { }
        if (IsOwner)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
    }
}
