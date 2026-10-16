# Windows benchmark run (and, with -Linux, the same run inside WSL), then the reference capture and analysis.
#
#   $env:IRONPDF_LICENSE_KEY = '<key>'      # or set it as a user environment variable
#   $env:ABCPDF_LICENSE_KEY = '<key>'       # optional on Windows (30-day trial), needed for -Linux
#   $env:DOCOTIC_LICENSE_KEY = '<key>'      # the same for Docotic.Pdf
#   ./run.ps1 [-Linux] [-Run 2026-10-07] [-Libraries selectpdf,itext] [-RunnerArgs '--rounds','3']
#
# Every adapter is harness/<Name>/<Name>.Adapter.csproj with AssemblyName adapter-<name in lower case>;
# all of them run unless -Libraries names some.
#
# Needs the .NET 8 SDK, Python 3 with playwright (chromium), pymupdf, pypdf, pypdfium2, pyhanko,
# opencv-python and numpy, veraPDF for the standards results ($env:VERAPDF = launcher path;
# $env:VERAPDF_JAVA_HOME if java is not on PATH), Docker for -Linux deployment, and for -Linux a WSL
# distribution with the .NET 8 SDK and python3.
param(
    [switch]$Linux,
    [string]$Run = (Get-Date -Format 'yyyy-MM-dd'),
    [string[]]$Libraries = @(),
    [string[]]$RunnerArgs = @()
)
# Native tools (dotnet, wsl, python) write progress to stderr; in Windows PowerShell a 'Stop' preference turns
# any such line into a terminating error. Failures are detected from exit codes instead.
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
Set-Location $root

if (-not $env:IRONPDF_LICENSE_KEY) {
    $env:IRONPDF_LICENSE_KEY = [Environment]::GetEnvironmentVariable('IRONPDF_LICENSE_KEY', 'User')
}
# ABCpdf runs on its 30-day trial on Windows without a key; Linux has no trial and needs this key (run.sh skips it).
if (-not $env:ABCPDF_LICENSE_KEY) {
    $env:ABCPDF_LICENSE_KEY = [Environment]::GetEnvironmentVariable('ABCPDF_LICENSE_KEY', 'User')
}
if (-not $env:DOCOTIC_LICENSE_KEY) {
    $env:DOCOTIC_LICENSE_KEY = [Environment]::GetEnvironmentVariable('DOCOTIC_LICENSE_KEY', 'User')
}
$libs = @($Libraries | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { $_.ToLower() })
$found = @(Get-ChildItem harness -Directory | Where-Object {
        (Get-ChildItem $_.FullName -Filter '*.Adapter.csproj') -and (-not $libs -or $libs -contains $_.Name.ToLower())
    })

$bin = Join-Path $env:LOCALAPPDATA 'pdf-benchmarks\bin'
foreach ($p in @('Runner') + $found.Name) {
    dotnet publish "harness/$p" -c Release -o "$bin\$p" --nologo -v quiet
    if ($LASTEXITCODE) { throw "publish $p failed" }
}

$server = Start-Process python -ArgumentList 'server/serve.py', '8777' -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Seconds 1
    $adapters = @()
    foreach ($a in $found) {
        $name = $a.Name.ToLower()
        if ($name -eq 'ironpdf' -and -not $env:IRONPDF_LICENSE_KEY) { Write-Warning 'IRONPDF_LICENSE_KEY not set: IronPDF is skipped'; continue }
        if ($name -eq 'docotic' -and -not $env:DOCOTIC_LICENSE_KEY) { Write-Warning 'DOCOTIC_LICENSE_KEY not set: Docotic.Pdf is skipped'; continue }
        $adapters += @('--adapter', "$name=$bin\$($a.Name)\adapter-$name.exe")
    }

    $win = "$Run-windows"
    dotnet "$bin\Runner\runner.dll" @adapters --base http://127.0.0.1:8777 --results "results/$win" --pdfs "out/$win/pdf" @RunnerArgs
    if ($LASTEXITCODE) { throw 'Windows run failed' }

    # The fidelity reference: one capture per run date, used for both operating systems
    # (every fixture embeds its fonts, so the page renders the same on both).
    python reference/reference.py --base http://127.0.0.1:8777 --out "out/$Run/reference"
    if ($LASTEXITCODE) { throw 'reference capture failed' }
}
finally {
    Stop-Process -Id $server.Id -ErrorAction SilentlyContinue
}

$runs = @("$Run-windows")
if ($Linux) {
    $lin = "$Run-linux"
    $wslRoot = (wsl.exe wslpath -a ($root -replace '\\', '/')).Trim()
    $env:RUN = $lin
    $env:LIBRARIES = ($libs -join ',')
    $env:WSLENV = 'IRONPDF_LICENSE_KEY:ABCPDF_LICENSE_KEY:DOCOTIC_LICENSE_KEY:RUN:LIBRARIES'
    wsl.exe --cd $wslRoot bash ./run.sh @RunnerArgs
    if ($LASTEXITCODE) { throw 'Linux run failed' }
    $runs += $lin

    # Deployment (METHODOLOGY.md, 4.9): Linux images, built and run with Docker; recorded with the Linux results.
    $deployLibs = if ($libs) { $libs -join ',' } else { ($found.Name | ForEach-Object { $_.ToLower() }) -join ',' }
    python deployment/deploy.py --results "results/$lin" --libraries $deployLibs
    if ($LASTEXITCODE) { throw 'deployment failed' }
}

foreach ($r in $runs) {
    # analysis/analyze.py expects the reference next to the PDFs
    Copy-Item "out/$Run/reference" "out/$r/reference" -Recurse -Force
    python analysis/analyze.py --run "out/$r" --results "results/$r"
    if ($LASTEXITCODE) { throw "analysis of $r failed" }
}
