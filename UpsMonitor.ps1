<#
.SYNOPSIS
    Monitors the UPS, raises desktop alerts, and hibernates the machine when the
    battery reserve runs out.

.DESCRIPTION
    Polls the UPS through UpsHid.psm1, classifies the situation into a severity level
    (Normal / Info / Warning / Critical), notifies on every transition, and — once a
    critical threshold has held for the configured grace period — hibernates.

    SAFETY: hibernation is SIMULATED unless the config sets "hibernateEnabled": true.
    In simulated mode every step runs for real (alerts, countdown, logging) except the
    final call to the power API, which is only logged. Run it that way until you have
    watched a full mains-failure cycle behave the way you want.

.PARAMETER ConfigPath
    Path to ups-config.json. Defaults to the file next to this script.

.PARAMETER Once
    Evaluate a single sample and exit. Useful for testing and for scheduled polling.

.PARAMETER SimulateOnBattery
    Pretend mains power is gone, optionally with forced charge/runtime values, so the
    whole alert-and-hibernate path can be exercised without unplugging anything.

.PARAMETER SimulateChargePercent
    Battery charge to report while -SimulateOnBattery is active.

.PARAMETER SimulateRuntimeSeconds
    Runtime-to-empty to report while -SimulateOnBattery is active.

.EXAMPLE
    .\UpsMonitor.ps1                     # run continuously, hibernation per config
    .\UpsMonitor.ps1 -Once               # single check
    .\UpsMonitor.ps1 -SimulateOnBattery -SimulateChargePercent 8 -SimulateRuntimeSeconds 120
#>
[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'ups-config.json'),
    [switch]$Once,
    [switch]$SimulateOnBattery,
    [int]$SimulateChargePercent = -1,
    [int]$SimulateRuntimeSeconds = -1
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'UpsHid.psm1') -Force

# --- config -----------------------------------------------------------------
$defaultConfig = [ordered]@{
    pollSeconds            = 10
    warnChargePercent      = 50
    warnRuntimeSeconds     = 900
    criticalChargePercent  = 20
    criticalRuntimeSeconds = 300
    overloadPercent        = 90
    criticalGraceSeconds   = 30
    hibernateEnabled       = $false
    hibernateForce         = $true
    notifyOnMainsLost      = $true
    notifyOnMainsBack      = $true
    reNotifySeconds        = 300
    logPath                = (Join-Path $PSScriptRoot 'ups-monitor.log')
    stateLogPath           = (Join-Path $PSScriptRoot 'ups-history.csv')
}

if (Test-Path $ConfigPath) {
    $userConfig = Get-Content $ConfigPath -Raw | ConvertFrom-Json
    $cfg = [ordered]@{}
    foreach ($k in $defaultConfig.Keys) { $cfg[$k] = $defaultConfig[$k] }
    foreach ($p in $userConfig.PSObject.Properties) { $cfg[$p.Name] = $p.Value }
} else {
    $cfg = $defaultConfig
    $defaultConfig | ConvertTo-Json | Set-Content -Path $ConfigPath -Encoding UTF8
    Write-Host "Config created: $ConfigPath" -ForegroundColor Yellow
}

# --- logging ----------------------------------------------------------------
function Write-Log {
    param([string]$Message, [ValidateSet('INFO','WARN','CRIT','ACTION')][string]$Level = 'INFO')
    $line = '{0} [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    $color = switch ($Level) { 'WARN' {'Yellow'} 'CRIT' {'Red'} 'ACTION' {'Magenta'} default {'Gray'} }
    Write-Host $line -ForegroundColor $color
    try { Add-Content -Path $cfg.logPath -Value $line -Encoding UTF8 } catch { }
}

function Write-History {
    param($Status, [string]$Severity)
    if (-not $cfg.stateLogPath) { return }
    try {
        if (-not (Test-Path $cfg.stateLogPath)) {
            'timestamp,onBattery,severity,chargePct,runtimeSec,loadPct,watts,inputV,outputV,batteryV' |
                Set-Content -Path $cfg.stateLogPath -Encoding UTF8
        }
        '{0},{1},{2},{3},{4},{5},{6},{7},{8},{9}' -f (Get-Date -Format 's'), $Status.OnBattery, $Severity,
            $Status.ChargePercent, $Status.RuntimeSeconds, $Status.LoadPercent, $Status.OutputActivePowerW,
            $Status.InputVoltage, $Status.OutputVoltage, $Status.BatteryVoltage |
            Add-Content -Path $cfg.stateLogPath -Encoding UTF8
    } catch { }
}

