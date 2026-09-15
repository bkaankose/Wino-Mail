using System.Text;
using Wino.Mapi.Rops;
using Wino.Mapi.Transport;
using Wino.Mapi.Wire;

namespace Wino.Mapi.AddressBook;

/// <summary>
/// The address book half of MAPI/HTTP (MS-OXCMAPIHTTP 2.2.5, the NSPI operations of MS-OXNSPI carried
/// as request bodies to the AddressBook endpoint): Bind opens a session on the GAL, GetMatches with
/// a PidTagAnr restriction asks the directory to do ambiguous-name resolution, Unbind closes. Rows
/// come back as AddressBookPropertyRows, whose string and binary cells carry a HasValue byte the
/// ROP PropertyRow does not.
/// </summary>
public sealed class NspiClient(MapiHttpTransport transport, Action<string>? diagnostics = null) : IAsyncDisposable
{
    private readonly MapiHttpTransport _transport = transport;
    private readonly Action<string>? _diagnostics = diagnostics;
    private bool _bound;

    /// <summary>PidTagAnr: the property a GetMatches restriction names to request ambiguous-name resolution.</summary>
    public const uint Anr = 0x360C001F;

    public const uint DisplayType = 0x39000003;
    public const uint CompanyName = 0x3A16001F;
    public const uint Title = 0x3A17001F;
    public const uint DepartmentName = 0x3A18001F;

    /// <summary>The columns a recipient suggestion needs; all strings or integers, which the row codec handles without guesswork.</summary>
    public static readonly uint[] SuggestionColumns =
    [
        PropertyTags.DisplayName, PropertyTags.SmtpAddress, PropertyTags.EmailAddress, PropertyTags.AddressType,
        CompanyName, Title, DepartmentName, DisplayType,
    ];

    /// <summary>
    /// The STAT code page. NspiBind refuses CP_WINUNICODE (1200) with MAPI_E_UNKNOWN_CPID, as the box
    /// showed; an ANSI page is what the bind wants, and rows still come back Unicode because the
    /// requested columns are PtypString.
    /// </summary>
    private const uint CodePageAnsi = 1252;

    /// <summary>The STAT (MS-OXNSPI 2.2.8) this client sends: GAL container, table start, Windows-1252, US English collation.</summary>
    public static byte[] Stat(uint codePage = CodePageAnsi)
    {
        var w = new RopWriter();
        w.UInt32(0);                     // SortType: SortTypeDisplayName
        w.UInt32(0);                     // ContainerID: the default GAL
        w.UInt32(0);                     // CurrentRec: MID_BEGINNING_OF_TABLE
        w.UInt32(0);                     // Delta
        w.UInt32(0);                     // NumPos
        w.UInt32(0);                     // TotalRecs
        w.UInt32(codePage);              // CodePage
        w.UInt32(1033);                  // TemplateLocale
        w.UInt32(1033);                  // SortLocale
        return w.ToArray();
    }

    /// <summary>Bind request body (2.2.5.1.1): Flags, HasState + STAT, no auxiliary buffer.</summary>
    public static byte[] BuildBind()
    {
        var w = new RopWriter();
        w.UInt32(0);                     // Flags
        w.UInt8(1);                      // HasState
        w.Bytes(Stat());
        w.UInt32(0);                     // AuxiliaryBufferSize
        return w.ToArray();
    }

    /// <summary>Unbind request body (2.2.5.16.1).</summary>
    public static byte[] BuildUnbind()
    {
        var w = new RopWriter();
        w.UInt32(0);                     // Reserved
        w.UInt32(0);                     // AuxiliaryBufferSize
        return w.ToArray();
    }

    /// <summary>
    /// The ways of asking the directory for name matches, tried in order: ambiguous-name resolution
    /// on PidTagAnr (what Outlook's To line gets), then a prefix match on the display name.
    /// </summary>
    public enum FilterKind
    {
        /// <summary>RES_PROPERTY RELOP_EQ PidTagAnr (Unicode).</summary>
        AnrUnicode,
        /// <summary>RES_CONTENT FL_PREFIX | FL_IGNORECASE on PidTagDisplayName.</summary>
        DisplayNamePrefix,
    }

