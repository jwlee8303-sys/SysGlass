using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SysGlass.Utilities;

namespace SysGlass.Services;

internal sealed class ClaudeConnectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly string _dataDirectory;
    private readonly string _sourceScript;
    private readonly Func<string> _shell;
    private string StatePath => Path.Combine(_dataDirectory, "claude-connection.json");

    public ClaudeConnectionService(string? settingsPath = null, string? dataDirectory = null,
        string? sourceScript = null, Func<string>? shell = null)
    {
        string? configuredDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string configDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : configuredDirectory;
        _settingsPath = settingsPath ?? Path.Combine(configDirectory, "settings.json");
        _dataDirectory = dataDirectory ?? AppPaths.DataDir;
        _sourceScript = sourceScript ?? Path.Combine(AppContext.BaseDirectory, "scripts", "claude-statusline.ps1");
        _shell = shell ?? FindStatusLineShell;
    }

    public static bool IsCliAvailable => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"))
        .Select(p => p.Trim().Trim('"')).Where(Path.IsPathFullyQualified)
        .Any(p => File.Exists(Path.Combine(p, "claude.exe")) || File.Exists(Path.Combine(p, "claude.cmd")));

    public bool IsConnected()
    {
        var state = ReadObject(StatePath);
        return state?["enabled"]?.GetValue<bool>() == true &&
            Command(ReadObject(_settingsPath)?["statusLine"]) == state["installedCommand"]?.GetValue<string>();
    }

    // Called on a worker thread: only the ordinary settings file and our own bridge files are touched.
    public void Connect()
    {
        string script = File.ReadAllText(_sourceScript, Encoding.UTF8);
        string? originalText = ReadText(_settingsPath);
        var settings = ParseObject(originalText) ?? new JsonObject();
        var state = ReadObject(StatePath);
        bool alreadyInstalled = state?["enabled"]?.GetValue<bool>() == true &&
            Command(settings["statusLine"]) == state["installedCommand"]?.GetValue<string>();
        if (alreadyInstalled)
        {
            WriteAtomic(Path.Combine(_dataDirectory, "claude-statusline.ps1"), script);
            return;
        }

        // An external edit becomes the new original; never restore an outdated full settings file.
        JsonNode? previous = settings["statusLine"]?.DeepClone();
        string? previousCommand = Command(previous);
        if (previous is not null && previousCommand is null)
            throw new InvalidOperationException("Unsupported statusLine setting.");
        string shell = _shell();
        string previousScript = Path.Combine(_dataDirectory,
            Path.GetFileName(shell).Equals("bash.exe", StringComparison.OrdinalIgnoreCase) ? "claude-previous-statusline.sh" : "claude-previous-statusline.ps1");
        string installedScript = Path.Combine(_dataDirectory, "claude-statusline.ps1");
        string invocation = $"& '{Literal(installedScript)}' -BridgePath '{Literal(StatePath)}' -CachePath '{Literal(Path.Combine(_dataDirectory, "claude-usage.json"))}'";
        string command = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(invocation));
        if (previousCommand == command)
            throw new InvalidOperationException("The original statusLine backup is missing. Refusing a recursive wrapper.");
        var installed = previous?.DeepClone() as JsonObject ?? new JsonObject();
        installed["type"] = "command";
        installed["command"] = command;
        settings["statusLine"] = installed;
        var newState = new JsonObject
        {
            ["enabled"] = true,
            ["hadStatusLine"] = ParseObject(originalText)?.ContainsKey("statusLine") == true,
            ["previousStatusLine"] = previous,
            ["installedCommand"] = command,
            ["previousShell"] = previousCommand is null ? null : shell,
            ["previousScript"] = previousCommand is null ? null : previousScript
        };
        Directory.CreateDirectory(_dataDirectory);
        WriteAtomic(installedScript, script);
        if (previousCommand is not null)
        {
            string prelude = Path.GetExtension(previousScript) == ".ps1"
                ? "[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)\n[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\n"
                : "";
            WriteAtomic(previousScript, prelude + previousCommand + "\n");
        }
        string? oldState = ReadText(StatePath);
        WriteAtomic(StatePath, newState.ToJsonString(JsonOptions));
        try { WriteSettings(settings, originalText); }
        catch
        {
            if (oldState is not null) WriteAtomic(StatePath, oldState);
            else File.Delete(StatePath);
            throw;
        }
    }

    public void Disconnect()
    {
        var state = ReadObject(StatePath);
        if (state is null || state["enabled"]?.GetValue<bool>() != true) return;
        string? originalText = ReadText(_settingsPath);
        var settings = ParseObject(originalText) ?? new JsonObject();
        if (Command(settings["statusLine"]) == state["installedCommand"]?.GetValue<string>())
        {
            if (state["hadStatusLine"]?.GetValue<bool>() == true)
            {
                var previous = state["previousStatusLine"]?.DeepClone();
                // Keep user edits to presentation options made after connecting.
                if (previous is JsonObject restored && settings["statusLine"] is JsonObject current)
                    foreach (var pair in current.Where(p => p.Key is not ("type" or "command")))
                        restored[pair.Key] = pair.Value?.DeepClone();
                settings["statusLine"] = previous;
            }
            else settings.Remove("statusLine");
            WriteSettings(settings, originalText);
        }
        // Keep the original command usable by sessions that have not reloaded their settings yet.
        state["enabled"] = false;
        WriteAtomic(StatePath, state.ToJsonString(JsonOptions));
        File.Delete(Path.Combine(_dataDirectory, "claude-usage.json"));
    }

    private void WriteSettings(JsonObject settings, string? expected)
    {
        if (ReadText(_settingsPath) != expected)
            throw new IOException("Settings changed during connection. Please retry.");
        WriteAtomic(_settingsPath, settings.ToJsonString(JsonOptions));
    }

    private static string Literal(string value) => value.Replace("'", "''");
    private static string? Command(JsonNode? setting) => setting is JsonObject obj &&
        obj["command"] is JsonValue command && command.TryGetValue<string>(out var text) ? text : null;
    private static string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
    private static JsonObject? ReadObject(string path) => ParseObject(ReadText(path));
    private static JsonObject? ParseObject(string? text) => text is null ? null :
        JsonNode.Parse(text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
        ?? throw new JsonException("Expected a settings object.");

    private static void WriteAtomic(string path, string content)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static string FindStatusLineShell()
    {
        string? configured = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(p => p.Trim('"'));
        foreach (string path in paths)
        {
            if (!File.Exists(Path.Combine(path, "git.exe"))) continue;
            foreach (string candidate in new[] { Path.Combine(path, "bash.exe"), Path.Combine(path, "..", "bin", "bash.exe") })
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
        {
            string candidate = Path.Combine(root, "Git", "bin", "bash.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    }
}
