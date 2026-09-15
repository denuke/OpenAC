using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace AcDream.Core.Net.Messages;

/// <summary>
/// Outbound book actions, all in the ordinary GameAction envelope.
/// A book is opened by a plain Use on the item; the server answers with
/// the OpenBook event. Everything here is what an already-open book
/// sends afterwards.
/// </summary>
public static class BookRequests
{
    public const uint GameActionEnvelope = 0xF7B1u;

    /// <summary>Re-request the whole book. Sent after an add-page answer
    /// that the open book could not fold in by itself.</summary>
    public const uint BookDataOpcode = 0x00AAu;

    public const uint BookModifyPageOpcode = 0x00ABu;
    public const uint BookAddPageOpcode = 0x00ACu;
    public const uint BookDeletePageOpcode = 0x00ADu;

    /// <summary>Request one page's text. Sent when turning to a page the
    /// open-book payload carried without its text.</summary>
    public const uint BookPageDataOpcode = 0x00AEu;

    public static byte[] BuildBookData(uint gameActionSequence, uint bookGuid) =>
        BuildGuidOnly(gameActionSequence, BookDataOpcode, bookGuid);

    public static byte[] BuildBookAddPage(uint gameActionSequence, uint bookGuid) =>
        BuildGuidOnly(gameActionSequence, BookAddPageOpcode, bookGuid);

    public static byte[] BuildBookPageData(
        uint gameActionSequence, uint bookGuid, int page) =>
        BuildGuidAndPage(gameActionSequence, BookPageDataOpcode, bookGuid, page);

    public static byte[] BuildBookDeletePage(
        uint gameActionSequence, uint bookGuid, int page) =>
        BuildGuidAndPage(gameActionSequence, BookDeletePageOpcode, bookGuid, page);

    public static byte[] BuildBookModifyPage(
        uint gameActionSequence, uint bookGuid, int page, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] encoded = Encodings.Windows1252.GetBytes(text);
        if (encoded.Length > ushort.MaxValue)
        {
            throw new ArgumentException(
                "Page text is too long for String16L.", nameof(text));
        }

        // The string record is padded out to a four-byte boundary, the
        // same way every other packed string on this wire is.
        int stringRecordLength = (2 + encoded.Length + 3) & ~3;
        byte[] body = new byte[20 + stringRecordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body, GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), gameActionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), BookModifyPageOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), bookGuid);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), page);
        BinaryPrimitives.WriteUInt16LittleEndian(
            body.AsSpan(20), (ushort)encoded.Length);
        encoded.CopyTo(body, 22);
        return body;
    }

    private static byte[] BuildGuidOnly(uint sequence, uint opcode, uint bookGuid)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body, GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), bookGuid);
        return body;
    }

    private static byte[] BuildGuidAndPage(
        uint sequence, uint opcode, uint bookGuid, int page)
    {
        byte[] body = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(body, GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), bookGuid);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), page);
        return body;
    }
}

/// <summary>One page of a book as it arrives on the wire.</summary>
/// <param name="TextIncluded">Zero means the payload carried the page's
/// author but not its text; the text has to be fetched separately.</param>
/// <param name="IgnoreAuthor">Set on books anyone may write in. It makes
/// a page editable even though the local player did not author it.</param>
public readonly record struct BookPage(
    uint AuthorId,
    string AuthorName,
    string AuthorAccount,
    uint TextIncluded,
    uint IgnoreAuthor,
    string PageText);

/// <summary>The page list carried by an OpenBook payload. It repeats the
/// page cap and adds the per-page character cap.</summary>
public readonly record struct BookPageList(
    int MaxNumPages,
    int MaxNumCharsPerPage,
    IReadOnlyList<BookPage> Pages);

/// <summary>Inbound book events.</summary>
public static class BookEvents
{
    /// <summary>Marks a page record that carries its text-included and
    /// ignore-author words explicitly. Anything else is an older record
    /// whose single word IS the text-included flag.</summary>
    private const uint ExplicitPageFlagsMarker = 0xFFFF0002u;

    public readonly record struct OpenBook(
        uint BookGuid,
        int MaxNumPages,
        BookPageList Pages,
        string Inscription,
        uint ScribeId,
        string ScribeName);

