using System.Net;
using Obstruo.Service.Dns;

namespace Obstruo.Tests;

/// <summary>
/// The :53 block (finding H1) hinges on ComputeIpv4Complement producing netsh
/// remoteip ranges that cover the ENTIRE IPv4 space except the upstream IPs. A
/// bug here either severs Obstruo's own upstream (machine fails closed) or leaves
/// a hole an app can resolve through — so the range math is pinned exactly.
/// </summary>
public class Dns53FirewallTests
{
    private static long Span(string range)
    {
        var parts = range.Split('-');
        return (long)ToUInt(parts[1]) - ToUInt(parts[0]) + 1;
    }

    private static uint ToUInt(string ip)
    {
        var b = IPAddress.Parse(ip).GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static bool Covers(List<string> ranges, string ip)
    {
        var v = ToUInt(ip);
        foreach (var r in ranges)
        {
            var p = r.Split('-');
            if (v >= ToUInt(p[0]) && v <= ToUInt(p[1])) return true;
        }
        return false;
    }

    [Fact]
    public void Complement_ExcludesEachAllowedIp_CoversEverythingElse()
    {
        var ranges = Dns53Firewall.ComputeIpv4Complement(["8.8.8.8", "1.1.1.1"]);

        Assert.False(Covers(ranges, "8.8.8.8"));   // allowed → not blocked
        Assert.False(Covers(ranges, "1.1.1.1"));
        Assert.True(Covers(ranges, "8.8.4.4"));     // other resolvers → blocked
        Assert.True(Covers(ranges, "9.9.9.9"));
        Assert.True(Covers(ranges, "8.8.8.7"));     // neighbours of the hole
        Assert.True(Covers(ranges, "8.8.8.9"));
    }

    [Fact]
    public void Complement_TotalCoverageIsFullSpaceMinusAllowed()
    {
        var allowed = new[] { "8.8.8.8", "1.1.1.1", "9.9.9.9" };
        var ranges = Dns53Firewall.ComputeIpv4Complement(allowed);

        long total = ranges.Sum(Span);
        Assert.Equal(0x100000000L - allowed.Length, total); // 2^32 minus the holes
    }

    [Fact]
    public void Complement_HandlesBoundaryAddresses()
    {
        var lo = Dns53Firewall.ComputeIpv4Complement(["0.0.0.0"]);
        Assert.False(Covers(lo, "0.0.0.0"));
        Assert.True(Covers(lo, "0.0.0.1"));
        Assert.Equal(0x100000000L - 1, lo.Sum(Span));

        var hi = Dns53Firewall.ComputeIpv4Complement(["255.255.255.255"]);
        Assert.False(Covers(hi, "255.255.255.255"));
        Assert.True(Covers(hi, "255.255.255.254"));
        Assert.Equal(0x100000000L - 1, hi.Sum(Span));
    }

    [Fact]
    public void Complement_NoAllowedIps_IsOneFullRange()
    {
        var ranges = Dns53Firewall.ComputeIpv4Complement([]);
        Assert.Single(ranges);
        Assert.Equal("0.0.0.0-255.255.255.255", ranges[0]);
    }

    [Fact]
    public void Complement_DedupesAndIgnoresNonIpv4()
    {
        var ranges = Dns53Firewall.ComputeIpv4Complement(
            ["8.8.8.8", "8.8.8.8", "2606:4700:4700::1111", "not-an-ip", ""]);

        // Only the single distinct IPv4 hole remains.
        Assert.False(Covers(ranges, "8.8.8.8"));
        Assert.Equal(0x100000000L - 1, ranges.Sum(Span));
    }

    [Fact]
    public void RuleNames_AreDistinct_AndCoverBothFamiliesAndProtocols()
    {
        Assert.Equal(4, Dns53Firewall.RuleNames.Distinct().Count());
        Assert.Contains($"{Dns53Firewall.RulePrefix}-v4-UDP", Dns53Firewall.RuleNames);
        Assert.Contains($"{Dns53Firewall.RulePrefix}-v6-TCP", Dns53Firewall.RuleNames);
    }
}
