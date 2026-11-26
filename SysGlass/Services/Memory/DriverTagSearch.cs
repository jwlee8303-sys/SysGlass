using System.IO;
using System.Text;

namespace SysGlass.Services.Memory;

/// <summary>
/// 드라이버 파일(.sys) 안에서 Pool Tag 문자열을 찾아 '후보'를 제시한다(findstr /m 방식과 같은 원리, 외부 프로세스 없이 수행).
/// 문자열이 들어 있다는 사실만 알려줄 뿐 해당 드라이버가 할당 주체이거나 누수 원인이라는 뜻은 아니다.
/// 사용자 요청 시에만 백그라운드에서 실행하며 취소할 수 있다.
/// </summary>
public static class DriverTagSearch
{
    public const long MaxFileBytes = 64L * 1024 * 1024;
    public const string Disclaimer = "태그 문자열이 파일 안에 있다는 뜻일 뿐이며, 실제 할당 주체나 누수 원인을 확정하지 않습니다. " +
        "커널(ntoskrnl.exe)도 함께 검색하며, 같은 문자열이 우연히 들어 있는 파일도 후보로 나올 수 있습니다.";

    public sealed record Candidate(string Path, string? Company, string? Version);
    /// <param name="SubstringOnly">태그가 더 긴 문자열의 일부로만 나온 파일 수(예: Token 안의 Toke). 후보 목록에서 뺀다.</param>
    public sealed record Result(string Tag, int Scanned, int Skipped, IReadOnlyList<Candidate> Candidates, int SubstringOnly, string? Error);

    /// <summary>검색할 수 있는 태그인지(출력 가능한 ASCII 4글자).</summary>
    public static bool IsSearchable(string tag) => tag.Length == 4 && tag.All(c => c is >= ' ' and < (char)127 && c != '?');

    public static Result Search(string tag, IEnumerable<string> extraFiles, CancellationToken token)
    {
        if (!IsSearchable(tag)) return new Result(tag, 0, 0, [], 0, "출력할 수 없는 문자가 있는 태그는 검색할 수 없습니다.");
        byte[] needle = Encoding.ASCII.GetBytes(tag);
        string driversDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers");
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var f in Directory.EnumerateFiles(driversDir, "*.sys")) files.Add(f); }
        catch (Exception ex) { return new Result(tag, 0, 0, [], 0, "드라이버 폴더를 읽을 수 없음: " + ex.Message); }
        foreach (var f in extraFiles)
            if (f.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) && File.Exists(f)) files.Add(f);
        string kernel = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ntoskrnl.exe");
        if (File.Exists(kernel)) files.Add(kernel);

        var found = new List<Candidate>();
        int scanned = 0, skipped = 0, substringOnly = 0;
        // 파일 전체를 읽지 않고 고정 크기 버퍼로 나눠 읽는다(할당량을 파일 크기와 무관하게 유지).
        var buffer = new byte[ChunkBytes + Overlap];
        foreach (var f in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(f);
                if (info.Length > MaxFileBytes) { skipped++; continue; }
                var match = FindInFile(f, needle, buffer, token);
                if (match == Match.Substring) substringOnly++;
                if (match == Match.Standalone)
                {
                    var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(f);
                    found.Add(new Candidate(f, v.CompanyName, v.FileVersion));
                }
                scanned++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new Result(tag, scanned, skipped, found, substringOnly, null);
    }

    private const int ChunkBytes = 1024 * 1024, Overlap = 8;

    /// <summary>
    /// 파일을 나눠 읽으며 찾는다. 앞 조각의 마지막 8바이트를 다음 조각 앞에 붙여 경계에 걸친 태그와 앞뒤 문맥을 놓치지 않는다.
    /// 마지막 조각이 아니면 뒤 문맥을 확인할 수 없는 끝부분 일치는 다음 조각에서 다시 판정한다.
    /// </summary>
    internal static Match FindInFile(string path, ReadOnlySpan<byte> needle, byte[] buffer, CancellationToken token)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var result = Match.None;
        int carry = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int filled = carry, n;
            while (filled < buffer.Length && (n = fs.Read(buffer, filled, buffer.Length - filled)) > 0) filled += n;
            bool final = filled < buffer.Length;
            var m = Find(buffer.AsSpan(0, filled), needle, final);
            if (m == Match.Standalone) return m;
            if (m > result) result = m;
            if (final) return result;
            Buffer.BlockCopy(buffer, filled - Overlap, buffer, 0, Overlap);
            carry = Overlap;
        }
    }

    internal enum Match { None, Substring, Standalone }

    /// <summary>
    /// 태그 바이트를 찾는다. 코드 안의 4바이트 상수는 앞이 명령어 바이트(영문자·숫자 아님)이고,
    /// 문자열의 일부(예: 'Token'의 'Toke')는 앞이 영문자이거나 뒤가 소문자로 이어진다. 이 기준으로 독립 일치를 가린다.
    /// 보호 풀(Protected pool) 태그는 마지막 바이트의 최상위 비트가 켜진 형태로 저장되므로 그 형태도 함께 찾는다.
    /// </summary>
    internal static Match Find(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, bool final = true)
    {
        Span<byte> protectedTag = stackalloc byte[4];
        needle.CopyTo(protectedTag);
        protectedTag[3] |= 0x80;
        var a = FindOne(haystack, needle, checkAfter: true, final);
        if (a == Match.Standalone) return a;
        var b = FindOne(haystack, protectedTag, checkAfter: false, final);
        return (Match)Math.Max((int)a, (int)b);
    }

    private static Match FindOne(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, bool checkAfter, bool final)
    {
        var result = Match.None;
        int offset = 0;
        while (offset < haystack.Length)
        {
            int i = haystack[offset..].IndexOf(needle);
            if (i < 0) return result;
            int at = offset + i;
            if (!final && at + needle.Length >= haystack.Length) return result; // 다음 조각에서 문맥과 함께 다시 본다
            bool before = at > 0 && IsWord(haystack[at - 1]);
            bool after = checkAfter && at + needle.Length < haystack.Length && haystack[at + needle.Length] is >= (byte)'a' and <= (byte)'z';
            if (!before && !after) return Match.Standalone;
            result = Match.Substring;
            offset = at + 1;
        }
        return result;
    }

    private static bool IsWord(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_';
}
