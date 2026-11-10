using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SysGlass.Models;
using SysGlass.Services;
using SysGlass.ViewModels;

if (args.FirstOrDefault() == "--fake-codex")
{
    await FakeServer(args[1], args[2]);
    return;
}

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
}

JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
var now = DateTimeOffset.UtcNow;
var codex = AiUsageParser.Codex(Json("""
    {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":2000000000},
    "secondary":{"usedPercent":100,"windowDurationMins":10080}}},"rateLimits":{"primary":{"usedPercent":99}}}
    """), now);
Check(codex.Windows.Count == 2 && codex.Windows[0].UsedPercent == 25, "prefer Codex bucket");
Check(codex.Windows[0].Label == "5시간" && codex.Windows[1].Label == "7일", "server window duration");
Check(AiUsageParser.Codex(Json("""{"rateLimits":{"primary":{"usedPercent":0}}}"""), now).Windows[0].UsedPercent == 0, "zero is real data");
Check(AiUsageParser.Codex(Json("""{"rateLimits":{"limitId":"other","primary":{"usedPercent":35}}}"""), now).Windows.Count == 0, "do not display other product quota");
foreach (string value in new[] { "null", "-1", "101", "\"50\"", "true", "1e999" })
    Check(AiUsageParser.Codex(Json("{\"rateLimits\":{\"primary\":{\"usedPercent\":" + value + "}}}"), now).Windows.Count == 0, "invalid percent " + value);
var noReset = AiUsageParser.Codex(Json("""{"rateLimits":{"primary":{"usedPercent":50,"resetsAt":9223372036854775807,"windowDurationMins":null}}}"""), now);
Check(noReset.Windows[0].ResetsAt is null, "out of range reset and null duration");
var claude = AiUsageParser.Claude(Json("""
    {"collected_at":"2026-10-01T12:00:00Z","rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":2000000000},"seven_day":null}}
    """));
Check(claude.Windows.Count == 1 && claude.Windows[0].UsedPercent == 23.5, "optional Claude window");
Check(AiUsageParser.Claude(Json("""{"collected_at":"2026-10-01T12:00:00Z","rate_limits":null}""")).Notice is not null, "no quota notice");
foreach (string json in new[] { "{}", "[]", "{\"collected_at\":42}" })
{
    try { AiUsageParser.Claude(Json(json)); Check(false, "reject invalid stamp"); }
    catch (JsonException) { checks++; }
}
var vm = new AiUsageViewModel("Test");
vm.Update(new([new("5시간", 70, now.AddHours(1))], now.AddMinutes(-11)));
vm.Refresh(now);
Check(vm.Status.Contains("오래된") && vm.Windows[0].HasUsage, "stale status retains previous value");
vm.Fail("연결 실패");
Check(vm.Status == "연결 실패" && vm.Windows[0].UsedPercent == 70 && vm.Updated.Length > 0, "failure retains last successful snapshot");
vm.Update(new([new("5시간", 10, now.AddSeconds(-1))], now));
Check(vm.Status == "" && !vm.Windows[0].HasUsage && vm.Windows[0].UsageText == "갱신 대기", "expired data not shown as zero usage");
vm.Update(new([], now, "한도 없음"));
Check(vm.Windows.Count == 0 && vm.Status == "한도 없음", "missing windows remove previous values");

