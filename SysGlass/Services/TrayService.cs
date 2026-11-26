using System.Drawing;
using System.Windows.Forms;
using SysGlass.Utilities;

namespace SysGlass.Services;

/// <summary>알림 영역 아이콘과 우클릭 메뉴. WinForms NotifyIcon(.NET 기본 제공)을 사용한다.</summary>
public sealed class TrayService : IDisposable
{
    private NotifyIcon? _icon;
    private Icon? _ownedIcon;
    private ToolStripMenuItem? _toggleVisible, _topmost, _clickThrough, _settings;

    public bool IsReady => _icon != null;

    /// <summary>메뉴를 열 때마다 현재 상태(체크 표시)를 가져오기 위한 콜백.</summary>
    public Func<bool>? GetTopmost { get; set; }
    public Func<bool>? GetClickThrough { get; set; }
    public Func<bool>? GetVisible { get; set; }

    /// <param name="memoryMenu">메모리 감시 메뉴 항목(표시 문구, 동작). 없으면 생략한다.</param>
    public bool Initialize(Action toggleVisible, Action toggleTopmost, Action toggleClickThrough, Action openSettings, Action exit,
        IReadOnlyList<(string Text, Action Click)>? memoryMenu = null)
    {
        try
        {
            var menu = new ContextMenuStrip();
            _toggleVisible = new ToolStripMenuItem("모니터 표시/숨기기", null, (_, _) => toggleVisible());
            _topmost = new ToolStripMenuItem("항상 위에 표시", null, (_, _) => toggleTopmost());
            _clickThrough = new ToolStripMenuItem("마우스 클릭 통과 (Ctrl+Alt+F10)", null, (_, _) => toggleClickThrough());
            _settings = new ToolStripMenuItem("설정...", null, (_, _) => openSettings());
            var quit = new ToolStripMenuItem("종료", null, (_, _) => exit());
            menu.Items.AddRange([_toggleVisible, _topmost, _clickThrough, _settings]);
            if (memoryMenu is { Count: > 0 })
            {
                menu.Items.Add(new ToolStripSeparator());
                foreach (var (text, click) in memoryMenu) menu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => click()));
            }
            menu.Items.AddRange([new ToolStripSeparator(), quit]);
            menu.Opening += (_, _) =>
            {
                _topmost.Checked = GetTopmost?.Invoke() ?? false;
                _clickThrough.Checked = GetClickThrough?.Invoke() ?? false;
            };

            _ownedIcon = CreateIcon();
            _icon = new NotifyIcon
            {
                Icon = _ownedIcon,
                Text = "SysGlass",
                ContextMenuStrip = menu,
                Visible = true
            };
            _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) toggleVisible(); };
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("트레이 초기화 실패", ex);
            Dispose();
            return false;
        }
    }

    /// <summary>알림 영역 알림을 표시한다(Windows 11에서는 알림 센터 토스트로 표시). UI 스레드에서 호출해야 한다.</summary>
    public void ShowNotification(string title, string text, bool warning)
    {
        try
        {
            // 풍선 알림 문자열은 길이 제한이 있어 넘치면 잘라낸다.
            _icon?.ShowBalloonTip(10_000, Trim(title, 63), Trim(text, 255), warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch (Exception ex) { Logger.Error("트레이 알림 표시 실패", ex); }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>간단한 유리 느낌의 아이콘을 코드로 그린다.</summary>
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(255, 28, 28, 31));
            using var bar1 = new SolidBrush(Color.FromArgb(255, 76, 194, 255));
            using var bar2 = new SolidBrush(Color.FromArgb(255, 156, 219, 127));
            using var pen = new Pen(Color.FromArgb(255, 200, 200, 200), 2);
            g.FillEllipse(bg, 1, 1, 30, 30);
            g.DrawEllipse(pen, 1, 1, 30, 30);
            g.FillRectangle(bar1, 8, 11, 16, 4);
            g.FillRectangle(bar2, 8, 18, 11, 4);
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { WindowStyleHelper.DestroyIcon(h); }
    }

    public void Dispose()
    {
        try
        {
            if (_icon != null)
            {
                _icon.Visible = false; // 종료 후 아이콘이 남지 않도록 먼저 숨긴다.
                _icon.ContextMenuStrip?.Dispose();
                _icon.Dispose();
            }
            _ownedIcon?.Dispose();
        }
        catch (Exception ex) { Logger.Error("트레이 해제 실패", ex); }
        _icon = null;
        _ownedIcon = null;
    }
}
