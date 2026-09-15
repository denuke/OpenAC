using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// These bind the controller against a tree built from the same element
/// ids the authored slot carries, and then drive it the way a reader
/// would: the point is that the wiring is live, not that the layout and
/// the state each work on their own.
/// </summary>
public sealed class BookPanelControllerTests
{
    private const uint Player = 0x5000000Au;
    private const uint Other = 0x5000000Bu;
    private const uint BookGuid = 0x80001234u;

    private sealed class Sent
    {
        public List<(uint Book, int Page)> PageTextRequests { get; } = [];
        public List<uint> AddPageRequests { get; } = [];
        public List<bool> Visibility { get; } = [];
        public List<(uint Book, int Page, string Text)> Saved { get; } = [];
        public List<(uint Book, int Page)> Deleted { get; } = [];
    }

    private static UiText Text(uint id) =>
        new() { DatElementId = id, Width = 160f, Height = 18f };

    private static UiField Field(uint id) =>
        new() { ElementId = id, DatElementId = id, Width = 200f, Height = 120f };

    private static UiButton Button(uint id) => new(
        new ElementInfo { Id = id, Type = 1, Width = 40, Height = 20 },
        static _ => (0u, 0, 0))
    {
        DatElementId = id,
    };

    private static UiElement BuildSlot()
    {
        var root = new UiPanel { Width = 400f, Height = 300f };
        root.AddChild(Text(BookPanelController.TitleTextId));
        root.AddChild(Field(BookPanelController.PageTextId));
        root.AddChild(Button(BookPanelController.PreviousButtonId));
        root.AddChild(Button(BookPanelController.NextButtonId));

        var menu = new UiMenu
        {
            DatElementId = BookPanelController.PageMenuId,
            Width = 292f,
            Height = 18f,
        };
        menu.AddChild(Text(BookPanelController.PageNumberTextId));
        root.AddChild(menu);
        root.AddChild(Button(BookPanelController.CloseButtonId));
        root.AddChild(new UiScrollbar
        {
            DatElementId = BookPanelController.PageScrollbarId,
            Width = 16f,
            Height = 240f,
        });
        return root;
    }

    private static BookPage Page(
        uint author, string text, uint textIncluded = 1u, uint ignoreAuthor = 0u,
        string authorName = "Someone", string authorAccount = "acct") =>
        new(author, authorName, authorAccount, textIncluded, ignoreAuthor, text);

    private static BookEvents.OpenBook Open(
        int maxPages, uint scribeId, params BookPage[] pages) =>
        new(
            BookGuid,
            maxPages,
            new BookPageList(maxPages, 500, pages),
            "The Grand Inscription",
            scribeId,
            "Scribbler");

    private static (BookPanelController Controller, RuntimeBookState State,
        UiElement Root, Sent Sent) Bind(bool showsAccount = false)
    {
        var state = new RuntimeBookState(() => Player);
        var sent = new Sent();
        UiElement root = BuildSlot();

        BookPanelController? controller = BookPanelController.BindTo(
            root,
            new BookPanelController.Bindings(
                Book: state.View,
                Commands: state,
                ResolveBookName: _ => "Parchment",
                RequestPageText: (book, page) =>
                    sent.PageTextRequests.Add((book, page)),
                RequestAddPage: book => sent.AddPageRequests.Add(book),
                SetVisible: sent.Visibility.Add,
                SavePage: (book, page, text) => sent.Saved.Add((book, page, text)),
                DeletePage: (book, page) => sent.Deleted.Add((book, page)),
                ShowsAuthorAccount: () => showsAccount));

        Assert.NotNull(controller);
        return (controller!, state, root, sent);
    }

    private static string TextOf(UiElement root, uint id)
    {
        var text = (UiText?)UiElement.FindDescendant(root, id);
        Assert.NotNull(text);
        IReadOnlyList<UiText.Line>? lines = text!.LinesProvider?.Invoke();
        return lines is null || lines.Count == 0
            ? string.Empty
            : string.Join("\n", lines.Select(l => l.Text));
    }