string scratch = Path.Combine(Path.GetTempPath(), "SysGlass-usage-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    foreach (string mode in new[] { "success", "error", "malformed", "eof", "hang" })
    {
        string pidPath = Path.Combine(scratch, mode + ".pid");
        var reader = new CodexUsageReader(() =>
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!);
            foreach (string arg in new[] { "--fake-codex", mode, pidPath }) info.ArgumentList.Add(arg);
            return info;
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(mode == "hang" ? 2 : 10));
        bool rejected = false;
        try
        {
            var snapshot = await reader.ReadAsync(timeout.Token);
            Check(mode == "success" && snapshot.Windows.Single().UsedPercent == 42, "RPC handshake and quota result");
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or IOException or OperationCanceledException)
        {
            rejected = true;
            Check(mode != "success", "RPC failure surfaced: " + mode);
            if (mode == "hang") Check(ex is OperationCanceledException, "cancel hanging server");
        }
        Check(mode == "success" || rejected, "non-success request rejected");
        if (File.Exists(pidPath))
        {
            int pid = int.Parse(await File.ReadAllTextAsync(pidPath, Encoding.UTF8), CultureInfo.InvariantCulture);
            bool exited;
            try { using var child = Process.GetProcessById(pid); exited = child.HasExited; }
            catch (ArgumentException) { exited = true; }
            Check(exited, "app-server cleaned up: " + mode);
        }
        else Check(false, "fake server started: " + mode);
    }
    try { await new CodexUsageReader(() => null).ReadAsync(CancellationToken.None); Check(false, "missing CLI"); }
    catch (FileNotFoundException) { checks++; }

    foreach (string mode in new[] { "login-existing", "login-new", "login-error", "login-cancel", "login-unsafe", "login-expired" })
    {
        var reader = new CodexUsageReader(() =>
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!);
            foreach (string arg in new[] { "--fake-codex", mode, Path.Combine(scratch, mode + ".pid") }) info.ArgumentList.Add(arg);
            return info;
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(mode == "login-cancel" ? 2 : 10));
        int opened = 0;
        try
        {
            var snapshot = await reader.ConnectAsync(uri =>
            {
                Check(uri.Scheme == "https" && uri.Host == "auth.openai.com", "official login URL");
                opened++;
            }, timeout.Token);
            Check(mode is "login-existing" or "login-new" or "login-expired", "only successful login returns quota");
            Check(snapshot.Windows.Single().UsedPercent == 42, "quota immediately after login");
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Check(mode is "login-error" or "login-cancel" or "login-unsafe", "login failure and cancellation surfaced");
        }
        Check(opened == (mode is "login-existing" or "login-unsafe" ? 0 : 1), "existing login reused and unsafe URL blocked");
    }

    await ConnectionChecks.RunAsync(scratch, Check);

    // Exercise real PATH discovery and cmd.exe quoting, including spaces and '&'.
    string shimDirectory = Path.Combine(scratch, "shim path & 한글");
    Directory.CreateDirectory(shimDirectory);
    string shimPid = Path.Combine(scratch, "shim.pid");
    await File.WriteAllTextAsync(Path.Combine(shimDirectory, "codex.cmd"),
        $"@echo off\r\n\"{Environment.ProcessPath}\" --fake-codex success \"{shimPid}\"\r\n", Encoding.ASCII);
    string? originalPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        Environment.SetEnvironmentVariable("PATH", shimDirectory);
        var shimResult = await new CodexUsageReader().ReadAsync(CancellationToken.None);
        Check(shimResult.Windows.Single().UsedPercent == 42, "npm shim discovery with spaces and Unicode");
        int pid = int.Parse(await File.ReadAllTextAsync(shimPid, Encoding.UTF8), CultureInfo.InvariantCulture);
        bool exited;
        try { using var child = Process.GetProcessById(pid); exited = child.HasExited; }
        catch (ArgumentException) { exited = true; }
        Check(exited, "npm shim child process cleaned up");
    }
    finally { Environment.SetEnvironmentVariable("PATH", originalPath); }

    string script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../scripts/claude-statusline.ps1"));
    string cachePath = Path.Combine(scratch, "한글 경로", "claude-usage.json");
    foreach (string input in new[]
    {
        """{"transcript_path":"PRIVATE","session_id":"PRIVATE","rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":2000000000,"secret":"PRIVATE"}}}""",
        """{"rate_limits":{"five_hour":{"used_percentage":0},"seven_day":{"used_percentage":null}}}""",
        "{}"
    })
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-CachePath", cachePath }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0 && stderr.Length == 0 && stdout.StartsWith("Claude |"), "statusLine runs on Windows PowerShell");
        string cache = await File.ReadAllTextAsync(cachePath, Encoding.UTF8);
        Check(!cache.Contains("PRIVATE") && !cache.Contains("session_id"), "only quota fields persisted");
        var parsed = AiUsageParser.Claude(Json(cache));
        Check(now.AddSeconds(-10) < parsed.CollectedAt && parsed.CollectedAt <= DateTimeOffset.UtcNow, "UTC cache timestamp");
        if (input == "{}") Check(parsed.Windows.Count == 0, "absent rate limits clears old cache");
        else Check(parsed.Windows.Single().UsedPercent == (input.Contains("23.5") ? 23.5 : 0), "script/parser roundtrip: " + cache);
        Check(Directory.GetFiles(Path.GetDirectoryName(cachePath)!).Length == 1, "atomic replacement leaves no temp files");
    }
}
finally { Directory.Delete(scratch, recursive: true); }

