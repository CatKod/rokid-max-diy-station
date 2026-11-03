$repo = 'D:\GitHub\rokid-max-diy-station'
$out = Join-Path $repo 'artifacts\ccd-test.log'
New-Item -ItemType Directory -Path (Split-Path $out) -Force | Out-Null
Set-Content -Path $out -Value "--- ccd test $(Get-Date) ---"

function Log($m) {
    $line = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $m
    Write-Host $line
    Add-Content -Path $out -Value $line
}

$devcon = 'D:\Programming\EBpro\devcon_x64.exe'
$probe = Join-Path $repo 'tools\ProbeCcd2\bin\Release\net8.0-windows\ProbeCcd2.exe'

Log '=== 1. with the virtual display driver present ==='
& $probe 2>&1 | ForEach-Object { Log "  $_" }

Log ''
Log '=== 2. removing the driver ==='
& $devcon remove 'Root\MttVDD' 2>&1 | ForEach-Object { Log "  $_" }
Start-Sleep -Seconds 5

Log ''
Log '=== 3. with the driver gone ==='
& $probe 2>&1 | ForEach-Object { Log "  $_" }

Log 'Done.'