    public readonly record struct PageDataResponse(
        uint BookGuid,
        int PageNumber,
        BookPage Page);

    public readonly record struct Inscription(
        uint ObjectGuid,
        string Text,
        uint ScribeId,
        string ScribeName,
        string ScribeAccount);

    /// <summary>The shape shared by the add-page, delete-page and
    /// modify-page answers.</summary>
    public readonly record struct PageResponse(
        uint BookGuid,
        int PageNumber,
        bool Success);

    public static OpenBook? ParseOpenBook(ReadOnlySpan<byte> payload)
    {
        try
        {
            if (payload.Length < 8) return null;
            uint bookGuid = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            int maxNumPages = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4));
            int pos = 8;

            BookPageList pages = ReadPageList(payload, ref pos);
            string inscription = StringReader.ReadString16L(payload, ref pos);
            if (payload.Length - pos < 4) return null;
            uint scribeId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos));
            pos += 4;
            string scribeName = StringReader.ReadString16L(payload, ref pos);

            return new OpenBook(
                bookGuid, maxNumPages, pages, inscription, scribeId, scribeName);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static PageDataResponse? ParsePageData(ReadOnlySpan<byte> payload)
    {
        try
        {
            if (payload.Length < 8) return null;
            uint bookGuid = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            int pageNumber = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4));
            int pos = 8;
            BookPage page = ReadPage(payload, ref pos);
            return new PageDataResponse(bookGuid, pageNumber, page);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The stand-alone inscription answer. Retail's own client has no
    /// handler for it and current servers do not send it, so this only
    /// exists so a server that does send one is not ignored.
    /// </summary>
    public static Inscription? ParseInscription(ReadOnlySpan<byte> payload)
    {
        try
        {
            if (payload.Length < 4) return null;
            uint objectGuid = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            int pos = 4;
            string inscription = StringReader.ReadString16L(payload, ref pos);
            if (payload.Length - pos < 4) return null;
            uint scribeId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos));
            pos += 4;
            string scribeName = StringReader.ReadString16L(payload, ref pos);
            string scribeAccount = payload.Length - pos >= 2
                ? StringReader.ReadString16L(payload, ref pos)
                : string.Empty;

            return new Inscription(
                objectGuid, inscription, scribeId, scribeName, scribeAccount);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static PageResponse? ParsePageResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12) return null;
        return new PageResponse(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8)) != 0u);
    }

    private static BookPageList ReadPageList(ReadOnlySpan<byte> payload, ref int pos)
    {
        if (payload.Length - pos < 12)
            throw new FormatException("truncated book page list header");

        int maxNumPages = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(pos));
        int maxNumCharsPerPage =
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(pos + 4));
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(pos + 8));
        pos += 12;

        if (count <= 0)
        {
            return new BookPageList(
                maxNumPages, maxNumCharsPerPage, Array.Empty<BookPage>());
        }

        var pages = new BookPage[count];
        for (int i = 0; i < count; i++)
            pages[i] = ReadPage(payload, ref pos);

        return new BookPageList(maxNumPages, maxNumCharsPerPage, pages);
    }

    private static BookPage ReadPage(ReadOnlySpan<byte> payload, ref int pos)
    {
        if (payload.Length - pos < 4)
            throw new FormatException("truncated book page author");
        uint authorId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos));
        pos += 4;

        string authorName = StringReader.ReadString16L(payload, ref pos);
        string authorAccount = StringReader.ReadString16L(payload, ref pos);

        if (payload.Length - pos < 4)
            throw new FormatException("truncated book page flags");
        uint marker = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos));
        pos += 4;

        uint textIncluded;
        uint ignoreAuthor;
        if (marker == ExplicitPageFlagsMarker)
        {
            if (payload.Length - pos < 8)
                throw new FormatException("truncated book page flag words");
            textIncluded = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos));
            ignoreAuthor =
                BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos + 4));
            pos += 8;
        }
        else
        {
            textIncluded = marker;
            ignoreAuthor = 0u;
        }

        string pageText = textIncluded != 0u
            ? StringReader.ReadString16L(payload, ref pos)
            : string.Empty;

        return new BookPage(
            authorId, authorName, authorAccount, textIncluded, ignoreAuthor, pageText);
    }
}
