using System.Xml.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SysGlass.Models;
using SysGlass.Services.Memory;

/// <summary>Pool Tag·프로세스 버퍼 해석과 진단 기록 검사. 가상 버퍼와 임시 폴더만 사용한다.</summary>
internal static class MemoryDiagnosticChecks
{
    private const ulong MB = 1024 * 1024;

    public static void Run(string scratch, Action<bool, string> check)
    {
        PoolTagChecks(check);
        ProcessChecks(check);
        WriterChecks(Path.Combine(scratch, "memory-logs"), check);
        EventChecks(check);
        TraceChecks(Path.Combine(scratch, "trace"), check);
        TraceLifecycleChecks(Path.Combine(scratch, "trace"), check).GetAwaiter().GetResult();
    }

    private static void TraceChecks(string dir, Action<bool, string> check)
    {
        check(PrecisionTraceService.ParseTags("Toke, SeAt;Irp", out var tags) is null
              && tags.SequenceEqual(["Toke", "SeAt", "Irp "]), "trace tags parsed and padded");
        check(PrecisionTraceService.ParseTags("Toke, Toke", out tags) is null && tags.Count == 1, "duplicate trace tags removed");
        check(PrecisionTraceService.ParseTags("", out tags) is null && tags.Count == 0, "empty trace tags mean all");
        check(PrecisionTraceService.ParseTags("Token", out _) is not null, "long trace tag rejected");
        check(PrecisionTraceService.ParseTags("To\"e", out _) is not null && PrecisionTraceService.ParseTags("가나", out _) is not null, "unsafe trace tag rejected");
        check(PrecisionTraceService.ParseTags("a,b,c,d,e,f,g,h,i", out _) is not null, "too many trace tags rejected");

        var xml = XDocument.Parse(PrecisionTraceService.BuildProfile(new(["Toke", "Irp "], 5, 128)));
        var root = xml.Root!;
        check(root.Descendants("PoolTag").Select(e => (string?)e.Attribute("Value")).SequenceEqual(["Toke", "Irp "]), "profile pool tag filter");
        check((string?)root.Descendants("Buffers").Single().Attribute("Value") == "128"
              && (string?)root.Descendants("BufferSize").Single().Attribute("Value") == "1024", "profile buffer limit");
        var profile = root.Descendants("Profile").Single();
        check((string?)profile.Attribute("Name") == PrecisionTraceService.ProfileName && (string?)profile.Attribute("LoggingMode") == "Memory", "profile memory mode");
        check(root.Descendants("Keyword").Select(e => (string?)e.Attribute("Value")).Contains("Pool")
              && root.Descendants("Stack").Count() == 4, "profile pool keyword and stacks");
        var all = XDocument.Parse(PrecisionTraceService.BuildProfile(new([], 5, 128)));
        check(!all.Descendants("PoolTags").Any(), "profile without tag filter");

        // 정리: 최근 3개만 남기고 30일 지난 파일과 다른 파일은 건드리지 않음
        Directory.CreateDirectory(dir);
        var now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 5; i++)
        {
            var f = Path.Combine(dir, $"sysglass-pool-2026112{i}-000000.etl");
            File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(f, now.AddDays(-i));
        }
        var other = Path.Combine(dir, "user-trace.etl");
        File.WriteAllText(other, "x");
        File.SetLastWriteTimeUtc(other, now.AddDays(-100));
        new PrecisionTraceService(dir).Cleanup(now);
        var left = Directory.GetFiles(dir, "sysglass-pool-*.etl").Length;
        check(left == PrecisionTraceService.KeepTraces && File.Exists(other), "trace cleanup keeps recent and foreign files");
    }

    private static async Task TraceLifecycleChecks(string dir, Action<bool, string> check)
    {
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool failCancel = false;
        async Task<(int, string)> Run(TimeSpan _, string[] args)
        {
            if (args[0] == "-start")
            {
                starting.SetResult();
                await releaseStart.Task;
            }
            if (args[0] == "-stop" || (args[0] == "-cancel" && failCancel)) return (1, "failed");
            return (0, "");
        }

        using var service = new PrecisionTraceService(dir, Run);
        var options = new PrecisionTraceService.Options([], 1, 64);
        var start = service.StartAsync(options);
        await starting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = service.StopAsync(save: true);
        check(!stop.IsCompleted, "stop waits for in-flight trace start");
        failCancel = true;
        releaseStart.SetResult();
        check(await start is null, "trace started with simulated recorder");
        await stop;
        check(service.State == PrecisionTraceService.TraceState.CancelFailed && service.LastError is not null,
            "save and cleanup failure retains retry state");
        check(await service.StartAsync(options) is not null, "uncertain trace blocks new recording");
        await service.StopAsync(save: false);
        check(service.State == PrecisionTraceService.TraceState.CancelFailed, "failed cancel can be retried again");
        failCancel = false;
        await service.StopAsync(save: false);
        check(service.State == PrecisionTraceService.TraceState.Idle && service.LastError is null, "successful retry clears failure");
    }

    private static void EventChecks(Action<bool, string> check)
    {
        var stats = new ProcessEventStats();
        var t0 = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        check(!stats.Snapshot(t0).Available && stats.Snapshot(t0).Error == "수집 전", "events not started");
        stats.MarkStarted(t0);
        ProcessEventRecord Ev(double min, ProcessEventKind k, string name) => new(t0.AddMinutes(min), k, 100, 4, name);
        for (int i = 0; i < 50; i++) stats.Record(Ev(i, ProcessEventKind.Start, "powershell.exe")); // 매분 1회, 50분
        for (int i = 0; i < 5; i++) stats.Record(Ev(45 + i, ProcessEventKind.Stop, "powershell.exe"));
        stats.Record(Ev(49, ProcessEventKind.Start, "conhost.exe"));
        var s = stats.Snapshot(t0.AddMinutes(49.5));
        check(s.Available && s.Starts1h == 51 && s.Stops1h == 5, "event counts in 1 hour");
        check(s.Starts10m == 11 && s.Stops10m == 5, "event counts in 10 minutes");
        check(s.TopStarters1h[0] == ("powershell.exe", 50) && s.TopStarters1h[1] == ("conhost.exe", 1), "top starters");
        check(s.Recent.Count == 56 && s.Recent[0].Name == "conhost.exe", "recent events newest first");

        // 1시간이 지난 버킷은 빠진다
        var later = stats.Snapshot(t0.AddMinutes(100));
        check(later.Starts1h == 10 && later.Starts10m == 0, "old buckets expire");

        // 크기 상한: 최근 300건, 버킷당 이름 500개 초과분은 '(기타)'
        for (int i = 0; i < 700; i++) stats.Record(Ev(120, ProcessEventKind.Start, $"p{i}.exe"));
        var big = stats.Snapshot(t0.AddMinutes(120));
        check(big.Recent.Count == ProcessEventStats.MaxRecent, "recent events bounded");
        check(big.Overflowed == 200 && big.TopStarters1h[0] == (ProcessEventStats.OtherName, 200), "names merged beyond cap");

        stats.MarkUnavailable("권한 부족 (관리자 권한 필요)");
        var doc = JsonSerializer.Serialize(DiagnosticWriter.ProcessEventsDoc(stats.Snapshot(t0.AddMinutes(120))));
        check(doc.Contains("권한 부족") || doc.Contains("\\uAD8C"), "unavailable event reason kept in diagnostic");
        stats.Reset();
        check(stats.Snapshot(t0).Starts1h == 0 && stats.Snapshot(t0).Recent.Count == 0, "event stats reset");

        // 드라이버 후보 검색 도우미
        check(DriverTagSearch.Find([0x41, 0x00, (byte)'T', (byte)'o', (byte)'k', (byte)'e', 0x8B], "Toke"u8) == DriverTagSearch.Match.Standalone, "standalone tag constant");
        check(DriverTagSearch.Find("ZwQueryInformationToken\0"u8, "Toke"u8) == DriverTagSearch.Match.Substring, "substring of longer identifier");
        check(DriverTagSearch.Find("TokenX\0Toke\0"u8, "Toke"u8) == DriverTagSearch.Match.Standalone, "later standalone match found");
        check(DriverTagSearch.Find("Tok"u8, "Toke"u8) == DriverTagSearch.Match.None, "no match");
        // x64 코드: mov r8d, 'nfMF' 다음에 REX 접두사(0x48 = 'H')가 오는 경우
        check(DriverTagSearch.Find([0x41, 0xB8, (byte)'F', (byte)'M', (byte)'f', (byte)'n', 0x48, 0x8B], "FMfn"u8) == DriverTagSearch.Match.Standalone, "tag followed by REX prefix");
        // 보호 풀 태그(마지막 바이트 최상위 비트)
        check(DriverTagSearch.Find([0xBA, (byte)'T', (byte)'o', (byte)'k', (byte)('e' | 0x80), 0x48], "Toke"u8) == DriverTagSearch.Match.Standalone, "protected pool tag variant");
        check(DriverTagSearch.IsSearchable("Toke") && DriverTagSearch.IsSearchable("Irp ") && !DriverTagSearch.IsSearchable("AB?C"), "searchable tag");
        // 조각 경계에 걸친 태그와 문맥(1MB 조각 + 8바이트 겹침)
        var chunkDir = Path.Combine(Path.GetTempPath(), "sysglass-chunk-" + Environment.ProcessId);
        Directory.CreateDirectory(chunkDir);
        try
        {
            DriverTagSearch.Match FileMatch(int at, byte[] text)
            {
                var data = new byte[1024 * 1024 * 2 + 100];
                text.CopyTo(data, at);
                var f = Path.Combine(chunkDir, "t.bin");
                File.WriteAllBytes(f, data);
                return DriverTagSearch.FindInFile(f, "Toke"u8, new byte[1024 * 1024 + 8], CancellationToken.None);
            }
            const int edge = 1024 * 1024 + 8;
            check(FileMatch(edge - 2, "Toke"u8.ToArray()) == DriverTagSearch.Match.Standalone, "tag across chunk boundary");
            check(FileMatch(edge - 4, "Token"u8.ToArray()) == DriverTagSearch.Match.Substring, "context kept across chunk boundary");
            check(FileMatch(edge - 5, "xToke"u8.ToArray()) == DriverTagSearch.Match.Substring, "preceding context kept across boundary");
            check(FileMatch(2 * 1024 * 1024 + 96, "Toke"u8.ToArray()) == DriverTagSearch.Match.Standalone, "tag at end of file");
        }
        finally { Directory.Delete(chunkDir, true); }

        var bad = DriverTagSearch.Search("A?B?", [], CancellationToken.None);
        check(bad.Error is not null && bad.Candidates.Count == 0, "unsearchable tag rejected");
    }

    private static uint Key(string tag) => BitConverter.ToUInt32(Encoding.ASCII.GetBytes(tag));

    /// <summary>x64 SYSTEM_POOLTAG_INFORMATION 버퍼를 만든다.</summary>
    private static byte[] PoolBuffer(params (string Tag, uint PA, uint PF, ulong PU, uint NA, uint NF, ulong NU)[] tags)
    {
        var buf = new byte[8 + tags.Length * 40];
        BitConverter.GetBytes(tags.Length).CopyTo(buf, 0);
        for (int i = 0; i < tags.Length; i++)
        {
            int o = 8 + i * 40;
            var t = tags[i];
            Encoding.ASCII.GetBytes(t.Tag).CopyTo(buf, o);
            BitConverter.GetBytes(t.PA).CopyTo(buf, o + 4);
            BitConverter.GetBytes(t.PF).CopyTo(buf, o + 8);
            BitConverter.GetBytes(t.PU).CopyTo(buf, o + 16);
            BitConverter.GetBytes(t.NA).CopyTo(buf, o + 24);
            BitConverter.GetBytes(t.NF).CopyTo(buf, o + 28);
            BitConverter.GetBytes(t.NU).CopyTo(buf, o + 32);
        }
        return buf;
    }

    private static T WithNative<T>(byte[] data, Func<IntPtr, T> f)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { return f(h.AddrOfPinnedObject()); }
        finally { h.Free(); }
    }

    private static void PoolTagChecks(Action<bool, string> check)
    {
        var buf = PoolBuffer(("Toke", 8915714, 8777064, 287 * MB, 0, 0, 0), ("ismc", 0, 0, 0, 5, 2, 350 * MB),
                             ("Wrap", 5, uint.MaxValue - 4, MB, 0, 0, 0));
        var (tags, error) = WithNative(buf, p => PoolTagCollector.Parse(p, buf.Length));
        check(error is null && tags.Count == 3, "pool tag buffer parsed");
        check(tags[0].Tag == "Toke" && tags[0].PagedUsed == 287 * MB && tags[0].Outstanding == 138650, "pool tag fields and outstanding count");
        check(tags[1].NonPagedUsed == 350 * MB && tags[1].Outstanding == 3, "non-paged fields");
        check(tags[2].Outstanding == 10, "32-bit counter wraparound handled");

        // 반환 길이가 항목 수와 맞지 않으면 구조 불일치로 실패(0으로 대신하지 않음)
        var (bad, badError) = WithNative(buf, p => PoolTagCollector.Parse(p, buf.Length - 40));
        check(bad.Count == 0 && badError!.Contains("구조 불일치"), "truncated pool buffer rejected");
        var (_, shortError) = WithNative(buf, p => PoolTagCollector.Parse(p, 4));
        check(shortError is not null, "too short buffer rejected");
        check(PoolTagCollector.TagText(0x00_41_42_43) == "CBA?", "non-printable tag char");

        // 태그별 증가량 이력
        var h = new PoolTagHistory();
        long t0 = TimeSpan.FromHours(10).Ticks;
        PoolTagEntry E(string tag, ulong used) => new(Key(tag), tag, used, 0, 0, 0, 0, 0);
        h.Add(t0, [E("Toke", 100 * MB), E("File", 50 * MB)]);
        check(h.Growth(TimeSpan.FromMinutes(10)) is null, "tag growth unmeasured at start");
        for (int m = 1; m <= 70; m++)
            h.Add(t0 + TimeSpan.FromMinutes(m).Ticks, m < 30 ? [E("Toke", (100 + (ulong)m) * MB), E("File", 50 * MB)]
                                                             : [E("Toke", (100 + (ulong)m) * MB), E("File", 50 * MB), E("New!", 7 * MB)]);
        var g10 = h.Growth(TimeSpan.FromMinutes(10))!;
        var g1 = h.Growth(TimeSpan.FromHours(1))!;
        check(g10[Key("Toke")] == (long)(10 * MB) && !g10.ContainsKey(Key("File")), "per-tag 10 minute growth");
        check(g1[Key("Toke")] == (long)(60 * MB) && g1[Key("New!")] == (long)(7 * MB), "per-tag 1 hour growth with new tag");
        check(h.SnapshotCount <= 120 + 160, "tag history bounded");
        h.Add(t0, [E("Toke", MB)]); // 활성 시간 역행 → 재시작
        check(h.Growth(TimeSpan.FromMinutes(10)) is null && h.TagCount == 1, "tag history resets on counter reset");

        // 수집 실패는 '측정 불가', 1.2GB 태그는 위험
        var settings = new SysGlass.Settings.AppSettings();
        var failed = new MemoryWatchState { Enabled = true, PoolTags = new PoolTagReport(DateTimeOffset.Now, [], "권한 부족") };
        var r = MemoryReadings.Build(failed, settings).Single(x => x.Key == "pooltag");
        check(r.Value is null && r.ValueText == "측정 불가", "failed pool tag is unknown");
        var big = failed with { PoolTags = new PoolTagReport(DateTimeOffset.Now, [E("Toke", (ulong)(1.2 * 1024) * MB)], null) };
        var rb = MemoryReadings.Build(big, settings).Single(x => x.Key == "pooltag");
        check(rb.Threshold.Evaluate(rb.Value) == MemoryLevel.High && rb.Label == "Pool Tag Toke", "large pool tag is high");
        check(PoolTagCatalog.Describe("Toke") is not null && PoolTagCatalog.Describe("SeTd")!.Contains("추정") && PoolTagCatalog.Describe("Zzzz") is null,
            "tag catalog is reference only");
    }

    private static void ProcessChecks(Action<bool, string> check)
    {
        // x64 SYSTEM_PROCESS_INFORMATION 두 항목(이름 문자열은 같은 버퍼 끝에 둔다)
        const int entry = 256;
        var data = new byte[entry * 2 + 64];
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            long baseAddr = h.AddrOfPinnedObject().ToInt64();
            void Put(int o, long v, int size) { var b = BitConverter.GetBytes(v); Array.Copy(b, 0, data, o, size); }
            byte[] name = Encoding.Unicode.GetBytes("lsass.exe");
            name.CopyTo(data, entry * 2);
            // 항목 0: System Idle (pid 0)
            Put(0, entry, 4);
            // 항목 1: lsass
            int o1 = entry;
            Put(o1 + 0, 0, 4);
            Put(o1 + 4, 12, 4);                      // 스레드
            Put(o1 + 32, 1234567, 8);                // 생성 시각
            Put(o1 + 56, name.Length, 2);            // 이름 길이
            Put(o1 + 64, baseAddr + entry * 2, 8);   // 이름 포인터
            Put(o1 + 80, 900, 8);                    // PID
            Put(o1 + 88, 700, 8);                    // 부모 PID
            Put(o1 + 96, 4321, 4);                   // 핸들
            Put(o1 + 144, (long)(80 * MB), 8);       // Working Set
            Put(o1 + 160, 1024, 8);                  // 페이징 풀 할당량
            Put(o1 + 184, (long)(60 * MB), 8);       // Private Bytes
            var (list, error) = ProcessMemoryCollector.Parse(h.AddrOfPinnedObject(), data.Length);
            check(error is null && list.Count == 2 && list[0].Name == "System Idle Process", "process buffer parsed");
            var p = list[1];
            check(p.Name == "lsass.exe" && p.Pid == 900 && p.ParentPid == 700 && p.HandleCount == 4321 && p.ThreadCount == 12, "process identity fields");
            check(p.PrivateBytes == 60 * MB && p.WorkingSet == 80 * MB && p.PagedPoolQuota == 1024, "process memory fields");

            // 이름 포인터가 버퍼 밖이면 읽지 않는다
            Put(o1 + 64, baseAddr + data.Length + 4096, 8);
            var (list2, _) = ProcessMemoryCollector.Parse(h.AddrOfPinnedObject(), data.Length);
            check(list2[1].Name == "", "name pointer outside buffer ignored");
            // 다음 항목 오프셋이 버퍼를 넘으면 구조 불일치
            Put(o1, 100_000, 4);
            var (_, err3) = ProcessMemoryCollector.Parse(h.AddrOfPinnedObject(), data.Length);
            check(err3 is not null && err3.Contains("구조 불일치"), "invalid next offset rejected");
        }
        finally { h.Free(); }
    }

    private static void WriterChecks(string dir, Action<bool, string> check)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var w = new DiagnosticWriter(dir);

        // 측정하지 못한 값은 빈 칸(0이 아님)
        var state = new MemoryWatchState
        {
            Enabled = true,
            Kernel = new KernelMemorySample { Time = DateTimeOffset.Now, PagedPool = 300 * MB, NonPagedPool = null },
            PoolTags = new PoolTagReport(DateTimeOffset.Now, [], "권한 부족"),
        };
        w.AppendSummary(state);
        w.AppendSummary(state);
        var csv = File.ReadAllLines(Directory.GetFiles(dir, "summary-*.csv").Single());
        var cols = csv[1].Split(',');
        check(csv.Length == 3 && csv[0].StartsWith("time,"), "summary csv header written once");
        check(cols[4] == "300.0" && cols[5] == "" && cols[13] == "", "missing values are blank, not zero");

        // 상세 진단: 수집 실패 사유와 안내 문구 포함, 명령줄 항목 없음
        var alert = new MemoryAlert(DateTimeOffset.Now, MemoryLevel.High, "paged", "페이징 풀", "15.22 GB", "주의 2 GB · 위험 4 GB", false, false, false);
        var sys = new SystemInfo("Windows 11 Pro", "25H2", 10, 0, 26200, 9711, true, true);
        string path = w.WriteDiagnostic(new DiagnosticWriter.Context("alert", state, [alert], sys, [], [], null, [], "서비스 조회 실패", null), DateTimeOffset.Now);
        var doc = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        check(doc.GetProperty("poolTags").GetProperty("error").GetString() == "권한 부족", "diagnostic keeps pool tag failure reason");
        check(doc.GetProperty("notice").GetString()!.Contains("확정하지 않습니다") && doc.GetProperty("system").GetProperty("version").GetString() == "10.0.26200.9711",
            "diagnostic notice and version");
        check(doc.GetProperty("system").GetProperty("fastStartup").GetProperty("active").GetBoolean(), "fast startup recorded");
        check(File.ReadAllBytes(path)[0] == (byte)'{', "diagnostic json has no BOM");
        string raw = File.ReadAllText(path);
        check(!raw.Contains("CommandLine", StringComparison.OrdinalIgnoreCase) && !raw.Contains("PathName"), "no command line collected");
        string after = w.WriteFollowUp(path, state, state with { Kernel = state.Kernel with { PagedPool = 310 * MB } });
        check(JsonDocument.Parse(File.ReadAllText(after)).RootElement.GetProperty("pagedDeltaMb").GetDouble() == 10, "follow-up delta");
        w.AppendAlerts([alert]);
        check(Directory.GetFiles(dir, "alerts-*.jsonl").Length == 1, "alert history written");

        // 정리: 보존 기간·용량 초과 시 오래된 것부터, 다른 파일은 건드리지 않음
        string foreign = Path.Combine(dir, "user-notes.txt");
        File.WriteAllText(foreign, "keep");
        File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-100));
        string old = Path.Combine(dir, "diag-20000101-000000.json");
        File.WriteAllText(old, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-31));
        int removed = w.Cleanup(30, long.MaxValue, DateTime.UtcNow);
        check(removed == 1 && !File.Exists(old) && File.Exists(foreign), "retention removes only expired owned files");
        for (int i = 0; i < 5; i++)
        {
            string f = Path.Combine(dir, $"diag-2026010{i}-000000.json");
            File.WriteAllText(f, new string('x', 1000));
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-10 + i));
        }
        long Total() => new[] { "summary-*.csv", "alerts-*.jsonl", "diag-*.json" }.SelectMany(p => Directory.GetFiles(dir, p)).Sum(f => new FileInfo(f).Length);
        long limit = Total() - 2500;
        w.Cleanup(30, limit, DateTime.UtcNow);
        check(Total() <= limit && !File.Exists(Path.Combine(dir, "diag-20260100-000000.json")) && File.Exists(Path.Combine(dir, "diag-20260104-000000.json")),
            "size limit removes oldest first");
        check(File.Exists(foreign), "foreign file kept under size limit");
    }
}
