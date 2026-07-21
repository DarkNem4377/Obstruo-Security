using Obstruo.Service.Data;
using Xunit;

namespace Obstruo.Tests;

/// <summary>
/// Pure-helper coverage for incidents: the reference format and bypass title.
/// (The DB write/read path has no in-repo test harness — those are exercised by
/// the running service.)
/// </summary>
public class IncidentRepositoryTests
{
    [Theory]
    [InlineData(1, "INC-0001")]
    [InlineData(7, "INC-0007")]
    [InlineData(42, "INC-0042")]
    [InlineData(12345, "INC-12345")]   // grows past the 4-digit pad
    public void FormatRef_PadsToFourDigits(long id, string expected)
        => Assert.Equal(expected, IncidentRepository.FormatRef(id));

    [Fact]
    public void BuildBypassTitle_IncludesDomain()
        => Assert.Equal(
            "Bypass attempt blocked: protonvpn.com",
            IncidentRepository.BuildBypassTitle("protonvpn.com"));
}
