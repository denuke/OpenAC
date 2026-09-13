using AcDream.Plugins.Agent.Intake;

namespace AcDream.Plugins.Agent.Tests.Intake;

public sealed class CommandLineTests
{
    [Fact]
    public void TheFirstWordIsTheVerbInLowerCaseAndTheRestIsTrimmed()
    {
        CommandLine line = CommandLine.Parse(4, "  Cast   Strength Self VI  ", "mcp");

        Assert.Equal(4, line.Id);
        Assert.Equal("Cast   Strength Self VI", line.Text);
        Assert.Equal("cast", line.Verb);
        Assert.Equal("Strength Self VI", line.Arguments);
        Assert.Equal("mcp", line.Source);
    }

    [Fact]
    public void ABareWordHasNoArguments()
    {
        CommandLine line = CommandLine.Parse(1, "jump", "mcp");

        Assert.Equal("jump", line.Verb);
        Assert.Equal(string.Empty, line.Arguments);
    }
}
