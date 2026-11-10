using System.IO;
using System.Text.Json;
using System.Text;
using SysGlass.Models;
using SysGlass.Utilities;

namespace SysGlass.Services;

internal static class ClaudeUsageReader
{
    public static string CachePath => Path.Combine(AppPaths.DataDir, "claude-usage.json");

    public static string MissingCacheNotice
    {
        get
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppPaths.DataDir, "claude-connection.json"), Encoding.UTF8));
                if (document.RootElement.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)
                    return "연결됨 · Claude Code를 새로 열고 응답을 기다려주세요";
            }
            catch { }
            return "설정에서 ‘Claude Code 연결’을 눌러주세요";
        }
    }

    public static async Task<AiUsageSnapshot> ReadAsync(CancellationToken token)
    {
        await using var stream = new FileStream(CachePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        if (stream.Length > 64 * 1024) throw new JsonException("Usage cache is too large.");
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return AiUsageParser.Claude(document.RootElement);
    }
}
