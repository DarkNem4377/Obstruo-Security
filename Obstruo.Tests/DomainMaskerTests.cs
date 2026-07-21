using Obstruo.Shared;

namespace Obstruo.Tests;

public class DomainMaskerTests
{
    [Theory]
    [InlineData("pornhub.com", "p*****b.com")]
    [InlineData("chaturbate.com", "c********e.com")]
    [InlineData("ab.com", "a*.com")]
    [InlineData("x.com", "*.com")]
    public void Mask_FollowsDocumentedFormat(string input, string expected)
        => Assert.Equal(expected, DomainMasker.Mask(input));

    [Fact]
    public void Mask_NoDot_MasksWholeString()
        => Assert.Equal("l*******t", DomainMasker.Mask("localhost"));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Mask_EmptyOrNull_PassesThrough(string? input)
        => Assert.Equal(input, DomainMasker.Mask(input!));

    [Fact]
    public void MaskBySource_SystemSource_AlwaysMasks()
    {
        var masked = DomainMasker.MaskBySource("pornhub.com", "obstruo-builtin",
            callerWantsMask: false);
        Assert.Equal("p*****b.com", masked);
    }

    [Fact]
    public void MaskBySource_CustomSource_UnmaskedByDefault()
        => Assert.Equal("example.com",
            DomainMasker.MaskBySource("example.com", "custom"));

    [Fact]
    public void MaskBySource_CustomSource_MaskedWhenRequested()
        => Assert.Equal("e*****e.com",
            DomainMasker.MaskBySource("example.com", "custom", callerWantsMask: true));
}
