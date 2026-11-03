// The driver's control pipe, with the framing the driver actually
// expects.
//
// The earlier attempt used StreamWriter (UTF-8, newline-terminated)
// and every command came back as an empty string - which is what a
// server that could not parse the request does: it closes the pipe
// without answering.
//
// The working framing is UTF-16LE with NO terminator, and the
// response is read as raw bytes until the pipe closes.
//
// This probe is read-only apart from SETDISPLAYCOUNT 1, which only
// asks the driver to publish ONE monitor.  The count 0 - the one that
// tears the adapter down and produces error code 43 - is never sent.
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;

internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("PING -> " + Send("PING"));
        Console.WriteLine("GETSETTINGS -> " + Send("GETSETTINGS"));
        Console.WriteLine("SETDISPLAYCOUNT 1 -> " + Send("SETDISPLAYCOUNT 1"));

        Console.WriteLine();
        Console.WriteLine("done - run the adapter probe again");
        return 0;
    }

    private static string Send(string command)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "MTTVirtualDisplayPipe", PipeDirection.InOut);
                pipe.Connect(3000);

                var bytes = Encoding.Unicode.GetBytes(command);
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();

                using var ms = new MemoryStream();
                var buffer = new byte[4096];
                int read;
                while ((read = pipe.Read(buffer, 0, buffer.Length)) > 0)
                    ms.Write(buffer, 0, read);

                var text = Encoding.Unicode.GetString(ms.ToArray());
                Console.WriteLine($"  (attempt {attempt})");
                return text.Length > 0 ? text : "(empty)";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  attempt {attempt}: {ex.GetType().Name}: {ex.Message}");
                System.Threading.Thread.Sleep(1000);
            }
        }

        return "(failed)";
    }
}
