<#
    Closes whatever a customer opened during their session, the moment it ends - so the next
    customer sits down to a clean desktop, not one still full of the last one's browser tabs,
    Discord, and half-finished game.

    Triggered by MainForm.cs the instant it gets the 'session-ended' message from the overlay
    page (posted on SessionStopped, not the later SessionEnded - play is over the moment the
    time runs out, whether or not billing has been settled yet).

    Two passes, not one, and browsers are handled separately from everything else. Chrome,
    Edge, Brave, Firefox and friends do not run as a single process - they spawn a whole tree
    of renderer/GPU/utility helper processes that have no window of their own, so a check that
    only looks for "does this process have a visible window" misses most of a browser's own
    process tree and leaves enough of it alive to relaunch itself straight back into the tabs
    it just had open. Known browser executables are therefore killed by name outright first,
    followed by the same generic "anything with a visible window" sweep as before for
    everything else a customer might have opened - games, Discord, Spotify, whatever - since
    there is no way to enumerate every app in advance. A second, identical pass runs a moment
    later to catch anything that was mid-relaunch (a "restore my tabs" child process, a
    crash-recovery dialog) when the first pass ran.

    The two things that must never be touched are excluded by name throughout.

    GAMES RUNNING AS ADMINISTRATOR
    Most games here run elevated, and this script runs as the logged-in customer account,
    which Windows does not allow to close an elevated program - so the game kept running
    behind the lock screen. Whatever still has a window after both passes is written to the
    Application event log (source AppleEsports, event 9001, its process ids), and a SYSTEM
    task registered by setup-end-session-cleanup.ps1 starts on that event and runs this same
    script with -Elevated to close exactly those processes (and anything they started).

    Usage:  powershell end-session-cleanup.ps1            (the app, as the customer account)
            powershell end-session-cleanup.ps1 -Elevated  (the SYSTEM task)
#>
param([switch]$Elevated)

$ErrorActionPreference = 'Stop'

# Never kill ourselves, and never kill the Windows shell - explorer.exe gone is a black
# desktop and a very bad time for whoever is standing at this PC next. msedgewebview2 is what
# the Apple Esports screens themselves are drawn by.
$Spared = @('AppleEsports', 'AppleEsportsAgent', 'explorer', 'msedgewebview2')

$EventSource = 'AppleEsports'
$EventId = 9001
$LogPath = Join-Path $env:ProgramData 'Apple Esports\logs\end-session-cleanup.log'

function Write-CleanupLog([string]$message) {
    try {
        New-Item -ItemType Directory -Force -Path (Split-Path $LogPath) | Out-Null
        Add-Content -Path $LogPath -Value ("{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message)
    } catch { }
}

# The SYSTEM side. Only ever closes the process ids the customer-side pass reported, and even
# then never a session-0 (service) process, anything Windows' own, or anything spared - so a
# forged event can at most close the customer's own programs, which is what this is for anyway.
function Close-Reported([int[]]$Ids) {
    foreach ($id in $Ids) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if (-not $p -or $p.SessionId -eq 0 -or $Spared -contains $p.ProcessName) { continue }
        $path = $null
        try { $path = $p.Path } catch { }
        if ($path -and $path.StartsWith($env:windir, [StringComparison]::OrdinalIgnoreCase)) { continue }

        & taskkill.exe /PID $id /T /F | Out-Null
        Write-CleanupLog "Closed $($p.ProcessName) ($id) as SYSTEM - exit $LASTEXITCODE"
    }
}

if ($Elevated) {
    $request = Get-WinEvent -MaxEvents 1 -ErrorAction SilentlyContinue -FilterHashtable @{
        LogName = 'Application'; ProviderName = $EventSource; Id = $EventId; StartTime = (Get-Date).AddMinutes(-1)
    }
    if (-not $request) { exit 0 }
    $text = [string]$request.Properties[0].Value
    $ids = @($text -split '[^0-9]+' | Where-Object { $_ } | ForEach-Object { [int]$_ })
    Close-Reported $ids
    exit 0
}

# Killed outright by name, whether or not they currently have a visible window - browsers
# spawn a tree of windowless helper processes the generic sweep below would otherwise miss
# entirely, leaving the browser able to relaunch itself from its own crash-recovery state.
$BrowserNames = @(
    'chrome', 'msedge', 'firefox', 'brave', 'opera', 'opera_gx', 'vivaldi', 'iexplore'
)

function Close-BrowsersByName {
    $closed = @()
    Get-Process -Name $BrowserNames -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $closed += $_.ProcessName
            Stop-Process -Id $_.Id -Force -ErrorAction Stop
        }
        catch {
            # Already gone, or this account can't touch it - the rest of the cleanup must
            # still run.
        }
    }
    return $closed
}

function Close-WindowedProcesses {
    $closed = @()
    Get-Process | Where-Object {
        $_.MainWindowHandle -ne 0 -and
        $_.MainWindowTitle -ne '' -and
        $Spared -notcontains $_.ProcessName
    } | ForEach-Object {
        try {
            $closed += $_.ProcessName
            Stop-Process -Id $_.Id -Force -ErrorAction Stop
        }
        catch {
            # A process that exits on its own between the check and the kill, or one this
            # account has no permission to touch, must not stop the rest of the cleanup.
        }
    }
    return $closed
}

$closed = @()
$closed += Close-BrowsersByName
$closed += Close-WindowedProcesses

Start-Sleep -Milliseconds 800
$closed += Close-BrowsersByName
$closed += Close-WindowedProcesses

$closed = $closed | Select-Object -Unique

# Anything still showing a window could not be closed from this account - almost always a
# game running as administrator. Hand exactly those to the SYSTEM task (see the top of file).
Start-Sleep -Milliseconds 500
$stuck = @(Get-Process | Where-Object {
    $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -ne '' -and $Spared -notcontains $_.ProcessName
})
if ($stuck.Count -gt 0) {
    try {
        Write-EventLog -LogName Application -Source $EventSource -EventId $EventId -EntryType Information `
            -Message ("pids=" + (($stuck | ForEach-Object { $_.Id }) -join ','))
        Write-Host "Asked SYSTEM to close: $(($stuck | ForEach-Object { $_.ProcessName }) -join ', ')"
    }
    catch {
        Write-Host "Could not ask SYSTEM to close $(($stuck | ForEach-Object { $_.ProcessName }) -join ', '): $($_.Exception.Message)"
    }
}

if ($closed.Count -gt 0) {
    Write-Host "Closed for the next customer: $($closed -join ', ')"
}
else {
    Write-Host 'Nothing to close - no windowed programs were running.'
}
