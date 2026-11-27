using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SysGlass.Utilities;

namespace SysGlass.Services;

/// <summary>GitHub Releases에 게시된 최신 정식 버전 정보.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl,
    string AssetName, string AssetUrl, long AssetSize, string? Sha256);

/// <summary>
/// GitHub Releases의 포터블 ZIP으로 업데이트를 확인·적용한다.
/// 다운로드한 ZIP은 크기·SHA-256을 검증한 뒤 임시 폴더에 풀고, 앱 종료 후 교체 스크립트가 실행 파일을 바꾸고 다시 실행한다.
/// 설정(%APPDATA%\SysGlass)은 건드리지 않는다.
/// </summary>
public static class UpdateService
{
    public const string Repository = "jwlee8303-sys/SysGlass";
    private const string AssetSuffix = "-win-x64-portable.zip";
    private const string ExeName = "SysGlass.exe";
    private const string NoticesName = "THIRD-PARTY-NOTICES.txt";

    private static readonly HttpClient Http = CreateClient();
    private static int s_applying;

    /// <summary>현재 실행 중인 앱 버전(Major.Minor.Build).</summary>
    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>개발 빌드(bin\Debug, bin\Release)에서 실행 중이면 실행 파일 교체를 하지 않는다.</summary>
    public static bool CanApply
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path) || !Path.GetFileName(path).Equals(ExeName, StringComparison.OrdinalIgnoreCase)) return false;
            var dir = Path.GetDirectoryName(path) ?? "";
            return !dir.Contains(@"\bin\Debug", StringComparison.OrdinalIgnoreCase)
                && !dir.Contains(@"\bin\Release", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // 시간 제한은 호출마다 CancellationToken으로 건다.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SysGlass-Updater");
        return client;
    }

    private static Version ReadCurrentVersion()
    {
        var asm = typeof(UpdateService).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (info != null && TryParseVersion(info.Split('+')[0], out var v)) return v;
        var n = asm.GetName().Version ?? new Version(0, 0, 0);
        return new Version(n.Major, n.Minor, Math.Max(n.Build, 0));
    }

    /// <summary>"v1.4.0" 또는 "1.4.0"을 해석한다. 시험판(-beta 등)과 형식이 다른 값은 거부한다.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        if (s.Contains('-') || s.Contains('+')) return false;
        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        var nums = new int[3];
        for (int i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, null, out nums[i])) return false;
        version = new Version(nums[0], nums[1], nums[2]);
        return true;
    }

    /// <summary>GitHub releases/latest 응답을 해석한다. 초안·시험판·포터블 ZIP이 없는 릴리스는 null.</summary>
    public static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        var tag = Str(root, "tag_name");
        if (!TryParseVersion(tag, out var version)) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var a in assets.EnumerateArray())
        {
            var name = Str(a, "name");
            if (!name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            var url = Str(a, "browser_download_url");
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) continue;
            long size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0;
            var digest = Str(a, "digest");
            string? sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
            return new UpdateInfo(version, tag, Str(root, "body").Trim(), Str(root, "html_url"), name, url, size, sha);
        }
        return null;
    }

    /// <summary>
    /// 릴리스 노트에서 사용자에게 보여 줄 변경 요약을 만든다. "## 변경 사항" 절이 있으면 그 부분만 쓰고,
    /// 마크다운 제목·강조 기호를 걷어 낸 뒤 maxLength를 넘으면 자른다.
    /// </summary>
    public static string SummarizeNotes(string notes, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "";
        var lines = notes.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => l.TrimStart().StartsWith("## ") && l.Contains("변경 사항"));
        IEnumerable<string> section = lines;
        if (start >= 0)
            section = lines.Skip(start + 1).TakeWhile(l => !l.TrimStart().StartsWith("## "));

        var sb = new StringBuilder();
        foreach (var raw in section)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            line = line.TrimStart('#').Trim().Replace("**", "").Replace("`", "");
            if (line.StartsWith("- ") || line.StartsWith("* ")) line = "• " + line[2..];
            sb.AppendLine(line);
        }
        var text = sb.ToString().TrimEnd();
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "…";
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>최신 정식 버전을 조회한다. 현재 버전보다 높을 때만 정보를 돌려주고, 통신 실패는 예외로 전달한다.</summary>
    public static async Task<UpdateInfo?> CheckAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // 게시된 릴리스 없음
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        var info = ParseRelease(json);
        return info != null && info.Version > CurrentVersion ? info : null;
    }


    private const string WorkDirPrefix = "SysGlass-update-";

    /// <summary>
    /// 업데이트 파일을 내려받아 검증하고 교체 스크립트를 실행한다. 성공하면 호출자는 즉시 앱을 종료해야 한다.
    /// 이미 진행 중이면 false.
    /// </summary>
    /// <remarks>
    /// 작업 폴더(%TEMP%)는 일반 권한 프로세스도 쓸 수 있으므로 신뢰하지 않는다.
    /// ZIP은 내려받기부터 압축 해제까지 배타적으로 연 채 처리하고, 꺼낸 파일의 SHA-256을 교체 스크립트에 넘긴다.
    /// 스크립트는 파일로 남기지 않고(-EncodedCommand) 복사 직전에 파일을 메모리로 읽어 해시를 다시 확인한 뒤 그 내용을 쓴다.
    /// </remarks>
    public static async Task<bool> PrepareAndLaunchAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!CanApply) throw new InvalidOperationException("개발 빌드에서는 업데이트를 적용할 수 없습니다.");
        if (info.Sha256 == null) throw new InvalidOperationException("릴리스 파일의 해시 정보가 없어 업데이트를 진행하지 않습니다.");
        if (Interlocked.Exchange(ref s_applying, 1) == 1) return false;
        var work = Path.Combine(Path.GetTempPath(), $"{WorkDirPrefix}{info.Version}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(work);
            var staged = new Dictionary<string, string>(); // 꺼낸 파일 이름 → SHA-256
            await using (var zip = new FileStream(Path.Combine(work, info.AssetName), FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose))
            {
                await DownloadAsync(info, zip, progress, ct).ConfigureAwait(false);
                zip.Position = 0;
                ExtractAndVerify(zip, work, info.Version, staged);
            }
            LaunchApplyScript(work, staged);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("업데이트 준비 실패", ex);
            DeleteWorkDir(work);
            Interlocked.Exchange(ref s_applying, 0);
            throw;
        }
    }

    private static async Task DownloadAsync(UpdateInfo info, FileStream dst, IProgress<double>? progress, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        using var resp = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? info.AssetSize;

        using var sha = SHA256.Create();
        await using var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
            sha.TransformBlock(buffer, 0, read, null, 0);
            done += read;
            if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
        }
        sha.TransformFinalBlock([], 0, 0);
        await dst.FlushAsync(cts.Token).ConfigureAwait(false);
        if (info.AssetSize > 0 && done != info.AssetSize)
            throw new InvalidDataException("다운로드한 파일의 크기가 릴리스 정보와 다릅니다.");
        if (Convert.ToHexString(sha.Hash!).ToLowerInvariant() != info.Sha256)
            throw new InvalidDataException("다운로드한 파일의 SHA-256이 릴리스 정보와 일치하지 않습니다.");
    }

    /// <summary>
    /// ZIP에서 실행 파일과 라이선스 고지만 꺼내며 각 파일의 SHA-256을 함께 계산하고,
    /// 실행 파일 버전이 릴리스 태그와 같은지 확인한다.
    /// </summary>
    private static void ExtractAndVerify(Stream zipStream, string work, Version expected, Dictionary<string, string> staged)
    {
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var name in new[] { ExeName, NoticesName })
            {
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    if (name == ExeName) throw new InvalidDataException("업데이트 파일에 SysGlass.exe가 없습니다.");
                    continue;
                }
                using var sha = SHA256.Create();
                using (var src = entry.Open())
                using (var dst = new FileStream(Path.Combine(work, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var hashing = new CryptoStream(dst, sha, CryptoStreamMode.Write))
                    src.CopyTo(hashing);
                staged[name] = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }
        }
        // 버전 확인용 읽기. 교체 단계에서 해시를 다시 확인하므로 이 사이에 파일이 바뀌어도 설치되지 않는다.
        var product = FileVersionInfo.GetVersionInfo(Path.Combine(work, ExeName)).ProductVersion;
        if (!TryParseVersion(product?.Split('+')[0], out var actual) || actual != expected)
            throw new InvalidDataException($"업데이트 파일의 버전({product})이 릴리스({expected})와 다릅니다.");
    }

    /// <summary>PowerShell 작은따옴표 문자열 리터럴로 감싼다.</summary>
    public static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>교체 스크립트 본문을 만든다. 경로·해시는 리터럴로 넣고, 스크립트 파일은 만들지 않는다.</summary>
    public static string BuildApplyScript(int parentPid, string work, string target, string log, IReadOnlyDictionary<string, string> staged)
    {
        var notices = staged.TryGetValue(NoticesName, out var nh) ? nh : "";
        var header = string.Join("\n",
            $"$ParentPid = {parentPid}",
            $"$Work = {PsQuote(work)}",
            $"$Target = {PsQuote(target)}",
            $"$Log = {PsQuote(log)}",
            $"$ExeHash = {PsQuote(staged[ExeName])}",
            $"$NoticesHash = {PsQuote(notices)}");
        return header + "\n" + ApplyScriptBody;
    }

    /// <summary>앱 종료를 기다린 뒤 실행 파일을 교체하고 다시 실행하는 PowerShell을 띄운다. 실패하면 기존 파일로 되돌린다.</summary>
    private static void LaunchApplyScript(string work, IReadOnlyDictionary<string, string> staged)
    {
        var script = BuildApplyScript(Environment.ProcessId, work, Environment.ProcessPath!,
            Path.Combine(AppPaths.DataDir, "update.log"), staged);
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                     "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("업데이트 적용 프로세스를 시작할 수 없습니다.");
    }

    /// <summary>이전 업데이트에서 남은 백업(SysGlass.exe.old)과 임시 작업 폴더를 지운다. 실패해도 무시한다.</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (CanApply && path != null && File.Exists(path + ".old")) File.Delete(path + ".old");
        }
        catch (Exception ex) { Logger.Error("이전 업데이트 백업 정리 실패", ex); }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), WorkDirPrefix + "*"))
                DeleteWorkDir(dir);
        }
        catch (Exception ex) { Logger.Error("업데이트 임시 폴더 정리 실패", ex); }
    }

    /// <summary>작업 폴더를 지운다. 연결 지점(reparse point)이면 링크만 제거하고 대상은 따라가지 않는다.</summary>
    private static void DeleteWorkDir(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists) return;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { info.Delete(); return; }
            info.Delete(recursive: true); // .NET은 하위 연결 지점을 따라가지 않고 링크만 지운다.
        }
        catch { /* 사용 중이면 다음 시작 때 다시 시도한다. */ }
    }

    private const string ApplyScriptBody = """
        $ErrorActionPreference = 'Stop'
        function Write-Log([string]$m) { try { Add-Content -LiteralPath $Log -Value ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $m) -Encoding UTF8 } catch {} }
        function Read-Verified([string]$name, [string]$hash) {
            $bytes = [System.IO.File]::ReadAllBytes((Join-Path $Work $name))
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try { $actual = ([System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant() } finally { $sha.Dispose() }
            if ($actual -ne $hash) { throw "해시 불일치: $name" }
            return ,$bytes
        }
        try { Wait-Process -Id $ParentPid -Timeout 60 -ErrorAction SilentlyContinue } catch {}
        $backup = "$Target.old"
        $dir = Split-Path -Parent $Target
        $ok = $false
        try {
            # 검증한 바이트를 그대로 쓴다(검증 뒤 작업 폴더의 파일이 바뀌어도 영향 없음).
            $exe = Read-Verified 'SysGlass.exe' $ExeHash
            for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
                try {
                    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
                    Move-Item -LiteralPath $Target -Destination $backup -Force
                    [System.IO.File]::WriteAllBytes($Target, $exe)
                    $ok = $true
                } catch {
                    if (Test-Path -LiteralPath $backup) {
                        try { if (Test-Path -LiteralPath $Target) { Remove-Item -LiteralPath $Target -Force } } catch {}
                        try { Move-Item -LiteralPath $backup -Destination $Target -Force } catch {}
                    }
                    Start-Sleep -Milliseconds 500
                }
            }
            if ($ok -and $NoticesHash) {
                try { [System.IO.File]::WriteAllBytes((Join-Path $dir 'THIRD-PARTY-NOTICES.txt'), (Read-Verified 'THIRD-PARTY-NOTICES.txt' $NoticesHash)) } catch {}
            }
        } catch {
            Write-Log ("업데이트 파일 확인 실패: " + $_.Exception.Message)
        }
        if ($ok) { Write-Log "업데이트 적용 완료: $Target" } else { Write-Log "업데이트 적용 실패, 기존 실행 파일을 유지합니다: $Target" }
        Start-Process -FilePath $Target -WorkingDirectory $dir
        """;
}
