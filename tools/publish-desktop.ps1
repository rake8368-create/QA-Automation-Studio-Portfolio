param([string]$Database)
# WHAT: Build and publish the actual native Windows desktop executable.
# WHY: The default entry point must not fall back to the preserved web prototype.
# CONTRACT: Self-contained win-x64, named single-file property, local F: project/cache; optional existing DB copy only.
# FAILURE: A failed command exits nonzero and leaves its raw log; absent DB/package input is never fabricated.
# TEST: This script is used for the final native EXE build/publish and existing-data deployment.
$ErrorActionPreference = 'Stop'
$desktopRoot = (Resolve-Path -LiteralPath "$PSScriptRoot\..").Path
$desktopSdk = Join-Path (Split-Path $desktopRoot -Parent) 'Tools\dotnet10\dotnet.exe'
$desktopProject = Join-Path $desktopRoot 'src\QA.AutomationStudio.Desktop\QA.AutomationStudio.Desktop.csproj'
$desktopOutput = Join-Path $desktopRoot 'release\win-x64'
$desktopLogs = Join-Path $desktopRoot 'reports\desktop-native'
$desktopSource = Join-Path $desktopRoot 'work\desktop-native\package-source'
$env:DOTNET_ROOT = Split-Path $desktopSdk -Parent
$env:DOTNET_CLI_HOME = Join-Path $desktopRoot 'work\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $desktopRoot 'work\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'
$desktopCommands = [Collections.Generic.List[object]]::new()
New-Item -ItemType Directory -Force -Path $desktopLogs | Out-Null
function Invoke-DesktopBuild([string]$Name, [string[]]$Arguments) {
    $desktopLog = Join-Path $desktopLogs "$Name.log"
    $desktopTimer = [Diagnostics.Stopwatch]::StartNew()
    & $desktopSdk @Arguments *> $desktopLog
    $desktopCode = $LASTEXITCODE
    $desktopTimer.Stop()
    $desktopCommands.Add([pscustomobject]@{command=@($desktopSdk)+$Arguments;exitCode=$desktopCode;seconds=$desktopTimer.Elapsed.TotalSeconds;log=$desktopLog;cwd=$desktopRoot})
    $desktopCommands | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $desktopLogs 'build-commands.json') -Encoding utf8
    Get-Content -LiteralPath $desktopLog -Tail 8
    if ($desktopCode -ne 0) { throw "$Name failed: exit $desktopCode. See $desktopLog" }
}
Push-Location -LiteralPath $desktopRoot
try {
    if (-not (Test-Path -LiteralPath $desktopSource)) { throw "Local .NET package source missing: $desktopSource" }
    Invoke-DesktopBuild 'final-build' @('build',$desktopProject,'-c','Release','-m:1','-p:UseSharedCompilation=false','--source',$desktopSource)
    Invoke-DesktopBuild 'final-publish' @('publish',$desktopProject,'-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-m:1','-p:UseSharedCompilation=false','--source',$desktopSource,'-o',$desktopOutput)
    if ($Database) {
        $desktopInput = (Resolve-Path -LiteralPath $Database).Path
        New-Item -ItemType Directory -Force -Path (Join-Path $desktopOutput 'data') | Out-Null
        Copy-Item -LiteralPath $desktopInput -Destination (Join-Path $desktopOutput 'data\qa-studio.sqlite')
    }
    Get-FileHash -LiteralPath (Join-Path $desktopOutput 'QA.AutomationStudio.Desktop.exe') -Algorithm SHA256
} finally { Pop-Location }