Console.WriteLine($"PASS: {checks} checks");

static async Task FakeServer(string mode, string pidPath)
{
    await File.WriteAllTextAsync(pidPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), Encoding.UTF8);
    Console.InputEncoding = Encoding.UTF8;
    Console.OutputEncoding = new UTF8Encoding(false);
    using var init = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
    if (init.RootElement.GetProperty("method").GetString() != "initialize") throw new Exception("Expected initialize");
    if (mode == "hang") { await Task.Delay(TimeSpan.FromMinutes(1)); return; }
    // Fill the pipe to verify concurrent stderr draining.
    await Console.Error.WriteAsync(new string('x', 128 * 1024));
    await Console.Out.WriteLineAsync("{\"method\":\"account/updated\",\"params\":{}}");
    await Console.Out.WriteLineAsync("{\"id\":1,\"result\":{}}");
    using var initialized = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
    if (initialized.RootElement.GetProperty("method").GetString() != "initialized") throw new Exception("Expected initialized");
    using var read = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
    if (mode.StartsWith("login-", StringComparison.Ordinal))
    {
        if (read.RootElement.GetProperty("method").GetString() != "account/read") throw new Exception("Expected account check");
        await Console.Out.WriteLineAsync(mode == "login-existing" ? """{"id":2,"result":{"account":{"type":"chatgpt"}}}""" :
            mode == "login-expired" ? """{"id":2,"error":{"message":"expired"}}""" : """{"id":2,"result":{"account":null}}""");
        if (mode != "login-existing")
        {
            using var login = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
            if (login.RootElement.GetProperty("method").GetString() != "account/login/start") throw new Exception("Expected browser login");
            string url = mode == "login-unsafe" ? "file:///C:/unexpected" : "https://auth.openai.com/authorize?test=1";
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = 3, result = new { loginId = "test-login", authUrl = url } }));
            if (mode is "login-cancel" or "login-unsafe") { await Task.Delay(TimeSpan.FromMinutes(1)); return; }
            await Console.Out.WriteLineAsync("""{"method":"account/login/completed","params":{"loginId":"unrelated","success":false}}""");
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { method = "account/login/completed", @params = new { loginId = "test-login", success = mode != "login-error" } }));
            if (mode == "login-error") { await Task.Delay(TimeSpan.FromMinutes(1)); return; }
        }
        using var quota = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
        if (quota.RootElement.GetProperty("method").GetString() != "account/rateLimits/read") throw new Exception("Expected quota after login");
        await Console.Out.WriteLineAsync("""{"id":4,"result":{"rateLimits":{"primary":{"usedPercent":42}}}}""");
        await Task.Delay(TimeSpan.FromMinutes(1));
        return;
    }
    if (read.RootElement.GetProperty("method").GetString() != "account/rateLimits/read") throw new Exception("Expected quota request");
    if (mode == "eof") return;
    await Console.Out.WriteLineAsync(mode switch
    {
        "success" => """{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":42}}}}""",
        "error" => """{"id":2,"error":{"message":"not logged in"}}""",
        _ => "invalid json"
    });
    await Task.Delay(TimeSpan.FromMinutes(1));
}
