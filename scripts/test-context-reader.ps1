param(
    [switch] $SkipLiveCheck
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$assemblyPath = Join-Path $dist "CodexEcamMonitor.exe"
$fixture = Join-Path $dist "context-reader-test"

if (-not (Test-Path $assemblyPath)) {
    $buildScript = Join-Path $PSScriptRoot "build.cmd"
    $commandProcessor = Join-Path $env:SystemRoot "System32\cmd.exe"
    $buildProcess = Start-Process -FilePath $commandProcessor -ArgumentList @("/d", "/c", "`"$buildScript`"") -Wait -PassThru -NoNewWindow
    if ($buildProcess.ExitCode -ne 0) { throw "Build failed with exit code $($buildProcess.ExitCode)." }
}

$assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
$readerType = $assembly.GetType("CodexEcamMonitor.LocalTokenReader", $true)
$resolverType = $assembly.GetType("CodexEcamMonitor.CodexPathResolver", $true)
$fromRoots = $readerType.GetMethod(
    "TryReadFromRoots",
    [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic)
$liveRead = $readerType.GetMethod(
    "TryRead",
    [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::Public)
$resolveHome = $resolverType.GetMethod(
    "ResolveHome",
    [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::Public)

function Write-Session([string] $path, [string] $id, [long] $tokens, [long] $window, [bool] $subagent) {
    New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
    $threadSource = if ($subagent) { "subagent" } else { "user" }
    $source = if ($subagent) { ',"source":{"subagent":{"other":"test"}}' } else { '' }
    $metadata = '{"type":"session_meta","payload":{"id":"' + $id + '","thread_source":"' + $threadSource + '"' + $source + '}}'
    $token = '{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"total_tokens":' + $tokens + '},"model_context_window":' + $window + '}}}'
    [IO.File]::WriteAllLines($path, @($metadata, $token), [Text.UTF8Encoding]::new($false))
}

function Invoke-FromRoots([string] $codexHome, [string] $localAppData) {
    $arguments = [object[]] @($codexHome, $localAppData, $null)
    $success = [bool] $fromRoots.Invoke($null, $arguments)
    if (-not $success) { throw "Context reader returned no data." }
    return $arguments[2]
}

function Assert-Equal($actual, $expected, [string] $label) {
    if ($actual -ne $expected) { throw "$label expected '$expected', got '$actual'." }
}

try {
    $focusedId = "11111111-1111-7111-8111-111111111111"
    $otherId = "22222222-2222-7222-8222-222222222222"
    $logLayouts = @(
        "Codex\Logs",
        "OpenAI\Codex\Logs",
        "Packages\OpenAI.Codex_testpackage\LocalCache\Local\Codex\Logs"
    )

    foreach ($layout in $logLayouts) {
        if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
        $codexHome = Join-Path $fixture "home"
        $local = Join-Path $fixture "local"
        $focused = Join-Path $codexHome "archived_sessions\rollout-old-$focusedId.jsonl"
        $other = Join-Path $codexHome "sessions\2099\01\01\rollout-new-$otherId.jsonl"
        Write-Session $focused $focusedId 27141 258400 $false
        Write-Session $other $otherId 99999 258400 $true
        [IO.File]::SetLastWriteTimeUtc($focused, [DateTime]::UtcNow.AddDays(-30))
        [IO.File]::SetLastWriteTimeUtc($other, [DateTime]::UtcNow)

        $log = Join-Path (Join-Path $local $layout) "codex-desktop-test.log"
        New-Item -ItemType Directory -Path (Split-Path $log) -Force | Out-Null
        [IO.File]::WriteAllText(
            $log,
            "2099-01-01T00:00:00.000Z info thread_stream_view_activity_changed active=true conversationId=$focusedId rendererWindowFocused=true",
            [Text.UTF8Encoding]::new($false))

        $usage = Invoke-FromRoots $codexHome $local
        Assert-Equal $usage.ConversationId $focusedId "focused conversation"
        Assert-Equal $usage.Tokens 27141 "focused tokens"
        Assert-Equal $usage.Window 258400 "context window"
        Assert-Equal ([Math]::Round($usage.Tokens / $usage.Window * 100, 1)) 10.5 "context percent"
        Write-Host "PASS focused archived session via $layout"
    }

    if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
    $codexHome = Join-Path $fixture "home"
    $local = Join-Path $fixture "local"
    $mainId = "33333333-3333-7333-8333-333333333333"
    $subagentId = "44444444-4444-7444-8444-444444444444"
    $main = Join-Path $codexHome "sessions\2020\01\01\rollout-main-$mainId.jsonl"
    $subagent = Join-Path $codexHome "sessions\2099\01\01\rollout-subagent-$subagentId.jsonl"
    Write-Session $main $mainId 12345 100000 $false
    Write-Session $subagent $subagentId 88888 100000 $true
    [IO.File]::SetLastWriteTimeUtc($main, [DateTime]::UtcNow.AddMinutes(-1))
    [IO.File]::SetLastWriteTimeUtc($subagent, [DateTime]::UtcNow)
    $missingId = "55555555-5555-7555-8555-555555555555"
    $staleLog = Join-Path $local "Codex\Logs\codex-desktop-test.log"
    New-Item -ItemType Directory -Path (Split-Path $staleLog) -Force | Out-Null
    [IO.File]::WriteAllText(
        $staleLog,
        "2099-01-01T00:00:00.000Z info thread_stream_view_activity_changed active=true conversationId=$missingId",
        [Text.UTF8Encoding]::new($false))
    $fallback = Invoke-FromRoots $codexHome $local
    Assert-Equal $fallback.ConversationId $mainId "fallback main conversation"
    Assert-Equal $fallback.Tokens 12345 "fallback main tokens"
    Write-Host "PASS fallback excludes newer subagent session"

    $originalHome = $env:CODEX_HOME
    try {
        $override = Join-Path $fixture "custom-codex-home"
        $env:CODEX_HOME = $override
        Assert-Equal ($resolveHome.Invoke($null, $null)) ([IO.Path]::GetFullPath($override)) "CODEX_HOME override"
        Write-Host "PASS CODEX_HOME override"
    }
    finally {
        $env:CODEX_HOME = $originalHome
    }

    if (-not $SkipLiveCheck) {
        $arguments = [object[]] @($null)
        if (-not [bool] $liveRead.Invoke($null, $arguments)) { throw "Live context check returned no data." }
        $usage = $arguments[0]
        $percent = [Math]::Round($usage.Tokens / $usage.Window * 100, 1)
        Write-Host "PASS live focused task $($usage.ConversationId): $($usage.Tokens) / $($usage.Window) = $percent%"
    }
}
finally {
    if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
}
