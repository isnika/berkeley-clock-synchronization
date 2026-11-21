param([string]$DataDirectory = '')
$ErrorActionPreference = 'Stop'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$project = Join-Path $PSScriptRoot '../src/Berkeley.Windows/Berkeley.Windows.csproj'
$runArgs = @('run', '--project', $project, '-c', 'Release')
if ($DataDirectory) {
    if (![IO.Path]::IsPathFullyQualified($DataDirectory)) { throw 'DataDirectory must be an absolute path.' }
    $runArgs += @('--', '--data-dir', $DataDirectory)
}
& dotnet @runArgs
if ($LASTEXITCODE -ne 0) { throw 'Windows application failed' }
