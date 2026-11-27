using SysGlass.Services;

/// <summary>업데이트 확인의 버전 해석·릴리스 응답 해석·변경 요약 검사. 네트워크는 사용하지 않는다.</summary>
static class UpdateChecks
{
    public static void Run(Action<bool, string> check)
    {
        // 버전 해석
        check(UpdateService.TryParseVersion("v1.4.0", out var v) && v == new Version(1, 4, 0), "태그 v1.4.0 해석");
        check(UpdateService.TryParseVersion("1.10.2", out v) && v == new Version(1, 10, 2), "접두어 없는 버전 해석");
        check(!UpdateService.TryParseVersion("v1.4.0-beta.1", out _), "시험판 태그 거부");
        check(!UpdateService.TryParseVersion("v1.4", out _), "두 자리 버전 거부");
        check(!UpdateService.TryParseVersion("latest", out _), "숫자가 아닌 태그 거부");
        check(!UpdateService.TryParseVersion(null, out _), "빈 태그 거부");
        check(new Version(1, 10, 0) > new Version(1, 9, 9), "버전은 숫자로 비교");

        // 릴리스 응답 해석
        const string sha = "1efe92eae665eacde4ecd1928815c6987807646862061e17766d76d2ec9c3552";
        string Release(string tag, bool draft = false, bool pre = false, string asset = "SysGlass-1.4.0-win-x64-portable.zip",
            string url = "https://github.com/jwlee8303-sys/SysGlass/releases/download/v1.4.0/SysGlass-1.4.0-win-x64-portable.zip") => $$"""
            {"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(pre ? "true" : "false")}},
             "html_url":"https://github.com/jwlee8303-sys/SysGlass/releases/tag/{{tag}}","body":"## 변경 사항\n- 기능 A",
             "assets":[{"name":"notes.txt","browser_download_url":"https://github.com/x","size":1},
                       {"name":"{{asset}}","browser_download_url":"{{url}}","size":71425375,"digest":"sha256:{{sha.ToUpperInvariant()}}"}]}
            """;

        var info = UpdateService.ParseRelease(Release("v1.4.0"));
        check(info != null && info.Version == new Version(1, 4, 0), "정식 릴리스 해석");
        check(info?.AssetName == "SysGlass-1.4.0-win-x64-portable.zip" && info.AssetSize == 71425375, "포터블 ZIP 자산 선택");
        check(info?.Sha256 == sha, "SHA-256 digest 소문자 정규화");
        check(UpdateService.ParseRelease(Release("v1.4.0", draft: true)) == null, "초안 릴리스 제외");
        check(UpdateService.ParseRelease(Release("v1.4.0", pre: true)) == null, "시험판 릴리스 제외");
        check(UpdateService.ParseRelease(Release("v1.4.0", asset: "SysGlass-1.4.0-win-arm64.zip")) == null, "포터블 ZIP 없으면 제외");
        check(UpdateService.ParseRelease(Release("v1.4.0", url: "https://example.com/a.zip")) == null, "GitHub 밖 다운로드 주소 거부");
        check(UpdateService.ParseRelease(Release("nightly")) == null, "버전 형식이 아닌 태그 제외");

        // 변경 요약
        const string notes = "## 다운로드\n- 파일\n\n## 변경 사항\n\n### Added\n\n- **컴팩트** 보기\n- `설정` 접기\n\n## 제3자 구성 요소\n- 라이선스";
        var summary = UpdateService.SummarizeNotes(notes, 800);
        check(summary.Contains("• 컴팩트 보기") && summary.Contains("• 설정 접기"), "변경 사항 절만 요약");
        check(!summary.Contains("다운로드") && !summary.Contains("라이선스"), "다른 절 제외");
        check(UpdateService.SummarizeNotes("- 하나\n- 둘", 800) == "• 하나\r\n• 둘".Replace("\r\n", Environment.NewLine), "절이 없으면 전체 사용");
        check(UpdateService.SummarizeNotes(new string('가', 50), 10).Length == 11, "길면 잘라서 말줄임");
        check(UpdateService.SummarizeNotes("", 10) == "", "빈 노트");

        // 교체 스크립트: 경로는 작은따옴표 리터럴로 넣어 $·따옴표가 해석되지 않게 한다.
        check(UpdateService.PsQuote(@"C:\a'b $x") == @"'C:\a''b $x'", "PowerShell 리터럴 따옴표 이스케이프");
        var staged = new Dictionary<string, string> { ["SysGlass.exe"] = "aa11" };
        var script = UpdateService.BuildApplyScript(42, @"C:\T\w", @"D:\앱 폴더\O'Neil\SysGlass.exe", @"C:\L\update.log", staged);
        check(script.Contains("$ParentPid = 42") && script.Contains(@"$Target = 'D:\앱 폴더\O''Neil\SysGlass.exe'"), "스크립트에 대상 경로 리터럴");
        check(script.Contains("$ExeHash = 'aa11'") && script.Contains("$NoticesHash = ''"), "스크립트에 검증 해시 전달");
        check(script.Contains("Read-Verified 'SysGlass.exe' $ExeHash"), "교체 직전 해시 재확인");
    }
}
