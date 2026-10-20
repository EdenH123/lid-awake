# Removes Lid Awake from this PC. Double-click "Uninstall Lid Awake (Windows).cmd" in the folder above.
# It stops the app, closes the phone page on Tailscale (if Lid Awake opened it), removes the
# Start menu entry, puts your lid setting back, and asks before deleting your settings and
# phone password.
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
# Where Lid Awake keeps what is only true of this PC (the saved lid values and their power plan);
# older versions kept the values in settings.json instead.
$machineFile = Join-Path $dest 'machine.json'
$startMenuLink = Join-Path (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs') 'Lid Awake.lnk'
# Lid Awake's phone page port (WebServer.DefaultPort in the app); exact, so 8787 never matches 87870.
$mapsOurPort = '(?<![\d.])127\.0\.0\.1:8787(?!\d)'
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

# ---- 2/5 Tailscale, Start menu and start at sign-in ------------------------------------
Show-Step '2/5  Closing the phone page, the Start menu entry and the start at sign-in'
try {
    # Only what Lid Awake added (servedByLidAwake in its settings): another Serve setup stays.
    $servedByUs = $false
    if (Test-Path -LiteralPath $settingsFile) {
        try { $servedByUs = (([IO.File]::ReadAllText($settingsFile) | ConvertFrom-Json).servedByLidAwake -eq $true) } catch { $servedByUs = $false }
    }
    if ($servedByUs -and (Test-Path -LiteralPath $tsExe)) {
        # Forget it only when the page is really gone: the command worked, or (when it failed)
        # the status no longer maps Lid Awake's port. Otherwise the flag stays, so running this
        # (or Lid Awake) again tries once more.
        $gone = $false
        [void](Invoke-Quietly { (& $tsExe serve --https=443 off 2>&1) | Out-String })
        if ($LASTEXITCODE -eq 0) {
            $gone = $true
        } else {
            $status = Invoke-Quietly { (& $tsExe serve status --json 2>$null) | Out-String }
            if ($LASTEXITCODE -eq 0 -and $status -notmatch $mapsOurPort) { $gone = $true }
        }
        if ($gone) {
            $raw = [IO.File]::ReadAllText($settingsFile)
            $cleared = [regex]::Replace($raw, '("servedByLidAwake"\s*:\s*)true', '${1}false')
            $tmp = $settingsFile + '.tmp'
            [IO.File]::WriteAllText($tmp, $cleared)
            Move-Item -LiteralPath $tmp -Destination $settingsFile -Force
            Show-Ok 'Phone page closed on Tailscale'
        } else {
            Show-Problem 'Could not close the phone page on Tailscale. Open Tailscale and turn off Serve, or run: tailscale serve --https=443 off'
        }
    }
    if (Test-Path -LiteralPath $startMenuLink) { Remove-Item -LiteralPath $startMenuLink -Force }
    if (Test-Path -LiteralPath $startMenuLink) { throw 'the Start menu entry is still there.' }
    Remove-ItemProperty -Path $runKey -Name 'LidAwake' -ErrorAction SilentlyContinue
    if (Get-ItemProperty -Path $runKey -Name 'LidAwake' -ErrorAction SilentlyContinue) { throw 'the start-at-sign-in entry is still there.' }
    Show-Ok 'Start menu entry removed, and it will not start when you sign in'
} catch {
    Show-Problem ('Could not finish this step: ' + $_.Exception.Message + ' Run this again.')
}

# ---- 3/5 Lid action ---------------------------------------------------------------------
Show-Step '3/5  Putting your lid setting back'
try {
    $restored = $false
    $skipped = $false
    # This PC's own file first, then the roaming settings an older version kept the values in.
    $lidFiles = @($machineFile, $settingsFile)
    $source = $null
    $s = $null
    foreach ($file in $lidFiles) {
        if ($null -ne $source) { break }
        if (-not (Test-Path -LiteralPath $file)) { continue }
        $candidate = $null
        try { $candidate = [IO.File]::ReadAllText($file) | ConvertFrom-Json } catch { $candidate = $null }
        if ($null -ne $candidate -and $null -ne $candidate.savedLidAc -and $null -ne $candidate.savedLidDc) {
            $source = $file
            $s = $candidate
        }
    }
    if ($null -ne $source) {
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
            # The power plan the values came from; the current plan when none was saved (an older
            # version) or when that plan is gone since.
            $scheme = 'SCHEME_CURRENT'
            $savedScheme = $s.savedLidScheme
            if ($savedScheme -is [string] -and $savedScheme -match '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') {
                $plans = Invoke-Quietly { (& powercfg /list 2>$null) | Out-String }
                if ($LASTEXITCODE -ne 0 -or $plans -match [regex]::Escape($savedScheme)) { $scheme = $savedScheme }
            }
            [void](Invoke-Quietly { (& powercfg /setacvalueindex $scheme $subButtons $lidAction $ac 2>&1) | Out-String })
            if ($LASTEXITCODE -ne 0) { throw 'powercfg could not set the plugged-in lid action.' }
            [void](Invoke-Quietly { (& powercfg /setdcvalueindex $scheme $subButtons $lidAction $dc 2>&1) | Out-String })
            if ($LASTEXITCODE -ne 0) { throw 'powercfg could not set the on-battery lid action.' }
            # Activating the current plan applies the change; it never switches to another plan.
            [void](Invoke-Quietly { (& powercfg /setactive SCHEME_CURRENT 2>&1) | Out-String })
            if ($LASTEXITCODE -ne 0) { throw 'powercfg could not apply the power plan.' }
            # Forget the saved values (in every file that holds them), so installing again later
            # never "restores" an old one.
            foreach ($file in $lidFiles) {
                if (-not (Test-Path -LiteralPath $file)) { continue }
                $raw = [IO.File]::ReadAllText($file)
                $cleared = [regex]::Replace($raw, '("savedLid(?:Ac|Dc)"\s*:\s*)-?\d+', '${1}null')
                $cleared = [regex]::Replace($cleared, '("savedLidScheme"\s*:\s*)"[^"]*"', '${1}null')
                if ($cleared -ne $raw) {
                    $tmp = $file + '.tmp'
                    [IO.File]::WriteAllText($tmp, $cleared)
                    Move-Item -LiteralPath $tmp -Destination $file -Force
                }
            }
            $restored = $true
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
            Write-Host '       Settings are your phone password and phone sign-ins.'
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
