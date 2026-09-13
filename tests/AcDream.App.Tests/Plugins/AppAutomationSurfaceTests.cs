using AcDream.App.Plugins;
using AcDream.Core.Chat;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using System.Numerics;

namespace AcDream.App.Tests.Plugins;

public sealed class AppAutomationSurfaceTests
{
    [Fact]
    public void ProjectileDebugSamplesAreDetachedValidatedAndClearedOnUnbind()
    {
        using var surface = new AppAutomationSurface();
        PluginProjectileDebugSample[] source =
        [
            new(new Vector3(1f, 2f, 3f), true, 0.4f),
            new(new Vector3(float.NaN, 0f, 0f), false, 0.4f),
        ];

        surface.Projectiles.ShowDebugSamples(source);
        source[0] = default;

        PluginProjectileDebugSample sample = Assert.Single(
            surface.CaptureProjectileDebugSamples());
        Assert.Equal(new Vector3(1f, 2f, 3f), sample.WorldPosition);
        Assert.True(sample.IsClear);

        surface.Unbind();
        Assert.Empty(surface.CaptureProjectileDebugSamples());
    }

    [Fact]
    public void SelectionAutomationUsesTheBoundCanonicalActionRoute()
    {
        using var surface = new AppAutomationSurface();
        var actions = new List<PluginSelectionAction>();
        surface.BindSelectionActions(action =>
        {
            actions.Add(action);
            return true;
        });

        Assert.True(surface.Selection.Execute(
            PluginSelectionAction.PreviousSelection));
        Assert.True(surface.Selection.Execute(
            PluginSelectionAction.NextPlayer));
        Assert.Equal(
            [
                PluginSelectionAction.PreviousSelection,
                PluginSelectionAction.NextPlayer,
            ],
            actions);
    }

    [Theory]
    [InlineData((uint)ItemType.MeleeWeapon, 0u, PluginObjectClass.MeleeWeapon)]
    [InlineData((uint)ItemType.Armor, 0u, PluginObjectClass.Armor)]
    [InlineData((uint)ItemType.Creature, 0x10u, PluginObjectClass.Monster)]
    [InlineData((uint)ItemType.Creature, 0u, PluginObjectClass.Npc)]
    [InlineData((uint)ItemType.Creature, 0x04000010u, PluginObjectClass.CombatPet)]
    [InlineData((uint)ItemType.Creature, 0x8u, PluginObjectClass.Player)]
    [InlineData((uint)ItemType.Misc, 0x200u, PluginObjectClass.Vendor)]
    [InlineData((uint)ItemType.Misc, 0x1000u, PluginObjectClass.Door)]
    public void ObjectClassProjectionMatchesVirindiPriority(
        uint itemType,
        uint publicFlags,
        PluginObjectClass expected)
    {
        var item = new ClientObject
        {
            ObjectId = 1u,
            Type = (ItemType)itemType,
            PublicWeenieBitfield = publicFlags,
        };

        Assert.Equal(expected, AppAutomationSurface.ClassifyObject(item));
    }

    [Fact]
    public void NavigationProjectionUsesVtankMapCoordinatesAndCompassHeading()
    {
        PluginNavigationPosition center =
            AppAutomationSurface.ProjectNavigationPosition(new Position(
                0x7F7F0001u,
                new Vector3(84f, 84f, 240f),
                Quaternion.Identity));

        Assert.Equal(0d, center.EastWest, 8);
        Assert.Equal(0d, center.NorthSouth, 8);
        Assert.Equal(1d, center.Elevation, 8);
        Assert.Equal(0f, center.HeadingDegrees, 4);
        Assert.True(center.IsOutdoor);

        PluginNavigationPosition nextBlock =
            AppAutomationSurface.ProjectNavigationPosition(new Position(
                0x80800041u,
                new Vector3(84f, 84f, 0f),
                Quaternion.Identity));

        Assert.Equal(0.8d, nextBlock.EastWest, 8);
        Assert.Equal(0.8d, nextBlock.NorthSouth, 8);
        Assert.False(nextBlock.IsOutdoor);
    }

    [Fact]
    public void ChatCapture_isOrderedCursorBasedAndDetachesAcrossSessions()
    {
        using var first = GameRuntimeTestFactory.Create();
        using var second = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(
            first,
            first.CharacterOwner,
            first.ActionOwner.SpellCast);

        first.CommunicationOwner.AddText(
            "You cast Imperil Other VII on Olthoi.",
            RetailLogTextType.Magic);
        PluginChatMessage one = Assert.Single(surface.CaptureMessages(0));
        Assert.Equal("You cast Imperil Other VII on Olthoi.", one.Text);
        Assert.Empty(surface.CaptureMessages(one.Sequence));

        surface.Bind(
            second,
            second.CharacterOwner,
            second.ActionOwner.SpellCast);
        first.CommunicationOwner.AddText(
            "stale first-session line",
            RetailLogTextType.Magic);
        second.CommunicationOwner.AddText(
            "You cast Fester Other VII on Olthoi.",
            RetailLogTextType.Magic);

        PluginChatMessage two = Assert.Single(
            surface.CaptureMessages(one.Sequence));
        Assert.True(two.Sequence > one.Sequence);
        Assert.Equal("You cast Fester Other VII on Olthoi.", two.Text);
        Assert.Equal(1, second.CommunicationOwner.SubscriberCount);

        surface.Dispose();
        Assert.Equal(0, second.CommunicationOwner.SubscriberCount);
    }

