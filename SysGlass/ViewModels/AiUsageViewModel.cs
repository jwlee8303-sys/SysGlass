using System.Globalization;
using SysGlass.Models;

namespace SysGlass.ViewModels;

public sealed class AiUsageViewModel(string name) : ViewModelBase
{
    private AiUsageSnapshot? _snapshot;
    private string? _error;
    private string _status = "조회 대기 중";
    private string _updated = "";
    private IReadOnlyList<UsageWindowViewModel> _windows = [];

    public string Name { get; } = name;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Updated { get => _updated; private set => Set(ref _updated, value); }
    public IReadOnlyList<UsageWindowViewModel> Windows { get => _windows; private set => Set(ref _windows, value); }

    public void Update(AiUsageSnapshot snapshot)
    {
        _snapshot = snapshot;
        _error = null;
        Refresh(DateTimeOffset.UtcNow);
    }

    public void Fail(string message)
    {
        _error = message;
        Refresh(DateTimeOffset.UtcNow);
    }

    public void Refresh(DateTimeOffset now)
    {
        Windows = _snapshot?.Windows.Select(w => new UsageWindowViewModel(w, now)).ToArray() ?? [];
        bool stale = _snapshot is { } s && now - s.CollectedAt > TimeSpan.FromMinutes(10);
        Status = _error ?? (stale ? "오래된 데이터 · 갱신 대기" : _snapshot?.Notice ?? (_snapshot is null ? "조회 대기 중" : ""));
        Updated = _snapshot is { } snapshot ? $"마지막 수집 {snapshot.CollectedAt.ToLocalTime():MM/dd HH:mm:ss}" : "";
    }
}

public sealed class UsageWindowViewModel
{
    public UsageWindowViewModel(UsageWindow window, DateTimeOffset now)
    {
        Label = window.Label;
        HasUsage = window.ResetsAt is null || window.ResetsAt > now;
        UsedPercent = HasUsage ? window.UsedPercent : 0;
        UsageText = HasUsage ? window.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture) + "% 사용" : "갱신 대기";
        ResetText = window.ResetsAt is { } reset
            ? reset <= now ? "초기화 시각 경과 · 새 데이터 필요" : $"초기화 {reset.ToLocalTime():MM/dd HH:mm}"
            : "초기화 시간 정보 없음";
    }

    public string Label { get; }
    public bool HasUsage { get; }
    public double UsedPercent { get; }
    public string UsageText { get; }
    public string ResetText { get; }
}
