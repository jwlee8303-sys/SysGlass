using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SysGlass.Models;
using SysGlass.Utilities;

namespace SysGlass.Services.Memory;

/// <summary>
/// Windows 버전·빠른 시작 설정(레지스트리 읽기)과 실행 중인 드라이버·서비스 목록(WMI)을 수집한다.
/// 드라이버·서비스 목록은 시작 시와 이상 감지 시에만 조회한다. 서비스 실행 경로는 인자가 포함될 수 있어 수집하지 않는다.
/// </summary>
public static class SystemInfoCollector
{
    public static SystemInfo ReadSystemInfo()
    {
        int? major = null, minor = null, build = null, ubr = null;
        string? product = null, display = null;
        bool? hiberboot = null, hibernate = null;
        try
        {
            var v = new NativeMethods.OSVERSIONINFOW { dwOSVersionInfoSize = (uint)Marshal.SizeOf<NativeMethods.OSVERSIONINFOW>() };
            if (NativeMethods.RtlGetVersion(ref v) == 0)
                (major, minor, build) = ((int)v.dwMajorVersion, (int)v.dwMinorVersion, (int)v.dwBuildNumber);
        }
        catch (Exception ex) { Logger.Error("OS 버전 조회 실패", ex); }
        try
        {
            using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            ubr = cv?.GetValue("UBR") as int?;
            display = cv?.GetValue("DisplayVersion") as string;
            product = cv?.GetValue("ProductName") as string;
            // Windows 11도 ProductName이 'Windows 10'으로 남아 있어 빌드 번호로 보정한다.
            if (product is not null && build >= 22000) product = product.Replace("Windows 10", "Windows 11");
            using var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            if (sm?.GetValue("HiberbootEnabled") is int hb) hiberboot = hb != 0;
            using var pw = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            if (pw?.GetValue("HibernateEnabled") is int he) hibernate = he != 0;
        }
        catch (Exception ex) { Logger.Error("시스템 정보 레지스트리 조회 실패", ex); }
        return new SystemInfo(product, display, major, minor, build, ubr, hiberboot, hibernate);
    }

    /// <summary>실행 중인 커널 드라이버. 실패하면 error에 사유를 담고 빈 목록을 돌려준다.</summary>
    public static List<DriverInfo> ReadDrivers(out string? error)
    {
        error = null;
        var list = new List<DriverInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, PathName FROM Win32_SystemDriver WHERE State = 'Running'");
            searcher.Options.Timeout = TimeSpan.FromSeconds(20);
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
                using (mo)
                {
                    string name = mo["Name"] as string ?? "";
                    string? path = NormalizeDriverPath(mo["PathName"] as string);
                    string? company = null, version = null;
                    if (path is not null && File.Exists(path))
                    {
                        try
                        {
                            var fv = FileVersionInfo.GetVersionInfo(path);
                            company = fv.CompanyName;
                            version = fv.FileVersion;
                        }
                        catch { /* 버전 정보가 없는 파일 */ }
                    }
                    list.Add(new DriverInfo(name, mo["DisplayName"] as string, path, company, version));
                }
        }
        catch (Exception ex) { error = "드라이버 목록 조회 실패: " + ex.Message; }
        return list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<ServiceInfo> ReadServices(out string? error)
    {
        error = null;
        var list = new List<ServiceInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, StartMode, ProcessId FROM Win32_Service WHERE State = 'Running'");
            searcher.Options.Timeout = TimeSpan.FromSeconds(20);
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
                using (mo)
                    list.Add(new ServiceInfo(mo["Name"] as string ?? "", mo["DisplayName"] as string, mo["StartMode"] as string,
                        mo["ProcessId"] is uint pid ? (int)pid : null));
        }
        catch (Exception ex) { error = "서비스 목록 조회 실패: " + ex.Message; }
        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>\SystemRoot\..., \??\C:\..., system32\... 형식의 드라이버 경로를 실제 파일 경로로 바꾼다.</summary>
    internal static string? NormalizeDriverPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string p = raw.Trim().Trim('"');
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(windows, p[12..]);
        else if (p.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(windows, p);
        return p;
    }
}
