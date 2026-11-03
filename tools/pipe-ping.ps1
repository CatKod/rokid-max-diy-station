$ErrorActionPreference = 'Continue'

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

Write-Output ("PING  -> " + (Send-Pipe 'PING'))
Write-Output ("STATS -> " + (Send-Pipe 'GETSETTINGS'))
