using Obstruo.Service.Dns;
using Xunit;

namespace Obstruo.Tests;

/// <summary>
/// Pins the SafeSearch host→target mapping: which hostnames are rewritten, the
/// per-engine on/off gate, YouTube moderate vs strict, and passthrough for
/// everything else. Uses the pure <see cref="SafeSearchRewriter.ResolveTarget"/>
/// so no database or DNS server is involved.
/// </summary>
public class SafeSearchRewriterTests
{
    // All engines on, YouTube moderate unless overridden.
    private static string? Resolve(string domain, bool youtubeStrict = false) =>
        SafeSearchRewriter.ResolveTarget(domain, google: true, youtube: true, bing: true, youtubeStrict: youtubeStrict);

    [Theory]
    [InlineData("google.com", SafeSearchRewriter.GoogleTarget)]
    [InlineData("www.google.com", SafeSearchRewriter.GoogleTarget)]
    [InlineData("www.bing.com", SafeSearchRewriter.BingTarget)]
    [InlineData("bing.com", SafeSearchRewriter.BingTarget)]
    [InlineData("www.youtube.com", SafeSearchRewriter.YouTubeModerateTarget)]
    [InlineData("m.youtube.com", SafeSearchRewriter.YouTubeModerateTarget)]
    [InlineData("youtubei.googleapis.com", SafeSearchRewriter.YouTubeModerateTarget)]
    public void ManagedHosts_MapToTarget(string domain, string expected)
        => Assert.Equal(expected, Resolve(domain));

    [Fact]
    public void Hostnames_AreCaseInsensitive()
        => Assert.Equal(SafeSearchRewriter.GoogleTarget, Resolve("WWW.Google.COM"));

    [Fact]
    public void YouTube_Strict_UsesStrictTarget()
        => Assert.Equal(
            SafeSearchRewriter.YouTubeStrictTarget,
            Resolve("www.youtube.com", youtubeStrict: true));

    [Theory]
    [InlineData("example.com")]
    [InlineData("duckduckgo.com")]   // no DNS SafeSearch — never mapped
    [InlineData("notgoogle.com")]
    [InlineData("google.com.evil.com")]
    public void UnmanagedHosts_ReturnNull(string domain)
        => Assert.Null(Resolve(domain));

    [Fact]
    public void DisabledEngine_ReturnsNull()
    {
        Assert.Null(SafeSearchRewriter.ResolveTarget(
            "www.google.com", google: false, youtube: true, bing: true, youtubeStrict: false));
        Assert.Null(SafeSearchRewriter.ResolveTarget(
            "www.youtube.com", google: true, youtube: false, bing: true, youtubeStrict: false));
        Assert.Null(SafeSearchRewriter.ResolveTarget(
            "www.bing.com", google: true, youtube: true, bing: false, youtubeStrict: false));
    }
}
