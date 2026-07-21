using Obstruo.Shared;

namespace Obstruo.Tests;

public class ObstruoVersionTests
{
    [Fact]
    public void DisplayVersion_AlwaysContainsProductVersion()
        => Assert.Contains(ObstruoVersion.Current, ObstruoVersion.DisplayVersion);

    // Deterministic parse logic — independent of whether this build was stamped.
    [Theory]
    [InlineData("1.0.1+636042f", "636042f")]
    [InlineData("1.0.1+636042f  ", "636042f")]     // trailing whitespace trimmed
    [InlineData("1.0.1", "unknown")]                // unstamped
    [InlineData("1.0.1+", "unknown")]               // empty revision id
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void ParseCommit_ExtractsRevisionId(string? info, string expected)
        => Assert.Equal(expected, ObstruoVersion.ParseCommit(info));

    // Soft end-to-end check: format is valid whether or not git was present.
    [Fact]
    public void CommitHash_HasValidFormat()
        => Assert.Matches("^(unknown|[0-9a-f]{7,40})$", ObstruoVersion.CommitHash);

    // Guards the v1.0.1 finding where the shipped binary self-reported "1.0.0+<commit>"
    // because Directory.Build.props carried no <Version>. The assembly's stamped
    // version must match the single-source-of-truth constant.
    [Fact]
    public void AssemblyVersion_MatchesVersionConstant()
    {
        var info = typeof(ObstruoVersion).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        Assert.False(string.IsNullOrEmpty(info));

        // InformationalVersion is "<version>" or "<version>+<commit>".
        var stampedVersion = info!.Split('+')[0];
        Assert.Equal(ObstruoVersion.Current, stampedVersion);
    }
}
