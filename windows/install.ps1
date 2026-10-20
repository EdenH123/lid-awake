# Installs Lid Awake for Windows. Double-click "Install Lid Awake (Windows).cmd" in the folder above.
# It checks Tailscale, puts the app in %LOCALAPPDATA%\LidAwake, makes it start when you sign in,
# adds "Lid Awake" to the Start menu, and starts it now. Safe to run again (to update): your settings and phone password stay.
#
# The switches below are FOR AUTOMATED TESTS ONLY. A person never needs them; without them the
# installer checks Tailscale, starts the app, and waits for Enter at the end.
#   -SkipTailscale   do not check for Tailscale
#   -NoStart         do not start the app after installing
#   -NoPause         do not wait for Enter at the end
#
# This file is plain ASCII on purpose: Windows PowerShell 5.1 misreads UTF-8 without a BOM.
param(
    [switch]$SkipTailscale,
    [switch]$NoStart,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
$dest = Join-Path $env:LOCALAPPDATA 'LidAwake'
$exe = Join-Path $dest 'LidAwake.exe'
$tsExe = 'C:\Program Files\Tailscale\tailscale.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$startMenuLink = Join-Path $startMenuDir 'Lid Awake.lnk'
$tempDir = $null

function Show-Step([string]$text) { Write-Host ''; Write-Host $text }
function Show-Ok([string]$text) { Write-Host "  [OK] $text" -ForegroundColor Green }
function Show-Note([string]$text) { Write-Host "       $text" }
function Wait-ForEnter {
    if (-not $NoPause) { Write-Host ''; [void](Read-Host 'Press Enter to close') }
}
function Stop-Here([string]$text) {
    Write-Host "  [X] $text" -ForegroundColor Red
    # Exiting skips the cleanup further down, so remove a half-built temp folder here too.
    if ($tempDir -and (Test-Path -LiteralPath $tempDir)) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Wait-ForEnter
    exit 1
}

# Native programs write to stderr in normal use; Stop would turn that into an error.
function Invoke-Quietly([scriptblock]$command) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $command } finally { $ErrorActionPreference = $saved }
}

# Runs the installed LidAwake.exe --claude-setup <on|off|status> (hidden, at most 30 seconds): the
# same code as the tray item "Claude alerts and usage from Claude Code...". Returns its exit code
# (-1 when it could not run) and what it printed. Status: 0 set up, 1 not set up, 2 no Claude
# Code on this PC, 3 Claude Code's settings file can't be read. On and off: 0 done, 3 refused.
function Invoke-ClaudeSetup([string]$what) {
    $out = Join-Path ([IO.Path]::GetTempPath()) ('LidAwake-claude-' + [Guid]::NewGuid().ToString('N') + '.txt')
    try {
        $p = Start-Process -FilePath $exe -ArgumentList '--claude-setup', $what -PassThru -WindowStyle Hidden -RedirectStandardOutput $out
        [void]$p.Handle  # Windows PowerShell 5.1 returns no ExitCode unless the handle is read
        if (-not $p.WaitForExit(30000)) {
            try { $p.Kill() } catch { }
            return [pscustomobject]@{ Code = -1; Text = 'it did not finish.' }
        }
        $text = ''
        if (Test-Path -LiteralPath $out) { $text = ([IO.File]::ReadAllText($out)).Trim() }
        return [pscustomobject]@{ Code = $p.ExitCode; Text = $text }
    } catch {
        return [pscustomobject]@{ Code = -1; Text = $_.Exception.Message }
    } finally {
        Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
    }
}

function Test-TailscaleSignedIn {
    try {
        $json = Invoke-Quietly { (& $tsExe status --json 2>$null) | Out-String }
        if ($LASTEXITCODE -ne 0) { return $false }
        return (($json | ConvertFrom-Json).BackendState -eq 'Running')
    } catch { return $false }
}

function Test-DotnetSdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    try {
        $sdks = Invoke-Quietly { (& dotnet --list-sdks 2>$null) | Out-String }
        return ($sdks.Trim().Length -gt 0)
    } catch { return $false }
}

Write-Host ''
Write-Host 'Installing Lid Awake'

