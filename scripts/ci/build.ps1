param([Parameter(Mandatory)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location $root
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path $OutputRoot) { throw 'Use a fresh output directory; existing data will not be removed.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$sha = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE) { throw 'Cannot identify source commit' }
$count = [int](git rev-list --first-parent --count HEAD)
if ($LASTEXITCODE -or $count -lt 2) { throw 'Full Git history required (initial commit plus changes)' }
$version = "0.1.$($count - 1)"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
# Never exercise a user's signed-in AI profile in CI.
Remove-Item Env:TRAYVORA_LIVE_TEST_PROFILE -ErrorAction SilentlyContinue
$artifacts = Join-Path $OutputRoot 'build'
$results = Join-Path $OutputRoot 'results'
$package = Join-Path $OutputRoot 'package'
$release = Join-Path $OutputRoot 'release'
New-Item -ItemType Directory -Path $results,$release | Out-Null
dotnet build VoiceAgent.slnx -c Release --artifacts-path $artifacts -p:Version=$version -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE) { throw 'Build failed' }
dotnet test VoiceAgent.slnx -c Release --no-build --artifacts-path $artifacts --results-directory $results --logger 'trx;LogFileName=tests.trx'
if ($LASTEXITCODE) { throw 'Tests failed; no package/release will be produced' }
dotnet publish src/VoiceAgent.Desktop -c Release -r win-x64 --self-contained true -p:PublishProfile=CompactWinX64 --artifacts-path $artifacts -o $package -p:Version=$version -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE) { throw 'Publish failed' }
# Keep the portable root small; Whisper's explicit-path native loader needs this one directory.
$expected = @('Trayvora.exe', 'runtimes/win-x64/whisper.dll', 'runtimes/win-x64/ggml-whisper.dll',
    'runtimes/win-x64/ggml-base-whisper.dll', 'runtimes/win-x64/ggml-cpu-whisper.dll')
$actual = @(Get-ChildItem $package -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\','/') })
if (@(Compare-Object $expected $actual).Count) { throw "Unexpected compact package layout: $($actual -join ', ')" }
Copy-Item scripts/portable/START.txt $package
Copy-Item docs/setup.md (Join-Path $package 'Setup.md')
$metadata = @{version=$version;commit=$sha;runtime='win-x64';selfContained=$true;singleFile=$true}
$metadata | ConvertTo-Json | Set-Content (Join-Path $package 'release.json') -Encoding utf8
$zip = Join-Path $release 'trayvora-win-x64.zip'
Compress-Archive -Path "$package/*" -DestinationPath $zip
# Validate extracted bytes and executable, not just that a zip file exists.
$check = Join-Path $OutputRoot 'extracted'
Expand-Archive $zip $check
foreach($file in Get-ChildItem $package -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($package,$file.FullName)
    if ((Get-FileHash $file.FullName).Hash -ne (Get-FileHash (Join-Path $check $relative)).Hash) { throw "Corrupt package: $relative" }
}
# Exercise the shipped executable (not dotnet/testhost), including extracted .NET/Skia/SQLite
# libraries and Whisper's sidecar libraries. Never initialize a user's profile or microphone.
$report = Join-Path $results 'package-smoke.json'
$previousBundleCache = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $OutputRoot 'bundle-cache'
try {
    $process = Start-Process -FilePath (Join-Path $check 'Trayvora.exe') -ArgumentList @('--verify-package', "`"$report`"") -WorkingDirectory $check -PassThru -WindowStyle Hidden
    if (!$process.WaitForExit(60000)) { $process.Kill($true); throw 'Packaged executable smoke test timed out' }
    if ($process.ExitCode -ne 0 -or !(Test-Path $report)) { throw 'Packaged executable smoke test failed' }
    $smoke = Get-Content -Raw $report | ConvertFrom-Json
    if (!$smoke.success -or !$smoke.singleFile) { throw 'Packaged native/single-file checks failed' }
} finally { $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousBundleCache }
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$metadata.sha256=$hash
$metadata | ConvertTo-Json | Set-Content (Join-Path $release 'release.json') -Encoding utf8
"$hash  trayvora-win-x64.zip" | Set-Content (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Validated Trayvora $version from $sha ($hash)"

