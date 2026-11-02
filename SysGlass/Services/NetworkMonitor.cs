using System.Diagnostics;
using System.Net.NetworkInformation;
using SysGlass.Utilities;

namespace SysGlass.Services;

/// <summary>활성 물리 어댑터의 송수신 속도 합계. 가상/터널/루프백 어댑터는 제외해 중복 계산을 막는다.</summary>
public sealed class NetworkMonitor
{
    private static readonly string[] VirtualHints =
        ["virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "tap-", "tap ", "wintun", "wireguard", "vpn", "loopback", "pseudo", "bluetooth", "wsl", "npcap", "teredo", "isatap"];

    private readonly Dictionary<string, (long rx, long tx)> _prev = new();
    private readonly Stopwatch _clock = new();
    private List<NetworkInterface> _adapters = [];
    private DateTime _lastScan = DateTime.MinValue;

    /// <summary>(다운로드, 업로드) bytes/s. 첫 호출이나 실패 시 null.</summary>
    public (double? down, double? up) Sample()
    {
        try
        {
            if (DateTime.UtcNow - _lastScan > TimeSpan.FromSeconds(5))
            {
                _adapters = Scan();
                _lastScan = DateTime.UtcNow;
            }

            double seconds = _clock.IsRunning ? _clock.Elapsed.TotalSeconds : 0;
            _clock.Restart();

            long rxDelta = 0, txDelta = 0;
            bool hasBaseline = false;
            var current = new Dictionary<string, (long, long)>();
            foreach (var nic in _adapters)
            {
                try
                {
                    var st = nic.GetIPv4Statistics();
                    long rx = st.BytesReceived, tx = st.BytesSent;
                    current[nic.Id] = (rx, tx);
                    if (_prev.TryGetValue(nic.Id, out var p) && rx >= p.rx && tx >= p.tx)
                    {
                        rxDelta += rx - p.rx;
                        txDelta += tx - p.tx;
                        hasBaseline = true;
                    }
                }
                catch { /* 어댑터가 사라진 경우 무시 */ }
            }
            _prev.Clear();
            foreach (var kv in current) _prev[kv.Key] = kv.Value;

            if (_adapters.Count == 0) return (0, 0); // 연결 없음: 속도 0
            if (!hasBaseline || seconds <= 0) return (null, null);
            return (rxDelta / seconds, txDelta / seconds);
        }
        catch (Exception ex)
        {
            Logger.Error("네트워크 수집 실패", ex);
            return (null, null);
        }
    }

    private static List<NetworkInterface> Scan()
    {
        var result = new List<NetworkInterface>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                or NetworkInterfaceType.Unknown) continue;
            string text = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (VirtualHints.Any(text.Contains)) continue;
            result.Add(nic);
        }
        return result;
    }
}
