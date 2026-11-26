using System.Globalization;
using SysGlass.Models;
using SysGlass.Settings;
using SysGlass.Utilities;

namespace SysGlass.Services.Memory;

/// <summary>수집 상태와 설정의 기준값으로 판정 항목 목록을 만든다.</summary>
public static class MemoryReadings
{
    private const double MB = 1024 * 1024;

    public static List<MetricReading> Build(MemoryWatchState s, AppSettings st)
    {
        var k = s.Kernel;
        var list = new List<MetricReading>
        {
            Pool("paged", "페이징 풀", k?.PagedPool, st.MemPagedWarnMb, st.MemPagedHighMb),
            Pool("nonpaged", "비페이징 풀", k?.NonPagedPool, st.MemNonPagedWarnMb, st.MemNonPagedHighMb),
        };

        // 증가량: 1시간 관측이 끝나야 판정한다(그 전에는 측정 불가).
        double? perHour = s.Growth1h.TotalPerHour is { } g ? g / MB : null;
        list.Add(new MetricReading("growth", "시간당 풀 증가량", perHour,
            new MetricThreshold(st.MemGrowthWarnMbPerHour, st.MemGrowthHighMbPerHour),
            perHour is { } ph ? Format.SignedBytes(ph * MB) + "/h" : "미측정",
            $"주의 {st.MemGrowthWarnMbPerHour} MB/h · 위험 {st.MemGrowthHighMbPerHour} MB/h", IsTrend: true));

        // 개별 Pool Tag: 가장 큰 태그 기준. 수집에 실패하면 0이 아니라 측정 불가.
        var top = s.PoolTags is { Available: true } pt ? pt.Largest : null;
        list.Add(new MetricReading("pooltag", top is { } t ? $"Pool Tag {t.Tag}" : "개별 Pool Tag",
            top is { } tv ? tv.TotalUsed / MB : null, new MetricThreshold(st.MemPoolTagWarnMb, st.MemPoolTagHighMb),
            top is { } tt ? Format.Bytes(tt.TotalUsed) : (s.PoolTags?.Error is not null ? "측정 불가" : "수집 전"),
            $"주의 {Format.Bytes(st.MemPoolTagWarnMb * MB)} · 위험 {Format.Bytes(st.MemPoolTagHighMb * MB)}"));

        list.Add(Percent("commit", "커밋 사용률", k?.CommitPercent, st.MemCommitWarnPercent, st.MemCommitHighPercent, false));
        list.Add(Percent("avail", "가용 RAM 비율", k?.AvailablePercent, st.MemAvailWarnPercent, st.MemAvailHighPercent, true));
        return list;
    }

    private static MetricReading Pool(string key, string label, ulong? bytes, int warnMb, int highMb) =>
        new(key, label, bytes / MB, new MetricThreshold(warnMb, highMb), Format.Bytes(bytes),
            $"주의 {Format.Bytes(warnMb * MB)} · 위험 {Format.Bytes(highMb * MB)}");

    private static MetricReading Percent(string key, string label, double? value, int warn, int high, bool lowerIsWorse)
    {
        string cmp = lowerIsWorse ? "이하" : "이상";
        return new(key, label, value, new MetricThreshold(warn, high, lowerIsWorse),
            value is { } v ? string.Format(CultureInfo.InvariantCulture, "{0:0.#}%", v) : Format.NA,
            $"주의 {warn}% {cmp} · 위험 {high}% {cmp}");
    }
}
