using System.IO;

namespace SysGlass.Utilities;

/// <summary>간단한 파일 로거. 오류만 기록하며 512KB를 넘으면 이전 로그를 .old로 교체해 무한 증가를 막는다.</summary>
internal static class Logger
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(AppPaths.DataDir, "sysglass.log");

    public static void Error(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var fi = new FileInfo(Path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = Path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(Path, old);
                }
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{(ex is null ? "" : " : " + ex.Message)}{Environment.NewLine}");
            }
        }
        catch
        {
            // 로깅 실패는 무시한다.
        }
    }
}
