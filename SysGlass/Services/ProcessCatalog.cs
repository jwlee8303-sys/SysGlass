using System.Diagnostics;
using System.IO;
using SysGlass.Models;

namespace SysGlass.Services;

/// <summary>
/// 프로세스 이름별 설명과 중요도를 제공한다.
/// 잘 알려진 프로세스는 내장 목록을 쓰고, 그 외에는 실행 파일의 버전 정보와 위치로 추정한다.
/// </summary>
public static class ProcessCatalog
{
    private static readonly Dictionary<string, ProcessInfo> Known = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>내장 목록에 없는 프로세스의 추정 결과. 같은 이름이라도 실행 경로가 다르면 다른 프로그램일 수 있어 경로를 키로 쓴다.</summary>
    private const int MaxResolvedPaths = 512;
    private static readonly Dictionary<string, ProcessInfo> Resolved = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> ResolvedOrder = new();
    private static readonly string WindowsDirPrefix =
        Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;

    private static void A(Importance level, string description, params string[] names)
    {
        foreach (var n in names) Known[n] = new ProcessInfo(description, level);
    }

    static ProcessCatalog()
    {
        // ── 핵심: 종료하면 시스템이 중단된다 ──
        A(Importance.Critical, "Windows 커널과 시스템 스레드. 시스템 전반의 CPU/드라이버 작업을 처리합니다.", "System");
        A(Importance.Critical, "레지스트리 데이터를 메모리에 유지하는 커널 프로세스.", "Registry");
        A(Importance.Critical, "세션 관리자. 사용자 세션을 만들고 시스템 초기화를 담당합니다.", "smss");
        A(Importance.Critical, "클라이언트/서버 런타임. 콘솔 창과 프로세스·스레드 생성을 담당합니다.", "csrss");
        A(Importance.Critical, "Windows 시작 프로세스. 부팅 중 핵심 서비스를 시작합니다.", "wininit");
        A(Importance.Critical, "로그온 관리자. 로그인/로그아웃과 화면 잠금을 처리합니다.", "winlogon");
        A(Importance.Critical, "서비스 제어 관리자. 모든 Windows 서비스를 시작·중지·관리합니다.", "services");
        A(Importance.Critical, "로컬 보안 기관. 로그인 인증과 보안 정책을 담당합니다.", "lsass");
        A(Importance.Critical, "Credential Guard 격리 프로세스. 자격 증명을 안전하게 보호합니다.", "lsaiso");
        A(Importance.Critical, "VBS 보안 커널 프로세스. 가상화 기반 보안을 담당합니다.", "Secure System");
        A(Importance.Critical, "메모리 압축 프로세스. 사용하지 않는 메모리를 압축해 RAM을 절약합니다.", "Memory Compression", "MemCompression");

        // ── 높음: 종료하면 주요 기능에 장애 ──
        A(Importance.High, "서비스 호스트. 여러 Windows 서비스를 묶어 실행합니다. (네트워크, 오디오, 업데이트 등)", "svchost");
        A(Importance.High, "데스크톱 창 관리자. 화면 합성, 창 효과, 투명도를 처리합니다.", "dwm");
        A(Importance.High, "Windows 탐색기. 바탕화면, 작업 표시줄, 파일 탐색기를 담당합니다.", "explorer");
        A(Importance.High, "셸 인프라 호스트. 작업 표시줄·알림 등 사용자 인터페이스 요소를 처리합니다.", "sihost");
        A(Importance.High, "Windows 작업의 호스트 프로세스. DLL 기반 작업을 실행합니다.", "taskhostw");
        A(Importance.High, "입력기/대체 사용자 입력(한글 입력 등)을 처리합니다.", "ctfmon", "TextInputHost");
        A(Importance.High, "Windows 오디오 장치 그래프. 소리 출력을 담당합니다.", "audiodg");
        A(Importance.High, "시작 메뉴 UI를 표시합니다.", "StartMenuExperienceHost");
        A(Importance.High, "셸 UI 구성 요소(작업 보기, 알림 센터 등)를 표시합니다.", "ShellExperienceHost");
        A(Importance.High, "UWP 앱 창 프레임을 관리합니다.", "ApplicationFrameHost");
        A(Importance.High, "UWP 앱 권한을 관리하는 런타임 브로커.", "RuntimeBroker");
        A(Importance.High, "Windows Defender 백신 실시간 보호 엔진.", "MsMpEng", "NisSrv", "MpDefenderCoreService");
        A(Importance.High, "Windows 보안 상태 모니터링 서비스.", "SecurityHealthService", "SecurityHealthSystray");
        A(Importance.High, "SmartScreen. 악성 앱/사이트를 걸러냅니다.", "smartscreen");
        A(Importance.High, "인쇄 스풀러. 프린터 작업을 관리합니다.", "spoolsv");
        A(Importance.High, "사용자 모드 드라이버 호스트. 장치 드라이버를 실행합니다.", "WUDFHost");
        A(Importance.High, "WMI 공급자 호스트. 시스템 관리 정보를 제공합니다.", "WmiPrvSE");
        A(Importance.High, "잠금 화면을 표시합니다.", "LockApp");
        A(Importance.High, "NVIDIA 드라이버 컨테이너. 그래픽 드라이버 기능을 제공합니다.", "NVDisplay.Container", "nvcontainer");
        A(Importance.High, "AMD 외부 이벤트 유틸리티. 그래픽 드라이버 기능을 제공합니다.", "AMDRSServ", "atiesrxx", "atieclxx");
        A(Importance.High, "Realtek 오디오 관리 서비스.", "RtkAudUService64", "RAVBg64");
        A(Importance.High, "Windows 설치/업데이트 서비스.", "TrustedInstaller", "TiWorker", "wuauclt", "MoUsoCoreWorker", "usocoreworker");
        A(Importance.Normal, "소프트웨어 보호 서비스. Windows와 앱의 디지털 라이선스 확인·갱신을 담당합니다.", "sppsvc");
        A(Importance.Normal, "가상 디스크 서비스. 디스크·볼륨 관리 도구가 사용하며 평소에는 거의 쉬고 있습니다.", "vds");

        // ── 보통: 일반 프로그램 ──
        A(Importance.Normal, "콘솔 창 호스트. 명령 프롬프트/터미널 창을 그립니다.", "conhost", "OpenConsole");
        A(Importance.Normal, "Windows 터미널.", "WindowsTerminal", "wt");
        A(Importance.Normal, "명령 프롬프트(cmd).", "cmd");
        A(Importance.Normal, "PowerShell 셸.", "powershell", "pwsh");
        A(Importance.Normal, "COM 서로게이트. 확장 프로그램을 별도 프로세스로 실행합니다.", "dllhost");
        A(Importance.Normal, "백그라운드 작업 호스트. UWP 앱의 백그라운드 작업을 실행합니다.", "backgroundTaskHost");
        A(Importance.Normal, "Windows 검색 UI.", "SearchHost", "SearchApp");
        A(Importance.Normal, "Google Chrome 웹 브라우저. (탭·확장 프로그램별로 여러 프로세스)", "chrome");
        A(Importance.Normal, "Microsoft Edge 웹 브라우저. (탭·확장 프로그램별로 여러 프로세스)", "msedge", "msedgewebview2");
        A(Importance.Normal, "Mozilla Firefox 웹 브라우저.", "firefox");
        A(Importance.Normal, "Naver Whale 웹 브라우저.", "whale");
        A(Importance.Normal, "Visual Studio Code 편집기.", "Code");
        A(Importance.Normal, "Visual Studio 통합 개발 환경.", "devenv");
        A(Importance.Normal, "Visual Studio 서비스 허브/컴파일러 보조 프로세스.", "ServiceHub.Host.Node.x86", "ServiceHub.IdentityHost", "ServiceHub.SettingsHost", "ServiceHub.VSDetouredHost", "ServiceHub.Host.dotnet.x64", "VBCSCompiler", "MSBuild");
        A(Importance.Normal, ".NET 런타임으로 실행 중인 앱 또는 빌드 도구.", "dotnet");
        A(Importance.Normal, "Node.js 런타임으로 실행 중인 앱 또는 개발 도구.", "node");
        A(Importance.Normal, "Python 인터프리터.", "python", "pythonw", "py");
        A(Importance.Normal, "Git 버전 관리 도구.", "git");
        A(Importance.Normal, "Claude 데스크톱/코드 도구.", "claude", "Claude");
        A(Importance.Normal, "Microsoft Teams 협업 도구.", "ms-teams", "Teams");
        A(Importance.Normal, "Slack 메신저.", "slack");
        A(Importance.Normal, "Discord 메신저.", "Discord");
        A(Importance.Normal, "카카오톡 메신저.", "KakaoTalk");
        A(Importance.Normal, "Zoom 화상 회의.", "Zoom");
        A(Importance.Normal, "Steam 게임 플랫폼.", "steam", "steamwebhelper");
        A(Importance.Normal, "Spotify 음악 재생기.", "Spotify");
        A(Importance.Normal, "메모장.", "notepad");
        A(Importance.Normal, "계산기.", "CalculatorApp");
        A(Importance.Normal, "Microsoft Word 문서 편집기.", "WINWORD");
        A(Importance.Normal, "Microsoft Excel 스프레드시트.", "EXCEL");
        A(Importance.Normal, "Microsoft PowerPoint 프레젠테이션.", "POWERPNT");
        A(Importance.Normal, "Microsoft Outlook 메일.", "OUTLOOK");
        A(Importance.Normal, "작업 관리자.", "Taskmgr");
        A(Importance.Normal, "SysGlass. 현재 실행 중인 이 모니터링 프로그램입니다.", "SysGlass");
        A(Importance.Normal, "LibreHardwareMonitor 센서 수집 프로그램.", "LibreHardwareMonitor");
        A(Importance.Normal, "Windows 시스템 정보/설정 UI.", "SystemSettings", "ms-settings");

        // ── 낮음: 종료해도 무방 ──
        A(Importance.Low, "Microsoft OneDrive 파일 동기화. 종료하면 동기화만 멈춥니다.", "OneDrive", "OneDrive.Sync.Service");
        A(Importance.Low, "Windows 검색 색인 서비스. 종료하면 검색이 느려질 수 있습니다.", "SearchIndexer", "SearchProtocolHost", "SearchFilterHost");
        A(Importance.Low, "Windows 위젯. 종료해도 사용에 지장이 없습니다.", "Widgets", "WidgetService");
        A(Importance.Low, "휴대폰 연결(Phone Link) 앱 구성 요소.", "PhoneExperienceHost", "YourPhone");
        A(Importance.Low, "Xbox 게임 바. 게임 녹화·오버레이 기능.", "GameBar", "GameBarPresenceWriter", "GameBarFTServer");
        A(Importance.Low, "Microsoft Edge 자동 업데이트.", "MicrosoftEdgeUpdate");
        A(Importance.Low, "Google 자동 업데이트.", "GoogleUpdate", "GoogleCrashHandler", "GoogleCrashHandler64");
        A(Importance.Low, "Microsoft Office 클릭-투-런(업데이트/스트리밍) 서비스.", "OfficeClickToRun", "OfficeC2RClient");
        A(Importance.Low, "Windows 팁/제안/광고 콘텐츠 표시 구성 요소.", "ContentDeliveryManager", "SystemSettingsBroker");
        A(Importance.Low, "Windows 진단·원격 분석 데이터 수집.", "CompatTelRunner", "DiagTrack", "wermgr", "WerFault");
        A(Importance.Low, "Adobe 백그라운드 보조 서비스.", "AdobeUpdateService", "AdobeIPCBroker", "armsvc", "CCXProcess", "CoreSync");
        A(Importance.Low, "Java 자동 업데이트.", "jusched", "jucheck");
    }

