using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.Tests.Contract;

public sealed class WireNamesTests
{
    [Theory]
    [InlineData("Magic", "magic")]
    [InlineData("MeleeWeapon", "melee-weapon")]
    [InlineData("WandStaffOrb", "wand-staff-orb")]
    [InlineData("Npc", "npc")]
    [InlineData("InFlight", "in-flight")]
    [InlineData("HTTPServer", "http-server")]
    public void IdentifiersBecomeKebabCase(string name, string expected)
    {
        Assert.Equal(expected, WireNames.Kebab(name));
    }
}
