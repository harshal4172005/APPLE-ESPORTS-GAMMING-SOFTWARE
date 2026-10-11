<#
    Assembles everything a branch needs to run on its own, into installer\branch\staging.

        staging\
          AppleEsports.exe        the dashboard window
          api\                    self-contained API (no .NET needed on the branch PC)
          api\wwwroot\            the dashboard itself, served by the API
          pgsql\                  PostgreSQL, trimmed to what actually runs
          setup-database.ps1
          setup-api.ps1

    The PostgreSQL binaries are not in the repository - 120 MB of third-party build
    output does not belong in git. This downloads them once and caches them.

    Usage:  pwsh installer\branch\build-branch-package.ps1
#>

$ErrorActionPreference = 'Stop'

$here      = $PSScriptRoot
$repoRoot  = Split-Path (Split-Path $here -Parent) -Parent
$staging   = Join-Path $here 'staging'
$cache     = Join-Path $here '.cache'

$pgVersion = '16.4-1'
$pgUrl     = "https://get.enterprisedb.com/postgresql/postgresql-$pgVersion-windows-x64-binaries.zip"

function Step($n, $text) { Write-Host "`n== $n. $text ==" -ForegroundColor Cyan }

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $staging, $cache | Out-Null

# -- 1. The dashboard, built into the API so one service serves everything --
Step 1 'Building the dashboard'
Push-Location (Join-Path $repoRoot 'client')
try {
    # Continue, not Stop: vite writes its own progress ("transforming...", "rendering
    # chunks...") to stderr even on a clean build, and with ErrorActionPreference=Stop,
    # PowerShell 7 turns every stderr line from a native command into a terminating error -
    # so this always threw before even reaching the real exit-code check below.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    npm run build 2>&1 | Select-String -Pattern 'error|built in' | Select-Object -Last 1
    $ErrorActionPreference = $prevEap
    if ($LASTEXITCODE -ne 0) { throw 'The dashboard build failed' }
}
finally { Pop-Location }

# -- 2. API, self-contained so the branch PC needs no .NET installed --
Step 2 'Publishing the API'
$apiOut = Join-Path $staging 'api'
dotnet publish (Join-Path $repoRoot 'AppleEsportsErp\src\AppleEsportsErp.Api\AppleEsportsErp.Api.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -o $apiOut --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'The API publish failed' }

# Served by the API at the branch, so there is no nginx and no second moving part.
Copy-Item (Join-Path $repoRoot 'client\dist') (Join-Path $apiOut 'wwwroot') -Recurse -Force

# A Development appsettings on a branch PC would turn on Swagger and detailed errors
# on a machine customers can reach.
Remove-Item (Join-Path $apiOut 'appsettings.Development.json') -Force -ErrorAction SilentlyContinue

Write-Host ("   api: {0:N0} MB" -f ((Get-ChildItem $apiOut -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))

# -- 3. The dashboard window --
Step 3 'Publishing the desktop client'
Push-Location (Join-Path $repoRoot 'desktop-client')
try {
    dotnet publish -c Release -o publish --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'The desktop client publish failed' }
    Copy-Item 'publish\AppleEsports.exe' $staging -Force
}
finally { Pop-Location }

# -- 4. PostgreSQL --
Step 4 'Preparing PostgreSQL'
$zip = Join-Path $cache "postgresql-$pgVersion.zip"

if (Test-Path $zip) {
    Write-Host '   using the cached download'
} else {
    Write-Host '   downloading (about 320 MB, once)'
    Invoke-WebRequest -Uri $pgUrl -OutFile $zip -TimeoutSec 1800
}

$extracted = Join-Path $cache 'pg'
if (-not (Test-Path (Join-Path $extracted 'pgsql\bin\postgres.exe'))) {
    Expand-Archive $zip -DestinationPath $extracted -Force
}

# Only bin, lib and share. The full archive is 920 MB, of which pgAdmin is 616 MB and
# debug symbols 156 MB - neither has any business on a branch till PC.
$pgOut = Join-Path $staging 'pgsql'
New-Item -ItemType Directory -Force $pgOut | Out-Null
foreach ($dir in @('bin', 'lib', 'share')) {
    Copy-Item (Join-Path $extracted "pgsql\$dir") $pgOut -Recurse -Force
}
Write-Host ("   pgsql: {0:N0} MB" -f ((Get-ChildItem $pgOut -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))

# -- 5. Setup scripts --
Step 5 'Adding the setup scripts'
Copy-Item (Join-Path $here 'setup-database.ps1') $staging -Force
Copy-Item (Join-Path $here 'setup-api.ps1') $staging -Force

$total = (Get-ChildItem $staging -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ""
Write-Host ("== Staged: {0:N0} MB ==" -f $total) -ForegroundColor Green
Write-Host "   $staging"
