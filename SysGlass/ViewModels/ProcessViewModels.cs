using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SysGlass.Models;
using SysGlass.Services;
using SysGlass.Utilities;

namespace SysGlass.ViewModels;

/// <summary>정렬 기준이 되는 자원.</summary>
public enum ProcessSortKey { Name, Importance, Cpu, Gpu, Memory, Disk }

/// <summary>프로세스 이름 하나(같은 이름의 여러 프로세스는 합산)의 표시용 데이터.</summary>
public sealed class ProcessItemViewModel : ViewModelBase
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private const double MiB = 1024.0 * 1024, GiB = MiB * 1024;

    private static readonly Brush[] LevelBrushes =
        [Frozen("#9DA0A6"), Frozen("#4CC2FF"), Frozen("#FFB86B"), Frozen("#FF6B6B")]; // 낮음/보통/높음/핵심
    private static readonly string[] LevelTexts = ["낮음", "보통", "높음", "핵심"];

    private string _nameToolTip = "", _displayName = "", _description = "", _cpuText = "", _gpuText = "", _memoryText = "", _diskText = "";
    private Importance _importance = Importance.Normal;

    public ProcessItemViewModel(string name) => Name = name;

    public string Name { get; }
    public double Cpu { get; private set; }
    public double Gpu { get; private set; }
    public double Memory { get; private set; }
    public double Disk { get; private set; }

    public string DisplayName { get => _displayName; private set => Set(ref _displayName, value); }
    public string NameToolTip { get => _nameToolTip; private set => Set(ref _nameToolTip, value); }
    public string Description { get => _description; private set => Set(ref _description, value); }
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }
    public string MemoryText { get => _memoryText; private set => Set(ref _memoryText, value); }
    public string DiskText { get => _diskText; private set => Set(ref _diskText, value); }

    public Importance Importance
    {
        get => _importance;
        private set { if (Set(ref _importance, value)) { Raise(nameof(ImportanceText)); Raise(nameof(ImportanceBrush)); } }
    }
    public string ImportanceText => LevelTexts[(int)_importance];
    public Brush ImportanceBrush => LevelBrushes[(int)_importance];

    /// <summary>자원/중요도 열의 정렬 값 (클수록 앞). 이름 열은 사용하지 않는다.</summary>
    public double SortValue(ProcessSortKey key) => key switch
    {
        ProcessSortKey.Importance => (double)_importance,
        ProcessSortKey.Cpu => Cpu,
        ProcessSortKey.Gpu => Gpu,
        ProcessSortKey.Memory => Memory,
        ProcessSortKey.Disk => Disk,
        _ => 0,
    };

    public void Update(ProcessGroupSample s, ProcessInfo info)
    {
        Cpu = s.CpuPercent;
        Gpu = s.GpuPercent ?? 0;
        Memory = s.MemoryBytes;
        Disk = s.IoBytesPerSec; // 읽기+쓰기 전송량 (디스크 전용 값이 아님)

        DisplayName = s.Count > 1 ? $"{s.Name} ({s.Count})" : s.Name;
        Description = info.Description;
        Importance = info.Importance;

        // 값을 읽지 못한 프로세스는 0이 아니라 N/A로, 일부만 읽었으면 '*'로 구분한다.
        bool none = s.Measured == 0;
        string partial = s.Measured > 0 && s.Measured < s.Count ? "*" : "";
        NameToolTip = none ? $"{s.Name}\n접근 권한이 없어 CPU·메모리·I/O 값을 읽을 수 없는 프로세스입니다."
                    : partial.Length > 0 ? $"{s.Name}\n{s.Count}개 중 {s.Measured}개만 측정되어 합계가 실제보다 작을 수 있습니다 (*)."
                    : s.Name;

        CpuText = none ? Format.NA : (s.CpuPercent < 0.05 ? "0%" : string.Format(C, "{0:0.0}%", s.CpuPercent)) + partial;
        MemoryText = none ? Format.NA
            : (s.MemoryBytes >= GiB ? string.Format(C, "{0:0.00} GB", s.MemoryBytes / GiB)
                                    : string.Format(C, "{0:0.0} MB", s.MemoryBytes / MiB)) + partial;
        DiskText = none ? Format.NA : (s.IoBytesPerSec < 1 ? "0 KB/s" : Format.Rate(s.IoBytesPerSec)) + partial;
        // GPU는 프로세스 접근 권한과 무관한 별도 카운터라 측정 개수와 상관없이 표시한다.
        GpuText = s.GpuPercent is not { } g ? Format.NA : g < 0.05 ? "0%" : string.Format(C, "{0:0.0}%", g);
    }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>
