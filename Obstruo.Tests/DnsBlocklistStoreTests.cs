using Obstruo.Service.Dns;

namespace Obstruo.Tests;

/// <summary>
/// Blocklist matching is the core of the product — these tests pin down the
/// exact/subdomain/wildcard semantics and the whitelist-wins rule so a future
/// refactor can't silently reopen the subdomain bypass.
/// </summary>
public class DnsBlocklistStoreTests
{
    private static DnsBlocklistStore NewStore(params string[] domains)
    {
        var store = new DnsBlocklistStore();
        store.LoadDomains(domains.Select(d => new DomainEntry(d, 1, "Adult", "High")));
        return store;
    }

    // ── Exact + subdomain coverage ────────────────────────────────────────────

    [Theory]
    [InlineData("pornhub.com")]              // apex
    [InlineData("www.pornhub.com")]          // subdomain
    [InlineData("m.deep.pornhub.com")]       // nested subdomain
    [InlineData("PORNHUB.COM")]              // case-insensitive
    [InlineData("pornhub.com.")]             // trailing dot (raw DNS form)
    public void ExactEntry_BlocksApexAndAllSubdomains(string query)
    {
        var store = NewStore("pornhub.com");
        Assert.True(store.IsBlocked(query).IsBlocked);
    }

    [Theory]
    [InlineData("notpornhub.com")]           // suffix of the label, not a subdomain
    [InlineData("pornhub.com.evil.net")]     // blocked name embedded as a prefix
    [InlineData("example.com")]
    [InlineData("com")]                      // bare TLD never matches
    public void ExactEntry_DoesNotBlockLookalikes(string query)
    {
        var store = NewStore("pornhub.com");
        Assert.False(store.IsBlocked(query).IsBlocked);
    }

    // ── Wildcard semantics ────────────────────────────────────────────────────

    [Fact]
    public void WildcardEntry_BlocksSubdomainsOnly_NeverApex()
    {
        var store = NewStore("*.example.com");

        Assert.True(store.IsBlocked("www.example.com").IsBlocked);
        Assert.True(store.IsBlocked("a.b.example.com").IsBlocked);
        Assert.False(store.IsBlocked("example.com").IsBlocked);
    }

    // ── Metadata ──────────────────────────────────────────────────────────────

    [Fact]
    public void BlockResult_CarriesCategoryAndSeverity()
    {
        var store = new DnsBlocklistStore();
        store.LoadDomains([new DomainEntry("badsite.com", 5, "Malware", "Critical")]);

        var result = store.IsBlocked("cdn.badsite.com");

        Assert.True(result.IsBlocked);
        Assert.Equal(5, result.CategoryId);
        Assert.Equal("Malware", result.CategoryName);
        Assert.Equal("Critical", result.Severity);
    }

    // ── Mutations ─────────────────────────────────────────────────────────────

    [Fact]
    public void AddThenRemoveDomain_TakesEffectImmediately()
    {
        var store = NewStore();

        store.AddDomain("newlyblocked.com", 2, "Chat", "Med");
        Assert.True(store.IsBlocked("newlyblocked.com").IsBlocked);

        store.RemoveDomain("newlyblocked.com");
        Assert.False(store.IsBlocked("newlyblocked.com").IsBlocked);
    }

    // ── Whitelist ─────────────────────────────────────────────────────────────

    [Fact]
    public void Whitelist_OverridesBlocklist_IncludingSubdomains()
    {
        var store = NewStore("example.com");
        store.LoadWhitelist([("example.com", (DateTime?)null)]);

        Assert.False(store.IsBlocked("example.com").IsBlocked);
        Assert.False(store.IsBlocked("www.example.com").IsBlocked);
    }

    [Fact]
    public void Whitelist_ParentEntry_UnblocksChildOfWildcard()
    {
        var store = NewStore("*.example.com");
        store.LoadWhitelist([("example.com", (DateTime?)null)]);

        Assert.False(store.IsBlocked("www.example.com").IsBlocked);
    }

    [Fact]
    public void Whitelist_ExpiredEntry_IsIgnored()
    {
        var store = NewStore("example.com");
        store.LoadWhitelist([("example.com", (DateTime?)DateTime.UtcNow.AddMinutes(-1))]);

        Assert.True(store.IsBlocked("example.com").IsBlocked);
    }

    [Fact]
    public void Whitelist_FutureExpiry_StillActive()
    {
        var store = NewStore("example.com");
        store.LoadWhitelist([("example.com", (DateTime?)DateTime.UtcNow.AddMinutes(30))]);

        Assert.False(store.IsBlocked("example.com").IsBlocked);
    }

