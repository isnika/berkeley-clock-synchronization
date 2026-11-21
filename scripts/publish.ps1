param(
    [string]$Output = (Join-Path $PSScriptRoot '../artifacts/release'),
    [string]$PackageSource = '',
    [string]$RuntimeVersion = ''
)
$ErrorActionPreference = 'Stop'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$repo = Split-Path $PSScriptRoot -Parent
$target = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force $target | Out-Null
$publish = Join-Path $target ('BerkeleyTimeSync-Windows-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6))
$publishArgs = @('publish', (Join-Path $repo 'src/Berkeley.Windows/Berkeley.Windows.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:UseAppHost=true', '-o', $publish)
if ($PackageSource) { $publishArgs += @('--source', $PackageSource, '-p:NuGetAudit=false') }
if ($RuntimeVersion) { $publishArgs += ('-p:RuntimeFrameworkVersion=' + $RuntimeVersion) }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed' }
Copy-Item -LiteralPath (Join-Path $repo 'docs/huong-dan-windows.md') -Destination $publish
Copy-Item -LiteralPath (Join-Path $repo 'docs/doi-chieu-giao-dien-windows.md') -Destination $publish
Copy-Item -LiteralPath (Join-Path $repo 'docs/cap-nhat-windows-chinh.md') -Destination $publish
Compress-Archive -LiteralPath $publish -DestinationPath ($publish + '.zip')
Write-Output ($publish + '.zip')
