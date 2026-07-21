namespace Obstruo.Service.Data;

/// <summary>
/// Single source of truth for the built-in block categories and whether each is
/// ON out of the box. Both the DB seed (<see cref="ObstruoDatabase"/>) and the
/// blocklist seed (<see cref="BlocklistRepository"/>) reference this so category
/// membership and default-enabled state can never drift between them.
///
/// Grey-tier categories (finding M6) are adult-adjacent or merely policy calls —
/// dating, imageboards, soft magazines/dictionaries, casual game portals, JP
/// storefronts. They ship <b>disabled</b>: the master list stays clean hard-adult
/// domains that are always blocked, while these are exposed as opt-in toggles the
/// user can switch on. Turning a category on reloads the live store, so its
/// domains start blocking immediately.
/// </summary>
public static class BlockCategoryDefaults
{
    public sealed record Category(string Name, string Severity, bool EnabledByDefault);

    public static readonly IReadOnlyList<Category> All =
    [
        // ── Always-on: hard adult + bypass/malware infrastructure ──────────────
        new("Adult",   "High", true),
        new("Chat",    "Med",  true),
        new("Games",   "Low",  true),
        new("AIAdult", "High", true),
        new("Malware", "High", true),
        new("Bypass",  "High", true),
        new("Custom",  "Med",  true),
        new("Paid",    "High", true),
        new("SexChat", "High", true),

        // ── Grey tier (M6): opt-in, OFF by default ─────────────────────────────
        new("Dating",      "Med", false),  // dating / hookup-adjacent
        new("Forums",      "Low", false),  // imageboards / message boards
        new("SoftContent", "Low", false),  // soft magazines, dictionaries, media
        new("CasualGames", "Low", false),  // casual browser-game portals
        new("JPStore",     "Med", false),  // Japanese storefronts (mixed catalogue)
    ];

    /// <summary>Names of categories that ship disabled (the grey tier).</summary>
    public static readonly IReadOnlySet<string> DisabledByDefault =
        All.Where(c => !c.EnabledByDefault)
           .Select(c => c.Name)
           .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
