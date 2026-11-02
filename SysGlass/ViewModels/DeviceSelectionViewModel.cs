using System.Collections.ObjectModel;
using SysGlass.Settings;

namespace SysGlass.ViewModels;

/// <summary>설정 화면의 장치 체크 항목 하나.</summary>
public sealed class DeviceChoice : ViewModelBase
{
    private readonly DeviceSelectionViewModel _owner;
    private bool _isSelected;
    private string _label;
    private bool _isPresent;

    public DeviceChoice(DeviceSelectionViewModel owner, DeviceKey key, string label, bool present, bool selected)
    {
        _owner = owner; Key = key; _label = label; _isPresent = present; _isSelected = selected;
    }

    public DeviceKey Key { get; }
    public string Label { get => _label; set => Set(ref _label, value); }
    public bool IsPresent { get => _isPresent; set { if (Set(ref _isPresent, value)) Raise(nameof(DisplayLabel)); } }
    public string DisplayLabel => IsPresent ? Label : Label + " (연결 안 됨)";

    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) _owner.OnChoiceToggled(); }
    }
    internal void SetSelectedSilently(bool v) { if (Set(ref _isSelected, v, nameof(IsSelected))) { } }
}

/// <summary>
/// GPU 또는 디스크 선택 상태. 설정의 목록(null=전체)을 읽고 쓰며,
/// 순서가 바뀌어도 모델명/Id로 기존 선택을 유지한다.
/// </summary>
public sealed class DeviceSelectionViewModel
{
    private readonly Func<List<DeviceKey>?> _get;
    private readonly Action<List<DeviceKey>?> _set;
    private readonly Action _changed;
    private bool _updating;

    public DeviceSelectionViewModel(Func<List<DeviceKey>?> get, Action<List<DeviceKey>?> set, Action changed)
    {
        _get = get; _set = set; _changed = changed;
    }

    public ObservableCollection<DeviceChoice> Choices { get; } = [];
    public bool HasChoices => Choices.Count > 0;

    /// <summary>detected의 장치가 선택 대상인지. 선택 목록이 null이면 모두 표시한다.</summary>
    public bool IsIncluded(string id, string name, IEnumerable<(string id, string name)> all)
    {
        var list = _get();
        if (list is null) return true;
        return list.Any(k => Matches(k, id, name, all));
    }

    /// <summary>Id+이름이 같거나, 이름이 같고 감지된 장치 중 그 이름이 유일하면 같은 장치로 본다.</summary>
    private static bool Matches(DeviceKey k, string id, string name, IEnumerable<(string id, string name)> all)
    {
        if (k.Name == name && k.Id == id) return true;
        if (k.Name != name || k.Name.Length == 0) return false;
        return all.Count(a => a.name == name) == 1;
    }

    /// <summary>감지된 장치 목록으로 체크 목록을 갱신한다(UI 스레드).</summary>
    public void UpdateDetected(IReadOnlyList<(string id, string name, string label)> detected)
    {
        _updating = true;
        try
        {
            var all = detected.Select(d => (d.id, d.name)).ToList();
            var saved = _get();

            // 감지된 장치
            foreach (var d in detected)
            {
                bool selected = saved is null || saved.Any(k => Matches(k, d.id, d.name, all));
                var existing = Choices.FirstOrDefault(c => c.Key.Id == d.id && c.Key.Name == d.name);
                if (existing is null)
                    Choices.Add(new DeviceChoice(this, new DeviceKey { Id = d.id, Name = d.name }, d.label, true, selected));
                else
                {
                    existing.Label = d.label;
                    existing.IsPresent = true;
                    existing.SetSelectedSilently(selected);
                }
            }

            // 저장되어 있지만 지금은 연결되지 않은 장치(선택 상태 보존용)
            foreach (var c in Choices.ToList())
            {
                bool present = detected.Any(d => d.id == c.Key.Id && d.name == c.Key.Name);
                if (!present) c.IsPresent = false;
            }
            if (saved is not null)
            {
                foreach (var k in saved)
                {
                    bool covered = Choices.Any(c => c.Key.Id == k.Id && c.Key.Name == k.Name)
                                   || detected.Any(d => Matches(k, d.id, d.name, all));
                    if (!covered)
                        Choices.Add(new DeviceChoice(this, new DeviceKey { Id = k.Id, Name = k.Name }, k.Name, false, true));
                }
            }
            // 연결 안 된 항목 중 선택도 아니고 저장도 안 된 것은 목록에서 제거
            foreach (var c in Choices.Where(c => !c.IsPresent && !c.IsSelected).ToList()) Choices.Remove(c);
        }
        finally { _updating = false; }
    }

    internal void OnChoiceToggled()
    {
        if (_updating) return;
        // 사용자가 처음 건드린 순간부터 명시적 목록으로 저장한다.
        _set(Choices.Where(c => c.IsSelected)
            .Select(c => new DeviceKey { Id = c.Key.Id, Name = c.Key.Name }).ToList());
        _changed();
    }

    /// <summary>선택 상태를 설정값(예: 기본값 복원 후)으로 다시 반영한다.</summary>
    public void RefreshFromSettings()
    {
        _updating = true;
        try
        {
            var saved = _get();
            var all = Choices.Where(c => c.IsPresent).Select(c => (c.Key.Id, c.Key.Name)).ToList();
            foreach (var c in Choices)
                c.SetSelectedSilently(saved is null || saved.Any(k => Matches(k, c.Key.Id, c.Key.Name, all)));
        }
        finally { _updating = false; }
    }
}