# ---- 1/5 Tailscale ----------------------------------------------------------------------
Show-Step '1/5  Tailscale (the private connection between your phone and this PC)'
if ($SkipTailscale) {
    Show-Note 'Skipped.'
} else {
    if (-not (Test-Path -LiteralPath $tsExe)) {
        try { Start-Process 'https://tailscale.com/download/windows' } catch { }
        Stop-Here 'Tailscale is not installed. Install it from the page that opened and sign in, then run this installer again.'
    }
    if (-not (Test-TailscaleSignedIn)) {
        Stop-Here 'Tailscale is not signed in. Open Tailscale (its icon is by the clock), sign in, then run this installer again.'
    }
    Show-Ok 'Tailscale is installed and signed in'
}

# ---- 2/5 The app files ------------------------------------------------------------------
Show-Step '2/5  Getting the app ready'
$appDir = $null
try {
    $prebuilt = Join-Path $root 'app'
    if (Test-Path -LiteralPath (Join-Path $prebuilt 'LidAwake.exe')) {
        $appDir = $prebuilt
        Show-Ok 'Found the ready-made app'
    } elseif (Test-DotnetSdk) {
        Show-Note 'No ready-made app found, building it. This takes a minute or two.'
        $tempDir = Join-Path ([IO.Path]::GetTempPath()) ('LidAwake-build-' + [Guid]::NewGuid().ToString('N'))
        $csproj = Join-Path $root 'LidAwake.Windows\LidAwake.Windows.csproj'
        $log = Invoke-Quietly { (& dotnet publish $csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o $tempDir 2>&1) | Out-String }
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $tempDir 'LidAwake.exe'))) {
            $tail = (($log -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -Last 15) -join "`r`n"
            Write-Host $tail
            Stop-Here 'Building the app failed (details above). You can use the ready-made "Lid Awake for Windows" ZIP instead (the LidAwake-Windows download of the Windows workflow on GitHub Actions).'
        }
        $appDir = $tempDir
        Show-Ok 'Built the app'
    }
} catch {
    Stop-Here ('Could not get the app ready: ' + $_.Exception.Message)
}
if (-not $appDir) {
    Write-Host '  [X] The app itself is missing from this folder.' -ForegroundColor Red
    Write-Host ''
    Write-Host '      Please download the ready-made "Lid Awake for Windows" ZIP:'
    Write-Host '      on GitHub open Actions, then the Windows workflow, then the newest run,'
    Write-Host '      and download "LidAwake-Windows" at the bottom of the page.'
    Write-Host '      Unpack the ZIP and open "Install Lid Awake (Windows)" inside it.'
    Write-Host '      (Or unpack it into the "windows\app" folder here and run this again.)'
    Wait-ForEnter
    exit 1
}