    /// <summary>
    /// GetMatches request body (2.2.5.5.1). The restriction is MS-OXCDATA 2.12 with one addition the
    /// server's parser revealed (Restriction.ReadNullablePropertyValue in its stack trace): after the
    /// PropTag comes a presence byte, and only then the AddressBookTaggedPropertyValue, whose string
    /// carries its own HasValue byte. Without the presence byte the server read the tag one byte late.
    /// </summary>
    public static byte[] BuildGetMatches(string query, IReadOnlyList<uint> columns, uint maxRows, FilterKind kind = FilterKind.AnrUnicode)
    {
        var w = new RopWriter();
        w.UInt32(0);                     // Reserved
        w.UInt8(1);                      // HasState
        w.Bytes(Stat());
        w.UInt8(0);                      // HasMinimalIds
        w.UInt32(0);                     // InterfaceOptionFlags
        w.UInt8(1);                      // HasFilter
        uint tag;
        if (kind == FilterKind.DisplayNamePrefix)
        {
            tag = PropertyTags.DisplayName;
            w.UInt8(0x03);               // RES_CONTENT
            w.UInt16(0x0002);            // FuzzyLevelLow: FL_PREFIX
            w.UInt16(0x0001);            // FuzzyLevelHigh: FL_IGNORECASE
        }
        else
        {
            tag = Anr;
            w.UInt8(0x04);               // RES_PROPERTY
            w.UInt8(0x04);               // RELOP_EQ
        }
        w.UInt32(tag);                   // PropTag
        w.UInt8(0xFF);                   // the value is present (nullable property value)
        w.UInt32(tag);                   // TaggedPropertyValue.PropertyTag
        w.UInt8(0xFF);                   // AddressBookPropertyValue.HasValue
        w.UnicodeZ(query);
        w.UInt8(0);                      // HasPropertyName
        w.UInt32(maxRows);               // Rows
        w.UInt8(1);                      // HasColumns
        w.UInt32((uint)columns.Count);   // LargePropertyTagArray.PropertyTagCount
        foreach (var t in columns) w.UInt32(t);
        w.UInt32(0);                     // AuxiliaryBufferSize
        return w.ToArray();
    }

    public async Task BindAsync(CancellationToken cancellationToken = default)
    {
        var response = await _transport.SendAddressBookAsync("Bind", BuildBind(), cancellationToken).ConfigureAwait(false);
        response.EnsureTransportSucceeded();
        var r = new RopReader(response.Body);
        var status = r.UInt32();
        var error = r.UInt32();
        if (status != 0 || error != 0)
            throw new MapiFormatException($"NSPI Bind returned StatusCode 0x{status:X8}, ErrorCode 0x{error:X8}.");
        _bound = true;
        _diagnostics?.Invoke("NSPI bound to the address book");
    }

    /// <summary>MAPI_E_TOO_COMPLEX: the directory will not evaluate the restriction as written.</summary>
    public const uint TooComplex = 0x80040117;

    /// <summary>The directory's matches for <paramref name="query"/>, one PropertyValue per requested column per row.</summary>
    public async Task<List<PropertyValue[]>> GetMatchesAsync(string query, IReadOnlyList<uint> columns, uint maxRows, CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        foreach (var kind in Enum.GetValues<FilterKind>())
        {
            var request = BuildGetMatches(query, columns, maxRows, kind);
            var response = await _transport.SendAddressBookAsync("GetMatches", request, cancellationToken).ConfigureAwait(false);
            if (!response.TransportSucceeded)
            {
                var text = Encoding.UTF8.GetString(response.RawBody, 0, Math.Min(response.RawBody.Length, 1024)).Replace("\0", string.Empty);
                _diagnostics?.Invoke($"NSPI GetMatches {kind} refused (X-ResponseCode {response.ResponseCodeHeader}); server said:" + Environment.NewLine + text + Environment.NewLine + "request:" + Environment.NewLine + HexDump.Render(request, 160));
                last = new MapiFormatException($"GetMatches ({kind}) was not accepted by the transport: X-ResponseCode {response.ResponseCodeHeader}");
                continue;
            }

            try
            {
                var rows = ParseGetMatches(response.Body, columns);
                _diagnostics?.Invoke($"NSPI GetMatches {kind}: {rows.Count} rows");
                return rows;
            }
            catch (MapiRopException ex) when (ex.ReturnValue == TooComplex)
            {
                _diagnostics?.Invoke($"NSPI GetMatches {kind}: too complex for the directory; trying the next form");
                last = ex;
            }
            catch (MapiException ex)
            {
                _diagnostics?.Invoke($"NSPI GetMatches {kind} not decoded ({ex.Message}); request:" + Environment.NewLine + HexDump.Render(request, 512) + Environment.NewLine + "response:" + Environment.NewLine + HexDump.Render(response.Body, 2048));
                throw;
            }
        }

        throw last ?? new MapiFormatException("NSPI GetMatches: no filter form was accepted.");
    }

