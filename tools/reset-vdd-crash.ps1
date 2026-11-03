# Resets the MttVDD device after a user-mode driver crash.
#
# WHY THIS IS NEEDED
# ------------------
# The event log is full of these:
#
#   Microsoft-Windows-DriverFrameworks-UserMode, Id 10111
#     The device Virtual Display Driver (location (unknown)) is offline
#     due to a user-mode device crash.  Windows will attempt to restart
#     the device N more times.
#
#   Microsoft-Windows-DriverFrameworks-UserMode, Id 10112
#     ... Windows will no longer attempt to restart this device because
#     the maximum restart limit has been reached.  Disconnecting the
#     device and reconnecting it, or disabling it and re-enabling it
#     from the device manager, will reset this limit.
#
# The symptoms on screen are misleading: the device manager says the
# driver is OK, the driver log says the monitor was created and its
# arrival was reported, and the monitor MTT1337 is enumerated - but the
# ADAPTER is not attached to the desktop (StateFlags = 0x0), it has no
# path in the display-configuration database, and the display can never
# be put on the desktop.
#
# The other symptom is a corrupted CCD database: GetDisplayConfig-
# BufferSizes(QDC_ALL_PATHS) reports ~57 paths for a machine with
# three displays, and QueryDisplayConfig(QDC_ALL_PATHS) answers 87
# (ERROR_INVALID_PARAMETER) for every capacity combination.  The stale
# paths are the crashed driver's leftovers.
#
# MUST BE RUN ELEVATED:
#   powershell -NoProfile -ExecutionPolicy Bypass -File reset-vdd-crash.ps1
$ErrorActionPreference = 'Continue'

$repo = Split-Path -Parent $PSScriptRoot
$logPath = Join-Path $repo 'artifacts\reset-vdd-crash.log'
New-Item -ItemType Directory -Path (Split-Path $logPath) -Force | Out-Null
function Log($message) {
    $line = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $message
    Write-Host $line
    Add-Content -Path $logPath -Value $line
}
Set-Content -Path $logPath -Value "--- reset-vdd-crash $(Get-Date) ---"

$devcon = 'D:\Programming\EBpro\devcon_x64.exe'
$store = 'C:\Windows\System32\DriverStore\FileRepository\mttvdd.inf_amd64_d9cd99eb029b9822'

function Get-VddDevice {
    Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -like '*Virtual Display*' }
}

# -------------------------------------------------------------------
Log '=== 1. before ==='
$dev = Get-VddDevice
if ($dev) {
    Log "  $($dev.InstanceId)  status=$($dev.Status)"
} else {
    Log '  the device is not there'
}

# -------------------------------------------------------------------
Log ''
Log '=== 2. the disable/enable cycle that resets the restart limit ==='
# The event log says so explicitly: "Disconnecting the device and
# reconnecting it, or disabling it and re-enabling it from the device
# manager, will reset this limit and allow the device to be accessed
# again."
if ($dev) {
    Log "  disabling $($dev.InstanceId)"
    & pnputil /disable-device $dev.InstanceId 2>&1 | ForEach-Object { Log "    $_" }
    Start-Sleep -Seconds 4

    Log "  enabling $($dev.InstanceId)"
    & pnputil /enable-device $dev.InstanceId 2>&1 | ForEach-Object { Log "    $_" }
}

# -------------------------------------------------------------------
Log ''
Log '=== 3. waiting for the device to start ==='
for ($i = 0; $i -lt 40; $i++) {
    $dev = Get-VddDevice
    if ($dev -and $dev.Status -eq 'OK') {
        Log "  the device is Started (after $($i + 1) s)"
        break
    }
    if ($i -eq 39) { Log "  the device did NOT start: $($dev.Status)" }
    Start-Sleep -Seconds 1
}
Start-Sleep -Seconds 5

# -------------------------------------------------------------------
Log ''
Log '=== 4. the display devices ==='
Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
    ForEach-Object { Log ("  {0,-30} {1,-12} {2}" -f $_.FriendlyName, $_.Status, $_.InstanceId) }

# -------------------------------------------------------------------
Log ''
Log '=== 5. the monitor ==='
Get-PnpDevice -Class Monitor -ErrorAction SilentlyContinue |
    ForEach-Object { Log ("  {0,-30} {1,-12} {2}" -f $_.FriendlyName, $_.Status, $_.InstanceId) }

# -------------------------------------------------------------------
Log ''
Log '=== 6. the driver log, from the start of this run ==='
$log = Get-ChildItem 'C:\VirtualDisplayDriver\Logs\*.txt' -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) {
    Log "  --- $($log.Name) ---"
    Get-Content $log.FullName -Tail 30 | ForEach-Object { Log "    $_" }
} else {
    Log '  there is no log'
}

# -------------------------------------------------------------------
Log ''
Log '=== 7. the crash count in the event log ==='
$crashes = Get-WinEvent -FilterHashtable @{
    LogName = 'System'
    ProviderName = 'Microsoft-Windows-DriverFrameworks-UserMode'
    StartTime = (Get-Date).Date
} -ErrorAction SilentlyContinue | Measure-Object
Log "  $($crashes.Count) event(s) today (before this run they were 68)"

Log ''
Log 'Done.'
