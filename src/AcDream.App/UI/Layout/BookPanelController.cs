using System;
using System.Collections.Generic;
using System.Globalization;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.UI.Layout;

/// <summary>
/// The book reader. It is one of the main panel's authored slots rather
/// than a layout of its own, and it shows itself when the server answers
/// a Use on a book with the open-book event.
/// </summary>
public sealed class BookPanelController : IRetainedPanelController
{
    public const uint HostLayoutId = 0x2100006Eu;
    public const uint SlotElementId = 0x10000182u;

    public const uint TitleTextId = 0x1000010Fu;
    public const uint PageTextId = 0x10000111u;
    public const uint PreviousButtonId = 0x10000114u;
    public const uint NextButtonId = 0x10000115u;
    public const uint PageMenuId = 0x10000470u;
    public const uint PageNumberTextId = 0x1000047Bu;

    /// <summary>The grey button at the top right of the parchment.</summary>
    public const uint CloseButtonId = 0x1000010Eu;

    /// <summary>The bar beside the page text, authored as its own child
    /// of the text element rather than as a sibling.</summary>
    public const uint PageScrollbarId = 0x1000048Au;

    /// <summary>How many rows of the page list are shown at once before
    /// the rest have to be scrolled to.</summary>
    public const int VisiblePageRows = 7;

    /// <summary>The authored flag that decides whether a text element
    /// takes typing.</summary>
    private const uint EditablePropertyId = 0x16u;

    /// <summary>
    /// What a page with no author of its own is listed as -- every page
    /// of a book that came written, and every slot not written yet.
    /// </summary>
    public const string UnauthoredPageLabel = "<prewritten>";

    /// <summary>
    /// How one entry of the page list reads: the page number, then who
    /// wrote it by name, then the account that wrote it in angle
    /// brackets. A page that came already written carries the account
    /// that says so, which is where the placeholder comes from; a page
    /// with no author at all carries only the brackets.
    ///
    /// The account half is only ever shown to a player the server will
    /// tell the truth about accounts to -- everyone else is handed a
    /// stand-in, and showing that would be worse than showing nothing.
    /// </summary>
    public static string PageLabel(
        int pageIndex, string? authorName, string? authorAccount, bool showsAccount)
    {
        string label = string.Create(
            CultureInfo.InvariantCulture, $"Page {pageIndex + 1}  - ");
        if (!string.IsNullOrWhiteSpace(authorName))
            label += $"by {authorName} ";
        if (showsAccount && !string.IsNullOrWhiteSpace(authorAccount))
            label += $"<{authorAccount}>";
        return label;
    }

    public sealed record Bindings(
        IRuntimeBookView Book,
        RuntimeBookState Commands,
        Func<uint, string> ResolveBookName,
        Action<uint /*bookGuid*/, int /*page*/> RequestPageText,
        Action<uint /*bookGuid*/> RequestAddPage,
        Action<bool> SetVisible,
        Action<uint /*bookGuid*/, int /*page*/, string /*text*/>? SavePage = null,
        Action<uint /*bookGuid*/, int /*page*/>? DeletePage = null,
        Func<bool>? ShowsAuthorAccount = null);

    private readonly Bindings _bindings;
    private readonly UiText? _title;
    private readonly UiText? _pageTextDisplay;
    private readonly UiField? _pageTextField;
    private readonly UiText? _pageNumber;
    private readonly UiMenu? _pageMenu;
    private readonly UiButton? _previous;
    private readonly UiButton? _next;

    private long _renderedRevision = -1;
    private bool _wasOpen;

    public UiElement Root { get; }

