# Compatibility entry point. macOS development and packaging are discontinued.
param(
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$Output = (Join-Path $PSScriptRoot '../artifacts/release'),
    [string]$PackageSource = '',
    [string]$RuntimeVersion = ''
)
& (Join-Path $PSScriptRoot 'publish.ps1') -Output $Output -PackageSource $PackageSource -RuntimeVersion $RuntimeVersion
