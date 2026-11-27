using System.Runtime.InteropServices;
using SysGlass.Models;
using SysGlass.Utilities;

namespace SysGlass.Services.Memory;

/// <summary>
/// NtQuerySystemInformation(SystemProcessInformation) 한 번으로 모든 프로세스의 Private Bytes, Working Set,
/// 핸들 수, 부모 PID, 풀 할당량을 읽는다(프로세스를 하나씩 열지 않는다). 명령줄·환경 변수는 읽지 않는다.
/// 1시간 전 스냅샷과 비교한 변화량, 직전 스냅샷 대비 생성·종료 수를 함께 계산한다. 한 스레드에서만 호출해야 한다.
/// </summary>
public sealed class ProcessMemoryCollector : IDisposable
{
    // x64 SYSTEM_PROCESS_INFORMATION 필드 오프셋
    private const int OffThreads = 4, OffCreateTime = 32, OffImageLength = 56, OffImageBuffer = 64,
        OffPid = 80, OffParent = 88, OffHandles = 96, OffWorkingSet = 144, OffPagedQuota = 160,
        OffNonPagedQuota = 176, OffPagefile = 184, MinEntryBytes = 208;
    private const int MaxProcesses = 100_000;
    private static readonly long Keep = TimeSpan.FromMinutes(65).Ticks;
    private const int MaxSnapshots = 400;

    private readonly NtSystemInfoBuffer? _buffer;
    private readonly Queue<(long Active, Dictionary<(int, long), (ulong Private, uint Handles)> Values)> _history = new();
    private HashSet<(int, long)>? _lastKeys;

    public ProcessMemoryCollector()
    {
        if (IntPtr.Size == 8) _buffer = new NtSystemInfoBuffer(512 * 1024);
    }

    public ProcessReport Read(long active100ns)
    {
        var now = DateTimeOffset.Now;
        if (_buffer is null) return new ProcessReport(now, [], "64비트 환경에서만 지원");
        if (!_buffer.Query(NativeMethods.SystemProcessInformation, out int length, out var error))
            return new ProcessReport(now, [], error);
        var (list, parseError) = Parse(_buffer.Pointer, length);
        if (parseError is not null) return new ProcessReport(now, [], parseError);

        // 생성·종료 수(직전 스냅샷 기준)
        var keys = list.Select(p => p.Key).ToHashSet();
        int started = 0, exited = 0;
        if (_lastKeys is not null)
        {
            started = keys.Count(k => !_lastKeys.Contains(k));
            exited = _lastKeys.Count(k => !keys.Contains(k));
        }
        _lastKeys = keys;

        // 1시간 변화량
        if (_history.Count > 0 && active100ns <= _history.Last().Active) _history.Clear();
        var values = list.ToDictionary(p => p.Key, p => (p.PrivateBytes, p.HandleCount));
        _history.Enqueue((active100ns, values));
        while (_history.Count > MaxSnapshots || (_history.Count > 1 && active100ns - _history.Peek().Active > Keep)) _history.Dequeue();

        Dictionary<(int, long), (long, long)>? change = null;
        long target = active100ns - TimeSpan.FromHours(1).Ticks;
        var baseline = _history.LastOrDefault(h => h.Active <= target);
        if (baseline.Values is not null && active100ns - baseline.Active <= TimeSpan.FromMinutes(90).Ticks)
        {
            change = new();
            foreach (var p in list)
                if (baseline.Values.TryGetValue(p.Key, out var b))
                    change[p.Key] = ((long)p.PrivateBytes - (long)b.Private, (long)p.HandleCount - b.Handles);
        }
        _buffer.Shrink(512 * 1024);
        return new ProcessReport(now, list, null) { Change1h = change, StartedSinceLast = started, ExitedSinceLast = exited };
    }

    internal static (List<ProcessMemoryInfo> List, string? Error) Parse(IntPtr p, int length)
    {
        var list = new List<ProcessMemoryInfo>(512);
        long start = p.ToInt64(), end = start + length;
        int offset = 0;
        for (int n = 0; n < MaxProcesses; n++)
        {
            if (offset < 0 || offset + MinEntryBytes > length) return (list, $"구조 불일치 (오프셋 {offset}) · Windows 변경 가능성");
            IntPtr e = p + offset;
            int next = Marshal.ReadInt32(e);
            int pid = (int)Marshal.ReadInt64(e + OffPid);
            string name = pid == 0 ? "System Idle Process" : "";
            ushort nameBytes = (ushort)Marshal.ReadInt16(e + OffImageLength);
            long namePtr = Marshal.ReadInt64(e + OffImageBuffer);
            // 이름 문자열은 같은 버퍼 안을 가리켜야 한다.
            if (nameBytes > 0 && namePtr >= start && namePtr + nameBytes <= end)
                name = Marshal.PtrToStringUni((IntPtr)namePtr, nameBytes / 2);
            else if (pid == 4) name = "System";

            list.Add(new ProcessMemoryInfo(pid, (int)Marshal.ReadInt64(e + OffParent), name,
                Marshal.ReadInt64(e + OffCreateTime),
                HandleCount: (uint)Marshal.ReadInt32(e + OffHandles),
                ThreadCount: (uint)Marshal.ReadInt32(e + OffThreads),
                PrivateBytes: (ulong)Marshal.ReadInt64(e + OffPagefile),
                WorkingSet: (ulong)Marshal.ReadInt64(e + OffWorkingSet),
                PagedPoolQuota: (ulong)Marshal.ReadInt64(e + OffPagedQuota),
                NonPagedPoolQuota: (ulong)Marshal.ReadInt64(e + OffNonPagedQuota)));
            if (next == 0) return (list, null);
            offset += next;
        }
        return (list, "프로세스 수가 상한을 넘음");
    }

    public void Reset()
    {
        _history.Clear();
        _lastKeys = null;
        _buffer?.Shrink(512 * 1024);
    }

    public void Dispose()
    {
        _buffer?.Dispose();
        _history.Clear();
    }
}
