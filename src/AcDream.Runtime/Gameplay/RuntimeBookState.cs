using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Gameplay;

/// <summary>What turning to a page asks the caller to do next.</summary>
public enum RuntimeBookPageAction
{
    /// <summary>The turn was refused: no book, a request is still in
    /// flight, the page is out of range, or it is already the open page.
    /// </summary>
    None = 0,

    /// <summary>The page is in hand with its text; just show it.</summary>
    Display = 1,

    /// <summary>The page exists but arrived without its text. Ask the
    /// server for it.</summary>
    RequestPageText = 2,

    /// <summary>There is no page there yet. Ask the server to add one.
    /// </summary>
    AddPage = 3,

    /// <summary>
    /// The reader is on the last page that exists, it is blank, and they
    /// wrote it -- so there is nothing to append after. The turn does
    /// not happen and the book is left where it is.
    /// </summary>
    RefusedLastPageBlank = 4,
}

/// <summary>What closing the open page asks the caller to send.</summary>
public enum RuntimeBookFlushAction
{
    /// <summary>Nothing to send: no book, a request is in flight, no page,
    /// or the page is not the local player's to write in.</summary>
    None = 0,

    /// <summary>The page still has text. Send the modify action.</summary>
    ModifyPage = 1,

    /// <summary>The page was left blank and the local player wrote it, so
    /// it goes away instead of being saved.</summary>
    DeletePage = 2,
}

/// <summary>
/// One page turn: what the page being left has to save, and what the
/// page being opened has to fetch.
/// </summary>
public readonly record struct RuntimeBookPageTurn(
    RuntimeBookFlushAction Flush,
    int FlushPage,
    RuntimeBookPageAction Action,
    int Page);

public readonly record struct RuntimeBookSnapshot(
    long Revision,
    bool IsOpen,
    uint BookGuid,
    int MaxNumPages,
    int MaxNumCharsPerPage,
    int PageCount,
    int CurrentPage,
    string Inscription,
    uint ScribeId,
    string ScribeName,
    bool RequestPending);

public interface IRuntimeBookView
{
    RuntimeBookSnapshot Snapshot { get; }

    /// <summary>The pages currently in hand, in page order.</summary>
    IReadOnlyList<BookPage> Pages { get; }

    /// <summary>The page at that index, or null when there is none.</summary>
    BookPage? GetPage(int index);

    /// <summary>Whether the page at that index may be typed into: the
    /// local player wrote it, or the book lets anyone write.</summary>
    bool IsPageEditable(int index);
}

/// <summary>
/// The one open book. A book opens when the server answers a Use with the
/// open-book event, and closes when the panel hides or the session ends.
/// </summary>
public sealed class RuntimeBookState
{
    private readonly object _gate = new();
    private readonly Func<uint>? _playerGuid;
    private readonly List<BookPage> _pages = [];
    private uint _bookGuid;
    private int _maxNumPages;
    private int _maxNumCharsPerPage;
    private int _currentPage = -1;
    private string _inscription = string.Empty;
    private uint _scribeId;
    private string _scribeName = string.Empty;
    private bool _requestPending;
    private long _revision;

    public RuntimeBookState(Func<uint>? playerGuid = null)
    {
        _playerGuid = playerGuid;
        View = new BookView(this);
    }

    public IRuntimeBookView View { get; }

    public RuntimeBookSnapshot Snapshot
    {
        get { lock (_gate) return SnapshotLocked(); }
    }

    /// <summary>
    /// Fold in an open-book answer. Re-opening the same book keeps the
    /// page the reader was on when it still exists; anything else starts
    /// at the last page that does.
    ///
    /// Opening a book ends in a page turn, so the answer may itself ask
    /// for something: an empty parchment has no first page to show and
    /// asks for one to be added, and a page whose text did not come with
    /// the book asks for its text. Without that, a fresh parchment opens
    /// with nothing in it and can never be written in.
    /// </summary>
    public RuntimeBookPageTurn ApplyOpenBook(BookEvents.OpenBook book)
    {
        lock (_gate)
        {
            int carriedPage = _bookGuid == book.BookGuid ? _currentPage : 0;

            _bookGuid = book.BookGuid;
            _maxNumPages = book.MaxNumPages;
            _maxNumCharsPerPage = book.Pages.MaxNumCharsPerPage;
            _pages.Clear();
            _pages.AddRange(book.Pages.Pages);
            _inscription = book.Inscription;
            _scribeId = book.ScribeId;
            _scribeName = book.ScribeName;
            _requestPending = false;

            int page = _pages.Count - 1 >= carriedPage ? carriedPage : _pages.Count - 1;
            if (page < 0) page = 0;

            // The book that was open is closed before the new one opens,
            // which leaves no page current -- so the turn below always
            // has somewhere to go, the first page included.
            _currentPage = -1;
            Bump();

            return TurnPageLocked(page, pageText: null);
        }
    }