    /// <summary>내장 목록에 있는 이름인지. 아니면 실행 파일 경로로 설명을 추정해야 한다.</summary>
    public static bool IsKnown(string name) => Known.ContainsKey(name);

    /// <summary>
    /// 이름에 해당하는 정보를 돌려준다. 내장 목록에 없으면 실행 파일 경로로 추정하며 경로별로 캐시한다.
    /// 경로를 모르면 캐시하지 않아 다음 수집 때 다시 시도한다.
    /// <paramref name="mixedPaths"/>가 true면 같은 이름의 다른 경로 프로세스가 섞여 있음을 설명에 덧붙인다.
    /// </summary>
    public static ProcessInfo Get(string name, string? imagePath, bool mixedPaths = false)
    {
        if (Known.TryGetValue(name, out var known)) return known;
        if (string.IsNullOrEmpty(imagePath))
            return new ProcessInfo("설명 정보를 확인할 수 없습니다. (접근 권한이 없는 프로세스)", Importance.Normal);

        ProcessInfo info;
        lock (Resolved)
        {
            if (!Resolved.TryGetValue(imagePath, out var cached))
            {
                info = Resolve(name, imagePath);
                if (Resolved.Count >= MaxResolvedPaths) Resolved.Remove(ResolvedOrder.Dequeue());
                Resolved.Add(imagePath, info);
                ResolvedOrder.Enqueue(imagePath);
            }
            else info = cached;
        }
        return mixedPaths
            ? info with { Description = info.Description + " (실행 경로가 서로 다른 동명 프로세스 포함)" }
            : info;
    }

