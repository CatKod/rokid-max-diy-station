$repo = 'D:\GitHub\rokid-max-diy-station'
$out = Join-Path $repo 'artifacts\restore-vdd-settings.log'
New-Item -ItemType Directory -Path (Split-Path $out) -Force | Out-Null
Set-Content -Path $out -Value "--- restore vdd settings $(Get-Date) ---"

function Log($m) {
    $line = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $m
    Write-Host $line
    Add-Content -Path $out -Value $line
}

$folder = 'C:\Windows\System32\DriverStore\FileRepository\mttvdd.inf_amd64_d9cd99eb029b9822'
$src = Join-Path $repo 'tools\vdd_settings.xml'
$dst = Join-Path $folder 'vdd_settings.xml'

Log '=== taking ownership of the DriverStore folder ==='
& takeown /F $folder /R /D Y 2>&1 | ForEach-Object { Log "  $_" }
& icacls $folder /grant '*S-1-5-32-544:F' /T /C 2>&1 | ForEach-Object { Log "  $_" }

Log ''
Log '=== copying the settings ==='
Copy-Item $src $dst -Force
if (Test-Path $dst) { Log "  copied -> $dst" } else { Log '  COPY FAILED' }

Log ''
Log '=== restarting the device so the driver re-advertises its modes ==='
& 'D:\Programming\EBpro\devcon_x64.exe' remove 'Root\MttVDD' 2>&1 | ForEach-Object { Log "  $_" }
Start-Sleep -Seconds 4
& 'D:\Programming\EBpro\devcon_x64.exe' install (Join-Path $folder 'MttVDD.inf') 'Root\MttVDD' 2>&1 | ForEach-Object { Log "  $_" }

for ($i = 0; $i -lt 30; $i++) {
    $dev = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
           Where-Object { $_.FriendlyName -like '*Virtual Display*' }
    if ($dev -and $dev.Status -eq 'OK') { Log "  the device is Started (after $($i + 1) s)"; break }
    Start-Sleep -Seconds 1
}
Start-Sleep -Seconds 4

Log ''
Log '=== the settings the driver now reads ==='
Get-Content $dst -ErrorAction SilentlyContinue | Select-Object -First 5 | ForEach-Object { Log "  $_" }

Log 'Done.'
