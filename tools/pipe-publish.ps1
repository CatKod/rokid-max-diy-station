$ErrorActionPreference = 'Continue'

# Publish ONE virtual monitor through the driver's control pipe.
#
# ONLY the count 1 is sent here.  The count 0 tears the adapter down
# and the driver comes back in error code 43, so it is never sent.
#
# This is a recovery step for a driver that booted with no monitor
# published - which is exactly the state after the machine was reset.

function Send-Pipe([string]$msg) {
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'MTTVirtualDisplayPipe', [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect(5000)
        $sw = New-Object System.IO.StreamWriter($pipe); $sw.WriteLine($msg); $sw.Flush()
        $sr = New-Object System.IO.StreamReader($pipe)
        $result = $sr.ReadLine()
        $pipe.Dispose()
        return $result
    } catch {
        return "ERROR: $($_.Exception.Message)"
    }
}

Write-Host ("PING  -> '" + (Send-Pipe 'PING') + "'")
Write-Host ("STATS -> '" + (Send-Pipe 'GETSETTINGS') + "'")
Write-Host ("COUNT1 -> '" + (Send-Pipe 'SETDISPLAYCOUNT 1') + "'")

Start-Sleep -Seconds 5
Write-Host 'done - now run the adapter probe again'
