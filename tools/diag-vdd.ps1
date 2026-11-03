$repo = 'D:\GitHub\rokid-max-diy-station'
$out = Join-Path $repo 'artifacts\vdd-diag.log'
New-Item -ItemType Directory -Path (Split-Path $out) -Force | Out-Null
Set-Content -Path $out -Value "--- vdd diag $(Get-Date) ---"

function Log($m) {
    $line = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $m
    Write-Host $line
    Add-Content -Path $out -Value $line
}

# -------------------------------------------------------------------
Log '=== 1. turning the driver logging on ==='
$settings = 'C:\VirtualDisplayDriver\vdd_settings.xml'
if (Test-Path $settings) {
    $xml = Get-Content $settings -Raw
    $xml = $xml -replace '<logging>\w+</logging>', '<logging>true</logging>'
    $xml = $xml -replace '<debuglogging>\w+</debuglogging>', '<debuglogging>true</debuglogging>'
    Set-Content -Path $settings -Value $xml -Encoding UTF8
    Log '  the settings file now has logging on'
}

# -------------------------------------------------------------------
Log ''
Log '=== 2. restarting the device ==='
$dev = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
       Where-Object { $_.FriendlyName -like '*Virtual Display*' }
if ($dev) {
    Log "  restarting $($dev.InstanceId)"
    pnputil /restart-device $dev.InstanceId 2>&1 | ForEach-Object { Log "    $_" }
}
Start-Sleep -Seconds 6

# -------------------------------------------------------------------
Log ''
Log '=== 3. the devices ==='
Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
    ForEach-Object { Log ("  {0,-30} {1,-12} {2}" -f $_.FriendlyName, $_.Status, $_.InstanceId) }

# -------------------------------------------------------------------
Log ''
Log '=== 4. the driver log ==='
$logDir = 'C:\VirtualDisplayDriver\Logs'
if (Test-Path $logDir) {
    Get-ChildItem $logDir -File | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object {
        Log "  --- $($_.Name) ($($_.Length) bytes) ---"
        Get-Content $_.FullName -Tail 120 | ForEach-Object { Log "    $_" }
    }
} else {
    Log "  $logDir does not exist"
    Log '  the files in C:\VirtualDisplayDriver:'
    Get-ChildItem 'C:\VirtualDisplayDriver' -Recurse -File -ErrorAction SilentlyContinue |
        ForEach-Object { Log "    $($_.FullName) ($($_.Length) bytes)" }
}

Log 'Done.'