    [Fact]
    public void PostSystemMessage_RoutesToChatLog_NeverSpewBox()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        surface.PostSystemMessage("MossTank: buffs applied.");

        var entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("MossTank: buffs applied.", entry.Text);
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);

        runtime.CommunicationOwner.SpewBox.Tick(0d);
        Assert.Equal(0, runtime.CommunicationOwner.SpewBox.Count);
    }

    [Fact]
    public void InventoryCompletionProjectsTheCanonicalRequestReceipt()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        const uint itemId = 0x50000123u;
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = itemId,
            Name = "Stack",
            StackSize = 10,
            StackSizeMax = 100,
        });

        Assert.True(runtime.InventoryOwner.Transactions.TryDispatch(
            InventoryRequestKind.Merge,
            itemId,
            static () => true));
        Assert.True(objects.UpdateStackSize(itemId, 9, 0));

        PluginInventoryCompletion completion =
            surface.Items.LastInventoryCompletion;
        Assert.True(completion.Revision > 0);
        Assert.Equal(PluginInventoryCommandKind.Merge, completion.Kind);
        Assert.Equal(itemId, completion.SourceObjectId);
        Assert.True(completion.IsSuccess);
    }

    [Fact]
    public void RecoveryClearsExactlyOneCanonicalBusyReference()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        runtime.InventoryOwner.Transactions.IncrementBusyCount();
        runtime.InventoryOwner.Transactions.IncrementBusyCount();

        PluginRecoveryResult first = surface.Recovery.ClearOneBusyReference();
        PluginRecoveryResult second = surface.Recovery.ClearOneBusyReference();
        PluginRecoveryResult alreadyClear =
            surface.Recovery.ClearOneBusyReference();

        Assert.True(first.Accepted);
        Assert.Equal((2, 1), (first.PreviousCount, first.CurrentCount));
        Assert.Equal((1, 0), (second.PreviousCount, second.CurrentCount));
        Assert.Equal((0, 0),
            (alreadyClear.PreviousCount, alreadyClear.CurrentCount));
        Assert.Equal(0, runtime.InventoryOwner.Transactions.BusyCount);
    }

    [Fact]
    public void EnchantmentLedgerSharesReportedAndConfirmedLocalDurationCasts()
    {
        var operations = new SpellOperations();
        using var runtime = GameRuntimeTestFactory.Create(spellCast: operations);
        runtime.CharacterOwner.InstallSpellMetadata(SpellTable.Create([DurationSpell()]));
        runtime.CharacterOwner.Spellbook.OnSpellLearned(42u);
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        Assert.True(surface.Enchantments.ReportCast(100u, 42u, 30d));
        PluginTrackedEnchantment reported = Assert.Single(
            surface.Enchantments.Capture(100u));
        Assert.Equal(7u, reported.Family);
        Assert.Equal(350, reported.Quality);
        Assert.InRange(reported.SecondsRemaining, 29d, 30d);

        runtime.ActionOwner.Selection.Select(
            200u,
            SelectionChangeSource.Plugin);
        Assert.Equal(
            CastRequestResult.Sent,
            runtime.ActionOwner.SpellCast.Cast(42u));
        Assert.True(runtime.ActionOwner.SpellCast.CompleteUse(0u));

        Assert.True(surface.Magic.LastCompletion.IsSuccess);
        PluginTrackedEnchantment local = Assert.Single(
            surface.Enchantments.Capture(200u));
        Assert.Equal(42u, local.SpellId);
        Assert.InRange(local.SecondsRemaining, 59d, 60d);

        surface.Unbind();
        Assert.Empty(surface.Enchantments.Capture(100u));
        Assert.Empty(surface.Enchantments.Capture(200u));
    }

    [Fact]
    public void KnownSpellsIncludesSpellsTheNarrowerListsLeaveOut()
    {
        var operations = new SpellOperations();
        using var runtime = GameRuntimeTestFactory.Create(spellCast: operations);
        runtime.CharacterOwner.InstallSpellMetadata(SpellTable.Create([RecallSpell()]));
        runtime.CharacterOwner.Spellbook.OnSpellLearned(157u);
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        PluginSpellInfo recall = Assert.Single(surface.Spells.KnownSpells);
        Assert.Equal(157u, recall.SpellId);
        Assert.Equal("Lifestone Recall", recall.Name);
        Assert.DoesNotContain(
            surface.Spells.KnownSelfBuffs
                .Concat(surface.Spells.KnownAttackSpells)
                .Concat(surface.Spells.KnownCombatSpells),
            spell => spell.SpellId == 157u);

        surface.Unbind();
        Assert.Empty(surface.Spells.KnownSpells);
    }

    private static SpellMetadata RecallSpell() => new(
        157u,
        "Lifestone Recall",
        "Item Enchantment",
        0u,
        0u,
        string.Empty,
        0f,
        50,
        false,
        false,
        string.Empty,
        0,
        100,
        0u,
        1,
        false,
        false,
        true,
        0f,
        0u,
        0u,
        0u,
        0);

    private static SpellMetadata DurationSpell() => new(
        42u,
        "Fire Vulnerability Other VII",
        "Life Magic",
        7u,
        0u,
        string.Empty,
        60f,
        10,
        true,
        false,
        string.Empty,
        0,
        350,
        0u,
        7,
        false,
        true,
        false,
        0f,
        0u,
        0u,
        1u,
        0);

    private sealed class SpellOperations : IRuntimeSpellCastOperations
    {
        public uint LocalPlayerId => 1u;
        public bool CanSend => true;
        public bool HasRequiredComponents(uint spellId) => true;
        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => true;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }

    [Fact]
    public void ProjectWorldObject_FillsIconIdFromTheClientObject()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        var item = new ClientObject
        {
            ObjectId = 0x50000456u,
            Name = "Test Item",
            IconId = 0x06000165u,
        };

        System.Reflection.MethodInfo method = typeof(AppAutomationSurface)
            .GetMethod(
                "ProjectWorldObject",
                System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "AppAutomationSurface.ProjectWorldObject was not found by reflection.");
        var result = (PluginWorldObject)method.Invoke(
            surface,
            [runtime, null, item, 0u])!;

        Assert.Equal(0x06000165u, result.IconId);
    }


    [Fact]
    public void FaceHeading_IsUnavailableOnAnUnboundSurface()
    {
        using var surface = new AppAutomationSurface();

        Assert.Equal(
            PluginNavigationCommandStatus.Unavailable,
            surface.Navigation.FaceHeading(90f));
    }

    /// <summary>
    /// The inert surface every host without a live local player falls back
    /// to — including <c>HeadlessAutomationSurface</c>, which has no
    /// navigation surface of its own — must refuse rather than pretend.
    /// </summary>
    [Fact]
    public void FaceHeading_IsUnavailableOnTheNoOpSurface()
    {
        INavigationAutomation navigation =
            NoOpAutomationSurface.Instance.Navigation;

        Assert.Equal(
            PluginNavigationCommandStatus.Unavailable,
            navigation.FaceHeading(90f));
    }

    [Fact]
    public void FaceHeading_IsImplementedByTheGraphicalSurface()
    {
        System.Reflection.InterfaceMapping map =
            typeof(AppAutomationSurface).GetInterfaceMap(
                typeof(INavigationAutomation));
        int index = Array.FindIndex(
            map.InterfaceMethods,
            static method => method.Name
                == nameof(INavigationAutomation.FaceHeading));

        Assert.True(index >= 0, "INavigationAutomation.FaceHeading not found.");
        Assert.Equal(
            typeof(AppAutomationSurface),
            map.TargetMethods[index].DeclaringType);
    }

    [Fact]
    public void OwnedItemProjectionCarriesTheSingularNameForAStack()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        var stack = new ClientObject
        {
            ObjectId = 0x50000777u,
            Name = "Lead Scarab",
            PluralName = "Lead Scarabs",
            StackSize = 5,
            StackSizeMax = 100,
        };

        PluginInventoryItem item = surface.ProjectInventoryItem(runtime, stack);

        Assert.Equal("Lead Scarabs", stack.GetAppropriateName());
        Assert.Equal("Lead Scarab", item.Name);
        Assert.Equal(5, item.StackSize);
    }

    [Fact]
    public void VendorStockProjectsEachListingAtThePlayersUnitPrice()
    {
        var vendor = new VendorState();
        Assert.True(vendor.Apply(
            VendorId,
            Profile(sellRate: 1.5f),
            [
                Listing(0x70000011u, stock: -1, value: 20) with
                {
                    PluralName = "Prismatic Tapers",
                    IconId = 0x06001234u,
                },
                Listing(0x70000012u, stock: 3, value: 50) with
                {
                    DescStackSize = 5,
                },
            ]));

        IReadOnlyList<PluginVendorItem> stock =
            AppAutomationSurface.ProjectVendorStock(vendor);

        Assert.Equal(2, stock.Count);
        Assert.Equal(0x70000011u, stock[0].ObjectId);
        Assert.Equal("Prismatic Taper", stock[0].Name);
        Assert.Equal("Prismatic Tapers", stock[0].PluralName);
        Assert.Equal(0x06001234u, stock[0].IconId);
        Assert.True(stock[0].IsUnlimited);
        Assert.Equal(
            VendorPricing.SellPrice(20, (uint)ItemType.Misc, 1.5f, 1),
            stock[0].UnitPrice);
        Assert.False(stock[1].IsUnlimited);
        Assert.Equal(3, stock[1].StockCount);
        Assert.Equal(
            VendorPricing.SellPrice(10, (uint)ItemType.Misc, 1.5f, 1),
            stock[1].UnitPrice);
    }

    [Fact]
    public void VendorStockIsEmptyWhenNoVendorIsOpen()
    {
        Assert.Empty(AppAutomationSurface.ProjectVendorStock(new VendorState()));
    }

    [Fact]
    public void BuyRefusesWhatTheOpenVendorCannotSell()
    {
        Assert.Equal(
            PluginItemCommandStatus.InvalidTarget,
            AppAutomationSurface.RefuseBuy(new VendorState(), 0x70000011u, 1u)
                ?.Status);

        var vendor = new VendorState();
        Assert.True(vendor.Apply(
            VendorId,
            Profile(sellRate: 1f),
            [Listing(0x70000011u, stock: 2, value: 10)]));

        Assert.Equal(
            PluginItemCommandStatus.Refused,
            AppAutomationSurface.RefuseBuy(vendor, 0x70000011u, 0u)?.Status);
        Assert.Equal(
            PluginItemCommandStatus.InvalidItem,
            AppAutomationSurface.RefuseBuy(vendor, 0x70000099u, 1u)?.Status);
        Assert.Equal(
            PluginItemCommandStatus.Refused,
            AppAutomationSurface.RefuseBuy(vendor, 0x70000011u, 3u)?.Status);
        Assert.Null(AppAutomationSurface.RefuseBuy(vendor, 0x70000011u, 2u));
    }

    [Fact]
    public void BuyAcceptsAnyQuantityOfAnUnlimitedListing()
    {
        var vendor = new VendorState();
        Assert.True(vendor.Apply(
            VendorId,
            Profile(sellRate: 1f),
            [Listing(0x70000011u, stock: -1, value: 10)]));

        Assert.Null(AppAutomationSurface.RefuseBuy(vendor, 0x70000011u, 500u));
    }

    [Fact]
    public void BuyAndVendorStockAreUnavailableWithoutALiveSession()
    {
        using var surface = new AppAutomationSurface();
        IItemAutomation noOp = NoOpAutomationSurface.Instance.Items;

        Assert.Equal(
            PluginItemCommandStatus.Unavailable,
            surface.Items.Buy(0x70000011u).Status);
        Assert.Empty(surface.Items.CaptureVendorStock());
        Assert.Equal(
            PluginItemCommandStatus.Unavailable,
            noOp.Buy(0x70000011u).Status);
        Assert.Empty(noOp.CaptureVendorStock());
    }

    [Theory]
    [InlineData(nameof(IItemAutomation.Buy))]
    [InlineData(nameof(IItemAutomation.CaptureVendorStock))]
    public void VendorMembersAreImplementedByTheGraphicalSurface(string member)
    {
        System.Reflection.InterfaceMapping map =
            typeof(AppAutomationSurface).GetInterfaceMap(
                typeof(IItemAutomation));
        int index = Array.FindIndex(
            map.InterfaceMethods,
            method => method.Name == member);

        Assert.True(index >= 0, $"IItemAutomation.{member} not found.");
        Assert.Equal(
            typeof(AppAutomationSurface),
            map.TargetMethods[index].DeclaringType);
    }

    private const uint VendorId = 0x70000010u;

    private static VendorShopProfile Profile(float sellRate) => new(
        MerchandiseItemTypes: uint.MaxValue,
        MerchandiseMinValue: 0u,
        MerchandiseMaxValue: uint.MaxValue,
        DealMagicalItems: true,
        BuyPrice: 0.5f,
        SellPrice: sellRate,
        AlternateCurrencyWcid: 0u,
        AlternateCurrencyAmount: 0u,
        AlternateCurrencyPluralName: string.Empty);

    private static VendorShopItem Listing(uint id, int stock, int value) => new(
        ItemGuid: id,
        StackSize: stock,
        WeenieClassId: 691u,
        Name: "Prismatic Taper",
        ItemType: (uint)ItemType.Misc,
        IconId: 0u,
        Value: value);
}
