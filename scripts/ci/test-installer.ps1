param([Parameter(Mandatory)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Run the installer smoke test only on a GitHub-hosted runner.'
}

function Invoke-SmokeProcess([string]$File, [string]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "$File exited with code $($process.ExitCode)" }
}

function Get-StartupCommand {
    [Microsoft.Win32.Registry]::GetValue("HKEY_CURRENT_USER\$startupKey", $startupName, $null)
}

$package = Join-Path $OutputRoot 'package'
$results = Join-Path $OutputRoot 'results'
$installer = Join-Path $OutputRoot 'release/trayvora-win-x64-setup.exe'
$app = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Trayvora'
$exe = Join-Path $app 'Trayvora.exe'
$shortcuts = @(
    (Join-Path ([Environment]::GetFolderPath('Programs', 'DoNotVerify')) 'Trayvora.lnk')
    (Join-Path ([Environment]::GetFolderPath('DesktopDirectory', 'DoNotVerify')) 'Trayvora.lnk')
)
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{97185E8B-5C64-4FBA-86A5-02D8F27B6B5D}_is1'
$userUninstall = "HKCU:\$uninstallKey"
$machineUninstall = "HKLM:\$uninstallKey"
$startupKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$startupName = 'Trayvora.VoiceAgent'
$quiet = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
if ((Test-Path -LiteralPath (@($app, $userUninstall, $machineUninstall) + $shortcuts)) -contains $true -or
    $null -ne (Get-StartupCommand)) {
    throw 'The smoke test requires a fresh runner profile.'
}

Invoke-SmokeProcess $installer "$quiet /TASKS=desktopicon /LOG=`"$results\installer-install.log`""
$files = @(Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object Name -ne 'START.txt')
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($package, $file.FullName)
    $installed = Join-Path $app $relative
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $installed).Hash) {
        throw "Installed payload mismatch: $relative"
    }
}
$shell = New-Object -ComObject WScript.Shell
foreach ($shortcut in $shortcuts) {
    if (!(Test-Path -LiteralPath $shortcut) -or $shell.CreateShortcut($shortcut).TargetPath -ne $exe) {
        throw "Missing or incorrect shortcut: $shortcut"
    }
}
if ((Test-Path -LiteralPath $machineUninstall) -or
    $null -ne (Get-StartupCommand)) {
    throw 'Setup should register only for the current user and leave startup opt-in.'
}
$uninstaller = (Get-ItemPropertyValue $userUninstall 'UninstallString').Trim('"')
if ((Split-Path -Parent $uninstaller) -ne $app) { throw 'Unexpected uninstall command' }

$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $OutputRoot 'bundle-cache'
$report = Join-Path $results 'installer-app-smoke.json'
Invoke-SmokeProcess $exe "--verify-package `"$report`""
$smoke = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (!$smoke.success -or !$smoke.singleFile) { throw 'Installed native/single-file checks failed' }

$data = foreach ($folder in 'settings', 'history', 'models', 'codex-data') {
    $directory = Join-Path $app $folder
    New-Item -Path $directory -ItemType Directory -Force | Out-Null
    $marker = Join-Path $directory 'installer-smoke.txt'
    Set-Content -LiteralPath $marker -Value $folder
    $marker
}
$startupCommand = "`"$exe`""
$startupPath = "HKCU:\$startupKey"
if (!(Test-Path -LiteralPath $startupPath)) { New-Item -Path $startupPath | Out-Null }
New-ItemProperty -LiteralPath $startupPath -Name $startupName -Value $startupCommand | Out-Null
Invoke-SmokeProcess $installer "$quiet /TASKS=desktopicon /LOG=`"$results\installer-reinstall.log`""
if ((Test-Path -LiteralPath $data) -contains $false -or
    (Get-StartupCommand) -cne $startupCommand) {
    throw 'Reinstall changed data or the startup preference.'
}

Invoke-SmokeProcess $uninstaller "$quiet /LOG=`"$results\installer-uninstall.log`""
$removed = @($uninstaller, $userUninstall) + $shortcuts
$removed += $files | ForEach-Object { Join-Path $app ([IO.Path]::GetRelativePath($package, $_.FullName)) }
if ((Test-Path -LiteralPath $removed) -contains $true -or
    $null -ne (Get-StartupCommand)) {
    throw 'Uninstall left installed files, shortcuts or registration.'
}
foreach ($marker in $data) {
    if ((Get-Content -LiteralPath $marker -Raw).Trim() -ne (Split-Path -Leaf (Split-Path -Parent $marker))) {
        throw "Uninstall changed user data: $marker"
    }
}
Write-Host 'Validated per-user install, packaged executable, shortcuts, reinstall and data-preserving uninstall.'