    [Fact]
    public void Whitelist_DoesNotUnblockUnrelatedDomains()
    {
        var store = NewStore("blocked-a.com", "blocked-b.com");
        store.LoadWhitelist([("blocked-a.com", (DateTime?)null)]);

        Assert.False(store.IsBlocked("blocked-a.com").IsBlocked);
        Assert.True(store.IsBlocked("blocked-b.com").IsBlocked);
    }

    [Fact]
    public void RemoveWhitelistEntry_RestoresBlocking()
    {
        var store = NewStore("example.com");
        store.AddWhitelistEntry("example.com", null);
        Assert.False(store.IsBlocked("example.com").IsBlocked);

        store.RemoveWhitelistEntry("example.com");
        Assert.True(store.IsBlocked("example.com").IsBlocked);
    }

    // ── Edge input ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceQuery_NeverBlocked(string query)
    {
        var store = NewStore("example.com");
        Assert.False(store.IsBlocked(query).IsBlocked);
    }

    // ── Reload atomicity / replacement ────────────────────────────────────────

    [Fact]
    public void LoadDomains_ReplacesPreviousSet_NotMerges()
    {
        var store = NewStore("old.com");
        Assert.True(store.IsBlocked("old.com").IsBlocked);

        store.LoadDomains([new DomainEntry("new.com", 1, "Adult", "High")]);

        Assert.False(store.IsBlocked("old.com").IsBlocked); // old set dropped
        Assert.True(store.IsBlocked("new.com").IsBlocked);
    }

    [Fact]
    public void LoadWhitelist_ReplacesPreviousWhitelist()
    {
        var store = NewStore("a.com", "b.com");
        store.LoadWhitelist([("a.com", (DateTime?)null)]);
        Assert.False(store.IsBlocked("a.com").IsBlocked);

        // Reload with only b.com whitelisted — a.com blocking must return.
        store.LoadWhitelist([("b.com", (DateTime?)null)]);
        Assert.True(store.IsBlocked("a.com").IsBlocked);
        Assert.False(store.IsBlocked("b.com").IsBlocked);
    }

    // ── Counts ────────────────────────────────────────────────────────────────

    [Fact]
    public void Counts_SeparateExactAndWildcard()
    {
        var store = new DnsBlocklistStore();
        store.LoadDomains(
        [
            new DomainEntry("a.com", 1, "Adult", "High"),
            new DomainEntry("b.com", 1, "Adult", "High"),
            new DomainEntry("*.c.com", 1, "Adult", "High"),
        ]);

        Assert.Equal(2, store.ExactCount);
        Assert.Equal(1, store.WildcardCount);
        Assert.Equal(3, store.TotalCount);
    }

    // ── Whitelist precedence over a directly-blocked subdomain ────────────────

    [Fact]
    public void Whitelist_ExactChild_OverridesBlockedParent()
    {
        var store = NewStore("example.com");
        store.AddWhitelistEntry("safe.example.com", null);

        Assert.False(store.IsBlocked("safe.example.com").IsBlocked); // whitelisted child
        Assert.True(store.IsBlocked("other.example.com").IsBlocked); // sibling still blocked
    }

    // ── Brand-family expansion ────────────────────────────────────────────────

    [Theory]
    [InlineData("pornhub.org")]              // alternate TLD
    [InlineData("pornhub.net")]
    [InlineData("pornhub.co")]
    [InlineData("pornhub.xxx")]
    [InlineData("www.pornhub.org")]          // subdomain of a family sibling
    [InlineData("pornhublive.com")]          // product-suffix sibling
    [InlineData("pornhubnetwork.com")]
    [InlineData("pornhubpremium.com")]
    public void FamilyExpansion_BlockedComApex_CoversSiblings(string query)
    {
        var store = NewStore("pornhub.com");
        Assert.True(store.IsBlocked(query).IsBlocked);
    }

    [Theory]
    [InlineData("family.xxx", "family.com")] // niche TLD entry must not bleed onto .com
    [InlineData("ok.xxx", "ok.com")]
    [InlineData("brand.org", "brand.net")]   // expansion only targets .com apexes
    public void FamilyExpansion_NeverFlowsTowardCom(string blocked, string query)
    {
        var store = NewStore(blocked);
        Assert.False(store.IsBlocked(query).IsBlocked);
    }

    [Theory]
    [InlineData("notpornhub.org")]           // lookalike stem, not the brand
    [InlineData("cbsnetwork.com")]           // stripped stem (cbs.com) is not blocked
    [InlineData("example.org")]
    public void FamilyExpansion_DoesNotBlockUnrelatedDomains(string query)
    {
        var store = NewStore("pornhub.com");
        Assert.False(store.IsBlocked(query).IsBlocked);
    }

