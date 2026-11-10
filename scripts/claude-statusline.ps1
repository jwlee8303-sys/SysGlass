param(
    [string]$CachePath = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'SysGlass/claude-usage.json'),
    [string]$BridgePath = ''
)

# Forward only quota fields, never the transcript, prompt, or account credentials.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$tempPath = $null
$bridge = $null
$rawInput = ''
$display = 'Claude | usage unavailable'
try {
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $rawInput = [Console]::In.ReadToEnd()
    $payload = $rawInput | ConvertFrom-Json
    if ($BridgePath) { $bridge = [IO.File]::ReadAllText($BridgePath, [Text.Encoding]::UTF8) | ConvertFrom-Json }
    $limits = [ordered]@{}
    $parts = @()
    foreach ($entry in @(@('five_hour', '5h'), @('seven_day', '7d'), @('spend_limit', 'spend'))) {
        $window = $payload.rate_limits.($entry[0])
        if ($null -eq $window -or $null -eq $window.used_percentage -or $window.used_percentage -is [string] -or $window.used_percentage -is [bool]) { continue }
        $used = [double]$window.used_percentage
        if ([double]::IsNaN($used) -or [double]::IsInfinity($used) -or $used -lt 0 -or $used -gt 100) { continue }
        $filtered = [ordered]@{ used_percentage = $used }
        $seconds = 0L
        if ($null -ne $window.resets_at -and [long]::TryParse([string]$window.resets_at, [ref]$seconds)) {
            $filtered.resets_at = $seconds
        }
        $limits[$entry[0]] = $filtered
        $parts += ('{0}: {1:0.#}%' -f $entry[1], $used)
    }
    $cache = [ordered]@{
        collected_at = [DateTimeOffset]::UtcNow.ToString('o')
        rate_limits = $limits
    } | ConvertTo-Json -Depth 5 -Compress
    if (-not $BridgePath -or $bridge.enabled) {
        $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($CachePath))
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $tempPath = Join-Path $directory ([IO.Path]::GetRandomFileName())
        [IO.File]::WriteAllText($tempPath, $cache, [Text.UTF8Encoding]::new($false))
        # Replace atomically so SysGlass never reads a partially written document.
        if ([IO.File]::Exists($CachePath)) { [IO.File]::Replace($tempPath, $CachePath, [NullString]::Value) }
        else { [IO.File]::Move($tempPath, $CachePath) }
    }
    if ($parts.Count -gt 0) { $display = 'Claude | ' + ($parts -join ' | ') }
}
catch {
    # A status line failure must not interrupt the Claude Code session.
    $display = 'Claude | usage unavailable'
}
finally {
    if ($tempPath -and [IO.File]::Exists($tempPath)) { [IO.File]::Delete($tempPath) }
}

# Preserve the old status line's shell, stdin and stdout. Never print bridge diagnostics.
if ($bridge -and $bridge.previousShell -and $bridge.previousScript) {
    $previous = [Diagnostics.Process]::new()
    try {
        $previous.StartInfo.FileName = $bridge.previousShell
        $scriptPath = $bridge.previousScript.Replace('\', '/')
        if ([IO.Path]::GetFileName($bridge.previousShell) -eq 'bash.exe') {
            $previous.StartInfo.Arguments = '--noprofile --norc "' + $scriptPath + '"'
        }
        else {
            $previous.StartInfo.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $scriptPath + '"'
        }
        $previous.StartInfo.UseShellExecute = $false
        $previous.StartInfo.CreateNoWindow = $true
        $previous.StartInfo.RedirectStandardInput = $true
        $previous.StartInfo.RedirectStandardOutput = $true
        $previous.StartInfo.RedirectStandardError = $true
        $previous.StartInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
        [void]$previous.Start()
        $outputTask = $previous.StandardOutput.ReadToEndAsync()
        $errorTask = $previous.StandardError.ReadToEndAsync()
        $inputBytes = [Text.Encoding]::UTF8.GetBytes($rawInput)
        $writeTask = $previous.StandardInput.BaseStream.WriteAsync($inputBytes, 0, $inputBytes.Length)
        if (-not $writeTask.Wait(5000)) { throw 'Status line input timeout' }
        $previous.StandardInput.Close()
        if (-not $previous.WaitForExit(5000)) { throw 'Status line timeout' }
        [Console]::Write($outputTask.GetAwaiter().GetResult())
    }
    catch { [Console]::WriteLine($display) }
    finally {
        try { if (-not $previous.HasExited) { $previous.Kill() } } catch { }
        $previous.Dispose()
    }
}
elseif (-not $BridgePath -or ($bridge -and $bridge.enabled)) { [Console]::WriteLine($display) }
