$repo = 'D:\GitHub\rokid-max-diy-station'
$out = Join-Path $repo 'artifacts\install-vdd.log'
New-Item -ItemType Directory -Path (Split-Path $out) -Force | Out-Null
Set-Content -Path $out -Value "--- install-vdd $(Get-Date) ---"

function Log($m) {
    $line = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $m
    Write-Host $line
    Add-Content -Path $out -Value $line
}

$devcon = 'D:\Programming\EBpro\devcon_x64.exe'
$inf = 'C:\Windows\System32\DriverStore\FileRepository\mttvdd.inf_amd64_d9cd99eb029b9822\MttVDD.inf'
$probe = Join-Path $repo 'tools\ProbeCcd2\bin\Release\net8.0-windows\ProbeCcd2.exe'

Log '=== the desktop before ==='
& $probe 2>&1 | ForEach-Object { Log "  $_" }

Log ''
Log '=== removing the device ==='
& $devcon remove 'Root\MttVDD' 2>&1 | ForEach-Object { Log "  $_" }
Start-Sleep -Seconds 3

Log ''
Log '=== installing it again ==='
& $devcon install $inf 'Root\MttVDD' 2>&1 | ForEach-Object { Log "  $_" }

Log ''
Log '=== waiting ==='
for ($i = 0; $i -lt 30; $i++) {
    $dev = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
           Where-Object { $_.FriendlyName -like '*Virtual Display*' }
    if ($dev -and $dev.Status -eq 'OK') { Log "  the device is Started (after $($i + 1) s)"; break }
    Start-Sleep -Seconds 1
}
Start-Sleep -Seconds 3

Log ''
Log '=== the desktop after ==='
& $probe 2>&1 | ForEach-Object { Log "  $_" }

Log 'Done.'
