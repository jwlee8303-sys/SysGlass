using System.Windows.Interop;
using SysGlass.Utilities;

namespace SysGlass.Services;

/// <summary>전역 단축키(Ctrl+Alt+F10) 등록. 이미 사용 중이면 등록을 포기하고 프로그램은 계속 동작한다.</summary>
public sealed class HotKeyService : IDisposable
{
    private const int HotKeyId = 0x5347;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const uint VK_F10 = 0x79;

    private HwndSource? _source;
    private IntPtr _hwnd;
    private Action? _callback;

    public bool IsRegistered { get; private set; }

    public bool Register(IntPtr hwnd, Action onPressed)
    {
        try
        {
            _hwnd = hwnd;
            _callback = onPressed;
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WndProc);
            IsRegistered = WindowStyleHelper.RegisterHotKey(hwnd, HotKeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_F10);
            if (!IsRegistered) Logger.Error("전역 단축키 Ctrl+Alt+F10 등록 실패(이미 사용 중일 수 있음)");
        }
        catch (Exception ex)
        {
            Logger.Error("전역 단축키 등록 예외", ex);
            IsRegistered = false;
        }
        return IsRegistered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            try { _callback?.Invoke(); } catch (Exception ex) { Logger.Error("단축키 처리 실패", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        try
        {
            if (IsRegistered) WindowStyleHelper.UnregisterHotKey(_hwnd, HotKeyId);
            _source?.RemoveHook(WndProc);
        }
        catch (Exception ex) { Logger.Error("단축키 해제 실패", ex); }
        IsRegistered = false;
        _source = null;
    }
}
