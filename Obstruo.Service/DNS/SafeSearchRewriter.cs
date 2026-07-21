using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Obstruo.Service.Data;

namespace Obstruo.Service.Dns;

/// <summary>
/// DNS-level SafeSearch enforcement. Search-engine hostnames are rewritten to
/// their vendor "force SafeSearch" host via a CNAME so filtered results are
/// mandatory system-wide. Google, YouTube, and Bing support this; DuckDuckGo has
/// no DNS mechanism, so it is never mapped (the UI correctly shows "Not supported").
///
/// Config keys (seeded ON — see ObstruoDatabase.SeedConfig):
///   safesearch_google        0|1
///   safesearch_youtube       0|1
///   safesearch_bing          0|1
///   safesearch_youtube_level moderate|strict
///
/// <see cref="Refresh"/> reloads a volatile snapshot; the DNS query path reads the
/// snapshot with a single dictionary lookup and no per-query DB hit.
/// </summary>
public sealed class SafeSearchRewriter
{
    public const string GoogleTarget = "forcesafesearch.google.com";
    public const string BingTarget = "strict.bing.com";
    public const string YouTubeModerateTarget = "restrictmoderate.youtube.com";
    public const string YouTubeStrictTarget = "restrict.youtube.com";

    private enum Engine { Google, YouTube, Bing }

    // Managed query hostnames → engine. Lowercase, no trailing dot.
    // ponytail: Google ccTLDs (google.co.uk, google.de, …) are not enumerated —
    // add them here if country-domain coverage is needed.
    private static readonly Dictionary<string, Engine> Hosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["google.com"] = Engine.Google,
            ["www.google.com"] = Engine.Google,
            ["youtube.com"] = Engine.YouTube,
            ["www.youtube.com"] = Engine.YouTube,
            ["m.youtube.com"] = Engine.YouTube,
            ["youtubei.googleapis.com"] = Engine.YouTube,
            ["youtube.googleapis.com"] = Engine.YouTube,
            ["www.youtube-nocookie.com"] = Engine.YouTube,
            ["bing.com"] = Engine.Bing,
            ["www.bing.com"] = Engine.Bing,
        };

    private readonly ObstruoDatabase _db;
    private readonly ILogger<SafeSearchRewriter> _logger;

    private volatile Snapshot _snap = new(false, false, false, false);
    private sealed record Snapshot(bool Google, bool YouTube, bool Bing, bool YouTubeStrict);

    public SafeSearchRewriter(ObstruoDatabase db, ILogger<SafeSearchRewriter> logger)
    {
        _db = db;
        _logger = logger;
        Refresh();
    }

    /// <summary>
    /// Pure mapping — testable without a database. Returns the SafeSearch target
    /// host for <paramref name="domain"/>, or null when the name is not a managed
    /// search host or that engine is disabled.
    /// </summary>
    public static string? ResolveTarget(
        string domain, bool google, bool youtube, bool bing, bool youtubeStrict)
    {
        if (!Hosts.TryGetValue(domain, out var engine)) return null;
        return engine switch
        {
            Engine.Google => google ? GoogleTarget : null,
            Engine.YouTube => youtube ? (youtubeStrict ? YouTubeStrictTarget : YouTubeModerateTarget) : null,
            Engine.Bing => bing ? BingTarget : null,
            _ => null
        };
    }

    /// <summary>Target host for a live query, using the current config snapshot.</summary>
    public string? TryGetTarget(string domain)
    {
        var s = _snap;
        return ResolveTarget(domain, s.Google, s.YouTube, s.Bing, s.YouTubeStrict);
    }

    /// <summary>Reloads the config snapshot. Called once at startup and after any
    /// <c>safesearch_*</c> setting changes over IPC.</summary>
    public void Refresh()
    {
        var google = ReadBool("safesearch_google", true);
        var youtube = ReadBool("safesearch_youtube", true);
        var bing = ReadBool("safesearch_bing", true);
        var strict = string.Equals(
            ReadValue("safesearch_youtube_level"), "strict", StringComparison.OrdinalIgnoreCase);

        _snap = new Snapshot(google, youtube, bing, strict);
        _logger.LogInformation(
            "SafeSearch snapshot: google={Google} youtube={YouTube}({Level}) bing={Bing}",
            google, youtube, strict ? "strict" : "moderate", bing);
    }

    private bool ReadBool(string key, bool fallback)
        => ReadValue(key) is { } v ? v == "1" : fallback;

    private string? ReadValue(string key)
    {
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM Config WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
            var r = cmd.ExecuteScalar()?.ToString();
            return string.IsNullOrEmpty(r) ? null : r;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SafeSearch: failed reading Config '{Key}'", key);
            return null;
        }
    }
}
