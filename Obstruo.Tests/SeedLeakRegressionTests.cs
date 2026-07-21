using Obstruo.Service.Data;
using Obstruo.Service.Dns;

namespace Obstruo.Tests;

/// <summary>
/// Regression suite for the 2026-07 v1.0.0 block-test findings (reports 1, 4, 5).
/// Loads the REAL shipped seed list and asserts every domain that leaked in
/// those tests is now blocked, that grey-tier policy entries stay unblocked
/// until they ship as opt-in categories, and that control domains still resolve.
/// A release must not ship if any of these regress.
/// </summary>
public class SeedLeakRegressionTests
{
    // Category ids only need to be consistent within the test — GetSeedDomains
    // resolves categories by name. Derived from the real category defaults so the
    // grey tier is included and stays in sync.
    private static readonly Dictionary<string, int> _categories =
        BlockCategoryDefaults.All
            .Select((c, i) => (c.Name, Id: i + 1))
            .ToDictionary(x => x.Name, x => x.Id, StringComparer.OrdinalIgnoreCase);

    // The default-on store: mirrors production's LoadIntoStore, which only loads
    // domains whose category is enabled. Grey-tier domains are therefore absent.
    private static readonly DnsBlocklistStore _store = BuildStoreFromSeed(enabledOnly: true);

    private static DnsBlocklistStore BuildStoreFromSeed(bool enabledOnly, string? alsoEnable = null)
    {
        var idToName = _categories.ToDictionary(kv => kv.Value, kv => kv.Key);
        var store = new DnsBlocklistStore();
        store.LoadDomains(BlocklistRepository.AllSeedEntries(_categories)
            .Where(s => !enabledOnly
                        || !BlockCategoryDefaults.DisabledByDefault.Contains(idToName[s.CategoryId])
                        || idToName[s.CategoryId].Equals(alsoEnable, StringComparison.OrdinalIgnoreCase))
            .Select(s => new DomainEntry(s.Domain, s.CategoryId, idToName[s.CategoryId], "High")));
        return store;
    }

    // ── Report 5 (List.txt) + consolidated “leaked in two independent tests” ──

    [Theory]
    [InlineData("adultfriendfinder.com")]
    [InlineData("brazzersnetwork.com")]
    [InlineData("keezmovies.com")]
    [InlineData("porn.com")]
    [InlineData("pornhublive.com")]
    [InlineData("spankwire.com")]
    public void DoubleConfirmedLeaks_AreBlocked(string domain)
        => Assert.True(_store.IsBlocked(domain).IsBlocked, $"{domain} must be blocked");

    // ── Report 1 (tester 24-site) ─────────────────────────────────────────────

    [Fact]
    public void Report1_FapsterCom_IsBlocked()
        => Assert.True(_store.IsBlocked("fapster.com").IsBlocked);

    // ── Report 4 (0001+0002) — full leak set minus grey-tier policy entries ──

    public static TheoryData<string> Report4Leaks() =>
    [
        "4tube.com", "8muses.com", "adultdvdempire.com", "adultfriendfinder.com",
        "analdin.com", "ancensored.com", "babestube.com", "bellesa.co",
        "blacked.com", "boyfriendtv.com", "brazzersnetwork.com", "cam4.es",
        "cameraprive.com", "caribbeancom.com", "celebjihad.com", "cityheaven.net",
        "crazyshit.com", "cumshots.com", "daftsex.com", "desixnxx2.net",
        "dirtypornvids.com", "duga.jp", "efukt.com", "e-hentai.org",
        "eporner.com", "erome.com", "escort-advisor.com", "evilangel.com",
        "freevideo.cz", "gayboystube.com", "gaymaletube.com", "hdsex.org",
        "heavy-r.com", "hotmovs.com", "iafd.com", "ice-gay.com",
        "iceporn.com", "imagefap.com", "iwank.tv", "ixxx.com",
        "jasmin.com", "katestube.com", "keezmovies.com", "kink.com",
        "kompoz.me", "literotica.com", "lsl.com", "manyvids.com",
        "muchohentai.com", "naughtyamerica.com", "nudevista.com", "oral-amateure.com",
        "pichunter.com", "planetsuzy.org", "porn.biz", "porn.com",
        "porndoe.com", "pornhub.org", "pornhublive.com", "pornhubpremium.com",
        "pornmd.com", "pornolab.net", "pornpics.com", "pornq.com",
        "porzo.com", "r18.com", "reallifecam.com", "redtubelive.com",
        "sankakucomplex.com", "sex.com", "shameless.com", "shooshtime.com",
        "sleazyneasy.com", "softcore69.com", "spankwire.com", "sunporno.com",
        "super.cz", "thefappening.pro", "theporndude.com", "theync.com",
        "thisav.com", "thisvid.com", "thumbzilla.com", "topescortbabes.com",
        "tt1069.com", "tubegalore.com", "tukif.com", "txxx.com",
        "upornia.com", "vercomicsporno.com", "videosdemadurasx.com",
        "videospornogratisx.net", "vikiporn.com", "vipergirls.to", "vjav.com",
        "vkmag.com", "vporn.com", "xhamster.desi", "xhamster9.com",
        "xhamsterlive.com", "xhamsterpremium.com", "xvidzz.com", "ypmate.com",
    ];

