using System.IO;

namespace SysGlass.Utilities;

internal static class AppPaths
{
    /// <summary>%AppData%\SysGlass</summary>
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysGlass");
}
