namespace Obstruo.Service.Dns;

/// <summary>
/// Returned by every DNS query check.
/// IsBlocked = false means forward to upstream.
/// CategoryId, CategoryName, and Severity are only meaningful when IsBlocked = true.
/// </summary>
public sealed record BlockResult(bool IsBlocked, int CategoryId, string CategoryName, string Severity);

/// <summary>
/// Input entry for LoadDomains — carries category and severity alongside the domain.
/// </summary>
public sealed record DomainEntry(string Domain, int CategoryId, string CategoryName, string Severity);

/// <summary>
/// In-memory blocklist store.
/// Exact domains and wildcard patterns live in HashSets for O(1) lookup.
/// A metadata dictionary maps each normalized domain/pattern to its category + severity.
/// All three structures are swapped atomically on reload — safe for concurrent DNS queries.
/// </summary>
public sealed class DnsBlocklistStore
{
    // Hot-path lookup
    private HashSet<string> _exactDomains = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _wildcardRules = new(StringComparer.OrdinalIgnoreCase);

    // Metadata: normalized domain/pattern → (categoryId, categoryName, severity)
    private Dictionary<string, (int CategoryId, string CategoryName, string Severity)> _metadata
        = new(StringComparer.OrdinalIgnoreCase);

    // Allow-list: normalized domain → expiry UTC (DateTime.MaxValue = permanent).
    // A whitelisted domain covers itself and all subdomains, and always wins
    // over the blocklist — its purpose is unblocking over-blocked domains.
    private Dictionary<string, DateTime> _whitelist = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _swapLock = new();

    private static readonly BlockResult _notBlocked = new(false, 0, string.Empty, string.Empty);

    // ── Load ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Atomically replaces the entire blocklist with full category/severity metadata.
    /// Safe to call while DNS queries are in-flight.
    /// </summary>
    public void LoadDomains(IEnumerable<DomainEntry> entries)
    {
        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wildcard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var meta = new Dictionary<string, (int, string, string)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var n = Normalize(entry.Domain);
            if (string.IsNullOrEmpty(n)) continue;

            if (n.StartsWith("*.")) wildcard.Add(n);
            else exact.Add(n);

            meta[n] = (entry.CategoryId, entry.CategoryName, entry.Severity);
        }