    /// <summary>
    /// Fold in a single page's text. A different book, or an answer that
    /// arrived after the book closed, is dropped. An answer for the page the
    /// reader is on is stored and lifts the request gate; an answer for any
    /// other page moves the reader to that page instead and leaves the gate
    /// as it is, so only the page that was asked for can settle a request.
    /// </summary>
    public bool ApplyPageData(BookEvents.PageDataResponse response)
    {
        lock (_gate)
        {
            if (_bookGuid == 0u || response.BookGuid != _bookGuid)
                return false;

            if (response.PageNumber == _currentPage)
            {
                SetPageLocked(response.PageNumber, response.Page);
                _requestPending = false;
            }
            else
            {
                _currentPage = response.PageNumber;
            }

            Bump();
            return true;
        }
    }

    /// <summary>
    /// Fold in an add-page answer. A success for the page the reader is
    /// on becomes a blank page authored by the local player; anything
    /// else leaves the book needing a full re-read, which the caller is
    /// told by the false return.
    /// </summary>
    public bool ApplyAddPageResponse(
        BookEvents.PageResponse response, string authorName)
    {
        lock (_gate)
        {
            if (_bookGuid == 0u || response.BookGuid != _bookGuid)
                return true;

            if (!response.Success)
                return false;

            if (response.PageNumber != _currentPage)
            {
                _currentPage = response.PageNumber;
                Bump();
                return false;
            }

            SetPageLocked(
                response.PageNumber,
                new BookPage(
                    AuthorId: _playerGuid?.Invoke() ?? 0u,
                    AuthorName: authorName ?? string.Empty,
                    AuthorAccount: string.Empty,
                    TextIncluded: 1u,
                    IgnoreAuthor: 0u,
                    PageText: string.Empty));
            _requestPending = false;
            Bump();
            return true;
        }
    }

    /// <summary>
    /// Fold in a stand-alone inscription answer for the open book. It
    /// retitles the reader without disturbing the pages.
    /// </summary>
    public bool ApplyInscription(BookEvents.Inscription inscription)
    {
        lock (_gate)
        {
            if (_bookGuid == 0u || inscription.ObjectGuid != _bookGuid)
                return false;

            _inscription = inscription.Text;
            _scribeId = inscription.ScribeId;
            _scribeName = inscription.ScribeName;
            Bump();
            return true;
        }
    }

    // The delete-page and modify-page answers are deliberately not
    // handled. The page was already removed or rewritten locally when
    // the request went out, so folding the answer in as well would
    // delete a second page -- the one that moved up into the gap. They
    // also carry no gate to lift, because neither request raises one.

    /// <summary>
    /// Turn to a page. Returns what the caller has to send, if anything;
    /// the in-flight gate is raised here for the two answers that need
    /// one, so a second turn cannot race the first.
    /// </summary>
    public RuntimeBookPageAction SetCurrentPage(int page) =>
        TurnPage(page, pageText: null).Action;

    /// <summary>
    /// Turn a page, saving what the reader typed on the one being left.
    /// Pass null for the text to turn without saving -- a reader who
    /// cannot write has nothing to save.
    ///
    /// The page being asked for is checked against the page the reader
    /// is on BEFORE the save, because a save that deletes a blank page
    /// shifts every later page down one, and the turn has to follow it.
    /// </summary>
    public RuntimeBookPageTurn TurnPage(int requested, string? pageText)
    {
        lock (_gate)
            return TurnPageLocked(requested, pageText);
    }

    private RuntimeBookPageTurn TurnPageLocked(int requested, string? pageText)
    {
        {
            if (_bookGuid == 0u || _requestPending)
                return default;
            if (requested < 0 || requested >= _maxNumPages)
                return default;
            if (requested == _currentPage)
                return default;
            if (requested > _pages.Count)
                return default;

            // Appending after a blank page you wrote is refused outright:
            // the empty page at the end is already the new page, so the
            // book neither deletes it nor asks for another.
            if (_currentPage >= 0
                && _currentPage == _pages.Count - 1
                && requested > _currentPage
                && IsBlank(pageText ?? _pages[_currentPage].PageText)
                && _pages[_currentPage].AuthorId == (_playerGuid?.Invoke() ?? 0u))
            {
                return new RuntimeBookPageTurn(
                    RuntimeBookFlushAction.None,
                    _currentPage,
                    RuntimeBookPageAction.RefusedLastPageBlank,
                    _currentPage);
            }

            RuntimeBookFlushAction flush = RuntimeBookFlushAction.None;
            int flushPage = _currentPage;
            if (pageText is not null)
                flush = FlushCurrentPageLocked(pageText, out flushPage);

            int page = requested;
            if (flush == RuntimeBookFlushAction.DeletePage && page > _currentPage)
                page--;

            _currentPage = page;
            Bump();

            RuntimeBookPageAction action;
            if (page >= _pages.Count)
            {
                _requestPending = true;
                action = RuntimeBookPageAction.AddPage;
            }
            else if (_pages[page].TextIncluded == 0u)
            {
                _requestPending = true;
                action = RuntimeBookPageAction.RequestPageText;
            }
            else
            {
                action = RuntimeBookPageAction.Display;
            }

            return new RuntimeBookPageTurn(flush, flushPage, action, page);
        }
    }

