# Removes Lid Awake from this PC. Double-click "Uninstall Lid Awake (Windows).cmd" in the folder above.
# It stops the app, closes the phone page on Tailscale, puts your lid setting back, and asks
# before deleting your settings and phone password.
#
# The switches below are FOR AUTOMATED TESTS ONLY. A person never needs them; without them the
# uninstaller asks the question and waits for Enter at the end.
#   -KeepSettings    keep the settings and phone password, without asking
#   -DeleteSettings  delete the settings and phone password, without asking
#   -NoPause         do not wait for Enter at the end (if neither of the two switches above
#                    is given, settings are kept, never deleted, since nobody can answer)
#
# This file is plain ASCII on purpose: Windows PowerShell 5.1 misreads UTF-8 without a BOM.
param(
    [switch]$KeepSettings,
    [switch]$DeleteSettings,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'

$dest = Join-Path $env:LOCALAPPDATA 'LidAwake'
$settingsDir = Join-Path $env:APPDATA 'LidAwake'
$settingsFile = Join-Path $settingsDir 'settings.json'
$tsExe = 'C:\Program Files\Tailscale\tailscale.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
# The same power setting KeepAwake.cs changes: SUB_BUTTONS and LIDACTION.
$subButtons = '4f971e89-eebd-4455-a8de-9e59040e7347'
$lidAction = '5ca83367-6e45-459f-a27b-476b1d01c936'

$problems = 0

function Show-Step([string]$text) { Write-Host ''; Write-Host $text }
function Show-Ok([string]$text) { Write-Host "  [OK] $text" -ForegroundColor Green }
function Show-Note([string]$text) { Write-Host "       $text" }
function Show-Problem([string]$text) {
    $script:problems++
    Write-Host "  [X] $text" -ForegroundColor Red
}
function Wait-ForEnter {
    if (-not $NoPause) { Write-Host ''; [void](Read-Host 'Press Enter to close') }
}

# Native programs write to stderr in normal use; Stop would turn that into an error.
function Invoke-Quietly([scriptblock]$command) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $command } finally { $ErrorActionPreference = $saved }
}

Write-Host ''
Write-Host 'Removing Lid Awake'

# ---- 1/5 Stop ---------------------------------------------------------------------------
Show-Step '1/5  Stopping the app'
try {
    $running = @(Get-Process -Name 'LidAwake' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    }
    if (Get-Process -Name 'LidAwake' -ErrorAction SilentlyContinue) { throw 'it is still running.' }
    Show-Ok 'Stopped'
} catch {
    Show-Problem ('Could not stop Lid Awake: ' + $_.Exception.Message + ' Right-click its icon by the clock and choose Quit, then run this again.')
}

# ---- 2/5 Tailscale and start at sign-in -------------------------------------------------
Show-Step '2/5  Closing the phone page and the start at sign-in'
try {
    if (Test-Path -LiteralPath $tsExe) {
        [void](Invoke-Quietly { (& $tsExe serve --https=443 off 2>&1) | Out-String })
    }
    Remove-ItemProperty -Path $runKey -Name 'LidAwake' -ErrorAction SilentlyContinue
    if (Get-ItemProperty -Path $runKey -Name 'LidAwake' -ErrorAction SilentlyContinue) { throw 'the start-at-sign-in entry is still there.' }
    Show-Ok 'Phone page closed, and it will not start when you sign in'
} catch {
    Show-Problem ('Could not finish this step: ' + $_.Exception.Message + ' Run this again.')
}

