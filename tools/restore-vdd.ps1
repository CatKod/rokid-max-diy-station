$repo = 'D:\GitHub\rokid-max-diy-station'
$out = Join-Path $repo 'artifacts\restore-vdd.log'
New-Item -ItemType Directory -Path (Split-Path $out) -Force | Out-Null
Set-Content -Path $out -Value "--- restore vdd $(Get-Date) ---"

function Log($m) {
    $line = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $m
    Write-Host $line
    Add-Content -Path $out -Value $line
}

$folder = 'C:\Windows\System32\DriverStore\FileRepository\mttvdd.inf_amd64_d9cd99eb029b9822'
$inf = Join-Path $folder 'MttVDD.inf'
$devcon = 'D:\Programming\EBpro\devcon_x64.exe'

Log '=== 1. the DriverStore settings ==='
# The driver reads the settings from the DriverStore folder next to
# MttVDD.dll.  The folder is TrustedInstaller-owned, so takeown first.
& takeown /F $folder /R /D Y 2>&1 | ForEach-Object { Log "  $_" }
& icacls $folder /grant '*S-1-5-32-544:(F)' /T /C 2>&1 | ForEach-Object { Log "  $_" }
Copy-Item (Join-Path $repo 'tools\vdd_settings.xml') (Join-Path $folder 'vdd_settings.xml') -Force
if (Test-Path (Join-Path $folder 'vdd_settings.xml')) { Log '  the settings are in place' }
else { Log '  THE SETTINGS COULD NOT BE WRITTEN' }

Log ''
Log '=== 2. the user-level settings too ==='
Copy-Item (Join-Path $repo 'tools\vdd_settings.xml') 'C:\VirtualDisplayDriver\vdd_settings.xml' -Force
Remove-Item 'C:\VirtualDisplayDriver\vdd_settings.xml.temp' -Force -ErrorAction SilentlyContinue
Log '  done'

Log ''
Log '=== 3. re-installing the device ==='
& $devcon remove 'Root\MttVDD' 2>&1 | ForEach-Object { Log "  $_" }
Start-Sleep -Seconds 5
& $devcon install $inf 'Root\MttVDD' 2>&1 | ForEach-Object { Log "  $_" }

for ($i = 0; $i -lt 40; $i++) {
    $dev = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
           Where-Object { $_.FriendlyName -like '*Virtual Display*' }
    if ($dev -and $dev.Status -eq 'OK') { Log "  the device is Started (after $($i + 1) s)"; break }
    if ($i -eq 39) { Log "  the device did NOT start: $($dev.Status)" }
    Start-Sleep -Seconds 1
}
Start-Sleep -Seconds 5

Log ''
Log '=== 4. the driver log ==='
$log = Get-ChildItem 'C:\VirtualDisplayDriver\Logs\*.txt' -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) {
    Get-Content $log.FullName -Tail 25 | ForEach-Object { Log "  $_" }
} else { Log '  there is no log' }

Log 'Done.'
