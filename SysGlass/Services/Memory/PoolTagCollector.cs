using System.Runtime.InteropServices;
using SysGlass.Models;
using SysGlass.Utilities;

namespace SysGlass.Services.Memory;

/// <summary>
/// NtQuerySystemInformation(SystemPoolTagInformation)으로 Pool Tag별 사용량을 읽는다.
/// 문서화가 충분하지 않은 API이므로 x64 구조(헤더 8바이트 + 항목당 40바이트)와 반환 길이가 맞지 않으면 실패로 처리한다.
/// 실패는 예외 대신 Error가 있는 보고서로 돌려주며, 일반 모니터링에 영향을 주지 않는다. 한 스레드에서만 호출해야 한다.
/// </summary>
public sealed class PoolTagCollector : IDisposable
{
    internal const int HeaderBytes = 8, EntryBytes = 40;
    private const int MaxTags = 200_000;

    private readonly NtSystemInfoBuffer? _buffer;
    private readonly string? _unsupported;

    public PoolTagCollector()
    {
        if (IntPtr.Size != 8) _unsupported = "64비트 환경에서만 지원";
        else _buffer = new NtSystemInfoBuffer(256 * 1024);
    }

    public (IReadOnlyList<PoolTagEntry> Tags, string? Error) Read()
    {
        if (_buffer is null) return ([], _unsupported);
        if (!_buffer.Query(NativeMethods.SystemPoolTagInformation, out int length, out var error)) return ([], error);
        return Parse(_buffer.Pointer, length);
    }

    /// <summary>버퍼를 해석한다. 검사용으로 분리했다.</summary>
    internal static (IReadOnlyList<PoolTagEntry> Tags, string? Error) Parse(IntPtr p, int length)
    {
        if (length < HeaderBytes) return ([], "반환 데이터가 너무 짧음");
        int count = Marshal.ReadInt32(p);
        if (count < 0 || count > MaxTags || HeaderBytes + (long)count * EntryBytes > length)
            return ([], $"구조 불일치 (항목 {count}개, {length}바이트) · Windows 변경 가능성");

        var tags = new PoolTagEntry[count];
        for (int i = 0; i < count; i++)
        {
            IntPtr e = p + HeaderBytes + i * EntryBytes;
            uint key = unchecked((uint)Marshal.ReadInt32(e));
            tags[i] = new PoolTagEntry(key, TagText(key),
                PagedUsed: unchecked((ulong)Marshal.ReadInt64(e + 16)),
                NonPagedUsed: unchecked((ulong)Marshal.ReadInt64(e + 32)),
                PagedAllocs: unchecked((uint)Marshal.ReadInt32(e + 4)),
                PagedFrees: unchecked((uint)Marshal.ReadInt32(e + 8)),
                NonPagedAllocs: unchecked((uint)Marshal.ReadInt32(e + 24)),
                NonPagedFrees: unchecked((uint)Marshal.ReadInt32(e + 28)));
        }
        return (tags, null);
    }

    /// <summary>태그 4바이트를 문자열로 바꾼다. 출력할 수 없는 문자는 '?'로 표시한다.</summary>
    public static string TagText(uint key) => string.Create(4, key, (span, k) =>
    {
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(k >> (8 * i));
            span[i] = b is >= 32 and < 127 ? (char)b : '?';
        }
    });

    public void Dispose() => _buffer?.Dispose();
}
