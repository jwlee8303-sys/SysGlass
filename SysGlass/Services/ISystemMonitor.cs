using SysGlass.Models;

namespace SysGlass.Services;

/// <summary>시스템 자원 수집 인터페이스. 다른 센서 라이브러리로 교체할 수 있도록 UI와 분리한다.</summary>
public interface ISystemMonitor
{
    /// <summary>현재 값을 수집한다. 예외를 던지지 않고 실패한 항목은 null로 반환한다.</summary>
    SystemSnapshot Sample(SensorOptions options);
}
