using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests;

/// <summary>
/// Literal-packet coverage for the book wire. Every buffer here is built
/// field by field in the order the client reads them, so a reordering in
/// the parser shows up as a decode failure rather than a silent shift.
/// </summary>
public sealed class BookMessagesTests
{
    private sealed class Buf
    {
        private readonly List<byte> _bytes = new();

        public Buf U32(uint v)
        {
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, v);
            _bytes.AddRange(tmp.ToArray());
            return this;
        }

        public Buf I32(int v) => U32(unchecked((uint)v));

        /// <summary>A packed string: 16-bit byte count, the CP1252 bytes,
        /// then padding out to the next four-byte boundary.</summary>
        public Buf Str(string s)
        {
            byte[] text = Encoding.Latin1.GetBytes(s);
            Span<byte> len = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(len, (ushort)text.Length);
            _bytes.AddRange(len.ToArray());
            _bytes.AddRange(text);
            int record = 2 + text.Length;
            int pad = (4 - (record & 3)) & 3;
            for (int i = 0; i < pad; i++) _bytes.Add(0);
            return this;
        }

        public byte[] Done() => _bytes.ToArray();
    }

    private static void AppendPageWithText(
        Buf target, uint authorId, string name, string account,
        bool ignoreAuthor, string text)
    {
        target
            .U32(authorId)
            .Str(name)
            .Str(account)
            .U32(0xFFFF0002u)
            .I32(1)
            .I32(ignoreAuthor ? 1 : 0)
            .Str(text);
    }

    private static void AppendPageWithoutText(
        Buf target, uint authorId, string name, string account, bool ignoreAuthor)
    {
        target
            .U32(authorId)
            .Str(name)
            .Str(account)
            .U32(0xFFFF0002u)
            .I32(0)
            .I32(ignoreAuthor ? 1 : 0);
    }

    [Fact]
    public void ParseOpenBook_ReadsGuidPageCapsPagesInscriptionAndScribe()
    {
        var buf = new Buf()
            .U32(0x80001234u)      // book guid
            .I32(8)                // the event's own page cap
            .I32(8)                // the list repeats the page cap
            .I32(1000)             // characters allowed per page
            .I32(2);               // page count
        AppendPageWithText(buf, 0x50000001u, "Scribbler", "acct", false, "first");
        AppendPageWithoutText(buf, 0x50000002u, "Other", "acct2", true);
        buf.Str("A Traveller's Log")   // inscription
           .U32(0x50000001u)           // scribe
           .Str("Scribbler");

        BookEvents.OpenBook? parsed = BookEvents.ParseOpenBook(buf.Done());

        Assert.NotNull(parsed);
        BookEvents.OpenBook book = parsed!.Value;
        Assert.Equal(0x80001234u, book.BookGuid);
        Assert.Equal(8, book.MaxNumPages);
        Assert.Equal(8, book.Pages.MaxNumPages);
        Assert.Equal(1000, book.Pages.MaxNumCharsPerPage);
        Assert.Equal(2, book.Pages.Pages.Count);

        BookPage first = book.Pages.Pages[0];
        Assert.Equal(0x50000001u, first.AuthorId);
        Assert.Equal("Scribbler", first.AuthorName);
        Assert.Equal("acct", first.AuthorAccount);
        Assert.NotEqual(0u, first.TextIncluded);
        Assert.Equal(0u, first.IgnoreAuthor);
        Assert.Equal("first", first.PageText);

        BookPage second = book.Pages.Pages[1];
        Assert.Equal(0x50000002u, second.AuthorId);
        Assert.Equal(0u, second.TextIncluded);
        Assert.Equal(1u, second.IgnoreAuthor);
        Assert.Equal(string.Empty, second.PageText);

        Assert.Equal("A Traveller's Log", book.Inscription);
        Assert.Equal(0x50000001u, book.ScribeId);
        Assert.Equal("Scribbler", book.ScribeName);
    }

    [Fact]
    public void ParseOpenBook_AcceptsAnEmptyBook()
    {
        byte[] payload = new Buf()
            .U32(0x8000AAAAu)
            .I32(4)
            .I32(4)
            .I32(500)
            .I32(0)
            .Str(string.Empty)
            .U32(0u)
            .Str(string.Empty)
            .Done();

        BookEvents.OpenBook? parsed = BookEvents.ParseOpenBook(payload);

        Assert.NotNull(parsed);
        Assert.Empty(parsed!.Value.Pages.Pages);
        Assert.Equal(0u, parsed.Value.ScribeId);
        Assert.Equal(string.Empty, parsed.Value.Inscription);
    }

    [Fact]
    public void ParseOpenBook_WithoutTheExplicitFlagWordsTreatsTheWordAsTextIncluded()
    {
        // Older page records carry no 0xFFFF0002 marker: the single word
        // after the two names IS the text-included flag, and there is no
        // ignore-author word at all.
        byte[] payload = new Buf()
            .U32(0x8000BBBBu)
            .I32(1)
            .I32(1)
            .I32(500)
            .I32(1)
            .U32(0x50000009u)
            .Str("Author")
            .Str("acct")
            .U32(1u)            // text included, no marker
            .Str("legacy page")
            .Str(string.Empty)
            .U32(0u)
            .Str(string.Empty)
            .Done();

        BookEvents.OpenBook? parsed = BookEvents.ParseOpenBook(payload);

        Assert.NotNull(parsed);
        BookPage page = Assert.Single(parsed!.Value.Pages.Pages);
        Assert.Equal(1u, page.TextIncluded);
        Assert.Equal(0u, page.IgnoreAuthor);
        Assert.Equal("legacy page", page.PageText);
    }

    [Fact]
    public void ParseOpenBook_RejectsATruncatedPayload()
    {
        byte[] payload = new Buf()
            .U32(0x8000CCCCu)
            .I32(4)
            .I32(4)
            .I32(500)
            .I32(3)             // claims three pages and then stops
            .Done();

        Assert.Null(BookEvents.ParseOpenBook(payload));
    }

    [Fact]
    public void ParsePageData_ReadsGuidPageNumberAndPage()
    {
        var buf = new Buf().U32(0x80005555u).I32(2);
        AppendPageWithText(buf, 0x50000003u, "Writer", "acct", true, "page three");

        BookEvents.PageDataResponse? parsed = BookEvents.ParsePageData(buf.Done());

        Assert.NotNull(parsed);
        Assert.Equal(0x80005555u, parsed!.Value.BookGuid);
        Assert.Equal(2, parsed.Value.PageNumber);
        Assert.Equal("page three", parsed.Value.Page.PageText);
        Assert.Equal(1u, parsed.Value.Page.IgnoreAuthor);
        Assert.Equal("Writer", parsed.Value.Page.AuthorName);
    }

    [Theory]
    [InlineData(1u, true)]
    [InlineData(0u, false)]
    public void ParsePageResponse_ReadsGuidPageAndSuccess(uint success, bool expected)
    {
        byte[] payload = new Buf()
            .U32(0x80007777u)
            .I32(3)
            .U32(success)
            .Done();

        BookEvents.PageResponse? parsed = BookEvents.ParsePageResponse(payload);

        Assert.NotNull(parsed);
        Assert.Equal(0x80007777u, parsed!.Value.BookGuid);
        Assert.Equal(3, parsed.Value.PageNumber);
        Assert.Equal(expected, parsed.Value.Success);
    }

    [Fact]
    public void ParsePageResponse_RejectsAShortPayload()
    {
        Assert.Null(BookEvents.ParsePageResponse(new byte[8]));
    }

    [Fact]
    public void BuildBookData_PacksEnvelopeSequenceOpcodeAndGuid()
    {
        byte[] body = BookRequests.BuildBookData(0x11u, 0x80001234u);

        Assert.Equal(16, body.Length);
        Assert.Equal(0xF7B1u, BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(0x11u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(0x00AAu, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(
            0x80001234u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void BuildBookAddPage_PacksTheGuidOnly()
    {
        byte[] body = BookRequests.BuildBookAddPage(0x12u, 0x80001234u);

        Assert.Equal(16, body.Length);
        Assert.Equal(0x00ACu, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(
            0x80001234u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void BuildBookPageData_PacksGuidThenPage()
    {
        byte[] body = BookRequests.BuildBookPageData(0x13u, 0x80001234u, 4);

        Assert.Equal(20, body.Length);
        Assert.Equal(0x00AEu, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(
            0x80001234u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(16)));
    }

    [Fact]
    public void BuildBookDeletePage_PacksGuidThenPage()
    {
        byte[] body = BookRequests.BuildBookDeletePage(0x14u, 0x80001234u, 1);

        Assert.Equal(20, body.Length);
        Assert.Equal(0x00ADu, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(16)));
    }

    [Fact]
    public void BuildBookModifyPage_PacksGuidPageThenPaddedText()
    {
        byte[] body = BookRequests.BuildBookModifyPage(0x15u, 0x80001234u, 0, "hi");

        // 20 header bytes plus a 2-byte length, 2 text bytes and no padding.
        Assert.Equal(24, body.Length);
        Assert.Equal(0x00ABu, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(
            0x80001234u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(16)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(20)));
        Assert.Equal((byte)'h', body[22]);
        Assert.Equal((byte)'i', body[23]);
    }

    [Fact]
    public void BuildBookModifyPage_PadsTheTextRecordToFourBytes()
    {
        // 2 + 3 = 5 bytes of string record, padded up to 8.
        byte[] body = BookRequests.BuildBookModifyPage(0x16u, 0x1u, 0, "abc");

        Assert.Equal(28, body.Length);
        Assert.Equal(0, body[25]);
        Assert.Equal(0, body[26]);
        Assert.Equal(0, body[27]);
    }

    [Fact]
    public void BuildBookModifyPage_RoundTripsThroughTheServerFieldOrder()
    {
        byte[] body = BookRequests.BuildBookModifyPage(0x17u, 0xDEADBEEFu, 7, "text");

        // Read it back exactly as the server does: guid, page, string.
        Assert.Equal(
            0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(16)));
        int length = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(20));
        Assert.Equal("text", Encoding.Latin1.GetString(body, 22, length));
    }

    [Fact]
    public void BuildBookModifyPage_RejectsNullText()
    {
        Assert.Throws<ArgumentNullException>(
            () => BookRequests.BuildBookModifyPage(1u, 1u, 0, null!));
    }

    [Fact]
    public void ParseInscription_ReadsGuidTextScribeAndAccount()
    {
        byte[] payload = new Buf()
            .U32(0x80004321u)
            .Str("Property of nobody")
            .U32(0x5000000Au)
            .Str("Acdream")
            .Str("testaccount")
            .Done();

        BookEvents.Inscription? parsed = BookEvents.ParseInscription(payload);

        Assert.NotNull(parsed);
        Assert.Equal(0x80004321u, parsed!.Value.ObjectGuid);
        Assert.Equal("Property of nobody", parsed.Value.Text);
        Assert.Equal(0x5000000Au, parsed.Value.ScribeId);
        Assert.Equal("Acdream", parsed.Value.ScribeName);
        Assert.Equal("testaccount", parsed.Value.ScribeAccount);
    }

    [Fact]
    public void ParseInscription_AcceptsAPayloadWithoutTheAccount()
    {
        byte[] payload = new Buf()
            .U32(0x80004321u)
            .Str("Inscribed")
            .U32(0u)
            .Str(string.Empty)
            .Done();

        BookEvents.Inscription? parsed = BookEvents.ParseInscription(payload);

        Assert.NotNull(parsed);
        Assert.Equal(string.Empty, parsed!.Value.ScribeAccount);
    }

    [Fact]
    public void ParseInscription_RejectsATruncatedPayload()
    {
        Assert.Null(BookEvents.ParseInscription(new byte[3]));
    }
}
