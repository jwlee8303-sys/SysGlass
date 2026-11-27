using SysGlass.Settings;

/// <summary>화면 표시 방법(컴팩트 모양) 설정 값 보정 검사.</summary>
static class DisplayChecks
{
    public static void Run(Action<bool, string> check)
    {
        for (int style = AppSettings.MinCompactStyle; style <= AppSettings.MaxCompactStyle; style++)
        {
            var s = new AppSettings { CompactStyle = style };
            s.Normalize();
            check(s.CompactStyle == style, $"표시 모양 {style} 유지");
        }
        check(AppSettings.MinCompactStyle == 1 && AppSettings.MaxCompactStyle == 4, "표시 모양은 캡슐·추이 그래프·LED 미터·아크 게이지 4종");
        check(new AppSettings().CompactStyle == AppSettings.ArcCompactStyle, "새 설정의 기본 모양은 아크 게이지");

        var ring = new AppSettings { CompactStyle = 0 };
        ring.Normalize();
        check(ring.CompactStyle == AppSettings.ArcCompactStyle, "제거된 원형 링(0)은 아크 게이지로 전환");

        var high = new AppSettings { CompactStyle = 99 };
        high.Normalize();
        check(high.CompactStyle == AppSettings.MaxCompactStyle, "범위를 넘는 표시 모양은 마지막 값으로 보정");

        var low = new AppSettings { CompactStyle = -3 };
        low.Normalize();
        check(low.CompactStyle == AppSettings.MinCompactStyle, "음수 표시 모양은 첫 값으로 보정");

        // 글자 색 견본(기본 흰색, 범위 밖은 흰색으로 보정)
        check(AppSettings.TextColorPresets.Count == 8 && AppSettings.TextColorPresets[0].Hex == "#F2F2F2", "글자 색 견본 8개, 첫 번째는 흰색");
        check(AppSettings.TextColorPresets[1] == ("흰색(통일)", "#F2F2F2", true), "두 번째 견본은 이름까지 흰색으로 표시하는 흰색(통일)");
        check(AppSettings.TextColorPresets.Count(p => p.SameLabel) == 2 && AppSettings.TextColorPresets[^1].SameLabel,
            "흰색(통일)·회색(통일) 견본만 항목 이름을 수치와 같은 색으로 표시");
        check(new AppSettings().TextColor == 0, "새 설정의 글자 색은 흰색");
        check(AppSettings.TextColorPresets.All(p => System.Text.RegularExpressions.Regex.IsMatch(p.Hex, "^#[0-9A-F]{6}$")), "글자 색 견본 형식 #RRGGBB");
        foreach (var (input, expected) in new[] { (1, 1), (7, 7), (8, 0), (-1, 0) })
        {
            var c = new AppSettings { TextColor = input };
            c.Normalize();
            check(c.TextColor == expected, $"글자 색 번호 {input} → {expected}");
        }

        // 아크 게이지 열 수(2~4열, 기본 3열)
        check(new AppSettings().ArcColumns == 3, "아크 게이지 기본 3열");
        foreach (var (input, expected) in new[] { (2, 2), (3, 3), (4, 4), (5, 5), (6, 6), (1, 2), (0, 2), (9, 6) })
        {
            var a = new AppSettings { ArcColumns = input };
            a.Normalize();
            check(a.ArcColumns == expected, $"아크 게이지 열 수 {input} → {expected}");
        }
    }
}
