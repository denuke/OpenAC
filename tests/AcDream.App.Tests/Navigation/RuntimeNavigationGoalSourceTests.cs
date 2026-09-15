using AcDream.App.Navigation;
using AcDream.Core.Items;

namespace AcDream.App.Tests.Navigation;

public sealed class RuntimeNavigationGoalSourceTests
{
    [Fact]
    public void ARouteKeepsOutOfAnObjectThatStandsStill() =>
        Assert.True(RuntimeNavigationGoalSource.StandsInTheWay(Item((ItemType)0, (PublicWeenieFlags)0)));

    [Theory]
    [InlineData(ItemType.Creature, (PublicWeenieFlags)0)]
    [InlineData(ItemType.Creature, PublicWeenieFlags.Player)]
    [InlineData((ItemType)0, PublicWeenieFlags.Door)]
    [InlineData((ItemType)0, PublicWeenieFlags.Corpse)]
    public void ARouteDoesNotKeepOutOfCreaturesPlayersDoorsOrCorpses(ItemType type, PublicWeenieFlags flags) =>
        Assert.False(RuntimeNavigationGoalSource.StandsInTheWay(Item(type, flags)));

    private static ClientObject Item(ItemType type, PublicWeenieFlags flags) => new()
    {
        ObjectId = 0x7000_0001u,
        Name = "Object",
        Type = type,
        PublicWeenieBitfield = (uint)flags,
    };
}
