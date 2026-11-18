param(
    [string]$OutputRoot = 'E:\Temp\polymarket-hl-strategy\Trayvora-Test',
    [string]$ModelFile
)

$ErrorActionPreference = 'Stop'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ([IO.Path]::GetPathRoot($OutputRoot) -ne 'E:\') {
    throw 'Choose a build directory on E:.'
}

$env:TEMP = Join-Path $OutputRoot 'tmp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $OutputRoot 'dotnet'
$env:NUGET_PACKAGES = Join-Path $OutputRoot 'nuget'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $OutputRoot 'nuget-http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $OutputRoot 'nuget-plugins'
$env:APPDATA = Join-Path $OutputRoot 'appdata'
$env:LOCALAPPDATA = Join-Path $OutputRoot 'localappdata'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null

$project = Join-Path $PSScriptRoot '../src/VoiceAgent.Desktop/VoiceAgent.Desktop.csproj'
$package = Join-Path $OutputRoot ('release-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
dotnet publish $project -m:1 -c Release -r win-x64 --self-contained true -p:PublishProfile=CompactWinX64 --artifacts-path (Join-Path $OutputRoot 'publish-obj') -o $package
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)." }

Copy-Item -Path (Join-Path $PSScriptRoot 'portable\*') -Destination $package
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../README.md') -Destination (Join-Path $package 'README.md')

if ($ModelFile) {
    $model = (Resolve-Path -LiteralPath $ModelFile).Path
    if ([IO.Path]::GetPathRoot($model) -ne 'E:\') { throw 'Choose a model file on E:.' }
    $expected = '60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe'
    $models = Join-Path $package 'models'
    New-Item -ItemType Directory -Path $models | Out-Null
    $destination = Join-Path $models 'ggml-base.bin'
    Copy-Item -LiteralPath $model -Destination $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $expected) {
        throw 'Whisper Base checksum mismatch.'
    }
}

$archive = "$package.zip"
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $([IO.Path]::GetFileName($archive))" -Encoding ascii
Write-Output $archive
