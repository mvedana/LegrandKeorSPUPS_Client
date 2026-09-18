<#
.SYNOPSIS
    Installs (or removes) KeorMon: Windows service + tray app at logon.

.DESCRIPTION
    - Windows service "KeorMon" (LocalSystem, start automatic): monitors the UPS,
      writes history to C:\ProgramData\KeorMon\keormon.db and hibernates the machine
      on critical battery — with or without a logged-on user.
    - Scheduled task "KeorMon Tray": tray icon + dashboard at every logon of the
      current user. When the service is running the tray only displays and notifies.

    Requires elevation (service registration). Re-run safe: reinstalls both.

.PARAMETER Uninstall
    Stops and removes the service and the tray task.
#>
[CmdletBinding()]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$serviceName = 'KeorMon'
$trayTask = 'KeorMon Tray'
$exe = Join-Path $PSScriptRoot 'bin\KeorMon.exe'
$dataDir = Join-Path $env:ProgramData 'KeorMon'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Servono privilegi amministrativi. Aprire un terminale come amministratore ed eseguire di nuovo questo script."
}

function Remove-IfPresent {
    if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
        if ((Get-Service $serviceName).Status -ne 'Stopped') { Stop-Service $serviceName -Force }
        & sc.exe delete $serviceName | Out-Null
        Write-Host "Servizio '$serviceName' rimosso."
    }
    foreach ($t in @($trayTask, 'KeorMon Worker')) {
        if (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $t -Confirm:$false
            Write-Host "Task '$t' rimosso."
        }
    }
}

if ($Uninstall) { Remove-IfPresent; return }

if (-not (Test-Path $exe)) {
    throw "Eseguibile non trovato: $exe. Eseguire prima: dotnet publish -c Release -o bin (in src\KeorMon)."
}

# Shared data dir: service (SYSTEM) writes, users read and save settings.
New-Item -ItemType Directory -Force $dataDir | Out-Null
& icacls $dataDir /grant 'BUILTIN\Users:(OI)(CI)M' | Out-Null

Remove-IfPresent

# --- Windows service ---------------------------------------------------------
& sc.exe create $serviceName binPath= "`"$exe`" --service" start= auto DisplayName= "KeorMon UPS Monitor" | Out-Null
& sc.exe description $serviceName "Monitora l'UPS Legrand Keor SP: storico su SQLite, avvisi e ibernazione su batteria critica (attivo anche senza utente collegato)." | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Start-Service $serviceName
Write-Host "Servizio '$serviceName' installato e avviato ($((Get-Service $serviceName).Status))."

# --- tray app at logon ---------------------------------------------------------
$action  = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -DontStopOnIdleEnd -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $trayTask -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
Write-Host "Task '$trayTask' registrato: icona tray a ogni accesso di $env:USERNAME."

Write-Host "Avvio tray..."
Start-ScheduledTask -TaskName $trayTask
