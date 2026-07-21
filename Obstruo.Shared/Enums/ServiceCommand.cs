namespace Obstruo.Shared.Enums;

public enum ServiceCommand
{
    AddDomain,
    RemoveDomain,
    AddWhitelist,
    RemoveWhitelist,
    EmergencyStop,
    EmergencyResume,
    SyncBlocklist,
    UpdateConfig,
    GetStatus,
    GetMetrics,
    GetBlocklist,

    /// <summary>
    /// Read-only. Returns the writable settings and category on/off states as
    /// JSON in Data so the Settings screen can populate itself:
    /// { "config": { "log_retention_hours": "720", ... },
    ///   "categories": [ { "name": "Adult", "enabled": true }, ... ] }
    /// No credential required — exposes no domains, hashes, or history.
    /// Writes still go through UpdateConfig, which is credential-gated.
    /// </summary>
    GetSettings,

    // ── Auth over IPC (Phase: auth refactor B) ────────────────────────────
    // The UI never touches the database. All credential storage and
    // verification happens in the service, behind these commands.

    /// <summary>
    /// Read-only. Returns setup/config state as JSON in Data:
    /// { "pinConfigured": bool, "passwordConfigured": bool,
    ///   "recoveryConfigured": bool, "isConfigured": bool }
    /// isConfigured = pinConfigured AND passwordConfigured (both are mandatory).
    /// No credential required.
    /// </summary>
    GetSetupState,

    /// <summary>
    /// Stores a credential hash. Payload: { "key": "pin_hash" | "password_hash"
    /// | "recovery_code_hash", "value": "<plaintext to hash service-side>" }.
    /// Bootstrap rule: allowed WITHOUT Credential only while isConfigured is
    /// false (first-run wizard). Once configured, requires a valid Credential.
    /// NOTE: the wizard saves pin → password → recovery code. After the first
    /// two saves isConfigured is TRUE, so the recovery-code save MUST carry
    /// the just-created PIN or password in Credential.
    /// </summary>
    SetCredential,

    /// <summary>
    /// Verifies a plaintext PIN or password in Credential against stored
    /// hashes. Success = matched. Failure counts toward the service-side
    /// lockout (3 wrong → 1, 3, 5, ... minute escalation).
    /// Data on failure includes lockout info as JSON:
    /// { "lockedOut": bool, "remainingSeconds": long, "attemptsBeforeLockout": int }
    /// </summary>
    VerifyCredential,

    /// <summary>
    /// Atomic recovery. Credential carries the NORMALIZED recovery code
    /// (dashes/spaces stripped, uppercased). On match: pin_hash,
    /// password_hash, and recovery_code_hash are all cleared in a single
    /// transaction and Success = true — the caller must redirect to the
    /// setup wizard. On mismatch: counts toward the shared lockout and
    /// returns the same lockout JSON as VerifyCredential.
    /// There is NO separate "verify recovery code" command by design —
    /// verification and clearing are one atomic operation.
    /// </summary>
    PerformRecovery,

    /// <summary>
    /// PIN/password-gated full uninstall. Credential carries the plaintext PIN
    /// or password. On a valid credential the service, in-process (as LocalSystem):
    ///   - stops tamper detection (so it does not re-pin DNS mid-teardown),
    ///   - restores original DNS (IPv4 + IPv6),
    ///   - removes DoH blocking (firewall rules + browser policies),
    ///   - removes the LAN DNS firewall rules,
    ///   - schedules a detached cleanup that stops and deletes the Windows
    ///     service and removes install files / registry state.
    /// This is the ONLY path that undoes the DNS/DoH lockdown — a bad credential
    /// changes nothing. Counts toward the shared lockout on failure.
    /// </summary>
    Uninstall,

    /// <summary>
    /// Credential-gated. Returns the live (non-expired) allow-list as JSON in
    /// Data: [ { "domain": "…", "addedAt": "…", "expiresAt": null|"…",
    /// "reason": null|"…" }, … ]. Appended at the enum tail so serialized
    /// values from older UIs keep their meaning.
    /// </summary>
    GetWhitelist,

    /// <summary>
    /// Credential-gated. Returns recent incidents (bypass attempts) as JSON in
    /// Data: [ { "ref": "INC-0007", "openedAt": "…", "closedAt": null|"…",
    /// "state": "Open", "severity": "…", "title": "…", "deviceName": "…",
    /// "mitre": null|"…" }, … ], newest first. Appended at the enum tail.
    /// </summary>
    GetIncidents,

    /// <summary>
    /// Credential-gated. Writes the activity log to a file the service creates
    /// directly (the export can exceed the 64 KB IPC cap). Payload:
    /// { "path": "C:\\…\\export.csv", "format": "csv"|"json", "days": "30" }.
    /// Returns { "rows": N } in Data. Appended at the enum tail.
    /// </summary>
    ExportLogs
}