    private BookPanelController(UiElement root, Bindings bindings)
    {
        Root = root;
        _bindings = bindings;

        _title = UiElement.FindDescendant(root, TitleTextId) as UiText;
        _pageTextDisplay = UiElement.FindDescendant(root, PageTextId) as UiText;
        _pageTextField = UiElement.FindDescendant(root, PageTextId) as UiField;
        _pageNumber = UiElement.FindDescendant(root, PageNumberTextId) as UiText;
        _pageMenu = UiElement.FindDescendant(root, PageMenuId) as UiMenu;

        _previous = UiElement.FindDescendant(root, PreviousButtonId) as UiButton;
        _next = UiElement.FindDescendant(root, NextButtonId) as UiButton;
        if (_previous is not null)
            _previous.OnClick = () => Turn(CurrentPage - 1);
        if (_next is not null)
            _next.OnClick = () => Turn(CurrentPage + 1);
        if (_pageMenu is not null)
        {
            _pageMenu.OnSelect = payload =>
            {
                if (payload is int page) Turn(page);
            };

            // The drop-down keeps its own face text. The authored text
            // child that carries it is not built as an element of its
            // own, so the menu draws it rather than the child.
            _pageMenu.ButtonLabelProvider = SelectedPageLabel;
            ApplyPopupChrome(_pageMenu);
        }

        // The page text is taller than the parchment shows, so it comes
        // with a bar of its own.
        if (_pageTextField is not null
            && UiElement.FindDescendant(root, PageScrollbarId) is UiScrollbar bar)
        {
            bar.Model = _pageTextField.Scroll;
        }

        if (UiElement.FindDescendant(root, CloseButtonId) is UiButton close)
            close.OnClick = Close;
        else
            Console.WriteLine(
                $"[UI] book panel: close button 0x{CloseButtonId:X8} not found.");

        Refresh();
    }