# ---- 3/5 Copy ---------------------------------------------------------------------------
Show-Step '3/5  Installing'
try {
    # Stop a running Lid Awake so its files can be replaced.
    $running = @(Get-Process -Name 'LidAwake' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
        Show-Note 'Stopped the running Lid Awake (it starts again below).'
    }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    # Only the program folder is replaced. Settings live in %APPDATA%\LidAwake and are not touched.
    # The program folder also holds machine.json (the saved lid setting) and the activity log:
    # the installer must never delete them. That is why only 'web' and 'sky' are wiped below.
    foreach ($sub in 'web', 'sky') {
        $old = Join-Path $dest $sub
        if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
    }
    $copied = $false
    for ($try = 1; $try -le 5 -and -not $copied; $try++) {
        try {
            Get-ChildItem -LiteralPath $appDir | Copy-Item -Destination $dest -Recurse -Force
            $copied = $true
        } catch {
            if ($try -eq 5) { throw }
            Start-Sleep -Seconds 1
        }
    }
    # The ZIP came from the internet; without this Windows may warn each time it starts.
    try { Get-ChildItem -LiteralPath $dest -Recurse -File | Unblock-File } catch { }
    if (-not (Test-Path -LiteralPath $exe)) { throw 'LidAwake.exe is not in the install folder after copying.' }
    Show-Ok "Lid Awake installed in $dest"
} catch {
    Stop-Here ('Could not copy the app: ' + $_.Exception.Message + ' Close any window showing the folder, then run this installer again.')
} finally {
    if ($tempDir -and (Test-Path -LiteralPath $tempDir)) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---- 4/5 Start when you sign in ---------------------------------------------------------
Show-Step '4/5  Start when you sign in, and the Start menu'
try {
    if (-not (Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    New-ItemProperty -Path $runKey -Name 'LidAwake' -Value ('"' + $exe + '"') -PropertyType String -Force | Out-Null
    Show-Ok 'Lid Awake starts when you sign in'
} catch {
    Stop-Here ('Could not set up starting at sign-in: ' + $_.Exception.Message + ' Run this installer again.')
}
# "Lid Awake" in the Start menu (for this user only), so it can be started again after Quit.
# Lid Awake works without it, so a failure here is told but does not stop the installation.
try {
    New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($startMenuLink)
        $link.TargetPath = $exe
        $link.WorkingDirectory = $dest
        $link.Description = 'Lid Awake'
        $link.Save()
    } finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
    if (-not (Test-Path -LiteralPath $startMenuLink)) { throw 'the shortcut file was not created.' }
    Show-Ok 'Lid Awake is in the Start menu'
} catch {
    Write-Host ('  [X] Could not add Lid Awake to the Start menu: ' + $_.Exception.Message + ' Lid Awake itself is installed. To start it again after Quit, run LidAwake.exe in ' + $dest + ', or run this installer again.') -ForegroundColor Red
}

# ---- 5/5 Start now ----------------------------------------------------------------------
Show-Step '5/5  Starting Lid Awake'
if ($NoStart) {
    Show-Note 'Skipped.'
} else {
    try {
        Start-Process -FilePath $exe -WorkingDirectory $dest
        $up = $false
        for ($i = 0; $i -lt 20 -and -not $up; $i++) {
            Start-Sleep -Milliseconds 500
            $up = [bool](Get-Process -Name 'LidAwake' -ErrorAction SilentlyContinue)
        }
        if (-not $up) { throw 'it did not stay running.' }
        Show-Ok 'Running. Look for the Lid Awake icon by the clock.'
    } catch {
        Stop-Here ('Lid Awake did not start: ' + $_.Exception.Message + ' Run this installer again.')
    }
}

# ---- Optional: Claude ------------------------------------------------------------------
# Only when Claude Code is on this PC (silent otherwise), and only with the owner's yes: it changes
# Claude Code's settings file. Not asked in automated tests (-NoPause), or with nobody to answer.
$claudeState = Invoke-ClaudeSetup 'status'
$claudeLater = 'Later: right-click the Lid Awake icon by the clock -> Claude alerts and usage from Claude Code...'
if ($claudeState.Code -eq 0) {
    Show-Step 'Claude alerts on your phone (optional)'
    Show-Ok 'Already set up'
} elseif ($claudeState.Code -eq 3) {
    Show-Step 'Claude alerts on your phone (optional)'
    Show-Note "Skipped: Claude Code's settings file (.claude\settings.json in your user folder) can't be read (invalid JSON, too big, or an unexpected shape), so Lid Awake left it alone."
} elseif ($claudeState.Code -eq 1) {
    Show-Step 'Claude alerts on your phone (optional)'
    Show-Note 'Shows your Claude usage after each reply, and alerts your phone when Claude finishes or needs you.'
    Show-Note "It adds Lid Awake to Claude Code's settings (a copy of the old file is saved first)."
    if ($NoPause -or -not [Environment]::UserInteractive) {
        Show-Note ('Skipped. ' + $claudeLater)
    } else {
        $answer = Read-Host '       Turn it on? [Y/n]'
        if ($answer.Trim() -match '^(n|no)$') {
            Show-Note ('Skipped. ' + $claudeLater)
        } else {
            $claudeOn = Invoke-ClaudeSetup 'on'
            if ($claudeOn.Code -eq 0) {
                Show-Ok 'Turned on. Claude Code picks it up in new sessions.'
                if ($claudeOn.Text -match 'own status line') { Show-Note 'Your own status line is kept; Lid Awake asks Claude Code for the usage numbers itself.' }
            } else {
                Write-Host ('  [X] Not turned on: ' + $claudeOn.Text) -ForegroundColor Red
            }
        }
    }
}

Write-Host ''
Write-Host 'Almost done: in the Set up Lid Awake window, set a phone password, turn Lid Awake on and scan the code with your phone. (Later: right-click the Lid Awake icon by the clock -> Set up phone...)'
Wait-ForEnter
exit 0