    private static UiField PageField(UiElement root) =>
        (UiField)UiElement.FindDescendant(root, BookPanelController.PageTextId)!;

    private static UiMenu Menu(UiElement root) =>
        (UiMenu)UiElement.FindDescendant(root, BookPanelController.PageMenuId)!;

    private static void Click(UiElement root, uint id) =>
        ((UiButton)UiElement.FindDescendant(root, id)!).OnClick!();

    [Fact]
    public void Bind_ReturnsNullWhenTheSlotCarriesNoPageText()
    {
        var root = new UiPanel();
        root.AddChild(Text(BookPanelController.TitleTextId));

        BookPanelController? controller = BookPanelController.BindTo(
            root,
            new BookPanelController.Bindings(
                Book: new RuntimeBookState().View,
                Commands: new RuntimeBookState(),
                ResolveBookName: _ => string.Empty,
                RequestPageText: (_, _) => { },
                RequestAddPage: _ => { },
                SetVisible: _ => { }));

        Assert.Null(controller);
    }

    [Fact]
    public void OpeningABook_ShowsThePanelWithTheFirstPage()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();

        state.ApplyOpenBook(Open(4, Other, Page(Other, "In the beginning")));
        controller.Tick();

        Assert.Equal([true], sent.Visibility);
        Assert.Equal("In the beginning", PageField(root).Text);
        Assert.Equal(
            "Page 1  - by Someone ",
            TextOf(root, BookPanelController.PageNumberTextId));
    }

    [Fact]
    public void AnInscribedBookIsTitledByItsInscription()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();

        state.ApplyOpenBook(Open(4, Other, Page(Other, "text")));
        controller.Tick();

        Assert.Equal(
            "The Grand Inscription", TextOf(root, BookPanelController.TitleTextId));
    }

    [Fact]
    public void AnUninscribedBookWearsTheItemName()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();

        state.ApplyOpenBook(Open(4, scribeId: 0u, Page(Other, "text")));
        controller.Tick();

        Assert.Equal("Parchment", TextOf(root, BookPanelController.TitleTextId));
    }

    [Fact]
    public void TheNextButtonTurnsToAPageAlreadyInHand()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(
            Open(4, Other, Page(Other, "one"), Page(Other, "two")));
        controller.Tick();

        Click(root, BookPanelController.NextButtonId);

        Assert.Equal(1, state.Snapshot.CurrentPage);
        Assert.Equal("two", PageField(root).Text);
        Assert.Equal(
            "Page 2  - by Someone ",
            TextOf(root, BookPanelController.PageNumberTextId));
        Assert.Empty(sent.PageTextRequests);
        Assert.Empty(sent.AddPageRequests);
    }

    [Fact]
    public void TheNextButtonAsksForAPageThatArrivedWithoutItsText()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(
            4,
            Other,
            Page(Other, "one"),
            Page(Other, string.Empty, textIncluded: 0u)));
        controller.Tick();

        Click(root, BookPanelController.NextButtonId);

        Assert.Equal([(BookGuid, 1)], sent.PageTextRequests);
        Assert.Equal(string.Empty, PageField(root).Text);

        state.ApplyPageData(
            new BookEvents.PageDataResponse(BookGuid, 1, Page(Other, "fetched")));
        controller.Tick();

        Assert.Equal("fetched", PageField(root).Text);
    }

    [Fact]
    public void TheNextButtonAsksForANewPageOffTheEndOfAWritableBook()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Player, "one")));
        controller.Tick();

        Click(root, BookPanelController.NextButtonId);

        Assert.Equal([BookGuid], sent.AddPageRequests);
    }

    [Fact]
    public void TheNextButtonStopsAtTheEndOfABookThatCannotGrow()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(1, Other, Page(Other, "only page")));
        controller.Tick();

        Click(root, BookPanelController.NextButtonId);

        Assert.Empty(sent.AddPageRequests);
        Assert.Equal(0, state.Snapshot.CurrentPage);
    }

    [Fact]
    public void ThePreviousButtonTurnsBackAndStopsAtTheFirstPage()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();
        state.ApplyOpenBook(
            Open(4, Other, Page(Other, "one"), Page(Other, "two")));
        controller.Tick();
        Click(root, BookPanelController.NextButtonId);

        Click(root, BookPanelController.PreviousButtonId);
        Assert.Equal(0, state.Snapshot.CurrentPage);

        Click(root, BookPanelController.PreviousButtonId);
        Assert.Equal(0, state.Snapshot.CurrentPage);
    }

    [Fact]
    public void ThePageMenuListsEverySlotTheBookCanHoldAndTracksTheOpenPage()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Other, "one"), Page(Other, "two")));
        controller.Tick();

        UiMenu menu = Menu(root);
        Assert.Equal(4, menu.Items.Count);
        Assert.Equal(
            [
                "Page 1  - by Someone ",
                "Page 2  - by Someone ",
                "Page 3  - ",
                "Page 4  - ",
            ],
            menu.Items.Select(i => i.Label));
        Assert.Equal(0, menu.Selected);

        Click(root, BookPanelController.NextButtonId);
        Assert.Equal(1, Menu(root).Selected);
    }

    [Fact]
    public void SelectingFromThePageMenuTurnsToThatPage()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();
        state.ApplyOpenBook(
            Open(4, Other, Page(Other, "one"), Page(Other, "two")));
        controller.Tick();

        Menu(root).OnSelect!(1);

        Assert.Equal(1, state.Snapshot.CurrentPage);
        Assert.Equal("two", PageField(root).Text);
    }

    [Fact]
    public void HidingThePanelClosesTheBook()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Other, "one")));
        controller.Tick();

        controller.OnHidden();

        Assert.False(state.Snapshot.IsOpen);
        Assert.Equal(string.Empty, PageField(root).Text);
        Assert.Equal(string.Empty, TextOf(root, BookPanelController.TitleTextId));
        Assert.Empty(Menu(root).Items);
    }

    [Fact]
    public void ClosingTheBookElsewhereTakesThePanelDown()
    {
        (BookPanelController controller, RuntimeBookState state,
            _, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Other, "one")));
        controller.Tick();

        state.ResetSession();
        controller.Tick();

        Assert.Equal([true, false], sent.Visibility);
    }

    [Fact]
    public void TickDoesNothingWhileTheBookHasNotChanged()
    {
        (BookPanelController controller, RuntimeBookState state,
            _, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Other, "one")));
        controller.Tick();
        controller.Tick();
        controller.Tick();

        Assert.Equal([true], sent.Visibility);
    }

    [Fact]
    public void ReOpeningTheSameBookDoesNotReRaiseVisibility()
    {
        (BookPanelController controller, RuntimeBookState state,
            _, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Other, "one")));
        controller.Tick();

        state.ApplyOpenBook(Open(4, Other, Page(Other, "one"), Page(Other, "two")));
        controller.Tick();

        Assert.Equal([true], sent.Visibility);
    }
    [Fact]
    public void APageTheReaderWroteIsTypeable()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();

        state.ApplyOpenBook(Open(4, Other, Page(Player, "mine")));
        controller.Tick();

        Assert.True(PageField(root).Editable);
        Assert.Equal(500, PageField(root).MaxCharacters);
    }

    [Fact]
    public void APageSomeoneElseWroteIsReadOnly()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();

        state.ApplyOpenBook(Open(4, Other, Page(Other, "theirs")));
        controller.Tick();

        Assert.False(PageField(root).Editable);
    }

    [Fact]
    public void ACommunalPageIsTypeableWhoeverWroteIt()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();

        state.ApplyOpenBook(Open(4, Other, Page(Other, "shared", ignoreAuthor: 1u)));
        controller.Tick();

        Assert.True(PageField(root).Editable);
    }

    [Fact]
    public void APageWhoseTextHasNotArrivedIsNotTypeable()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();

        state.ApplyOpenBook(
            Open(4, Other, Page(Player, string.Empty, textIncluded: 0u)));
        controller.Tick();

        Assert.False(PageField(root).Editable);
    }

    [Fact]
    public void TurningAwayFromAnEditedPageSavesIt()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(
            Open(4, Other, Page(Player, "old"), Page(Player, "two")));
        controller.Tick();
        PageField(root).SetText("rewritten");

        Click(root, BookPanelController.NextButtonId);

        Assert.Equal([(BookGuid, 0, "rewritten")], sent.Saved);
        Assert.Equal("rewritten", state.View.GetPage(0)!.Value.PageText);
        Assert.Equal("two", PageField(root).Text);
    }

    [Fact]
    public void TurningAwayFromSomeoneElsesPageSavesNothing()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(
            Open(4, Other, Page(Other, "theirs"), Page(Other, "two")));
        controller.Tick();

        Click(root, BookPanelController.NextButtonId);

        Assert.Empty(sent.Saved);
        Assert.Empty(sent.Deleted);
    }

    [Fact]
    public void BlankingYourOwnPageAndTurningForwardDeletesItAndFollowsTheShift()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(
            4, Other, Page(Player, "one"), Page(Player, "two"), Page(Player, "three")));
        controller.Tick();
        Click(root, BookPanelController.NextButtonId);
        PageField(root).SetText("   ");

        Click(root, BookPanelController.NextButtonId);

        Assert.Equal([(BookGuid, 1)], sent.Deleted);
        // Turning off page one saved it on the way past, unchanged --
        // every turn away from a writable page saves it.
        Assert.Equal([(BookGuid, 0, "one")], sent.Saved);
        Assert.Equal("three", PageField(root).Text);
        Assert.Equal(2, state.Snapshot.PageCount);
    }

    [Fact]
    public void ClosingThePanelSavesTheOpenPageFirst()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Player, "old")));
        controller.Tick();
        PageField(root).SetText("final words");

        controller.OnHidden();

        Assert.Equal([(BookGuid, 0, "final words")], sent.Saved);
        Assert.False(state.Snapshot.IsOpen);
    }

    [Fact]
    public void ClosingThePanelOnAReadOnlyBookSavesNothing()
    {
        (BookPanelController controller, RuntimeBookState state,
            _, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Other, "lore")));
        controller.Tick();

        controller.OnHidden();

        Assert.Empty(sent.Saved);
        Assert.Empty(sent.Deleted);
    }

    [Fact]
    public void AddingAPageAndGettingTheAnswerLeavesABlankTypeablePage()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Player, "one")));
        controller.Tick();

        Click(root, BookPanelController.NextButtonId);
        Assert.Equal([BookGuid], sent.AddPageRequests);

        state.ApplyAddPageResponse(
            new BookEvents.PageResponse(BookGuid, 1, true), "Acdream");
        controller.Tick();

        Assert.Equal(string.Empty, PageField(root).Text);
        Assert.True(PageField(root).Editable);
        Assert.Equal(2, state.Snapshot.PageCount);
    }

    [Fact]
    public void NoTurnHappensWhileARequestIsStillInFlight()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Player, "one")));
        controller.Tick();

        Click(root, BookPanelController.NextButtonId);
        Click(root, BookPanelController.NextButtonId);

        Assert.Single(sent.AddPageRequests);
    
    }

    [Fact]
    public void MarkPageTextTypeable_TurnsAnAuthoredReadOnlyPageIntoATypeableOne()
    {
        // Retail authors the page text read-only and flips it at run
        // time. Our importer decides field-or-text once, at build, so
        // the flag has to be on before the tree is built or there is
        // nothing to flip and the whole edit path is dead.
        var root = new ElementInfo { Id = BookPanelController.SlotElementId };
        var pageText = new ElementInfo { Id = BookPanelController.PageTextId };
        root.Children.Add(pageText);

        Assert.False(pageText.TryGetEffectiveBool(0x16u, out _));

        BookPanelController.MarkPageTextTypeable(root);

        Assert.True(pageText.TryGetEffectiveBool(0x16u, out bool editable));
        Assert.True(editable);
    }

    [Fact]
    public void MarkPageTextTypeable_KeepsAnAlreadyTypeablePageTypeable()
    {
        var root = new ElementInfo { Id = BookPanelController.SlotElementId };
        var pageText = new ElementInfo { Id = BookPanelController.PageTextId };
        root.Children.Add(pageText);
        BookPanelController.MarkPageTextTypeable(root);

        BookPanelController.MarkPageTextTypeable(root);

        Assert.True(pageText.TryGetEffectiveBool(0x16u, out bool editable));
        Assert.True(editable);
    }

    [Fact]
    public void MarkPageTextTypeable_IgnoresASlotWithoutAPageText()
    {
        var root = new ElementInfo { Id = BookPanelController.SlotElementId };

        BookPanelController.MarkPageTextTypeable(root);

        Assert.Empty(root.Children);
    }

    [Fact]
    public void PageTextElementKind_ReportsWhatTheSlotActuallyBuilt()
    {
        (BookPanelController typeable, _, _, _) = Bind();
        Assert.Equal("typeable field", typeable.PageTextElementKind);

        // The same slot with a read-only text in place of the field.
        var root = new UiPanel { Width = 400f, Height = 300f };
        root.AddChild(Text(BookPanelController.PageTextId));
        BookPanelController? readOnly = BookPanelController.BindTo(
            root,
            new BookPanelController.Bindings(
                Book: new RuntimeBookState().View,
                Commands: new RuntimeBookState(),
                ResolveBookName: _ => string.Empty,
                RequestPageText: (_, _) => { },
                RequestAddPage: _ => { },
                SetVisible: _ => { }));

        Assert.NotNull(readOnly);
        Assert.Equal("read-only text", readOnly!.PageTextElementKind);
    }

    [Fact]
    public void ThePageButtonsAreOffAtTheEndsOfTheBook()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();
        state.ApplyOpenBook(Open(2, Other, Page(Other, "one"), Page(Other, "two")));
        controller.Tick();

        UiButton previous =
            (UiButton)UiElement.FindDescendant(root, BookPanelController.PreviousButtonId)!;
        UiButton next =
            (UiButton)UiElement.FindDescendant(root, BookPanelController.NextButtonId)!;

        Assert.False(previous.Enabled);
        Assert.True(next.Enabled);

        Click(root, BookPanelController.NextButtonId);

        Assert.True(previous.Enabled);
        Assert.False(next.Enabled);
    }

    [Fact]
    public void ThePageButtonsAreOffWithNoBookOpen()
    {
        (BookPanelController controller, _, UiElement root, _) = Bind();
        controller.Refresh();

        Assert.False(
            ((UiButton)UiElement.FindDescendant(
                root, BookPanelController.PreviousButtonId)!).Enabled);
        Assert.False(
            ((UiButton)UiElement.FindDescendant(
                root, BookPanelController.NextButtonId)!).Enabled);
    }

    [Theory]
    // Both halves, as the live client shows a book off a shelf.
    [InlineData(7, "F.P.", "prewritten", true, "Page 8  - by F.P. <prewritten>")]
    [InlineData(0, "", "prewritten", true, "Page 1  - <prewritten>")]
    [InlineData(0, "   ", "prewritten", true, "Page 1  - <prewritten>")]
    // A page written in play carries its writer but no stand-in tail.
    [InlineData(2, "Alinta", "", true, "Page 3  - by Alinta ")]
    [InlineData(2, "Alinta", null, true, "Page 3  - by Alinta ")]
    // A player the server will not tell the truth about accounts to
    // never sees the account half at all.
    [InlineData(7, "F.P.", "prewritten", false, "Page 8  - by F.P. ")]
    [InlineData(0, "", "prewritten", false, "Page 1  - ")]
    public void PageLabel_ReadsAsThePageNumberThenWhoWroteIt(
        int index, string? author, string? account, bool showsAccount, string expected) =>
        Assert.Equal(
            expected, BookPanelController.PageLabel(index, author, account, showsAccount));

    [Fact]
    public void APreWrittenBookListsTheAccountThatWroteIt()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind(showsAccount: true);
        state.ApplyOpenBook(Open(
            8,
            Other,
            Page(Other, "one", authorName: "F.P.", authorAccount: "prewritten")));
        controller.Tick();

        Assert.Equal(
            "Page 1  - by F.P. <prewritten>",
            Menu(root).Items[0].Label);
        Assert.Equal(
            "Page 1  - by F.P. <prewritten>",
            TextOf(root, BookPanelController.PageNumberTextId));
    }

    [Fact]
    public void TheCloseButtonShutsTheBookAndTakesThePanelDown()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Player, "one")));
        controller.Tick();
        PageField(root).SetText("last words");

        Click(root, BookPanelController.CloseButtonId);

        Assert.Equal([(BookGuid, 0, "last words")], sent.Saved);
        Assert.False(state.Snapshot.IsOpen);
        Assert.Equal([true, false], sent.Visibility);
    }

    [Fact]
    public void ThePageTextDrivesTheBarBesideIt()
    {
        (_, _, UiElement root, _) = Bind();

        var bar = (UiScrollbar)UiElement.FindDescendant(
            root, BookPanelController.PageScrollbarId)!;

        Assert.NotNull(bar.Model);
        Assert.Same(PageField(root).Scroll, bar.Model);
    }

    [Fact]
    public void TheOpenListIsOneColumnTheWidthOfTheClosedRow()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Other, "one")));
        controller.Tick();

        UiMenu menu = Menu(root);
        Assert.True(menu.Scrollable);
        Assert.False(menu.OpenUpward);
        Assert.Equal(menu.Width, menu.PopupOuterWidth, 1);

        // No row wears a mark of its own.
        Assert.Equal(menu.NormalSprite, menu.ItemNormalSprite);
    }

    [Fact]
    public void TheOpenListStaysOneColumnWideOnceItHasToScroll()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();
        state.ApplyOpenBook(Open(40, Other, Page(Other, "one")));
        controller.Tick();

        UiMenu menu = Menu(root);
        Assert.Equal(40, menu.Items.Count);
        Assert.Equal(menu.Width, menu.PopupOuterWidth, 1);
        Assert.True(
            menu.PopupOuterHeight < 200f,
            $"open list is {menu.PopupOuterHeight} tall for 40 pages");
    }

    [Fact]
    public void TheDropDownFaceShowsTheEntryTheReaderIsOn()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Other, "one"), Page(Other, "two")));
        controller.Tick();

        UiMenu menu = Menu(root);
        Assert.NotNull(menu.ButtonLabelProvider);
        Assert.Equal("Page 1  - by Someone ", menu.ButtonLabelProvider!());

        Click(root, BookPanelController.NextButtonId);
        Assert.Equal("Page 2  - by Someone ", menu.ButtonLabelProvider!());
    }

    [Fact]
    public void TheDropDownFaceIsEmptyWithNoBookOpen()
    {
        (BookPanelController controller, _, UiElement root, _) = Bind();
        controller.Refresh();

        Assert.Equal(string.Empty, Menu(root).ButtonLabelProvider!());
        Assert.Equal(
            string.Empty, TextOf(root, BookPanelController.PageNumberTextId));
    }

    [Fact]
    public void PressingNextOffABlankPageYouWroteAtTheEndDoesNothing()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Player, "one"), Page(Player, "two")));
        controller.Tick();
        Click(root, BookPanelController.NextButtonId);
        sent.Saved.Clear();
        PageField(root).SetText("  ");

        Click(root, BookPanelController.NextButtonId);

        Assert.Empty(sent.Saved);
        Assert.Empty(sent.Deleted);
        Assert.Empty(sent.AddPageRequests);
        Assert.Equal(1, state.Snapshot.CurrentPage);
        Assert.Equal(2, state.Snapshot.PageCount);
    }
}
