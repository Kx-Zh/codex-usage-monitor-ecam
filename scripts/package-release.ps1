param(
    [string] $Version = "1.0.1"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$release = Join-Path $root "release"
$stage = Join-Path $dist "package"
$archiveName = "CodexEcamMonitor-v$Version-win-x64.zip"
$archive = Join-Path $release $archiveName
$checksum = "$archive.sha256"

$buildScript = Join-Path $PSScriptRoot "build.cmd"
$commandProcessor = Join-Path $env:SystemRoot "System32\cmd.exe"
$buildProcess = Start-Process -FilePath $commandProcessor -ArgumentList @("/d", "/c", "`"$buildScript`"") -Wait -PassThru -NoNewWindow
if ($buildProcess.ExitCode -ne 0) { throw "Build failed with exit code $($buildProcess.ExitCode)." }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $stage "assets") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage "docs\images") -Force | Out-Null
New-Item -ItemType Directory -Path $release -Force | Out-Null

Copy-Item (Join-Path $dist "CodexEcamMonitor.exe") $stage
Copy-Item (Join-Path $dist "assets\ECAMFontRegular.ttf") (Join-Path $stage "assets")
Copy-Item (Join-Path $root "Start Codex ECAM Monitor.bat") $stage
Copy-Item (Join-Path $root "README.md") $stage
Copy-Item (Join-Path $root "README.zh-CN.md") $stage
Copy-Item (Join-Path $root "LICENSE") $stage
Copy-Item (Join-Path $root "NOTICE.md") $stage
Copy-Item (Join-Path $root "docs\images\main-window.png") (Join-Path $stage "docs\images")
Copy-Item (Join-Path $root "docs\images\tray-icon.png") (Join-Path $stage "docs\images")

$required = @(
    "CodexEcamMonitor.exe",
    "assets\ECAMFontRegular.ttf",
    "Start Codex ECAM Monitor.bat",
    "README.md",
    "README.zh-CN.md",
    "LICENSE",
    "NOTICE.md",
    "docs\images\main-window.png",
    "docs\images\tray-icon.png"
)
foreach ($relativePath in $required) {
    if (-not (Test-Path (Join-Path $stage $relativePath))) {
        throw "Release package is missing $relativePath."
    }
}

$forbidden = Get-ChildItem $stage -Recurse -File | Where-Object {
    $_.Name -ieq "codex.exe" -or $_.Name -ieq "auth.json" -or
    $_.Name -ieq ".credentials.json" -or $_.Extension -ieq ".jsonl"
}
if ($forbidden) { throw "Release package contains forbidden private or upstream files." }

$oversized = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Length -gt 100MB }
if ($oversized) { throw "Release package contains a file larger than 100 MB." }

if (Test-Path $archive) { Remove-Item $archive -Force }
if (Test-Path $checksum) { Remove-Item $checksum -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $archive -CompressionLevel Optimal

$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $checksum -Value "$hash  $archiveName" -Encoding Ascii

Write-Host "Created $archive"
Write-Host "Created $checksum"