/// 자원별 프로세스 점유 현황 창의 뷰모델. 창이 열려 있는 동안에만 수집하며,
/// 정렬 기준이 정해져 있으면 값이 바뀔 때마다 목록을 내림차순으로 다시 정렬한다.
/// </summary>
public sealed class ProcessListViewModel : ViewModelBase, IDisposable
{
    private const int IntervalMs = 1000;

    private readonly ProcessMonitor _monitor = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, ProcessItemViewModel> _byName = new(StringComparer.OrdinalIgnoreCase);
    private ProcessSortKey _sortKey = ProcessSortKey.Cpu;
    private bool _ascending;
    private string _status = "수집 중…";
    private bool _gpuAvailable = true;

    public ObservableCollection<ProcessItemViewModel> Items { get; } = [];

    public ProcessSortKey SortKey => _sortKey;
    public bool SortAscending => _ascending;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool GpuAvailable { get => _gpuAvailable; private set => Set(ref _gpuAvailable, value); }

    /// <summary>정렬 기준이 바뀌면 알린다 (헤더의 ▼ 표시 갱신용).</summary>
    public event Action? SortChanged;

    /// <summary>해당 열 기준으로 정렬하고(이름은 오름차순, 나머지는 내림차순), 이후 갱신에서도 이 기준을 유지한다.</summary>
    public void SortBy(ProcessSortKey key)
    {
        if (key == _sortKey && key is ProcessSortKey.Name or ProcessSortKey.Importance)
            _ascending = !_ascending; // 이름/중요도는 같은 머리글을 다시 누르면 방향이 바뀐다.
        else
        {
            _sortKey = key;
            _ascending = key == ProcessSortKey.Name; // 처음 누르면 이름은 오름차순, 나머지는 내림차순
        }
        Resort();
        SortChanged?.Invoke();
    }

    public void Start()
    {
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var samples = _monitor.Sample();
                    // 설명 조회(파일 버전 정보)는 수집 스레드에서 끝내 UI 스레드를 막지 않는다.
                    var resolved = samples.Select(s => (s, info: ProcessCatalog.Get(s.Name, s.ImagePath, s.MixedPaths))).ToList();
                    bool gpu = _monitor.GpuAvailable;
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(resolved, gpu));
                }
                catch (Exception ex) { Logger.Error("프로세스 수집 실패", ex); }

                try { await Task.Delay(IntervalMs, token); }
                catch (OperationCanceledException) { break; }
            }
        }, token);
    }

    private void Apply(List<(ProcessGroupSample s, ProcessInfo info)> data, bool gpuAvailable)
    {
        if (_cts.IsCancellationRequested) return;
        GpuAvailable = gpuAvailable;

        var names = new HashSet<string>(data.Select(d => d.s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _byName.Keys.Where(k => !names.Contains(k)).ToList())
        {
            Items.Remove(_byName[gone]);
            _byName.Remove(gone);
        }

        foreach (var (s, info) in data)
        {
            if (!_byName.TryGetValue(s.Name, out var vm))
            {
                _byName[s.Name] = vm = new ProcessItemViewModel(s.Name);
                Items.Add(vm);
            }
            vm.Update(s, info);
        }

        Resort();
        int total = data.Sum(d => d.s.Count);
        Status = $"프로세스 {total}개 · 이름 {data.Count}종 · {IntervalMs / 1000.0:0.#}초마다 갱신";
    }

    /// <summary>항목을 새로 만들지 않고 Move로 순서만 바꿔 깜빡임과 선택 해제를 막는다.</summary>
    private void Resort()
    {
        // 값이 같으면 항상 이름 오름차순으로 안정적으로 정렬한다.
        IOrderedEnumerable<ProcessItemViewModel> ordered;
        if (_sortKey == ProcessSortKey.Name)
            ordered = _ascending
                ? Items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                : Items.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase);
        else
            ordered = (_ascending
                    ? Items.OrderBy(i => i.SortValue(_sortKey))
                    : Items.OrderByDescending(i => i.SortValue(_sortKey)))
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var sorted = ordered.ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = Items.IndexOf(sorted[i]);
            if (cur != i) Items.Move(cur, i);
        }
    }

    public void Dispose()
    {
        _cts.Cancel(); // 수집 작업이 토큰을 아직 쓰고 있을 수 있어 Dispose는 하지 않는다.
    }
}
