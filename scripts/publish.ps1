param([string]$Output = (Join-Path $PSScriptRoot '../artifacts/release'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$target = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force $target | Out-Null
$publish = Join-Path $target ('BerkeleyTimeSync-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
dotnet publish (Join-Path $repo 'src/Berkeley.Desktop(tv3)/Berkeley.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item -LiteralPath (Join-Path $repo 'docs/huong-dan-tv3.md') -Destination $publish
Compress-Archive -LiteralPath $publish -DestinationPath ($publish + '.zip')
Write-Output ($publish + '.zip')
