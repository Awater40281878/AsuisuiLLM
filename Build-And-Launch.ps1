$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$project = Join-Path $root 'DesktopAI.Gui\DesktopAI.Gui.csproj'
$outputName = 'AsuisuiLLM-' + [char]0x8996 + [char]0x7A97 + [char]0x7248 + '-v7'
$output = Join-Path $root $outputName
$log = Join-Path $env:TEMP 'AsuisuiAI-build.log'

& dotnet build $project --configuration Debug --output $output --no-restore --nologo *> $log
exit $LASTEXITCODE
