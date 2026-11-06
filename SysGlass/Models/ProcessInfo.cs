namespace SysGlass.Models;

/// <summary>프로세스를 종료했을 때 시스템에 미치는 영향 정도.</summary>
public enum Importance
{
    /// <summary>종료해도 무방한 보조/업데이트/자동 실행 프로그램</summary>
    Low = 0,
    /// <summary>일반 응용 프로그램. 종료하면 해당 프로그램만 닫힌다.</summary>
    Normal = 1,
    /// <summary>종료하면 주요 기능(화면, 소리, 보안, 입력 등)에 장애가 생길 수 있다.</summary>
    High = 2,
    /// <summary>종료하면 시스템이 멈추거나 강제 재부팅(블루스크린)된다.</summary>
    Critical = 3,
}

/// <summary>프로세스 이름에 대한 설명과 중요도.</summary>
public sealed record ProcessInfo(string Description, Importance Importance);

/// <summary>프로세스를 이름 기준으로 합산한 한 시점의 사용량.</summary>
public sealed class ProcessGroupSample
{
    public required string Name { get; init; }
    public int Count { get; set; }
    /// <summary>자원 값을 실제로 읽은 프로세스 수. 보호 프로세스 등은 읽지 못해 Count보다 작을 수 있다.</summary>
    public int Measured { get; set; }
    public double CpuPercent { get; set; }
    public double? GpuPercent { get; set; }
    public ulong MemoryBytes { get; set; }
    /// <summary>읽기+쓰기 전송량. 디스크뿐 아니라 네트워크·장치 I/O도 포함한다.</summary>
    public double IoBytesPerSec { get; set; }
    /// <summary>설명 조회용 실행 파일 경로(내장 목록에 없는 이름만 채워진다).</summary>
    public string? ImagePath { get; set; }
    /// <summary>같은 이름인데 실행 경로가 서로 다른 프로세스가 섞여 있는지.</summary>
    public bool MixedPaths { get; set; }
}
