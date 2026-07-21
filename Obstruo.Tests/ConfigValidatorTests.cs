using Obstruo.Service;

namespace Obstruo.Tests;

/// <summary>
/// Pins down the IPC UpdateConfig allowlist (IpcServer.ConfigValidators).
/// These validators are the only thing standing between a credential-holding
/// client and arbitrary Config writes — credential hashes, lockout state, and
/// version markers must never become writable, and each writable key must
/// reject out-of-range values.
/// </summary>
public class ConfigValidatorTests
{
    private static bool Validate(string key, string value)
        => IpcServer.ConfigValidators.TryGetValue(key, out var v) && v(value);

    // ── Allowlist boundary ────────────────────────────────────────────────────

    [Theory]
    [InlineData("pin_hash")]
    [InlineData("password_hash")]
    [InlineData("recovery_code_hash")]
    [InlineData("lockout_until")]
    [InlineData("lockout_failed")]
    [InlineData("lockout_count")]
    [InlineData("schema_version")]
    [InlineData("version")]
    [InlineData("emergency_cooldown_until")]
    [InlineData("last_cleanup")]
    [InlineData("lan_ip")]
    public void Sensitive_keys_are_not_writable(string key)
        => Assert.False(IpcServer.ConfigValidators.ContainsKey(key));

    [Fact]
    public void Key_lookup_is_case_sensitive()
        // "Blocklist_Url" must not slip past an OrdinalIgnoreCase comparer and
        // create a shadow row the service never reads.
        => Assert.False(IpcServer.ConfigValidators.ContainsKey("Blocklist_Url"));

    // ── Per-key validation ────────────────────────────────────────────────────

    [Theory]
    [InlineData("log_retention_hours", "1", true)]
    [InlineData("log_retention_hours", "8760", true)]   // 24 * 365
    [InlineData("log_retention_hours", "0", false)]
    [InlineData("log_retention_hours", "8761", false)]
    [InlineData("log_retention_hours", "-5", false)]
    [InlineData("log_retention_hours", "abc", false)]
    public void Log_retention_hours(string key, string value, bool expected)
        => Assert.Equal(expected, Validate(key, value));

    [Theory]
    [InlineData("02:00", true)]
    [InlineData("00:00", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)]
    [InlineData("-01:00", false)]
    [InlineData("noon", false)]
    public void Cleanup_time(string value, bool expected)
        => Assert.Equal(expected, Validate("cleanup_time", value));

    [Theory]
    [InlineData("emergency_disable_max_minutes", "1", true)]
    [InlineData("emergency_disable_max_minutes", "240", true)]
    [InlineData("emergency_disable_max_minutes", "0", false)]
    [InlineData("emergency_disable_max_minutes", "241", false)]
    [InlineData("emergency_disable_cooldown_minutes", "0", true)]
    [InlineData("emergency_disable_cooldown_minutes", "10080", true)]
    [InlineData("emergency_disable_cooldown_minutes", "10081", false)]
    [InlineData("emergency_disable_cooldown_minutes", "-1", false)]
    public void Emergency_pause_limits(string key, string value, bool expected)
        => Assert.Equal(expected, Validate(key, value));

    [Theory]
    [InlineData("5", true)]
    [InlineData("3600", true)]
    [InlineData("4", false)]
    [InlineData("3601", false)]
    public void Metrics_refresh_seconds(string value, bool expected)
        => Assert.Equal(expected, Validate("metrics_refresh_seconds", value));

    [Theory]
    [InlineData("", true)]                               // cleared = sync disabled
    [InlineData("https://feeds.example.com/list.json", true)]
    [InlineData("HTTPS://FEEDS.EXAMPLE.COM/LIST.JSON", true)]
    [InlineData("http://feeds.example.com/list.json", false)]  // plaintext feed = poisoning risk
    [InlineData("ftp://feeds.example.com/list.json", false)]
    [InlineData("feeds.example.com", false)]
    public void Blocklist_url_requires_https_or_empty(string value, bool expected)
        => Assert.Equal(expected, Validate("blocklist_url", value));

    [Theory]
    [InlineData("ui_theme", "dark", true)]
    [InlineData("ui_theme", "light", true)]
    [InlineData("ui_theme", "solarized", false)]
    [InlineData("ui_theme", "", false)]
    [InlineData("ui_mask_custom", "0", true)]
    [InlineData("ui_mask_custom", "1", true)]
    [InlineData("ui_mask_custom", "true", false)]
    public void Ui_settings(string key, string value, bool expected)
        => Assert.Equal(expected, Validate(key, value));

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData("true", false)]
    [InlineData("2", false)]
    [InlineData("", false)]
    public void Lan_mode_enabled_is_boolean(string value, bool expected)
        => Assert.Equal(expected, Validate("lan_mode_enabled", value));
}
