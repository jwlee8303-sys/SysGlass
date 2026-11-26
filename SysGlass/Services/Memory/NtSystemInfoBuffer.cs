using System.Runtime.InteropServices;
using SysGlass.Utilities;

namespace SysGlass.Services.Memory;

/// <summary>
/// NtQuerySystemInformation용 재사용 네이티브 버퍼. 버퍼가 부족하면 필요한 크기에 여유를 두고 다시 시도하며,
/// 상한(MaxBytes)을 넘지 않는다. Dispose 시 네이티브 메모리를 해제한다. 한 스레드에서만 사용해야 한다.
/// </summary>
internal sealed class NtSystemInfoBuffer : IDisposable
{
    public const int MaxBytes = 32 * 1024 * 1024;
    private const int MaxTries = 4;

    private IntPtr _buffer;
    private int _capacity;

    public NtSystemInfoBuffer(int initialBytes) => Grow(initialBytes);

    public IntPtr Pointer => _buffer;

    /// <summary>정보를 조회한다. 성공 시 length에 유효 바이트 수, 실패 시 error에 사유를 담는다.</summary>
    public bool Query(int infoClass, out int length, out string? error)
    {
        length = 0;
        error = null;
        if (_buffer == IntPtr.Zero) { error = "버퍼가 해제됨"; return false; }
        for (int i = 0; i < MaxTries; i++)
        {
            int status;
            try { status = NativeMethods.NtQuerySystemInformation(infoClass, _buffer, _capacity, out length); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                error = "시스템 정보 API를 사용할 수 없음";
                return false;
            }
            if (status == 0)
            {
                if (length <= 0 || length > _capacity) { error = $"반환 길이 이상 ({length})"; return false; }
                return true;
            }
            if (status != NativeMethods.STATUS_INFO_LENGTH_MISMATCH)
            {
                error = status == unchecked((int)0xC0000022) ? "권한 부족 (STATUS_ACCESS_DENIED)" : $"조회 실패 (NTSTATUS 0x{status:X8})";
                return false;
            }
            // 호출 사이에도 목록이 늘 수 있으므로 25% 여유를 둔다.
            long wanted = Math.Max((long)length, (long)_capacity * 2) * 5 / 4;
            if (wanted > MaxBytes) { error = $"필요한 버퍼가 상한({MaxBytes / 1024 / 1024}MB)을 넘음"; return false; }
            Grow((int)wanted);
        }
        error = "버퍼 크기를 맞추지 못함";
        return false;
    }

    private void Grow(int bytes)
    {
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
        _capacity = 0;
        _buffer = Marshal.AllocHGlobal(bytes);
        _capacity = bytes;
    }

    /// <summary>큰 버퍼를 오래 붙잡지 않도록 초기 크기로 되돌린다.</summary>
    public void Shrink(int bytes)
    {
        if (_capacity > bytes * 4) Grow(bytes);
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
        _capacity = 0;
    }
}