    /// <summary>
    /// Close the open page with whatever the reader typed. Returns what
    /// to send. A page nobody may write in, or one with a request still
    /// in flight, sends nothing and keeps the stored text.
    /// </summary>
    public RuntimeBookFlushAction FlushCurrentPage(string text, out int page)
    {
        lock (_gate)
            return FlushCurrentPageLocked(text, out page);
    }

    private RuntimeBookFlushAction FlushCurrentPageLocked(string text, out int page)
    {
        page = _currentPage;
        if (_bookGuid == 0u || _requestPending)
            return RuntimeBookFlushAction.None;
        if (_currentPage < 0 || _currentPage >= _pages.Count)
            return RuntimeBookFlushAction.None;

        BookPage current = _pages[_currentPage];
        if (!IsEditableLocked(current))
            return RuntimeBookFlushAction.None;

        uint playerGuid = _playerGuid?.Invoke() ?? 0u;
        if (IsBlank(text) && current.AuthorId == playerGuid)
        {
            // The page the reader is on keeps its index: a turn that
            // follows adjusts against it, and a close clears it anyway.
            _pages.RemoveAt(_currentPage);
            Bump();
            return RuntimeBookFlushAction.DeletePage;
        }

        _pages[_currentPage] = current with { PageText = text, TextIncluded = 1u };
        Bump();
        return RuntimeBookFlushAction.ModifyPage;
    }

    /// <summary>
    /// A page counts as blank when every character on it is a space, a
    /// newline, or a terminator.
    /// </summary>
    public static bool IsBlank(string? text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        foreach (char c in text)
        {
            if (c != ' ' && c != '\n' && c != '\0')
                return false;
        }

        return true;
    }

    /// <summary>Raise the in-flight gate for a request the caller sent
    /// outside the page-turn path (the whole-book re-read).</summary>
    public void MarkRequestPending()
    {
        lock (_gate)
        {
            if (_bookGuid == 0u) return;
            _requestPending = true;
            Bump();
        }
    }

    public void CloseBook()
    {
        lock (_gate)
        {
            if (_bookGuid == 0u
                && _pages.Count == 0
                && _currentPage == -1
                && !_requestPending)
            {
                return;
            }

            ClearLocked();
        }
    }

    /// <summary>Cleared on generation reset, like every other
    /// session-scoped owner.</summary>
    public void ResetSession() => CloseBook();

    private void ClearLocked()
    {
        _bookGuid = 0u;
        _maxNumPages = 0;
        _maxNumCharsPerPage = 0;
        _pages.Clear();
        _currentPage = -1;
        _inscription = string.Empty;
        _scribeId = 0u;
        _scribeName = string.Empty;
        _requestPending = false;
        Bump();
    }

    private void SetPageLocked(int index, BookPage page)
    {
        if (index < 0) return;
        while (_pages.Count < index)
        {
            _pages.Add(new BookPage(
                0u, string.Empty, string.Empty, 0u, 0u, string.Empty));
        }

        if (index < _pages.Count)
            _pages[index] = page;
        else
            _pages.Add(page);
    }

    private bool IsEditableLocked(BookPage page) =>
        page.IgnoreAuthor != 0u || page.AuthorId == (_playerGuid?.Invoke() ?? 0u);

    private RuntimeBookSnapshot SnapshotLocked() =>
        new(
            _revision,
            _bookGuid != 0u,
            _bookGuid,
            _maxNumPages,
            _maxNumCharsPerPage,
            _pages.Count,
            _currentPage,
            _inscription,
            _scribeId,
            _scribeName,
            _requestPending);

    private void Bump() => _revision++;

    private sealed class BookView(RuntimeBookState owner) : IRuntimeBookView
    {
        public RuntimeBookSnapshot Snapshot => owner.Snapshot;

        public IReadOnlyList<BookPage> Pages
        {
            get { lock (owner._gate) return owner._pages.ToArray(); }
        }

        public BookPage? GetPage(int index)
        {
            lock (owner._gate)
            {
                return index >= 0 && index < owner._pages.Count
                    ? owner._pages[index]
                    : null;
            }
        }

        public bool IsPageEditable(int index)
        {
            lock (owner._gate)
            {
                if (index < 0 || index >= owner._pages.Count) return false;
                return owner.IsEditableLocked(owner._pages[index]);
            }
        }
    }
}
