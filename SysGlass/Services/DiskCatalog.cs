using System.Management;
using SysGlass.Utilities;

namespace SysGlass.Services;

/// <summary>MSFT_PhysicalDisk(WMI)로 물리 디스크 번호와 종류(NVMe/SSD/HDD)를 조회한다.</summary>
internal sealed class DiskCatalog
{
    private readonly record struct Entry(int Number, string Kind);
    private readonly Dictionary<string, List<Entry>> _byName = new(StringComparer.OrdinalIgnoreCase);

    public DiskCatalog()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, FriendlyName, BusType, MediaType FROM MSFT_PhysicalDisk");
            foreach (ManagementObject d in searcher.Get())
            {
                string name = (d["FriendlyName"] as string ?? "").Trim();
                if (name.Length == 0 || !int.TryParse(d["DeviceId"]?.ToString(), out int num)) continue;
                ushort bus = d["BusType"] is ushort b ? b : (ushort)0;
                ushort media = d["MediaType"] is ushort m ? m : (ushort)0;
                string kind = bus == 17 ? "NVMe" : media == 4 ? "SSD" : media == 3 ? "HDD" : "";
                if (!_byName.TryGetValue(name, out var list)) _byName[name] = list = [];
                list.Add(new Entry(num, kind));
            }
        }
        catch (Exception ex)
        {
            Logger.Error("물리 디스크 정보 조회 실패", ex);
        }
    }

    private List<Entry>? Find(string lhmName)
    {
        lhmName = lhmName.Trim();
        if (_byName.TryGetValue(lhmName, out var l)) return l;
        // 제조사 접두어 등으로 이름이 다른 경우: 한쪽이 다른 쪽을 포함하고 후보가 하나일 때만 허용
        var cands = _byName.Where(k => k.Key.Contains(lhmName, StringComparison.OrdinalIgnoreCase)
                                    || lhmName.Contains(k.Key, StringComparison.OrdinalIgnoreCase)).ToList();
        return cands.Count == 1 ? cands[0].Value : null;
    }

    public int? NumberFor(string name) => Find(name) is { Count: 1 } l ? l[0].Number : null;

    public string KindFor(string name) => Find(name) is { Count: > 0 } l && l.Select(e => e.Kind).Distinct().Count() == 1 ? l[0].Kind : "";
}
