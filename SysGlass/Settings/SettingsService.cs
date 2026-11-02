using System.IO;
using System.Text.Json;
using SysGlass.Utilities;

namespace SysGlass.Settings;

/// <summary>%AppData%\SysGlass\settings.json 로드/저장. 손상된 파일은 .bak으로 보존하고 기본값을 사용한다.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly string FilePath = Path.Combine(AppPaths.DataDir, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
            s.Normalize();
            return s;
        }
        catch (Exception ex)
        {
            Logger.Error("설정 파일 로드 실패, 기본값 사용", ex);
            try { File.Copy(FilePath, FilePath + ".bak", true); } catch { /* 무시 */ }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            // 임시 파일에 쓴 뒤 교체해 저장 중 손상을 방지한다.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            Logger.Error("설정 저장 실패", ex);
        }
    }
}
