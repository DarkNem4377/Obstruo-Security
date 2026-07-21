using Microsoft.Extensions.Logging;
using Obstruo.Shared.Enums;
using Obstruo.UI.Ipc;
using System.IO;
using System.Text.Json;
using Windows.Security.Credentials.UI;

namespace Obstruo.UI.Auth;

/// <summary>
/// Auth client — all credential storage and verification happens in the
/// Obstruo service over IPC. This class NEVER touches the database.
///
/// Design (auth refactor B):
///   - VerifyCredentialAsync sends the plaintext PIN or password to the service;
///     the service BCrypt-verifies it against stored hashes. Either type matching
///     grants access.
///   - Lockout policy lives in the service (3 wrong → 1, 3, 5... min escalation).
///     This class only relays the service's answer — no local counters to bypass.
///   - Windows Hello remains local: it is an OS-level check with no DB involved.
///     Hello failures do not touch the service lockout counter.
///   - If the service is unreachable, all auth fails closed (ServiceUnavailable).
/// </summary>
public sealed class AuthService
{
    private readonly IpcClient _ipc;
    private readonly ILogger<AuthService> _logger;

    // ── Windows Hello availability (cached after first check) ─────────────────
    private bool? _helloAvailable;

    /// <summary>
    /// Lockout details from the most recent failed VerifyCredentialAsync call.
    /// Null if the last attempt succeeded, hit no lockout, or never ran.
    /// </summary>
    public LockoutInfo? LastLockoutInfo { get; private set; }

