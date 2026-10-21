<#
    Lets end-session-cleanup.ps1 close games that run as administrator - see that script's own
    comment. Run by the installer, already elevated, on every install and upgrade (idempotent),
    the same reason as setup-restart-on-kill.ps1.

    1. Registers "AppleEsports" as an Application event log source - needs admin once; after
       that the customer account may write to it, which is how it asks for help.
    2. Registers a hidden SYSTEM task that starts on that event (9001) and runs the cleanup
       script with -Elevated. Nothing runs in between: no service, no polling.
#>
$ErrorActionPreference = 'Stop'
$TaskName = 'AppleEsports End Session Cleanup'
$ScriptPath = Join-Path $PSScriptRoot 'end-session-cleanup.ps1'

if (-not [System.Diagnostics.EventLog]::SourceExists('AppleEsports')) {
    New-EventLog -LogName Application -Source 'AppleEsports'
}

$escapedScript = $ScriptPath -replace '&', '&amp;'
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Closes games still running as administrator when an Apple Esports session ends - see end-session-cleanup.ps1.</Description>
  </RegistrationInfo>
  <Triggers>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="Application"&gt;&lt;Select Path="Application"&gt;*[System[Provider[@Name='AppleEsports'] and EventID=9001]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
    </EventTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>Queue</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>
    <Hidden>true</Hidden>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "$escapedScript" -Elevated</Arguments>
    </Exec>
  </Actions>
</Task>
"@

$xmlPath = Join-Path $env:TEMP 'AppleEsportsEndSessionCleanup.xml'
# UTF-16 with BOM - schtasks.exe /XML silently fails to import an event-triggered task otherwise.
[System.IO.File]::WriteAllText($xmlPath, $xml, [System.Text.Encoding]::Unicode)
try {
    & schtasks.exe /Create /F /TN $TaskName /XML $xmlPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Output "Could not register '$TaskName' (schtasks exit $LASTEXITCODE)."
        exit $LASTEXITCODE
    }
    Write-Output "Registered '$TaskName'."
}
finally {
    Remove-Item $xmlPath -Force -ErrorAction SilentlyContinue
}
