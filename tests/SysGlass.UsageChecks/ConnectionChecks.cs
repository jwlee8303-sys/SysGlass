using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SysGlass.Services;

internal static class ConnectionChecks
{
    public static async Task RunAsync(string scratch, Action<bool, string> check)
    {
        string root = Path.Combine(scratch, "연결 ' & $ test");
        string settingsPath = Path.Combine(root, "claude", "settings.json");
        string dataPath = Path.Combine(root, "SysGlass");
        string script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../scripts/claude-statusline.ps1"));
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var connection = new ClaudeConnectionService(settingsPath, dataPath, script, () => shell);
        check(!connection.IsConnected(), "Claude starts disconnected");
        connection.Connect();
        check(connection.IsConnected(), "new settings installed");
        connection.Disconnect();
        check(!connection.IsConnected() && Read(settingsPath)["statusLine"] is null, "disconnect restores absent statusLine");

        var originalStatus = new JsonObject
        {
            ["type"] = "command", ["padding"] = 2,
            ["command"] = "[Console]::Write('original:' + ([Console]::In.ReadToEnd() | ConvertFrom-Json).model.display_name)"
        };
        var settings = new JsonObject { ["statusLine"] = originalStatus.DeepClone(), ["unrelated"] = new JsonObject { ["value"] = "preserve" } };
        await File.WriteAllTextAsync(settingsPath, settings.ToJsonString(), Encoding.UTF8);
        connection.Connect();
        string firstInstall = await File.ReadAllTextAsync(settingsPath, Encoding.UTF8);
        connection.Connect();
        check(await File.ReadAllTextAsync(settingsPath, Encoding.UTF8) == firstInstall, "repeat connect does not wrap recursively");
        check(Read(settingsPath)["statusLine"]!["padding"]!.GetValue<int>() == 2, "preserve status line presentation options");
        string command = Read(settingsPath)["statusLine"]!["command"]!.GetValue<string>();
        var info = new ProcessStartInfo(shell)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string arg in command.Split(' ').Skip(1)) info.ArgumentList.Add(arg);
        using (var process = Process.Start(info)!)
        {
            await process.StandardInput.WriteAsync("""{"model":{"display_name":"한글모델"},"rate_limits":{"five_hour":{"used_percentage":35}}}""");
            process.StandardInput.Close();
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            check(process.ExitCode == 0 && stdout == "original:한글모델" && stderr.Length == 0, "existing statusLine gets exact stdin and keeps Unicode stdout: " + JsonSerializer.Serialize(new { stdout, stderr, process.ExitCode }));
        }
        check(File.Exists(Path.Combine(dataPath, "claude-usage.json")), "installed bridge records usage in its own data directory");
        var changed = Read(settingsPath);
        changed["laterEdit"] = true;
        changed["statusLine"]!["padding"] = 7;
        await File.WriteAllTextAsync(settingsPath, changed.ToJsonString(), Encoding.UTF8);
        connection.Disconnect();
        var restored = Read(settingsPath);
        check(restored["laterEdit"]!.GetValue<bool>() && restored["unrelated"]!["value"]!.GetValue<string>() == "preserve", "disconnect keeps unrelated edits");
        check(restored["statusLine"]!["command"]!.GetValue<string>() == originalStatus["command"]!.GetValue<string>() && restored["statusLine"]!["padding"]!.GetValue<int>() == 7, "restore original command while preserving later padding changes");
        check(!File.Exists(Path.Combine(dataPath, "claude-usage.json")), "disconnect removes old usage cache");

        connection.Connect();
        changed = Read(settingsPath);
        changed["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = "echo external" };
        await File.WriteAllTextAsync(settingsPath, changed.ToJsonString(), Encoding.UTF8);
        connection.Disconnect();
        check(Read(settingsPath)["statusLine"]!["command"]!.GetValue<string>() == "echo external", "external statusLine replacement is never overwritten on disconnect");

        await File.WriteAllTextAsync(settingsPath, "{broken", Encoding.UTF8);
        try { connection.Connect(); check(false, "invalid settings rejected"); }
        catch (JsonException) { check(await File.ReadAllTextAsync(settingsPath, Encoding.UTF8) == "{broken", "invalid settings left untouched"); }
        await File.WriteAllTextAsync(settingsPath, "// comment\n{\"unrelated\":1,}", Encoding.UTF8);
        connection.Connect();
        check(connection.IsConnected() && Read(settingsPath)["unrelated"]!.GetValue<int>() == 1, "allow comments and trailing commas");
        connection.Disconnect();

        string bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (File.Exists(bash))
        {
            var bashConnection = new ClaudeConnectionService(settingsPath, dataPath, script, () => bash);
            settings = new JsonObject { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = "cat" } };
            await File.WriteAllTextAsync(settingsPath, settings.ToJsonString(), Encoding.UTF8);
            bashConnection.Connect();
            string bashCommand = Read(settingsPath)["statusLine"]!["command"]!.GetValue<string>();
            var bashInfo = new ProcessStartInfo(shell)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
            };
            foreach (string arg in bashCommand.Split(' ').Skip(1)) bashInfo.ArgumentList.Add(arg);
            using var process = Process.Start(bashInfo)!;
            const string raw = "{\"model\":{\"display_name\":\"한글\"}}";
            await process.StandardInput.WriteAsync(raw);
            process.StandardInput.Close();
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            check(process.ExitCode == 0 && stdout == raw && stderr.Length == 0, "Git Bash statusLine preserves raw stdin and stdout");
            bashConnection.Disconnect();
        }
    }

    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!.AsObject();
}