    [Theory]
    [MemberData(nameof(Report4Leaks))]
    public void Report4Leaks_AreBlocked(string domain)
        => Assert.True(_store.IsBlocked(domain).IsBlocked, $"{domain} must be blocked");

    // ── Grey-tier policy entries — deliberately NOT blocked by the base seed ──
    // These ship later as opt-in category toggles (P2.5); blocking them by
    // default would overreach the product's hard-adult scope.

    [Theory]
    [InlineData("4chan.org")]
    [InlineData("urbandictionary.com")]
    [InlineData("okcupid.com")]
    [InlineData("cosmopolitan.com")]
    [InlineData("girlsgogames.com")]
    [InlineData("dmm.com")]
    [InlineData("dlsite.com")]
    [InlineData("videa.hu")]
    [InlineData("joyclub.de")]
    public void GreyTierEntries_StayUnblockedByDefault(string domain)
        => Assert.False(_store.IsBlocked(domain).IsBlocked, $"{domain} is policy-tier, off by default");

    // Grey tier is a real, seeded, opt-in category — turning it on blocks its
    // domains. This proves the toggle works, not just that the default is off.
    [Theory]
    [InlineData("Dating", "okcupid.com")]
    [InlineData("Forums", "4chan.org")]
    [InlineData("SoftContent", "urbandictionary.com")]
    [InlineData("CasualGames", "girlsgogames.com")]
    [InlineData("JPStore", "dmm.com")]
    public void GreyTierEntries_BlockWhenTheirCategoryEnabled(string category, string domain)
    {
        var store = BuildStoreFromSeed(enabledOnly: true, alsoEnable: category);
        Assert.True(store.IsBlocked(domain).IsBlocked, $"{domain} should block when {category} is on");
    }

    // ── Shipped seed size — the site claims "5,900+ domains from the first minute" ──

    [Fact]
    public void SeededDomainCount_MeetsThe5900PlusClaim()
    {
        var total = BlocklistRepository.AllSeedEntries(_categories)
            .Select(s => s.Domain).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.True(total >= 5900, $"clean-install seed is {total}; site claims 5,900+");
    }

    [Fact]
    public void EmbeddedList_DoesNotHardBlockGreyDomains()
    {
        // The curated list files grey domains under Adult; AllSeedEntries must
        // never emit them under an enabled category.
        var greyUnderEnabled = BlocklistRepository.AllSeedEntries(_categories)
            .Where(s => "4chan.org okcupid.com dmm.com dlsite.com cosmopolitan.com urbandictionary.com videa.hu girlsgogames.com joyclub.de"
                        .Split(' ').Contains(s.Domain, StringComparer.OrdinalIgnoreCase))
            .Select(s => _categories.First(kv => kv.Value == s.CategoryId).Key)
            .Where(cat => !BlockCategoryDefaults.DisabledByDefault.Contains(cat))
            .ToList();
        Assert.Empty(greyUnderEnabled);
    }

    [Fact]
    public void GreyTierCategories_AreDisabledByDefault()
    {
        Assert.Contains("Dating", BlockCategoryDefaults.DisabledByDefault);
        Assert.Contains("JPStore", BlockCategoryDefaults.DisabledByDefault);
        // Hard-adult categories must never be in the opt-in tier.
        Assert.DoesNotContain("Adult", BlockCategoryDefaults.DisabledByDefault);
        Assert.DoesNotContain("AIAdult", BlockCategoryDefaults.DisabledByDefault);
    }

    // ── Controls — the filter must stay selective ─────────────────────────────

    [Theory]
    [InlineData("example.com")]
    [InlineData("microsoft.com")]
    [InlineData("github.com")]
    [InlineData("wikipedia.org")]
    [InlineData("cloudflare.com")]
    [InlineData("google.com")]
    public void ControlDomains_AreNeverBlocked(string domain)
        => Assert.False(_store.IsBlocked(domain).IsBlocked, $"{domain} must resolve");

    // ── Family expansion + real seed: niche entries must not bleed onto .com ──

    [Theory]
    [InlineData("family.com")]   // family.xxx is seeded
    [InlineData("ok.com")]       // ok.xxx is seeded
    [InlineData("go.com")]       // go.porn is seeded
    [InlineData("crush.com")]    // crush.to is seeded
    [InlineData("candy.com")]    // candy.ai is seeded
    public void RealSeed_NicheTldEntries_DoNotBlockLegitComDomains(string domain)
        => Assert.False(_store.IsBlocked(domain).IsBlocked, $"{domain} must resolve");

    // ── Report 3 (Obstruo built-in) sample — the 332/332 sweep must hold ─────

    [Theory]
    [InlineData("pornhub.com")]
    [InlineData("xvideos.com")]
    [InlineData("onlyfans.com")]
    [InlineData("chaturbate.com")]
    [InlineData("candy.ai")]
    [InlineData("nutaku.net")]
    [InlineData("dirtyroulette.com")]
    [InlineData("summertimesaga.com")]
    public void BuiltinSeedSample_StillBlocked(string domain)
        => Assert.True(_store.IsBlocked(domain).IsBlocked, $"{domain} must stay blocked");
}
