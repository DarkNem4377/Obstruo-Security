namespace Obstruo.Shared.Enums;

public enum AlertType
{
    TamperDetected,
    BypassAttempt,
    UpdateAvailable,
    ServiceError,
    EmergencyDisabled,
    EmergencyRestored,
    Port53Conflict,
    LanIpChanged,       // Phase 5
    ProxyUnresponsive,  // HealthMonitor: proxy stopped answering, service self-restarting
    WhitelistExpired    // a temporary allow-list exception lapsed and is blocked again
}