# ---- 3/5 Lid action ---------------------------------------------------------------------
Show-Step '3/5  Putting your lid setting back'
try {
    $restored = $false
    $skipped = $false
    if (Test-Path -LiteralPath $settingsFile) {
        $raw = [IO.File]::ReadAllText($settingsFile)
        $s = $raw | ConvertFrom-Json
        if ($null -ne $s -and $null -ne $s.savedLidAc -and $null -ne $s.savedLidDc) {
            $savedAc = $s.savedLidAc
            $savedDc = $s.savedLidDc
            # The lid action is 0 (nothing), 1 (sleep), 2 (hibernate) or 3 (shut down). Anything
            # else was not written by Lid Awake: leave it alone.
            $valid = $true
            foreach ($v in @($savedAc, $savedDc)) {
                if (-not (($v -is [int] -or $v -is [long]) -and $v -ge 0 -and $v -le 3)) { $valid = $false }
            }
            if (-not $valid) {
                $skipped = $true
                Show-Problem 'The saved lid setting looks wrong; set the lid action by hand in Control Panel -> Power Options -> Choose what closing the lid does.'
            } else {
                $ac = [int]$savedAc
                $dc = [int]$savedDc
                [void](Invoke-Quietly { (& powercfg /setacvalueindex SCHEME_CURRENT $subButtons $lidAction $ac 2>&1) | Out-String })
                if ($LASTEXITCODE -ne 0) { throw 'powercfg could not set the plugged-in lid action.' }
                [void](Invoke-Quietly { (& powercfg /setdcvalueindex SCHEME_CURRENT $subButtons $lidAction $dc 2>&1) | Out-String })
                if ($LASTEXITCODE -ne 0) { throw 'powercfg could not set the on-battery lid action.' }
                [void](Invoke-Quietly { (& powercfg /setactive SCHEME_CURRENT 2>&1) | Out-String })
                if ($LASTEXITCODE -ne 0) { throw 'powercfg could not apply the power plan.' }
                # Forget the saved values, so installing again later never "restores" an old one.
                $cleared = [regex]::Replace($raw, '("savedLid(?:Ac|Dc)"\s*:\s*)-?\d+', '${1}null')
                $tmp = $settingsFile + '.tmp'
                [IO.File]::WriteAllText($tmp, $cleared)
                Move-Item -LiteralPath $tmp -Destination $settingsFile -Force
                $restored = $true
            }
        }
    }
    if ($restored) { Show-Ok 'Lid setting back to what it was before Lid Awake' }
    elseif (-not $skipped) { Show-Ok 'Nothing to put back (Lid Awake was off)' }
} catch {
    Show-Problem ('Could not put the lid setting back: ' + $_.Exception.Message + ' Open Control Panel, Power Options, "Choose what closing the lid does" and check it.')
}

# ---- 4/5 Remove the app -----------------------------------------------------------------
Show-Step '4/5  Removing the app'
try {
    if (Test-Path -LiteralPath $dest) {
        $gone = $false
        for ($try = 1; $try -le 5 -and -not $gone; $try++) {
            try {
                Remove-Item -LiteralPath $dest -Recurse -Force
                $gone = $true
            } catch {
                if ($try -eq 5) { throw }
                Start-Sleep -Seconds 1
            }
        }
    }
    if (Test-Path -LiteralPath $dest) { throw 'the folder is still there.' }
    Show-Ok 'App removed'
} catch {
    Show-Problem ('Could not remove ' + $dest + ': ' + $_.Exception.Message + ' Close any window showing it, then run this again.')
}

# ---- 5/5 Settings -----------------------------------------------------------------------
Show-Step '5/5  Your settings'
try {
    if (-not (Test-Path -LiteralPath $settingsDir)) {
        Show-Ok 'No settings to delete'
    } else {
        $delete = $false
        if ($KeepSettings) {
            $delete = $false
        } elseif ($DeleteSettings) {
            $delete = $true
        } elseif (-not $NoPause) {
            Write-Host '       Settings are your phone password, phone sign-ins and alerts.'
            $answer = Read-Host '       Also delete your settings and phone password? (y/N)'
            $delete = ($answer.Trim() -match '^(y|yes)$')
        }
        if ($delete) {
            Remove-Item -LiteralPath $settingsDir -Recurse -Force
            Show-Ok 'Settings and phone password deleted'
        } else {
            Show-Ok 'Settings kept (installing again picks up where you left off)'
        }
    }
} catch {
    Show-Problem ('Could not delete the settings: ' + $_.Exception.Message + ' Delete the folder ' + $settingsDir + ' yourself if you want them gone.')
}

Write-Host ''
if ($problems -gt 0) {
    Write-Host 'Lid Awake was not removed completely. Read the [X] lines above, then run this again.' -ForegroundColor Red
    Wait-ForEnter
    exit 1
}
Write-Host 'Lid Awake removed.'
Wait-ForEnter
exit 0
