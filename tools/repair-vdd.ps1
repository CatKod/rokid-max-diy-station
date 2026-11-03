# Repairs the MttVDD (Virtual Display Driver) device.
#
# MUST BE RUN ELEVATED:
#   powershell -NoProfile -ExecutionPolicy Bypass -File repair-vdd.ps1
$ErrorActionPreference = 'Continue'

$repo = Split-Path -Parent $PSScriptRoot
$logPath = Join-Path $repo 'artifacts\repair-vdd.log'
New-Item -ItemType Directory -Path (Split-Path $logPath) -Force | Out-Null
function Log($message) {
    $line = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $message
    Write-Host $line
    Add-Content -Path $logPath -Value $line
}
Set-Content -Path $logPath -Value "--- repair-vdd $(Get-Date) ---"

$store = 'C:\Windows\System32\DriverStore\FileRepository\mttvdd.inf_amd64_d9cd99eb029b9822'

Log '=== 1. the settings: one virtual monitor ==='
$settings = 'C:\VirtualDisplayDriver\vdd_settings.xml'
if (Test-Path $settings) {
    $xml = Get-Content $settings -Raw
    if ($xml -notmatch '<count>\s*1\s*</count>') {
        $xml = $xml -replace '<count>\s*\d+\s*</count>', '<count>1</count>'
        Set-Content -Path $settings -Value $xml -Encoding UTF8
        Log '  the monitor count is now 1'
    } else {
        Log '  the monitor count is already 1'
    }
} else {
    Log "  $settings does not exist - creating it"
    New-Item -ItemType Directory -Path (Split-Path $settings) -Force | Out-Null
    @'
<?xml version="1.0" encoding="UTF-8"?>
<vdd_settings>
    <monitors><count>1</count></monitors>
    <gpu><friendlyname>default</friendlyname></gpu>
    <resolutions>
        <resolution><width>1920</width><height>1080</height><refresh_rate>60</refresh_rate></resolution>
    </resolutions>
    <options>
        <CustomEdid>false</CustomEdid>
        <PreventSpoof>false</PreventSpoof>
        <HardwareCursor>true</HardwareCursor>
        <SDR10bit>false</SDR10bit>
        <HDRPlus>false</HDRPlus>
        <logging>false</logging>
        <debuglogging>false</debuglogging>
    </options>
</vdd_settings>
'@ | Set-Content -Path $settings -Encoding UTF8
    Log '  created'
}

Log ''
Log '=== 2. re-installing the driver package ==='
pnputil /add-driver "$store\MttVDD.inf" /install 2>&1 |
    Where-Object { $_ -match 'Published|Added|error|Error|fail|Fail' } |
    ForEach-Object { Log "  $_" }

Log ''
Log '=== 3. scanning for the device ==='
pnputil /scan-devices 2>&1 | ForEach-Object { Log "  $_" }
Start-Sleep -Seconds 5

$dev = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
       Where-Object { $_.FriendlyName -like '*Virtual Display*' }
if ($dev) {
    Log "  found: $($dev.InstanceId) ($($dev.Status))"
} else {
    Log '  the device is not there - creating it through SetupAPI'
    $creator = Join-Path $repo 'tools\InstallVddDevice\bin\Release\net8.0-windows10.0.26100.0\InstallVddDevice.exe'
    if (Test-Path $creator) {
        & $creator 2>&1 | ForEach-Object { Log "  $_" }
    } else {
        Log "  $creator does not exist - build tools\InstallVddDevice first"
    }
}

Log ''
Log '=== 4. waiting for the device to start ==='
for ($i = 0; $i -lt 40; $i++) {
    $dev = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
           Where-Object { $_.FriendlyName -like '*Virtual Display*' }
    if ($dev -and $dev.Status -eq 'OK') {
        Log "  the device is Started (after $($i + 1) s)"
        break
    }
    Start-Sleep -Seconds 1
}

Log ''
Log '=== 5. the devices ==='
Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
    ForEach-Object { Log ("  {0,-30} {1,-12} {2}" -f $_.FriendlyName, $_.Status, $_.InstanceId) }
Get-PnpDevice -Class Monitor -ErrorAction SilentlyContinue |
    ForEach-Object { Log ("  monitor {0,-30} {1}" -f $_.FriendlyName, $_.Status) }

Log ''
Log '=== 6. the monitors on the desktop ==='
& (Join-Path $repo 'tools\ProbeCcd2\bin\Release\net8.0-windows\ProbeCcd2.exe') 2>&1 |
    ForEach-Object { Log "  $_" }

Log ''
Log 'Done.'
