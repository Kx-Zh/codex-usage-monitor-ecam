param(
    [Parameter(Mandatory = $true)] [string] $CodexExe
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$assemblyPath = Join-Path $dist "CodexEcamMonitor.exe"
$codexPath = (Resolve-Path $CodexExe).Path

if (-not (Test-Path $assemblyPath)) {
    & (Join-Path $PSScriptRoot "build.cmd")
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}

$assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
$clientType = $assembly.GetType("CodexEcamMonitor.AppServerClient", $true)
$queryMethod = $clientType.GetMethod("QueryUsage")
$disposeMethod = $clientType.GetMethod("Dispose")
$originalOverride = $env:CODEX_CLI_PATH
$originalPath = $env:PATH
$testRoot = Join-Path $dist "cli-discovery-test"

function Invoke-UsageQuery([string] $applicationRoot) {
    $client = [Activator]::CreateInstance(
        $clientType,
        [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic,
        $null,
        [object[]] @($applicationRoot),
        $null)
    try {
        return $queryMethod.Invoke($client, [object[]] @(30000))
    }
    finally {
        $null = $disposeMethod.Invoke($client, $null)
    }
}

function Assert-LiveSnapshot($snapshot, [string] $caseName) {
    if ($null -eq $snapshot -or $snapshot.UpdatedAt -eq [DateTime]::MinValue) {
        throw "$caseName did not return a live usage snapshot."
    }
    Write-Host "PASS $caseName ($($snapshot.Remaining)% remaining)"
}

try {
    $env:CODEX_CLI_PATH = $codexPath
    Assert-LiveSnapshot (Invoke-UsageQuery $dist) "CODEX_CLI_PATH override"

    $env:CODEX_CLI_PATH = $null
    Assert-LiveSnapshot (Invoke-UsageQuery (Split-Path $codexPath)) "sibling codex.exe"

    if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $wrapper = Join-Path $testRoot "codex.cmd"
    Set-Content -Path $wrapper -Encoding Ascii -Value @(
        "@echo off",
        "`"$codexPath`" %*"
    )
    $env:PATH = "$testRoot;$originalPath"
    Assert-LiveSnapshot (Invoke-UsageQuery $dist) "PATH codex.cmd wrapper"

    $env:PATH = "$env:SystemRoot\System32"
    try {
        $null = Invoke-UsageQuery $dist
        throw "Missing-CLI case unexpectedly succeeded."
    }
    catch {
        $message = $_.Exception.ToString()
        if ($message -notmatch "CODEX CLI NOT FOUND") { throw }
        Write-Host "PASS missing CLI returns an actionable error"
    }
}
finally {
    $env:CODEX_CLI_PATH = $originalOverride
    $env:PATH = $originalPath
    if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force }
}
