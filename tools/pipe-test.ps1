$ErrorActionPreference = 'Continue'

function Log($m) { Write-Host ("{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }

function Send-Pipe([string]$msg) {
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'MTTVirtualDisplayPipe', [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect(5000)
        $sw = New-Object System.IO.StreamWriter($pipe)
        $sw.WriteLine($msg); $sw.Flush()
        $sr = New-Object System.IO.StreamReader($pipe)
        $result = $sr.ReadLine()
        $pipe.Dispose()
        return $result
    } catch {
        return "ERROR: $($_.Exception.Message)"
    }
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public static class Disp {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct MI { public int cb; public RECT m, w; public uint f;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string d; }
  delegate bool CB(IntPtr h, IntPtr dc, ref RECT r, IntPtr x);
  [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr a, IntPtr b, CB c, IntPtr d);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr h, ref MI mi);
  public static List<string> List() {
    var l = new List<string>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr x) => {
      var mi = new MI { cb = Marshal.SizeOf<MI>() };
      if (GetMonitorInfo(h, ref mi)) l.Add(mi.d + " " + (mi.m.R-mi.m.L) + "x" + (mi.m.B-mi.m.T) + " @ (" + mi.m.L + "," + mi.m.T + ")");
      return true; }, IntPtr.Zero);
    return l;
  }
}
'@

Log '=== PING ==='
Log "  $(Send-Pipe 'PING')"

Log ''
Log '=== GETSETTINGS ==='
Log "  $(Send-Pipe 'GETSETTINGS')"

Log ''
Log '=== SETDISPLAYCOUNT 0 (no virtual display) ==='
Log "  $(Send-Pipe 'SETDISPLAYCOUNT 0')"
Start-Sleep -Seconds 3
Log '  the desktop now:'
foreach ($m in [Disp]::List()) { Log "    $m" }

Log ''
Log '=== SETDISPLAYCOUNT 1 (virtual display back) ==='
Log "  $(Send-Pipe 'SETDISPLAYCOUNT 1')"
Start-Sleep -Seconds 3
Log '  the desktop now:'
foreach ($m in [Disp]::List()) { Log "    $m" }
