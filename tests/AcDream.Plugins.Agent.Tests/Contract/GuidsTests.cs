using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.Tests.Contract;

public sealed class GuidsTests
{
    [Theory]
    [InlineData("0x5000000A", 0x5000000Au)]
    [InlineData("0x5000000a", 0x5000000Au)]
    [InlineData(" 1342177290 ", 1342177290u)]
    public void HexWithPrefixAndDecimalAreRead(string text, uint expected)
    {
        Assert.True(Guids.TryParse(text, out uint id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("5000000A")]
    [InlineData("0x0")]
    [InlineData("-5")]
    [InlineData("drudge")]
    public void AnythingElseIsRefused(string text)
    {
        Assert.False(Guids.TryParse(text, out _));
    }
}
