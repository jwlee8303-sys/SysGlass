using System.Runtime.InteropServices;

namespace SysGlass.Utilities;

/// <summary>확장 윈도우 스타일을 안전하게 변경하는 Win32 래퍼. 필요한 비트만 추가/제거한다.</summary>
internal static class WindowStyleHelper
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_LAYERED = 0x00080000;

    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

    // x64 전용 빌드이므로 *Ptr 엔트리를 직접 사용한다.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>제목 표시줄을 어두운 테마로 표시한다(Windows 10 2004 이상). 실패해도 무시한다.</summary>
    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        try { int on = 1; DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); } catch { /* 지원하지 않는 환경 */ }
    }

    /// <summary>클릭 통과 on/off. 다른 확장 스타일 비트는 보존한다. 실패 시 false.</summary>
    public static bool SetClickThrough(IntPtr hwnd, bool enable)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;

        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        long updated = enable
            ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED   // 투명 입력에는 LAYERED가 함께 필요
            : style & ~WS_EX_TRANSPARENT;                  // LAYERED는 WPF(AllowsTransparency)가 사용하므로 유지

        if (updated != style)
        {
            Marshal.SetLastSystemError(0);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(updated));
            if (Marshal.GetLastSystemError() != 0) return false;
            // 순서/크기/활성 상태는 바꾸지 않고 스타일 변경만 반영
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        return (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TRANSPARENT) != 0 == enable;
    }
}
