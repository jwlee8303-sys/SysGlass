using System.IO;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace SysGlass.Utilities;

/// <summary>진단용 센서 목록 덤프. 센서 지원 여부 확인에 사용한다.</summary>
internal static class SensorDump
{
    public static void Write(string path)
    {
        var sb = new StringBuilder();
        try
        {
            // PawnIO 드라이버 설치 여부(LHM이 MSR/SuperIO 접근에 사용)를 레지스트리로 확인
            using (var k = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                       .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
                sb.AppendLine("PawnIO 설치: " + (k == null ? "아니오" : "예 (버전 " + k.GetValue("DisplayVersion") + ")"));
            sb.AppendLine("관리자 권한: " + new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
            var c = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsStorageEnabled = true, IsMemoryEnabled = true, IsMotherboardEnabled = true };
            c.Open();
            // 일부 센서는 두 번째 갱신부터 값이 채워진다.
            foreach (var hw in c.Hardware) hw.Update();
            Thread.Sleep(1500);
            foreach (var hw in c.Hardware) Dump(hw, sb, 0);
            c.Close();
        }
        catch (Exception ex) { sb.AppendLine("오류: " + ex); }
        File.WriteAllText(path, sb.ToString());
    }

    private static void Dump(IHardware hw, StringBuilder sb, int depth)
    {
        hw.Update();
        string pad = new(' ', depth * 2);
        sb.AppendLine($"{pad}[{hw.HardwareType}] {hw.Name} ({hw.Identifier})");
        foreach (var s in hw.Sensors)
            sb.AppendLine($"{pad}  {s.SensorType,-12} {s.Name} = {s.Value?.ToString() ?? "null"}");
        foreach (var sub in hw.SubHardware) Dump(sub, sb, depth + 1);
    }
}