# --- notifications ----------------------------------------------------------
# Toast via the Windows Runtime API when available; balloon tip as the fallback,
# because a toast needs a registered AppUserModelID and a real user session.
$script:notifyIcon = $null
function Show-Alert {
    param(
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet('Info','Warning','Error')][string]$Kind = 'Info'
    )
    Write-Log "$Title - $Message" -Level $(switch ($Kind) { 'Error' {'CRIT'} 'Warning' {'WARN'} default {'INFO'} })

    $toastOk = $false
    try {
        [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent(
            [Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $texts = $template.GetElementsByTagName('text')
        $texts.Item(0).AppendChild($template.CreateTextNode($Title))  | Out-Null
        $texts.Item(1).AppendChild($template.CreateTextNode($Message)) | Out-Null
        $toast = [Windows.UI.Notifications.ToastNotification]::new($template)
        # PowerShell's own AUMID: present on every Windows 10/11 install.
        $appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
        $toastOk = $true
    } catch { }

    if (-not $toastOk) {
        try {
            Add-Type -AssemblyName System.Windows.Forms
            if (-not $script:notifyIcon) {
                $script:notifyIcon = New-Object System.Windows.Forms.NotifyIcon
                $script:notifyIcon.Icon = [System.Drawing.SystemIcons]::Information
                $script:notifyIcon.Visible = $true
            }
            $script:notifyIcon.BalloonTipTitle = $Title
            $script:notifyIcon.BalloonTipText  = $Message
            $script:notifyIcon.BalloonTipIcon  = [System.Windows.Forms.ToolTipIcon]::$Kind
            $script:notifyIcon.ShowBalloonTip(10000)
        } catch { Write-Log "Notification failed: $($_.Exception.Message)" -Level WARN }
    }
}

# --- hibernation ------------------------------------------------------------
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PowerNative
{
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetSuspendState(bool Hibernate, bool ForceCritical, bool DisableWakeEvent);
}
'@ -ErrorAction SilentlyContinue

function Test-HibernationAvailable {
    $out = & powercfg /a 2>&1 | Out-String
    # Localised output: match both the English and Italian spellings.
    return ($out -match '(?m)^\s*(Hibernate|Ibernazione)\s*$')
}

function Invoke-Hibernate {
    param([switch]$Simulate)
    if ($Simulate) {
        Write-Log 'SIMULATION: hibernation would be triggered now (SetSuspendState). No action taken.' -Level ACTION
        Show-Alert -Title 'UPS - ibernazione SIMULATA' `
                   -Message 'In modalita reale il PC sarebbe stato ibernato ora.' -Kind Warning
        return $true
    }
    if (-not (Test-HibernationAvailable)) {
        Write-Log 'Hibernation is not available on this system (powercfg /a). Aborting.' -Level CRIT
        return $false
    }
    Write-Log 'Hibernating now.' -Level ACTION
    $ok = [PowerNative]::SetSuspendState($true, [bool]$cfg.hibernateForce, $false)
    if (-not $ok) {
        Write-Log 'SetSuspendState failed; falling back to shutdown.exe /h' -Level WARN
        & shutdown.exe /h
    }
    return $true
}

# --- severity ---------------------------------------------------------------
function Get-Severity {
    param($Status)
    $reasons = New-Object System.Collections.Generic.List[string]
    $level = 'Normal'

    if ($Status.OnBattery) {
        $level = 'Info'
        $reasons.Add('alimentazione di rete assente')

        $charge  = $Status.ChargePercent
        $runtime = $Status.RuntimeSeconds

        if ($null -ne $charge -and $charge -le $cfg.warnChargePercent) {
            $level = 'Warning'; $reasons.Add("carica $charge% <= $($cfg.warnChargePercent)%")
        }
        if ($null -ne $runtime -and $runtime -le $cfg.warnRuntimeSeconds) {
            $level = 'Warning'; $reasons.Add("autonomia $([math]::Round($runtime/60,1)) min <= $([math]::Round($cfg.warnRuntimeSeconds/60,1)) min")
        }
        if ($null -ne $charge -and $charge -le $cfg.criticalChargePercent) {
            $level = 'Critical'; $reasons.Add("carica CRITICA $charge% <= $($cfg.criticalChargePercent)%")
        }
        if ($null -ne $runtime -and $runtime -le $cfg.criticalRuntimeSeconds) {
            $level = 'Critical'; $reasons.Add("autonomia CRITICA $([math]::Round($runtime/60,1)) min <= $([math]::Round($cfg.criticalRuntimeSeconds/60,1)) min")
        }
    }

    if ($null -ne $Status.LoadPercent -and $Status.LoadPercent -ge $cfg.overloadPercent) {
        if ($level -eq 'Normal') { $level = 'Warning' }
        $reasons.Add("sovraccarico $($Status.LoadPercent)% >= $($cfg.overloadPercent)%")
    }

    [pscustomobject]@{ Level = $level; Reasons = $reasons }
}

function Format-StatusLine {
    param($Status)
    '{0} | carica {1}% | autonomia {2} min | carico {3}% ({4} W) | in {5} V | out {6} V | batt {7} V' -f
        $(if ($Status.OnBattery) { 'BATTERIA' } else { 'RETE' }),
        $Status.ChargePercent, $Status.RuntimeMinutes, $Status.LoadPercent,
        $Status.OutputActivePowerW, $Status.InputVoltage, $Status.OutputVoltage, $Status.BatteryVoltage
}

# --- main loop --------------------------------------------------------------
$simulating = $SimulateOnBattery -or -not $cfg.hibernateEnabled
$mode = if ($cfg.hibernateEnabled) { 'REALE (il PC verra ibernato)' } else { 'SIMULATA (nessuna ibernazione)' }
Write-Log "UPS monitor avviato. Ibernazione: $mode. Poll ogni $($cfg.pollSeconds)s." -Level INFO
if ($SimulateOnBattery) { Write-Log 'Modalita test: stato "su batteria" simulato.' -Level WARN }

$prevLevel      = 'Normal'
$prevOnBattery  = $false
$criticalSince  = $null
$lastNotify     = @{}
$hibernateDone  = $false

do {
    try {
        $status = Get-UpsStatus

        if ($SimulateOnBattery) {
            $status.OnBattery = $true
            if ($SimulateChargePercent  -ge 0) { $status.ChargePercent  = $SimulateChargePercent }
            if ($SimulateRuntimeSeconds -ge 0) {
                $status.RuntimeSeconds  = $SimulateRuntimeSeconds
                $status.RuntimeMinutes  = [math]::Round($SimulateRuntimeSeconds / 60, 1)
            }
        }

        $sev = Get-Severity -Status $status
        Write-History -Status $status -Severity $sev.Level

        # Mains transitions.
        if ($status.OnBattery -and -not $prevOnBattery -and $cfg.notifyOnMainsLost) {
            Show-Alert -Title 'UPS: rete elettrica assente' `
                       -Message "Sistema su batteria. $(Format-StatusLine $status)" -Kind Warning
        }
        if (-not $status.OnBattery -and $prevOnBattery -and $cfg.notifyOnMainsBack) {
            Show-Alert -Title 'UPS: rete elettrica ripristinata' `
                       -Message (Format-StatusLine $status) -Kind Info
            $criticalSince = $null
            $hibernateDone = $false
        }

        # Severity transitions, plus periodic re-notification while it persists.
        if ($sev.Level -ne 'Normal') {
            $key = $sev.Level
            $due = (-not $lastNotify.ContainsKey($key)) -or
                   ((Get-Date) - $lastNotify[$key]).TotalSeconds -ge $cfg.reNotifySeconds
            if ($sev.Level -ne $prevLevel -or $due) {
                $kind = switch ($sev.Level) { 'Critical' {'Error'} 'Warning' {'Warning'} default {'Info'} }
                Show-Alert -Title "UPS: $($sev.Level)" `
                           -Message "$($sev.Reasons -join '; '). $(Format-StatusLine $status)" -Kind $kind
                $lastNotify[$key] = Get-Date
            }
        } else {
            Write-Log (Format-StatusLine $status) -Level INFO
            $lastNotify.Clear()
        }

        # Critical grace period, then hibernate.
        if ($sev.Level -eq 'Critical' -and -not $hibernateDone) {
            if ($null -eq $criticalSince) {
                $criticalSince = Get-Date
                Write-Log "Stato critico. Ibernazione tra $($cfg.criticalGraceSeconds)s se persiste." -Level CRIT
            }
            $elapsed = ((Get-Date) - $criticalSince).TotalSeconds
            if ($elapsed -ge $cfg.criticalGraceSeconds) {
                Show-Alert -Title 'UPS: ibernazione in corso' `
                           -Message "Batteria esaurita. $($sev.Reasons -join '; ')" -Kind Error
                [void](Invoke-Hibernate -Simulate:(-not $cfg.hibernateEnabled))
                $hibernateDone = $true
            } else {
                Write-Log ("Critico da {0:N0}s / {1}s." -f $elapsed, $cfg.criticalGraceSeconds) -Level CRIT
            }
        } elseif ($sev.Level -ne 'Critical') {
            $criticalSince = $null
        }

        $prevLevel     = $sev.Level
        $prevOnBattery = $status.OnBattery
    }
    catch {
        Write-Log "Errore lettura UPS: $($_.Exception.Message)" -Level WARN
    }

    if (-not $Once) { Start-Sleep -Seconds $cfg.pollSeconds }
} while (-not $Once)
