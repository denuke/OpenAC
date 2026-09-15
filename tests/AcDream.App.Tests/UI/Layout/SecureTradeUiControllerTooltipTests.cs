using System;
using System.Collections.Generic;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

// #87: the hover caption is the same name flavour the selection caption
// shows - the composed name, material prefix included.
public sealed class SecureTradeUiControllerTooltipTests
{
    private const uint Partner = 0x50000002u;
    private const uint MaterialItem = 0x60000001u;
    private const uint PlainItem = 0x60000002u;

    private sealed class TestElement : UiElement { }

    private sealed class StagedTrade : IRuntimeTradeView
    {
        public readonly List<uint> Self = [];

        public RuntimeTradeSnapshot Snapshot => new(
            Revision: 1L,
            IsOpen: true,
            PartnerGuid: Partner,
            SelfAccepted: false,
            PartnerAccepted: false,
            SelfItemCount: Self.Count,
            PartnerItemCount: 0,
            LastFailureItemGuid: 0u,
            LastFailureReason: 0u);

        public IReadOnlyList<uint> GetItems(RuntimeTradeSide side)
            => side == RuntimeTradeSide.Self ? Self : [];
    }

    [Fact]
    public void HoverCaption_carriesTheMaterialPrefix_andStaysPlainWithoutOne()
    {
        var selfList = new UiItemList { Width = 320f, Height = 32f };
        var partnerList = new UiItemList { Width = 320f, Height = 32f };
        var root = new TestElement { Width = 320f, Height = 200f };
        root.AddChild(selfList);
        root.AddChild(partnerList);
        var layout = new ImportedLayout(root, new Dictionary<uint, UiElement>
        {
            [SecureTradeUiController.SelfListId] = selfList,
            [SecureTradeUiController.PartnerListId] = partnerList,
        });

        var objects = new ClientObjectTable();
        objects.AddOrUpdate(ItemTooltipCaptionNames.Material(MaterialItem));
        objects.AddOrUpdate(ItemTooltipCaptionNames.Plain(PlainItem));

        var trade = new StagedTrade();
        trade.Self.Add(MaterialItem);
        trade.Self.Add(PlainItem);

        using SecureTradeUiController controller = SecureTradeUiController.Bind(
            layout,
            new SecureTradeUiController.Bindings(
                Trade: trade,
                Objects: objects,
                ResolveIcon: static (_, _, _, _, _) => 0u,
                OpenTrade: static _ => { },
                CloseTrade: static () => { },
                AddToTrade: static _ => { },
                AcceptTrade: static (_, _, _) => { },
                DeclineTrade: static () => { },
                ResetTrade: static () => { },
                SetWindowVisible: static _ => { },
                ResolveAppropriateName: ItemTooltipCaptionNames.Resolve))!;
        controller.Tick();

        Assert.Equal("Pyreal Scarab", selfList.GetItem(0)!.GetTooltipText());
        Assert.Equal("Bread Loaf", selfList.GetItem(1)!.GetTooltipText());
    }
}