    /// <summary>경로를 정규화한 뒤 Windows 폴더 안인지 디렉터리 경계까지 맞춰 판정한다. (C:\WindowsBackup은 제외)</summary>
    private static bool IsUnderWindows(string path)
    {
        try { return Path.GetFullPath(path).StartsWith(WindowsDirPrefix, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static ProcessInfo Resolve(string name, string path)
    {
        string? description = null, company = null;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            description = string.IsNullOrWhiteSpace(v.FileDescription) ? v.ProductName : v.FileDescription;
            company = v.CompanyName;
        }
        catch { /* 버전 정보를 읽지 못해도 계속 진행 */ }

        bool inWindows = IsUnderWindows(path);
        bool microsoft = company?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;

        var text = description?.Trim();
        if (string.IsNullOrEmpty(text)) text = name + (inWindows ? " (Windows 구성 요소)" : "");
        else if (!string.IsNullOrWhiteSpace(company) && !text.Contains(company.Trim(), StringComparison.OrdinalIgnoreCase))
            text += $" — {company.Trim()}";

        // Windows 폴더의 Microsoft 구성 요소는 시스템 기능일 가능성이 높아 '높음', 그 외는 '보통'으로 본다.
        var level = inWindows && microsoft ? Importance.High : Importance.Normal;
        return new ProcessInfo(text, level);
    }
}