    /// <summary>GetMatches response (2.2.5.5.2): status, optional STAT, optional minimal ids, then the columns and rows when HasColsAndRows.</summary>
    public static List<PropertyValue[]> ParseGetMatches(byte[] body, IReadOnlyList<uint> requestedColumns)
    {
        var r = new RopReader(body);
        var status = r.UInt32();
        var error = r.UInt32();
        if (status != 0)
            throw new MapiFormatException($"NSPI GetMatches returned StatusCode 0x{status:X8}.");
        if (error != 0 && error != 0x00040380)                              // MAPI_W_PARTIAL_COMPLETION: the row cap was hit
            throw new MapiRopException("NspiGetMatches", error);

        if (r.UInt8() != 0) r.Bytes(36);                                      // HasState + STAT
        if (r.UInt8() != 0)                                                   // HasMinimalIds
        {
            var count = r.UInt32();
            for (var i = 0; i < count; i++) r.UInt32();
        }

        var rows = new List<PropertyValue[]>();
        if (r.UInt8() == 0)                                                   // HasColsAndRows
            return rows;

        var columnCount = r.UInt32();
        var columns = new uint[columnCount];
        for (var i = 0; i < columnCount; i++) columns[i] = r.UInt32();
        var rowCount = r.UInt32();
        for (var i = 0; i < rowCount; i++)
            rows.Add(ReadRow(r, columns));
        return rows;
    }

    /// <summary>
    /// AddressBookPropertyRow (2.2.1.7): a Flags byte, then per column either an AddressBookPropertyValue
    /// (Flags 0x00) or a flagged one (0x01: 0x0 value follows, 0x1 absent, 0xA a uint32 error).
    /// String and binary values carry a HasValue byte (0xFF present) before the value.
    /// </summary>
    public static PropertyValue[] ReadRow(RopReader r, IReadOnlyList<uint> columns)
    {
        var flagged = r.UInt8() == 0x01;
        var cells = new PropertyValue[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var tag = columns[i];
            if (flagged)
            {
                var flag = r.UInt8();
                if (flag == 0x1) { cells[i] = PropertyValue.Absent(tag); continue; }
                if (flag == 0xA) { cells[i] = PropertyValue.Failed(tag, r.UInt32()); continue; }
            }

            cells[i] = PropertyValue.Of(tag, ReadValue(r, (ushort)(tag & 0xFFFF)));
        }
        return cells;
    }

    private static object? ReadValue(RopReader r, ushort type)
    {
        switch (type)
        {
            case PropertyTypes.Unicode:
                return r.UInt8() == 0 ? null : r.UnicodeZ();
            case PropertyTypes.String8:
                return r.UInt8() == 0 ? null : r.AsciiZ();
            case PropertyTypes.Binary:
            {
                if (r.UInt8() == 0) return null;
                var count = r.UInt32();
                return r.Bytes((int)count).ToArray();
            }
            case PropertyTypes.MultipleUnicode:
            {
                if (r.UInt8() == 0) return null;
                var count = r.UInt32();
                var values = new string[count];
                for (var i = 0; i < count; i++) values[i] = r.UnicodeZ();
                return values;
            }
            default:
                return PropertyRow.ReadValue(r, type);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_bound)
        {
            try { await _transport.SendAddressBookAsync("Unbind", BuildUnbind()).ConfigureAwait(false); }
            catch { }
        }
        _transport.Dispose();
    }
}
