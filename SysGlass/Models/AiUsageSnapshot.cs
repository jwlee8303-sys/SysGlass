using System.Text.Json;

namespace SysGlass.Models;

public sealed record UsageWindow(string Label, double UsedPercent, DateTimeOffset? ResetsAt);

public sealed record AiUsageSnapshot(IReadOnlyList<UsageWindow> Windows, DateTimeOffset CollectedAt, string? Notice = null);

internal static class AiUsageParser
{
    public static AiUsageSnapshot Codex(JsonElement result, DateTimeOffset now)
    {
        JsonElement limits = default;
        // Never substitute another product's bucket for the Codex quota.
        if (Object(result, "rateLimitsByLimitId", out var buckets) && Object(buckets, "codex", out var codex))
            limits = codex;
        else if (Object(result, "rateLimits", out var legacy) &&
                 (!legacy.TryGetProperty("limitId", out var id) || id.ValueKind == JsonValueKind.Null || id.GetString() == "codex"))
            limits = legacy;

        List<UsageWindow> windows = [];
        Add(limits, "primary", "usedPercent", "resetsAt", "기본 한도", windows, true);
        Add(limits, "secondary", "usedPercent", "resetsAt", "추가 한도", windows, true);
        return new(windows, now, windows.Count == 0 ? "사용 한도 정보 없음 · ChatGPT 로그인 확인" : null);
    }

    public static AiUsageSnapshot Claude(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("collected_at", out var stamp) ||
            stamp.ValueKind != JsonValueKind.String || !stamp.TryGetDateTimeOffset(out var collected))
            throw new JsonException("Missing collection timestamp.");
        List<UsageWindow> windows = [];
        if (Object(root, "rate_limits", out var limits))
        {
            Add(limits, "five_hour", "used_percentage", "resets_at", "5시간", windows);
            Add(limits, "seven_day", "used_percentage", "resets_at", "7일", windows);
            Add(limits, "spend_limit", "used_percentage", "resets_at", "지출 한도", windows);
        }
        return new(windows, collected, windows.Count == 0 ? "한도 정보 없음 · 구독 및 첫 응답 확인" : null);
    }

    private static bool Object(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }

    private static void Add(JsonElement root, string name, string percentName, string resetName,
        string label, List<UsageWindow> windows, bool durationLabel = false)
    {
        if (!Object(root, name, out var window) || !window.TryGetProperty(percentName, out var percent) ||
            percent.ValueKind != JsonValueKind.Number || !percent.TryGetDouble(out var used) ||
            !double.IsFinite(used) || used < 0 || used > 100) return;

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty(resetName, out var reset) && reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var seconds))
        {
            try { resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { }
        }
        if (durationLabel && window.TryGetProperty("windowDurationMins", out var duration) &&
            duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out int minutes) && minutes > 0)
            label = minutes % 1440 == 0 ? $"{minutes / 1440}일" : minutes % 60 == 0 ? $"{minutes / 60}시간" : $"{minutes}분";
        windows.Add(new(label, used, resetsAt));
    }
}
