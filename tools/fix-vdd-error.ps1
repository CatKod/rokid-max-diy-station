# Repairs the MttVDD (Virtual Display Driver) device after it has been
# left in error code 43.
#
# WHY IT GOES INTO ERROR 43
# --------------------------
# The driver publishes a "monitor count" in its settings file
# (C:\VirtualDisplayDriver\vdd_settings.xml).  When that count is 0
# the driver tears the adapter down and re-creates it, and it comes
# back in error code 43 - the device is started but not working.  The
# control pipe then stops answering and the monitor cannot be brought
# back at all.
#
# The count is written by the driver's own SETDISPLAYCOUNT command, so
# it survives a reboot.  Putting the count back to 1 and restarting the
# device is the whole repair.
#
# MUST BE RUN ELEVATED:
#   powershell -NoProfile -ExecutionPolicy Bypass -File fix-vdd-error.ps1
$ErrorActionPreference = 'Continue'

$repo = Split-Path -Parent $PSScriptRoot
$logPath = Join-Path $repo 'artifacts\fix-vdd-error.log'
New-Item -ItemType Directory -Path (Split-Path $logPath) -Force | Out-Null

function Log($message) {
    $line = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $message
    Write-Host $line
    Add-Content -Path $logPath -Value $line
}

Set-Content -Path $logPath -Value "--- fix-vdd-error $(Get-Date) ---"

$devcon = 'D:\Programming\EBpro\devcon_x64.exe'
$inf = 'C:\Windows\System32\DriverStore\FileRepository\mttvdd.inf_amd64_d9cd99eb029b9822\MttVDD.inf'
$settings = 'C:\VirtualDisplayDriver\vdd_settings.xml'

Log '=== 1. the state before ==='
Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
    ForEach-Object { Log ("  {0,-30} {1,-12} {2}" -f $_.FriendlyName, $_.Status, $_.InstanceId) }

Log ''
Log '=== 2. the settings: one virtual monitor ==='
if (Test-Path $settings) {
    $xml = Get-Content $settings -Raw
    if ($xml -notmatch '<count>\s*1\s*</count>') {
        $xml = $xml -replace '<count>\s*\d+\s*</count>', '<count>1</count>'
        Set-Content -Path $settings -Value $xml -Encoding UTF8
        Log '  the monitor count is now 1'
    } else {
        Log '  the monitor count is already 1'
    }
    # The driver writes a .temp file next to the settings and renames
    # it into place; a stale one would be picked up instead.
    Remove-Item "$settings.temp" -Force -ErrorAction SilentlyContinue
} else {
    Log "  $settings does not exist - writing it"
    New-Item -ItemType Directory -Path (Split-Path $settings) -Force | Out-Null
    @'
<?xml version="1.0" encoding="UTF-8"?>
<vdd_settings>
    <monitors><count>1</count></monitors>
    <gpu><friendlyname>default</friendlyname></gpu>
    <global><g_refresh_rate>60</g_refresh_rate></global>
    <resolutions>
        <resolution><width>1920</width><height>1080</height><refresh_rate>60</refresh_rate></resolution>
    </resolutions>
    <options>
        <CustomEdid>false</CustomEdid>
        <PreventSpoof>false</PreventSpoof>
        <HardwareCursor>true</HardwareCursor>
        <SDR10bit>false</SDR10bit>
        <HDRPlus>false</HDRPlus>
        <logging>true</logging>
        <debuglogging>true</debuglogging>
    </options>
</vdd_settings>
'@ | Set-Content -Path $settings -Encoding UTF8
    Log '  written'
}

Log ''
Log '=== 3. restarting the device ==='
$dev = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
       Where-Object { $_.FriendlyName -like '*Virtual Display*' }
if ($dev) {
    Log "  restarting $($dev.InstanceId)"
    pnputil /restart-device $dev.InstanceId 2>&1 |
        ForEach-Object { $_.ToString().Trim() } |
        Where-Object { $_ -ne '' } |
        ForEach-Object { Log "    $_" }
} else {
    Log '  the device is not there - creating it'
    if (Test-Path $devcon) {
        & $devcon install $inf 'Root\MttVDD' 2>&1 | ForEach-Object { Log "    $_" }
    } else {
        pnputil /add-driver $inf /install 2>&1 | ForEach-Object { Log "    $_" }
    }
}

Log ''
Log '=== 4. waiting for the device to start ==='
for ($i = 0; $i -lt 45; $i++) {
    $dev = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
           Where-Object { $_.FriendlyName -like '*Virtual Display*' }
    if ($dev -and $dev.Status -eq 'OK') {
        Log "  the device is Started (after $($i + 1) s)"
        break
    }
    if ($i -eq 44) { Log "  the device did NOT start: $($dev.Status)" }
    Start-Sleep -Seconds 1
}

Log ''
Log '=== 5. the state after ==='
Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
    ForEach-Object { Log ("  {0,-30} {1,-12} {2}" -f $_.FriendlyName, $_.Status, $_.InstanceId) }

Log ''
Log '=== 6. the driver log ==='
$log = Get-ChildItem 'C:\VirtualDisplayDriver\Logs\*.txt' -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) {
    Get-Content $log.FullName -Tail 20 | ForEach-Object { Log "  $_" }
} else { Log '  there is no log' }

Log 'Done.'
