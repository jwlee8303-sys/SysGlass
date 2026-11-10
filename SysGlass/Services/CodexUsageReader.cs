using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using SysGlass.Models;

namespace SysGlass.Services;

internal sealed class CodexUsageReader
{
    private readonly Func<ProcessStartInfo?> _startInfo;

    public CodexUsageReader(Func<ProcessStartInfo?>? startInfo = null) => _startInfo = startInfo ?? FindCli;

    public static bool IsAvailable => FindCli() is not null;

    public Task<AiUsageSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        RunAsync(async (process, token) =>
        {
            await SendAsync(process, new { id = 2, method = "account/rateLimits/read" }, token);
            return AiUsageParser.Codex(await ReadResultAsync(process, 2, token), DateTimeOffset.UtcNow);
        }, TimeSpan.FromSeconds(20), cancellationToken);

    public Task<AiUsageSnapshot> ConnectAsync(Action<Uri> openBrowser, CancellationToken cancellationToken) =>
        RunAsync(async (process, token) =>
        {
            await SendAsync(process, new { id = 2, method = "account/read", @params = new { refreshToken = true } }, token);
            JsonElement account = default;
            // An expired managed session can reject the refresh; offer a fresh browser login.
            try { account = await ReadResultAsync(process, 2, token); }
            catch (InvalidOperationException) { }
            bool signedIn = account.ValueKind == JsonValueKind.Object && account.TryGetProperty("account", out var details) && details.ValueKind == JsonValueKind.Object &&
                details.TryGetProperty("type", out var type) && type.GetString() == "chatgpt";
            if (!signedIn)
            {
                await SendAsync(process, new { id = 3, method = "account/login/start", @params = new { type = "chatgpt" } }, token);
                var login = await ReadResultAsync(process, 3, token);
                var uri = new Uri(login.GetProperty("authUrl").GetString()!);
                // Only open an official HTTPS login URL, never arbitrary CLI-provided schemes.
                if (uri.Scheme != Uri.UriSchemeHttps ||
                    !(uri.Host == "auth.openai.com" || uri.Host == "chatgpt.com" || uri.Host == "auth0.openai.com"))
                    throw new InvalidOperationException("Unexpected login URL.");
                openBrowser(uri);
                string? loginId = login.GetProperty("loginId").GetString();
                while (await process.StandardOutput.ReadLineAsync(token) is { } line)
                {
                    using var document = JsonDocument.Parse(line);
                    var notification = document.RootElement;
                    if (!notification.TryGetProperty("method", out var method) || method.GetString() != "account/login/completed") continue;
                    var data = notification.GetProperty("params");
                    if (data.GetProperty("loginId").GetString() != loginId) continue;
                    if (!data.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Login failed.");
                    await SendAsync(process, new { id = 4, method = "account/rateLimits/read" }, token);
                    return AiUsageParser.Codex(await ReadResultAsync(process, 4, token), DateTimeOffset.UtcNow);
                }
                throw new IOException("Login server disconnected.");
            }
            await SendAsync(process, new { id = 4, method = "account/rateLimits/read" }, token);
            return AiUsageParser.Codex(await ReadResultAsync(process, 4, token), DateTimeOffset.UtcNow);
        }, TimeSpan.FromMinutes(5), cancellationToken);

    private async Task<AiUsageSnapshot> RunAsync(Func<Process, CancellationToken, Task<AiUsageSnapshot>> operation,
        TimeSpan duration, CancellationToken cancellationToken)
    {
        var info = _startInfo() ?? throw new FileNotFoundException("Codex CLI not found.");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.StandardInputEncoding = new UTF8Encoding(false);
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration);
        process.Start();
        // Drain stderr without keeping CLI diagnostics (which can contain account details).
        var drain = DrainAsync(process.StandardError);
        try
        {
            await SendAsync(process, new { id = 1, method = "initialize", @params = new
            {
                clientInfo = new { name = "sysglass", title = "SysGlass", version = typeof(CodexUsageReader).Assembly.GetName().Version?.ToString() ?? "unknown" }
            } }, timeout.Token);
            await ReadResultAsync(process, 1, timeout.Token);
            await SendAsync(process, new { method = "initialized" }, timeout.Token);
            return await operation(process, timeout.Token);
        }
        finally
        {
            // Also terminate the child node/native process when using an npm command shim.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await drain;
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        char[] buffer = new char[1024];
        while (await reader.ReadAsync(buffer) > 0) { }
    }

    private static Task SendAsync(Process process, object message, CancellationToken token) =>
        process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);

    private static async Task<JsonElement> ReadResultAsync(Process process, int id, CancellationToken token)
    {
        while (await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number ||
                !responseId.TryGetInt32(out int value) || value != id) continue;
            if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("Codex usage request rejected.");
            if (root.TryGetProperty("result", out var result)) return result.Clone();
            throw new JsonException("Missing RPC result.");
        }
        throw new IOException("Codex app-server disconnected.");
    }

    private static ProcessStartInfo? FindCli()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(p => p.Trim().Trim('"'))
            .Concat([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")]);
        foreach (string directory in directories.Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string name in new[] { "codex.exe", "codex.cmd" })
            {
                string path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return new(path, "app-server");
                // cmd expands percent signs even inside quotes; reject such shim paths.
                if (path.Contains('%') || path.Contains('"')) continue;
                return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                    $"/d /s /c \"\"{path}\" app-server\"");
            }
        }
        return null;
    }
}
