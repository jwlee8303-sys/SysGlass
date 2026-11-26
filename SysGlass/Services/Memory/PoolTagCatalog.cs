namespace SysGlass.Services.Memory;

/// <summary>
/// 널리 알려진 Pool Tag의 일반적인 용도. 참고 정보일 뿐이며, 같은 태그를 여러 드라이버가 쓸 수 있으므로
/// 원인을 단정하는 근거로 쓰지 않는다. 목록에 없는 태그는 null.
/// </summary>
public static class PoolTagCatalog
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["Toke"] = "보안 토큰 객체 (Windows 커널 보안)",
        ["SeAt"] = "보안 속성 (Windows 커널 보안)",
        ["Proc"] = "프로세스 객체",
        ["Thre"] = "스레드 객체",
        ["File"] = "파일 객체",
        ["Even"] = "이벤트 객체",
        ["MmSt"] = "메모리 관리자 섹션 관련 구조",
        ["FMfn"] = "필터 관리자 파일 이름 정보",
        ["Ntff"] = "NTFS 파일 제어 블록",
        ["ObNm"] = "객체 이름",
        ["Irp "] = "I/O 요청 패킷",
    };

    /// <summary>알려진 용도 설명. 접두사 규칙(Se = 커널 보안)도 참고로 표시한다.</summary>
    public static string? Describe(string tag)
    {
        if (Known.TryGetValue(tag, out var d)) return d;
        if (tag.StartsWith("Se", StringComparison.Ordinal)) return "Windows 커널 보안 관련으로 추정 (Se 접두사)";
        return null;
    }

    public const string Disclaimer = "Pool Tag 설명은 참고용이며 실제 할당 주체나 누수 원인을 확정하지 않습니다.";
}
