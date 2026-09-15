using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// The authored book slot, checked against the installed files. These
/// pin the two things a hand-built tree cannot see: that the page list
/// resolves to the drop-down widget with the art the layout gives it,
/// and that the page text resolves to a field that takes typing and
/// wraps inside its own rectangle.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class BookPanelLiveDatTests
{
    private static string DatDirectory =>
        Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");

    private static (ElementInfo Info, ImportedLayout Layout) ImportSlot()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        ElementInfo? slot = LayoutImporter.ImportInfos(
            dats,
            BookPanelController.HostLayoutId,
            BookPanelController.SlotElementId);
        Assert.NotNull(slot);
        return (slot!, LayoutImporter.Build(slot!, _ => (1u, 16, 16), null, null));
    }

    [Fact]
    public void PageListResolvesToTheDropDownWidget()
    {
        (ElementInfo slot, ImportedLayout layout) = ImportSlot();

        UiElement? element = layout.FindElement(BookPanelController.PageMenuId);
        UiMenu menu = Assert.IsType<UiMenu>(element);

        // A closed drop-down at rest, sat in the narrow row the layout
        // gives it rather than covering the page.
        Assert.False(menu.IsOpen);
        Assert.True(menu.RetailButtonArt);
        Assert.True(menu.Height <= 24f, $"page list is {menu.Height} tall");

        // It wears its own face and its own open/closed arrow.
        Assert.NotEqual(0u, menu.NormalSprite);
        Assert.NotEqual(0u, menu.ArrowCapClosedSprite);
        Assert.NotEqual(0u, menu.ArrowCapOpenSprite);

        ElementInfo menuInfo = Assert.Single(
            Flatten(slot), i => i.Id == BookPanelController.PageMenuId);
        Assert.Equal(6u, menuInfo.Type);
    }

    [Fact]
    public void PageTextResolvesToAWrappingTypeableField()
    {
        (ElementInfo slot, ImportedLayout layout) = ImportSlot();

        UiElement? element = layout.FindElement(BookPanelController.PageTextId);
        UiField field = Assert.IsType<UiField>(element);
        Assert.True(field.Editable);

        // The layout already marks it typeable; forcing the flag on must
        // not change that.
        ElementInfo pageText = Assert.Single(
            Flatten(slot), i => i.Id == BookPanelController.PageTextId);
        Assert.True(pageText.TryGetEffectiveBool(0x16u, out bool authoredEditable));
        Assert.True(authoredEditable);

        // Many lines of prose have to land inside the rectangle, not run
        // off the end of the first one.
        string paragraph = string.Join(
            ' ', Enumerable.Repeat("the letter runs on for a good while", 12));
        field.SetText(paragraph + "\n\n" + paragraph);

        Assert.Equal(paragraph.Length * 2 + 2, field.Text.Length);
        Assert.True(field.Width > 200f, $"page text is {field.Width} wide");
        Assert.True(field.Height > 120f, $"page text is {field.Height} tall");
    }

    [Fact]
    public void EveryChildTheReaderBindsToIsPresent()
    {
        (ElementInfo slot, _) = ImportSlot();
        var ids = Flatten(slot).Select(i => i.Id).ToHashSet();

        foreach (uint id in new[]
        {
            BookPanelController.TitleTextId,
            BookPanelController.PageTextId,
            BookPanelController.PreviousButtonId,
            BookPanelController.NextButtonId,
            BookPanelController.PageMenuId,
            BookPanelController.PageNumberTextId,
        })
        {
            Assert.True(ids.Contains(id), $"0x{id:X8} is not in the authored slot");
        }
    }

    /// <summary>
    /// Binds the real slot the way the panel does, so the popup and the
    /// page bar are measured on the authored rectangles rather than on
    /// numbers a test made up.
    /// </summary>
    private static (BookPanelController Controller, RuntimeBookState State,
        ImportedLayout Layout) BindReal(int pages, int maxPages)
    {
        (_, ImportedLayout layout) = ImportSlot();
        var state = new RuntimeBookState(() => 0x5000000Au);

        BookPanelController? controller = BookPanelController.BindTo(
            layout.Root,
            new BookPanelController.Bindings(
                Book: state.View,
                Commands: state,
                ResolveBookName: _ => "A Guide",
                RequestPageText: (_, _) => { },
                RequestAddPage: _ => { },
                SetVisible: _ => { }));
        Assert.NotNull(controller);

        var written = new BookPage[pages];
        for (int i = 0; i < pages; i++)
        {
            written[i] = new BookPage(
                0x5000000Bu, "F.P.", "prewritten", 1u, 0u, $"page {i + 1}");
        }

        state.ApplyOpenBook(new BookEvents.OpenBook(
            0x80001234u,
            maxPages,
            new BookPageList(maxPages, 1000, written),
            "A Portal-Jumper's Guide",
            0x5000000Bu,
            "F.P."));
        controller!.Tick();
        return (controller, state, layout);
    }

    [Fact]
    public void TheOpenPageListIsOneColumnUnderTheClosedRow()
    {
        (_, _, ImportedLayout layout) = BindReal(pages: 8, maxPages: 8);
        var menu = (UiMenu)layout.FindElement(BookPanelController.PageMenuId)!;

        Assert.Equal(8, menu.Items.Count);
        Assert.True(menu.Scrollable);
        Assert.False(menu.OpenUpward, "the list opens below the closed row");

        // Exactly as wide as the row it drops out of, scrollbar included.
        Assert.Equal(menu.Width, menu.PopupOuterWidth, 1);

        // Eight pages do not fit the visible rows, so it scrolls rather
        // than growing a second column.
        Assert.True(
            menu.Items.Count > BookPanelController.VisiblePageRows,
            "this book is supposed to overflow the visible rows");
        Assert.True(
            menu.PopupOuterHeight
                <= (BookPanelController.VisiblePageRows * menu.RowHeight) + 16f,
            $"open list is {menu.PopupOuterHeight} tall");

        // No row wears a mark of its own.
        Assert.Equal(menu.NormalSprite, menu.ItemNormalSprite);
    }

    [Fact]
    public void AShortPageListNeitherScrollsNorChangesWidth()
    {
        (_, _, ImportedLayout layout) = BindReal(pages: 3, maxPages: 3);
        var menu = (UiMenu)layout.FindElement(BookPanelController.PageMenuId)!;

        Assert.Equal(3, menu.Items.Count);
        Assert.Equal(menu.Width, menu.PopupOuterWidth, 1);
    }

    [Fact]
    public void ALongPageScrollsInsideTheParchment()
    {
        (_, _, ImportedLayout layout) = BindReal(pages: 1, maxPages: 8);
        var field = (UiField)layout.FindElement(BookPanelController.PageTextId)!;
        var bar = (UiScrollbar)UiElement.FindDescendant(
            layout.Root, BookPanelController.PageScrollbarId)!;

        Assert.Same(field.Scroll, bar.Model);

        string forty = string.Join(
            "\n", Enumerable.Range(1, 40).Select(n => $"line {n} of the page"));
        field.SetText(forty);
        field.RefreshScrollExtents();

        Assert.True(
            field.Scroll.ContentHeight > field.Height,
            $"40 lines measured {field.Scroll.ContentHeight} in {field.Height}");
        Assert.True(
            field.Scroll.MaxScroll > 0,
            "a page longer than the parchment has nowhere to scroll to");
    }

    [Fact]
    public void TheCloseButtonIsBound()
    {
        (_, RuntimeBookState state, ImportedLayout layout) =
            BindReal(pages: 2, maxPages: 8);
        var close = (UiButton)layout.FindElement(BookPanelController.CloseButtonId)!;

        Assert.NotNull(close.OnClick);
        Assert.True(state.Snapshot.IsOpen);

        close.OnClick!();

        Assert.False(state.Snapshot.IsOpen);
    }

    private static IEnumerable<ElementInfo> Flatten(ElementInfo e)
    {
        yield return e;
        foreach (ElementInfo child in e.Children)
        {
            foreach (ElementInfo nested in Flatten(child))
                yield return nested;
        }
    }
}