        lock (_swapLock)
        {
            _exactDomains = exact;
            _wildcardRules = wildcard;
            _metadata = meta;
        }
    }

    /// <summary>
    /// Compatibility overload — plain string list, defaults to category 1 / Adult / High.
    /// </summary>
    public void LoadDomains(IEnumerable<string> domains) =>
        LoadDomains(domains.Select(d => new DomainEntry(d, 1, "Adult", "High")));

    // ── Mutations ─────────────────────────────────────────────────────────────

    public void AddDomain(string domain, int categoryId = 1, string categoryName = "Adult", string severity = "High")
    {
        var n = Normalize(domain);
        if (string.IsNullOrEmpty(n)) return;
        lock (_swapLock)
        {
            if (n.StartsWith("*.")) _wildcardRules.Add(n);
            else _exactDomains.Add(n);
            _metadata[n] = (categoryId, categoryName, severity);
        }
    }

    public void RemoveDomain(string domain)
    {
        var n = Normalize(domain);
        if (string.IsNullOrEmpty(n)) return;
        lock (_swapLock)
        {
            _exactDomains.Remove(n);
            _wildcardRules.Remove(n);
            _metadata.Remove(n);
        }
    }

    // ── Whitelist ─────────────────────────────────────────────────────────────

    /// <summary>Atomically replaces the whole allow-list.</summary>
    public void LoadWhitelist(IEnumerable<(string Domain, DateTime? ExpiresAt)> entries)
    {
        var wl = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var (domain, expiresAt) in entries)
        {
            var n = Normalize(domain);
            if (!string.IsNullOrEmpty(n))
                wl[n] = expiresAt ?? DateTime.MaxValue;
        }

        lock (_swapLock)
        {
            _whitelist = wl;
        }
    }

    public void AddWhitelistEntry(string domain, DateTime? expiresAt)
    {
        var n = Normalize(domain);
        if (string.IsNullOrEmpty(n)) return;
        lock (_swapLock)
        {
            _whitelist[n] = expiresAt ?? DateTime.MaxValue;
        }
    }

    public void RemoveWhitelistEntry(string domain)
    {
        var n = Normalize(domain);
        if (string.IsNullOrEmpty(n)) return;
        lock (_swapLock)
        {
            _whitelist.Remove(n);
        }
    }

    // ── Query (hot path) ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns BlockResult with IsBlocked, CategoryId, CategoryName, and Severity.
    ///
    /// A blocked domain covers itself AND all of its subdomains: if "pornhub.com"
    /// is on the list, "www.pornhub.com" and "m.pornhub.com" are blocked too.
    /// This is the expected behavior for a content filter — otherwise prefixing
    /// "www." defeats every entry.
    ///
    /// Lookup walks the domain's own labels from most-specific to least, stopping
    /// before the bare TLD:
    ///   - exact set matches the label suffix directly (apex + subdomains)
    ///   - wildcard set ("*.suffix") matches subdomains only, never the apex
    ///
    /// Brand-family expansion: a blocked ".com" apex also covers its common
    /// sibling domains — alternate TLDs (brand.org / .net / .co / .xxx) and
    /// product suffixes (brandlive.com, brandnetwork.com, brandpremium.com).
    /// The match is a REVERSE probe: the query is reduced to a candidate ".com"
    /// apex and looked up in the exact set. Expansion therefore only ever flows
    /// away from a blocked .com brand — a blocked niche entry such as
    /// "family.xxx" or "ok.xxx" never blocks the unrelated .com domain.
    ///
    /// Lock scope is minimal — only captures four references, work happens outside lock.
    /// </summary>
    public BlockResult IsBlocked(string domain) => Check(domain, respectWhitelist: true);

    /// <summary>
    /// Whitelist-add guard probe: the exact same lookup as <see cref="IsBlocked"/>
    /// (label walk, wildcards, brand-family expansion) but with the allow-list
    /// ignored, so it answers "would the system block this if it were not
    /// whitelisted?". Used to refuse allow-list entries for blocked domains.
    /// </summary>
    public BlockResult ProbeSystemBlock(string domain) => Check(domain, respectWhitelist: false);

    private BlockResult Check(string domain, bool respectWhitelist)
    {
        var n = Normalize(domain);
        if (string.IsNullOrEmpty(n)) return _notBlocked;

        // Capture all refs under one lock so they come from the same snapshot
        HashSet<string> exact;
        HashSet<string> wildcard;
        Dictionary<string, (int CategoryId, string CategoryName, string Severity)> meta;
        Dictionary<string, DateTime> whitelist;

        lock (_swapLock)
        {
            exact = _exactDomains;
            wildcard = _wildcardRules;
            meta = _metadata;
            whitelist = _whitelist;
        }

        var parts0 = n.Split('.');

        // Whitelist walk FIRST — an allow entry on the domain or any parent
        // overrides every blocklist rule (that is its purpose). Expired
        // entries are ignored at query time.
        if (respectWhitelist && whitelist.Count > 0)
        {
            var now = DateTime.UtcNow;
            for (int i = 0; i < parts0.Length - 1; i++)
            {
                var suffix = i == 0 ? n : string.Join('.', parts0[i..]);
                if (whitelist.TryGetValue(suffix, out var expiry) && expiry > now)
                    return _notBlocked;
            }
        }

        // Walk up labels, stop before the bare TLD.
        //   i == 0 → the full queried domain (apex or subdomain)
        //   i >  0 → a parent domain of the query
        var parts = parts0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var suffix = i == 0 ? n : string.Join('.', parts[i..]);

            // Exact entry blocks the domain itself and, when matched as a parent
            // (i > 0), every subdomain beneath it.
            if (exact.Contains(suffix))
                return Blocked(meta, suffix);

            // Wildcard ("*.suffix") blocks subdomains only — never the apex,
            // so it can only match when suffix is a parent of the query.
            if (i > 0)
            {
                var wildcardKey = "*." + suffix;
                if (wildcard.Contains(wildcardKey))
                    return Blocked(meta, wildcardKey);
            }
        }

        // Brand-family probe (see summary). Uses the query's registrable
        // 2-label apex; a whitelisted probe target suppresses the family block
        // just like it suppresses a direct one.
        if (parts.Length >= 2)
        {
            var probe = FamilyProbe(parts[^2], parts[^1]);
            if (probe is not null && exact.Contains(probe) &&
                !(respectWhitelist &&
                  whitelist.TryGetValue(probe, out var probeExpiry) && probeExpiry > DateTime.UtcNow))
            {
                return Blocked(meta, probe);
            }
        }

        return _notBlocked;
    }

    /// <summary>
    /// Maps a query apex (stem + tld) to the canonical ".com" apex that would
    /// cover it under brand-family rules, or null when no rule applies:
    ///   brand.org / .net / .co / .xxx      → brand.com
    ///   brandlive|network|premium .com     → brand.com  (stripped stem ≥ 3 chars)
    /// </summary>
    private static string? FamilyProbe(string stem, string tld)
    {
        if (tld is "org" or "net" or "co" or "xxx")
            return stem + ".com";

        if (tld == "com")
        {
            foreach (var suffix in _familySuffixes)
            {
                if (stem.Length >= suffix.Length + 3 &&
                    stem.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return string.Concat(stem.AsSpan(0, stem.Length - suffix.Length), ".com");
                }
            }
        }

        return null;
    }

    private static readonly string[] _familySuffixes = ["live", "network", "premium"];

    private static BlockResult Blocked(
        Dictionary<string, (int CategoryId, string CategoryName, string Severity)> meta,
        string key)
    {
        var (catId, catName, sev) = meta.TryGetValue(key, out var m) ? m : (1, "Adult", "High");
        return new BlockResult(true, catId, catName, sev);
    }

    /// <summary>
    /// Whitelist-add guard, part 2: true when any blocklist entry equals the
    /// domain or lives beneath it (a whitelisted domain covers all subdomains,
    /// so allowing "example.com" would also unblock a blocked
    /// "bad.example.com"). Linear over the list — fine for a rare admin action,
    /// never on the query path. <paramref name="example"/> carries one matching
    /// blocked entry for the error message.
    /// </summary>
    public bool HasBlockedDescendant(string domain, out string example)
    {
        example = string.Empty;
        var n = Normalize(domain);
        if (string.IsNullOrEmpty(n)) return false;

        HashSet<string> exact;
        HashSet<string> wildcard;
        lock (_swapLock)
        {
            exact = _exactDomains;
            wildcard = _wildcardRules;
        }

        var dotSuffix = "." + n;
        foreach (var entry in exact)
        {
            if (entry.Equals(n, StringComparison.OrdinalIgnoreCase) ||
                entry.EndsWith(dotSuffix, StringComparison.OrdinalIgnoreCase))
            {
                example = entry;
                return true;
            }
        }
        foreach (var entry in wildcard)
        {
            // "*.suffix" — strip the marker, then same containment test.
            var bare = entry[2..];
            if (bare.Equals(n, StringComparison.OrdinalIgnoreCase) ||
                bare.EndsWith(dotSuffix, StringComparison.OrdinalIgnoreCase))
            {
                example = entry;
                return true;
            }
        }
        return false;
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    public int ExactCount { get { lock (_swapLock) { return _exactDomains.Count; } } }
    public int WildcardCount { get { lock (_swapLock) { return _wildcardRules.Count; } } }
    public int TotalCount => ExactCount + WildcardCount;

    // ── Normalization ─────────────────────────────────────────────────────────

    private static string Normalize(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return string.Empty;
        return domain.Trim().ToLowerInvariant().TrimEnd('.');
    }
}