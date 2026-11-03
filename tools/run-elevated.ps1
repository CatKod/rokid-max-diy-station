$repo = 'D:\GitHub\rokid-max-diy-station'
$script = Join-Path $repo 'tools\restore-vdd-settings.ps1'
$taskName = 'RokidRestoreVddSettings'

# A scheduled task that runs as SYSTEM is the reliable way to get
# elevation without an interactive UAC prompt - the UAC prompt has
# been declined or timed out several times in this session, and the
# DriverStore folder needs administrator rights to write.
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`""

$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5)

Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal `
    -Settings $settings -Force | Out-Null

Write-Host "the task is registered"
Start-ScheduledTask -TaskName $taskName

$deadline = (Get-Date).AddMinutes(3)
while ((Get-Date) -lt $deadline) {
    $state = (Get-ScheduledTask -TaskName $taskName).State
    if ($state -eq 'Ready') { break }
    Start-Sleep -Seconds 3
}

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
Write-Host 'the task has finished and been removed'
