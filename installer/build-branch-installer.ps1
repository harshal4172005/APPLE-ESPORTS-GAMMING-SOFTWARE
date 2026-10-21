<#
    Builds AppleEsports-Branch-Setup-<version>.exe — the installer that turns a PC into
    either an operator counter (running the whole branch) or a locked gaming machine.

    Stages everything first so the installer can never ship a stale binary.

    Usage:  pwsh installer\build-branch-installer.ps1
#>

$ErrorActionPreference = 'Stop'

$here     = $PSScriptRoot
$repoRoot = Split-Path $here -Parent
$distDir  = Join-Path $repoRoot 'dist'

function Find-Iscc {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw "Inno Setup not found. Install it with:  winget install --id JRSoftware.InnoSetup"
}

Write-Host "== 1. Staging the branch package ==" -ForegroundColor Cyan
& (Join-Path $here 'branch\build-branch-package.ps1')

Write-Host "`n== 2. Publishing the gaming PC agent ==" -ForegroundColor Cyan
$agentDir = Join-Path $repoRoot 'AppleEsportsErp\src\AppleEsportsErp.ClientAgent'
Push-Location $agentDir
try {
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o publish --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'The agent publish failed' }
}
finally { Pop-Location }

# Staged next to the branch installer under its own version-suffixed name, so it can be
# published to Head Office as a second, much smaller artifact - see ReleasesController.UploadAgent.
# A gaming PC's own AgentSelfUpdater fetches only this file, never the ~165MB branch installer,
# because it has no database, API or Windows service to replace - only this one exe.
$issContent = Get-Content (Join-Path $here 'AppleEsportsBranch.iss') -Raw
if ($issContent -notmatch '#define AppVersion\s+"([^"]+)"') { throw 'Could not read AppVersion from AppleEsportsBranch.iss' }
$agentVersion = $Matches[1]

New-Item -ItemType Directory -Force $distDir | Out-Null
$agentExe = Join-Path $agentDir 'publish\AppleEsportsAgent.exe'
$agentDist = Join-Path $distDir "AppleEsportsAgent-$agentVersion.exe"
Copy-Item $agentExe $agentDist -Force

Write-Host "`n== 3. Microsoft prerequisites ==" -ForegroundColor Cyan
# Bundled so a fresh PC installs them by itself (InstallPrerequisites in the .iss). Fetched
# here rather than kept in git; each must carry Microsoft's own valid signature.
$prereqDir = Join-Path $here 'prereqs'
New-Item -ItemType Directory -Force $prereqDir | Out-Null
$prereqs = @{
    'vc_redist.x64.exe'              = 'https://aka.ms/vs/17/release/vc_redist.x64.exe'
    'MicrosoftEdgeWebview2Setup.exe' = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
}
foreach ($name in $prereqs.Keys) {
    $file = Join-Path $prereqDir $name
    if (-not (Test-Path $file)) { Invoke-WebRequest -Uri $prereqs[$name] -OutFile $file -UseBasicParsing }
    $sig = Get-AuthenticodeSignature $file
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        Remove-Item $file -Force
        throw "$name is not validly signed by Microsoft - refusing to bundle it"
    }
}

Write-Host "`n== 4. Compiling the installer ==" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $distDir | Out-Null
$iscc = Find-Iscc
& $iscc (Join-Path $here 'AppleEsportsBranch.iss') |
    Where-Object { $_ -match 'error|Successful compile' }
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed' }

$setup = Get-ChildItem $distDir -Filter 'AppleEsports-Branch-Setup-*.exe' |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1

Write-Host "`n== Done ==" -ForegroundColor Green
Write-Host ("   {0}  ({1:N0} MB)" -f $setup.Name, ($setup.Length / 1MB))
Write-Host "   $($setup.FullName)"
Write-Host ("   {0}  ({1:N0} MB)" -f (Split-Path $agentDist -Leaf), ((Get-Item $agentDist).Length / 1MB))
Write-Host "   $agentDist"
