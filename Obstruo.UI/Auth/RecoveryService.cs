using Microsoft.Extensions.Logging;
using Obstruo.Shared.Enums;
using Obstruo.UI.Ipc;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Obstruo.UI.Auth;

/// <summary>
/// Manages the one-time recovery code — pure IPC client (auth refactor B).
/// This class NEVER touches the database. Code generation is local crypto;
/// storage and recovery execution happen in the service.
///
/// The recovery code is generated once at first launch, shown once to the user,
/// and never retrievable again. Stored as a bcrypt hash in Config BY THE SERVICE.
///
/// Recovery is a single atomic IPC command (PerformRecovery):
///   - Service verifies the code against recovery_code_hash.
///   - On match: service clears pin_hash, password_hash, recovery_code_hash
///     in ONE transaction and returns success.
///   - On mismatch: counts toward the service's shared escalating lockout
///     (same policy as PIN/password guesses: 3 wrong → 1, 3, 5... min).
///   - Caller redirects to SetupWizard on success.
/// There is deliberately NO separate "verify code" step — verification and
/// clearing cannot be split, so there is no exploitable gap between them.
///
/// Lost code = clean uninstall. This is by design and documented in SetupWizard.
///
/// Code format: XXXXX-XXXXX-XXXXX-XXXXX-XXXXX
///   - 5 groups of 5 characters, dash-separated
///   - Alphabet: A–Z minus I, L, O; digits 2–9 (no 0, 1)
///     → 32 characters, exactly divides 256 = zero modulo bias
///   - 25 characters of entropy = ~125 bits
///   - User may type with or without dashes — normalized before sending
/// </summary>
public sealed class RecoveryService
{
    // Unambiguous 32-character alphabet.
    // Excludes: 0 (looks like O), 1 (looks like I/L), I, L, O.
    // 256 / 32 = 8 exactly — no modulo bias when mapping bytes to chars.
    private static readonly char[] Alphabet =
        "ABCDEFGHJKMNPQRSTUVWXYZ23456789".ToCharArray();

    private const int GroupCount = 5;
    private const int GroupSize = 5;
    private const char Separator = '-';

    private readonly IpcClient _ipc;
    private readonly AuthService _authService;
    private readonly ILogger<RecoveryService> _logger;

    /// <summary>
    /// Lockout details from the most recent failed PerformRecoveryAsync call.
    /// Null if the last attempt succeeded, hit no lockout, or never ran.
    /// </summary>
    public LockoutInfo? LastLockoutInfo { get; private set; }

    public RecoveryService(IpcClient ipc, AuthService authService, ILogger<RecoveryService> logger)
    {
        _ipc = ipc;
        _authService = authService;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  STATE
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Asks the service whether a recovery code hash is stored.
    /// Returns:
    ///   true  — configured
    ///   false — not configured (wizard hasn't run, or recovery wiped it)
    ///   null  — service unreachable; treat as UNKNOWN, not as "not configured"
    /// </summary>
    public async Task<bool?> IsConfiguredAsync()
    {
        var state = await _authService.GetSetupStateAsync();
        return state?.RecoveryConfigured;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CODE GENERATION  (local — pure crypto, no service involvement)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates a new cryptographically random recovery code.
    /// Returns the plaintext code — caller must show it to the user immediately.
    /// Does NOT save anything — call SaveCodeAsync() after the user confirms
    /// they have copied it.
    /// </summary>
    public string GenerateCode()
    {
        // GroupCount * GroupSize = 25 bytes needed
        var bytes = RandomNumberGenerator.GetBytes(GroupCount * GroupSize);
        var groups = new string[GroupCount];

        for (int g = 0; g < GroupCount; g++)
        {
            var chars = new char[GroupSize];
            for (int c = 0; c < GroupSize; c++)
            {
                // 256 / 32 = 8 exactly → no modulo bias
                chars[c] = Alphabet[bytes[g * GroupSize + c] % Alphabet.Length];
            }
            groups[g] = new string(chars);
        }

        var code = string.Join(Separator, groups);
        _logger.LogInformation("Recovery code generated (not yet saved)");
        return code;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  STORAGE  (via service — SetCredential)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sends the normalized code to the service, which bcrypt-hashes and stores it.
    /// Call this only after the user has confirmed they saved the code.
    /// Calling this again replaces the existing code — the old one stops working.
    ///
    /// existingCredential: the wizard saves pin → password → recovery code, and
    /// by the time this runs, setup is complete — the service's bootstrap window
    /// has CLOSED. This call MUST carry the just-created PIN or password, or the
    /// service will reject it. Passing null only works if setup is somehow still
    /// incomplete, which should never be the case on this path.
    ///
    /// Returns (true, null) on success or (false, error) on failure —
    /// CALLERS MUST CHECK THE RESULT.
    /// </summary>
    public async Task<(bool Success, string? Error)> SaveCodeAsync(
        string plaintextCode, string? existingCredential)
    {
        var normalized = Normalize(plaintextCode);

        var result = await _authService.SaveCredentialAsync(
            "recovery_code_hash", normalized, existingCredential);

        if (result.Success)
            _logger.LogInformation("Recovery code saved via service");
        else
            _logger.LogWarning("Recovery code save failed: {Error}", result.Error);

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  RECOVERY  (atomic — verify + clear all credentials in one IPC command)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes recovery in ONE atomic service command:
    ///   - Service verifies the code against recovery_code_hash.
    ///   - On match: pin_hash, password_hash, recovery_code_hash all cleared
    ///     in one transaction. Returns Success — caller MUST redirect to
    ///     SetupWizard, because no auth methods exist anymore.
    ///   - On mismatch: counts toward the service's escalating lockout.
    ///     Check LastLockoutInfo for countdown details.
    /// Never throws; service failures return ServiceUnavailable (fail-closed).
    /// </summary>
    public async Task<AuthResult> PerformRecoveryAsync(string input)
    {
        LastLockoutInfo = null;

        if (string.IsNullOrWhiteSpace(input))
            return AuthResult.WrongCredential;

        var normalized = Normalize(input);

        try
        {
            var response = await _ipc.SendCommandAndWaitAsync(
                ServiceCommand.PerformRecovery,
                credential: normalized);

            if (response.Success)
            {
                _logger.LogWarning(
                    "Recovery performed — all credentials cleared. SetupWizard required.");
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
                    "Recovery blocked by service lockout — {Seconds}s remaining",
                    LastLockoutInfo.RemainingSeconds);
                return AuthResult.LockedOut;
            }

            // "No recovery code is configured" from the service
            if (response.Error?.Contains("configured", StringComparison.OrdinalIgnoreCase) == true)
                return AuthResult.NotConfigured;

            _logger.LogWarning("Recovery code rejected: {Error}", response.Error);
            return AuthResult.WrongCredential;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            _logger.LogError(ex, "PerformRecovery — service unreachable. Failing closed.");
            return AuthResult.ServiceUnavailable;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Strips dashes and spaces, uppercases. 
    /// Allows the user to type XXXXX-XXXXX or XXXXXXXXXXXXX — both verify correctly.
    /// Must match the normalization used when the code was saved.
    /// </summary>
    private static string Normalize(string code)
        => code
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToUpperInvariant();

    // ── JSON ──────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}