    public AuthService(IpcClient ipc, ILogger<AuthService> logger)
    {
        _ipc = ipc;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SETUP STATE
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Asks the service whether setup is complete (both PIN and password stored).
    /// Returns null if the service is unreachable — callers must treat that as
    /// "unknown", NOT as "not configured", or a dead service would re-trigger
    /// the setup wizard on a fully configured machine.
    /// </summary>
    public async Task<SetupState?> GetSetupStateAsync()
    {
        try
        {
            var response = await _ipc.SendCommandAndWaitAsync(ServiceCommand.GetSetupState);

            if (!response.Success || string.IsNullOrEmpty(response.Data))
            {
                _logger.LogWarning("GetSetupState failed: {Error}", response.Error);
                return null;
            }

            return JsonSerializer.Deserialize<SetupState>(response.Data, _jsonOptions);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            _logger.LogWarning(ex, "GetSetupState — service unreachable");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CREDENTIAL VERIFICATION (PIN or password — service decides)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies a plaintext PIN or password against the service.
    /// The service accepts either credential type — one method, one code path.
    /// Never throws; service failures return ServiceUnavailable (fail-closed).
    /// </summary>
    public async Task<AuthResult> VerifyCredentialAsync(string credential)
    {
        LastLockoutInfo = null;

        if (string.IsNullOrWhiteSpace(credential))
            return AuthResult.WrongCredential;

        try
        {
            var response = await _ipc.SendCommandAndWaitAsync(
                ServiceCommand.VerifyCredential,
                credential: credential);

            if (response.Success)
            {
                _logger.LogInformation("Credential verified by service");
                return AuthResult.Success;
            }

            // Structured lockout info rides in Data on failures
            if (!string.IsNullOrEmpty(response.Data))
            {
                try
                {
                    LastLockoutInfo = JsonSerializer.Deserialize<LockoutInfo>(
                        response.Data, _jsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Could not parse lockout info from service");
                }
            }

            if (LastLockoutInfo?.LockedOut == true)
            {
                _logger.LogWarning(
                    "Service lockout active — {Seconds}s remaining",
                    LastLockoutInfo.RemainingSeconds);
                return AuthResult.LockedOut;
            }

            // "No PIN or password is configured" from the service
            if (response.Error?.Contains("configured", StringComparison.OrdinalIgnoreCase) == true)
                return AuthResult.NotConfigured;

            _logger.LogWarning("Credential rejected: {Error}", response.Error);
            return AuthResult.WrongCredential;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            _logger.LogError(ex, "VerifyCredential — service unreachable. Failing closed.");
            return AuthResult.ServiceUnavailable;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CREDENTIAL STORAGE (setup wizard, future settings screen)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stores a credential via the service, which hashes it (bcrypt, wf 12).
    /// key: "pin_hash", "password_hash", or "recovery_code_hash".
    /// existingCredential: required once setup is complete; null during
    /// first-run bootstrap (service enforces this rule, not us).
    /// Returns (true, null) on success or (false, error) on failure —
    /// CALLERS MUST CHECK THE RESULT. Silent-failure setup is what we are
    /// digging this project out of.
    /// </summary>
    public async Task<(bool Success, string? Error)> SaveCredentialAsync(
        string key, string plaintextValue, string? existingCredential = null)
    {
        try
        {
            var response = await _ipc.SendCommandAndWaitAsync(
                ServiceCommand.SetCredential,
                payload: new Dictionary<string, string>
                {
                    ["key"] = key,
                    ["value"] = plaintextValue
                },
                credential: existingCredential);

            if (response.Success)
            {
                _logger.LogInformation("Credential '{Key}' stored via service", key);
                return (true, null);
            }

            _logger.LogWarning("SetCredential '{Key}' failed: {Error}", key, response.Error);
            return (false, response.Error ?? "The service rejected the credential.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            _logger.LogError(ex, "SetCredential — service unreachable");
            return (false, "Cannot reach the Obstruo service. Is it running?");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WINDOWS HELLO (local — no service involvement)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks whether Windows Hello is available on this device.
    /// Caches the result after the first call — availability doesn't change at runtime.
    /// </summary>
    public async Task<bool> IsWindowsHelloAvailableAsync()
    {
        if (_helloAvailable.HasValue)
            return _helloAvailable.Value;

        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            _helloAvailable = availability == UserConsentVerifierAvailability.Available;

            _logger.LogInformation(
                "Windows Hello availability: {Availability} → available={Result}",
                availability, _helloAvailable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows Hello availability check failed — treating as unavailable");
            _helloAvailable = false;
        }

        return _helloAvailable.Value;
    }

    /// <summary>
    /// Prompts the user with Windows Hello.
    /// Hello failures (cancelled, not enrolled, device locked) do NOT touch
    /// the service lockout counter — the OS manages its own lockout.
    /// Returns HelloUnavailable if the device does not support it.
    /// </summary>
    public async Task<AuthResult> VerifyWindowsHelloAsync()
    {
        if (!await IsWindowsHelloAvailableAsync())
            return AuthResult.HelloUnavailable;

        try
        {
            var result = await UserConsentVerifier.RequestVerificationAsync(
                "Verify your identity to access Obstruo Security");

            _logger.LogInformation("Windows Hello result: {Result}", result);

            if (result == UserConsentVerificationResult.Verified)
                return AuthResult.Success;

            if (result == UserConsentVerificationResult.Canceled)
                return AuthResult.Cancelled;

            // Device not present, not configured, too many failed biometric attempts, etc.
            _logger.LogWarning("Windows Hello returned non-verified, non-cancelled result: {Result}", result);
            return AuthResult.HelloUnavailable;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Windows Hello request threw");
            return AuthResult.HelloUnavailable;
        }
    }

    // ── JSON ──────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

/// <summary>Setup state as reported by the service (GetSetupState).</summary>
public sealed record SetupState(
    bool PinConfigured,
    bool PasswordConfigured,
    bool RecoveryConfigured,
    bool IsConfigured);

/// <summary>Lockout details from a failed VerifyCredential (service-side policy).</summary>
public sealed record LockoutInfo(
    bool LockedOut,
    long RemainingSeconds,
    int AttemptsBeforeLockout);

/// <summary>Result of an authentication attempt.</summary>
public enum AuthResult
{
    /// <summary>Credentials matched — access granted.</summary>
    Success,

    /// <summary>Credentials did not match — attempt counted by the service.</summary>
    WrongCredential,

    /// <summary>
    /// Too many wrong attempts — service lockout active.
    /// Check AuthService.LastLockoutInfo for the remaining time.
    /// </summary>
    LockedOut,

    /// <summary>No PIN or password stored — setup not complete.</summary>
    NotConfigured,

    /// <summary>Windows Hello is not available or not enrolled on this device.</summary>
    HelloUnavailable,

    /// <summary>User cancelled the Windows Hello prompt.</summary>
    Cancelled,

    /// <summary>
    /// The Obstruo service is unreachable — authentication is impossible.
    /// Fail-closed: no access is granted while the service is down.
    /// </summary>
    ServiceUnavailable,
}