    /// <summary>
    /// The page text is switched between read-only and typeable while a
    /// book is open, so it has to be built as an element that can take
    /// typing even when the authored default is read-only -- otherwise
    /// there is nothing to switch and the whole edit path is dead.
    /// Call this on the imported description before the tree is built.
    /// </summary>
    public static void MarkPageTextTypeable(ElementInfo root)
    {
        ArgumentNullException.ThrowIfNull(root);
        ElementInfo? pageText = FindInfo(root, PageTextId);
        if (pageText is null) return;

        if (!pageText.States.TryGetValue(UiStateInfo.DirectStateId, out UiStateInfo? direct))
        {
            direct = new UiStateInfo { Id = UiStateInfo.DirectStateId };
            pageText.States[UiStateInfo.DirectStateId] = direct;
        }

        direct.Properties.Values[EditablePropertyId] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Bool,
            MasterPropertyId = EditablePropertyId,
            BoolValue = true,
        };
    }

    private static ElementInfo? FindInfo(ElementInfo info, uint id)
    {
        if (info.Id == id) return info;
        foreach (ElementInfo child in info.Children)
        {
            if (FindInfo(child, id) is { } found) return found;
        }

        return null;
    }

    /// <summary>What the page text actually built as. The connected gate
    /// needs to see this: a read-only element here means no typing.
    /// </summary>
    public string PageTextElementKind =>
        _pageTextField is not null ? "typeable field"
        : _pageTextDisplay is not null ? "read-only text"
        : "missing";

    /// <summary>
    /// Binds against an imported slot. Returns null when the slot did not
    /// carry the page text, which is the one child the reader cannot do
    /// without.
    /// </summary>
    public static BookPanelController? Bind(ImportedLayout layout, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(bindings);

        UiElement root = layout.Root;
        bool hasPageText =
            UiElement.FindDescendant(root, PageTextId) is UiText or UiField;
        return hasPageText ? new BookPanelController(root, bindings) : null;
    }

    /// <summary>For tests and hosts that already hold the built tree.</summary>
    public static BookPanelController? BindTo(UiElement root, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(bindings);
        bool hasPageText =
            UiElement.FindDescendant(root, PageTextId) is UiText or UiField;
        return hasPageText ? new BookPanelController(root, bindings) : null;
    }

    private int CurrentPage => _bindings.Book.Snapshot.CurrentPage;

    public void Tick()
    {
        RuntimeBookSnapshot snapshot = _bindings.Book.Snapshot;
        if (snapshot.Revision == _renderedRevision)
            return;

        Refresh();

        // A book opens itself when the server answers the Use, and takes
        // the panel down with it when the session drops it.
        if (snapshot.IsOpen != _wasOpen)
        {
            _wasOpen = snapshot.IsOpen;
            _bindings.SetVisible(snapshot.IsOpen);
        }
    }

    /// <summary>Hiding the panel closes the book, the way retail does.
    /// </summary>
    public void OnHidden()
    {
        // Closing a book saves the open page first, the same as turning
        // away from it.
        RuntimeBookSnapshot snapshot = _bindings.Book.Snapshot;
        if (TypedText() is { } typed)
        {
            RuntimeBookFlushAction flush =
                _bindings.Commands.FlushCurrentPage(typed, out int page);
            Send(snapshot.BookGuid, flush, page);
        }

        _bindings.Commands.CloseBook();
        _wasOpen = false;
        Refresh();
    }

    public void Dispose()
    {
    }

    public void Refresh()
    {
        RuntimeBookSnapshot snapshot = _bindings.Book.Snapshot;
        _renderedRevision = snapshot.Revision;

        // An inscribed book is titled by its inscription; anything else
        // wears the item's own name.
        SetText(
            _title,
            !snapshot.IsOpen
                ? string.Empty
                : snapshot.ScribeId != 0u
                    ? snapshot.Inscription
                    : _bindings.ResolveBookName(snapshot.BookGuid));

        BookPage? page = _bindings.Book.GetPage(snapshot.CurrentPage);
        string pageText = page?.TextIncluded != 0u
            ? page?.PageText ?? string.Empty
            : string.Empty;
        SetText(_pageTextDisplay, pageText);
        if (_pageTextField is not null)
        {
            _pageTextField.SetText(pageText);

            // A page is the reader's to write in when they wrote it, or
            // when the book lets anyone write; everything else is read
            // only, and so is a page whose text has not arrived yet.
            _pageTextField.Editable =
                snapshot.IsOpen
                && page is not null
                && page.Value.TextIncluded != 0u
                && _bindings.Book.IsPageEditable(snapshot.CurrentPage);
            // The book says how much fits on a page.
            if (snapshot.MaxNumCharsPerPage > 0)
                _pageTextField.MaxCharacters = snapshot.MaxNumCharsPerPage;

            // A page longer than the parchment has to be scrollable the
            // moment it is shown, not a frame later.
            _pageTextField.RefreshScrollExtents();
        }

        SetText(_pageNumber, SelectedPageLabel());

        // The first page has nothing before it and the last slot the
        // book can hold has nothing after it.
        if (_previous is not null)
            _previous.Enabled = snapshot.IsOpen && snapshot.CurrentPage > 0;
        if (_next is not null)
        {
            _next.Enabled = snapshot.IsOpen
                && snapshot.CurrentPage < snapshot.MaxNumPages - 1;
        }

        RefreshMenu(snapshot);
    }

    /// <summary>
    /// The menu lists every page the book can hold, not only the ones
    /// written so far, so a writer can turn to the next blank slot.
    /// </summary>
    private void RefreshMenu(RuntimeBookSnapshot snapshot)
    {
        if (_pageMenu is null) return;

        if (!snapshot.IsOpen || snapshot.MaxNumPages <= 0)
        {
            _pageMenu.Items = Array.Empty<UiMenu.MenuItem>();
            _pageMenu.Selected = null;
            return;
        }

        var items = new UiMenu.MenuItem[snapshot.MaxNumPages];
        for (int i = 0; i < items.Length; i++)
            items[i] = new UiMenu.MenuItem(Label(i), i);

        _pageMenu.Items = items;
        _pageMenu.SizePopupToWidth(_pageMenu.Width);
        _pageMenu.Selected =
            snapshot.CurrentPage >= 0 && snapshot.CurrentPage < items.Length
                ? snapshot.CurrentPage
                : null;
    }

    /// <summary>
    /// The open list is one column the width of the closed row, sat
    /// directly below it, with a bar down its right only once there are
    /// more pages than fit. Rows carry no mark of their own; the row the
    /// reader is on is picked out by the face text, not by a glyph.
    /// </summary>
    private static void ApplyPopupChrome(UiMenu menu)
    {
        menu.Scrollable = true;
        menu.PopupSizeToContent = false;
        menu.PopupScrollbarHideWhenDisabled = true;
        menu.OpenUpward = false;
        menu.RowsPerColumn = VisiblePageRows;
        menu.ItemNormalSprite = menu.NormalSprite;
        menu.ItemHighlightSprite = menu.PressedSprite;
        menu.PopupBgSprite = menu.NormalSprite;
        menu.ItemTextCentered = false;
        menu.ScrollTrackSprite = PopupScrollbarSprites.Track;
        menu.ScrollThumbTopSprite = PopupScrollbarSprites.ThumbTop;
        menu.ScrollThumbSprite = PopupScrollbarSprites.Thumb;
        menu.ScrollThumbBottomSprite = PopupScrollbarSprites.ThumbBottom;
        menu.ScrollUpSprite = PopupScrollbarSprites.Up;
        menu.ScrollDownSprite = PopupScrollbarSprites.Down;
    }

    private static class PopupScrollbarSprites
    {
        public const uint Track = 0x06004C5Fu;
        public const uint ThumbTop = 0x06004C60u;
        public const uint Thumb = 0x06004C63u;
        public const uint ThumbBottom = 0x06004C66u;
        public const uint Up = RetailScrollbarChrome.UpNormal;
        public const uint Down = RetailScrollbarChrome.DownNormal;
    }

    /// <summary>Shuts the book and takes the panel down with it.</summary>
    public void Close()
    {
        OnHidden();
        _bindings.SetVisible(false);
    }

    /// <summary>The entry the reader is on, as the list spells it.</summary>
    private string SelectedPageLabel()
    {
        RuntimeBookSnapshot snapshot = _bindings.Book.Snapshot;
        if (!snapshot.IsOpen || snapshot.CurrentPage < 0)
            return string.Empty;

        return Label(snapshot.CurrentPage);
    }

    private string Label(int pageIndex)
    {
        BookPage? page = _bindings.Book.GetPage(pageIndex);
        return PageLabel(
            pageIndex,
            page?.AuthorName,
            page?.AuthorAccount,
            _bindings.ShowsAuthorAccount?.Invoke() ?? false);
    }

    private void Turn(int page)
    {
        RuntimeBookSnapshot before = _bindings.Book.Snapshot;
        RuntimeBookPageTurn turn = _bindings.Commands.TurnPage(page, TypedText());

        Send(before.BookGuid, turn.Flush, turn.FlushPage);

        switch (turn.Action)
        {
            case RuntimeBookPageAction.RequestPageText:
                _bindings.RequestPageText(before.BookGuid, turn.Page);
                break;
            case RuntimeBookPageAction.AddPage:
                _bindings.RequestAddPage(before.BookGuid);
                break;
            case RuntimeBookPageAction.RefusedLastPageBlank:
            case RuntimeBookPageAction.Display:
            case RuntimeBookPageAction.None:
            default:
                break;
        }

        Refresh();
    }

    /// <summary>What the reader typed, or null when this book cannot be
    /// written in at all and so has nothing to save.</summary>
    private string? TypedText() =>
        _pageTextField is { Editable: true } field ? field.Text : null;

    private void Send(uint bookGuid, RuntimeBookFlushAction flush, int page)
    {
        switch (flush)
        {
            case RuntimeBookFlushAction.ModifyPage:
                _bindings.SavePage?.Invoke(
                    bookGuid, page, _pageTextField?.Text ?? string.Empty);
                break;
            case RuntimeBookFlushAction.DeletePage:
                _bindings.DeletePage?.Invoke(bookGuid, page);
                break;
            case RuntimeBookFlushAction.None:
            default:
                break;
        }
    }

    private static void SetText(UiText? text, string value)
    {
        if (text is null) return;
        text.LinesProvider = () => [new UiText.Line(value, text.DefaultColor)];
    }
}