    [Fact]
    public void FamilyExpansion_ShortStrippedStem_IsIgnored()
    {
        // "xlive.com" would strip to "x.com" — below the 3-char stem guard.
        var store = NewStore("x.com");
        Assert.False(store.IsBlocked("xlive.com").IsBlocked);
    }

    [Fact]
    public void FamilyExpansion_CarriesParentMetadata()
    {
        var store = new DnsBlocklistStore();
        store.LoadDomains([new DomainEntry("brazzers.com", 8, "Paid", "High")]);

        var result = store.IsBlocked("brazzersnetwork.com");

        Assert.True(result.IsBlocked);
        Assert.Equal("Paid", result.CategoryName);
    }

    [Fact]
    public void FamilyExpansion_WhitelistedSibling_StaysUnblocked()
    {
        var store = NewStore("pornhub.com");
        store.AddWhitelistEntry("pornhub.org", null);

        Assert.False(store.IsBlocked("pornhub.org").IsBlocked);
        Assert.True(store.IsBlocked("pornhub.xxx").IsBlocked); // other siblings unaffected
    }

    [Fact]
    public void FamilyExpansion_WhitelistedParent_SuppressesFamilyBlocks()
    {
        var store = NewStore("pornhub.com");
        store.AddWhitelistEntry("pornhub.com", null);

        Assert.False(store.IsBlocked("pornhub.org").IsBlocked);
        Assert.False(store.IsBlocked("pornhublive.com").IsBlocked);
    }

    // ── Whitelist-add guard: ProbeSystemBlock + HasBlockedDescendant ──────────

    [Theory]
    [InlineData("pornhub.com")]              // exact blocked apex
    [InlineData("www.pornhub.com")]          // subdomain of a blocked apex
    [InlineData("pornhub.org")]              // brand-family sibling
    [InlineData("pornhublive.com")]          // product-suffix sibling
    public void ProbeSystemBlock_MatchesBlockedDomains_EvenWhenWhitelisted(string candidate)
    {
        var store = NewStore("pornhub.com");
        // An existing (illegitimate) whitelist entry must not mask the probe —
        // that is the whole point of the whitelist-ignoring walk.
        store.AddWhitelistEntry(candidate, null);

        Assert.True(store.ProbeSystemBlock(candidate).IsBlocked);
        Assert.False(store.IsBlocked(candidate).IsBlocked); // live path still honors whitelist
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("github.com")]
    public void ProbeSystemBlock_PassesCleanDomains(string candidate)
    {
        var store = NewStore("pornhub.com");
        Assert.False(store.ProbeSystemBlock(candidate).IsBlocked);
    }

    [Fact]
    public void ProbeSystemBlock_CarriesCategoryForErrorMessage()
    {
        var store = new DnsBlocklistStore();
        store.LoadDomains([new DomainEntry("badsite.com", 5, "Malware", "Critical")]);

        var result = store.ProbeSystemBlock("cdn.badsite.com");

        Assert.True(result.IsBlocked);
        Assert.Equal("Malware", result.CategoryName);
    }

    [Fact]
    public void HasBlockedDescendant_CatchesParentOfBlockedEntry()
    {
        var store = NewStore("bad.example.com");

        Assert.True(store.HasBlockedDescendant("example.com", out var example));
        Assert.Equal("bad.example.com", example);
    }

    [Fact]
    public void HasBlockedDescendant_CatchesWildcardBeneath()
    {
        var store = NewStore("*.bad.example.com");
        Assert.True(store.HasBlockedDescendant("example.com", out _));
    }

    [Theory]
    [InlineData("example.com")]              // nothing blocked beneath
    [InlineData("notpornhub.com")]           // label-suffix lookalike is NOT a descendant
    public void HasBlockedDescendant_PassesCleanParents(string candidate)
    {
        var store = NewStore("pornhub.com");
        Assert.False(store.HasBlockedDescendant(candidate, out _));
    }

    [Fact]
    public void FamilyExpansion_DirectEntryWins_OverFamilyMetadata()
    {
        var store = new DnsBlocklistStore();
        store.LoadDomains(
        [
            new DomainEntry("xhamster.com", 1, "Adult", "High"),
            new DomainEntry("xhamsterlive.com", 2, "Chat", "Med"),
        ]);

        var result = store.IsBlocked("xhamsterlive.com");

        Assert.True(result.IsBlocked);
        Assert.Equal("Chat", result.CategoryName); // its own entry, not the family parent's
    }
}
