param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$DotNet = 'dotnet',
    [string]$Python = 'python',
    [string]$NuGetSource = 'https://api.nuget.org/v3/index.json'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory for isolated verification.' }
New-Item -ItemType Directory -Path $output | Out-Null

function Invoke-Required([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command exited $LASTEXITCODE ($Command)." }
}

Push-Location -LiteralPath $root
try {
    $workbook = Join-Path $root 'test-data/QA_Automation_Portfolio_TC_70.xlsx'
    $map = Join-Path $root 'config/portfolio-import.json'
    $native = Join-Path $output 'work/native-run-20261028/before.sqlite'
    $fixtures = Join-Path $output 'desktop-fixture'
    $harness = Join-Path $root 'tests/QAStudio.Tests/bin/Release/net10.0/QAStudio.Tests.dll'
    $app = 'src/QA.AutomationStudio.Desktop/QA.AutomationStudio.Desktop.csproj'
    Invoke-Required $DotNet @('build', 'QAStudio.slnx', '-c', 'Release', '-warnaserror')
    Invoke-Required $DotNet @('run', '--project', 'tools/QAStudio.PublicFixture', '-c', 'Release', '--',
        $workbook, $map, (Join-Path $fixtures 'before.sqlite'), $native)
    Invoke-Required $Python @('tools/verify-public.py', 'fixtures', $fixtures)
    Push-Location -LiteralPath $output
    try {
        Invoke-Required $DotNet @($harness, $workbook, $map, (Join-Path $output 'core'))
    } finally {
        Pop-Location
    }
    Invoke-Required $DotNet @($harness, '--result-package', (Join-Path $output 'package'))

    $publish = Join-Path $output 'publish'
    Invoke-Required $DotNet @('publish', $app, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-warnaserror',
        '--source', $NuGetSource, '-o', $publish)
    $files = @(Get-ChildItem -LiteralPath $publish -Force)
    if ($files.Count -ne 1 -or $files[0].PSIsContainer -or $files[0].Name -cne 'QA.AutomationStudio.Desktop.exe') {
        throw 'Expected exactly one published desktop EXE.'
    }

    $priorExtraction = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
    try {
        $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $output 'bundle'
        $desktopResults = Join-Path $output 'desktop-results'
        $arguments = @('--verify-import', ('"' + $fixtures + '"'), ('"' + $desktopResults + '"'))
        $process = Start-Process -FilePath $files[0].FullName -WorkingDirectory $output -PassThru -WindowStyle Hidden -ArgumentList $arguments
        if (!$process.WaitForExit(90000)) { $process.Kill(); throw 'Published EXE import verification timed out.' }
        if ($process.ExitCode -ne 0) { throw "Published EXE import verification exited $($process.ExitCode)." }
    } finally {
        $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $priorExtraction
    }
    Invoke-Required $Python @('tools/verify-public.py', 'results', $output)
} finally {
    Pop-Location
}
