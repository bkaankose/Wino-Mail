using Wino.Mapi;
using Wino.Mapi.Restrictions;
using Wino.Mapi.Rops;
using Wino.Mapi.Transport;
using Wino.Mapi.Wire;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>
/// The MAPI codec against captured wire vectors. The specs give the format; only a real server says
/// what it actually emits, so every vector here was captured from Exchange 2019 and then reduced to
/// protocol scaffolding: ROP headers, FAI message classes, rule names. No addresses, no bodies.
///
/// Each test pins a mistake that was actually made while bringing the wire up. In this protocol a
/// wrong read produces a plausible number rather than an error, so these are the tripwires.
/// </summary>
public class MapiWireTests
{
    [Fact]
    public void ExtendedRuleCondition_SubjectContains_Decodes()
    {
        // PidTagExtendedRuleMessageCondition from a live rule named "Alert;".
        // 0000            NoOfNamedProps = 0
        // 03              RES_CONTENT
        // 0100 0100       FL_SUBSTRING, FL_IGNORECASE
        // 1F00 3700       PidTagSubject (PT_UNICODE)
        // 1F00 3700       tagged value, same tag
        // "Alert;" \0
        var node = Restriction.DecodeExtendedRuleCondition(Convert.FromHexString("000003010001001F0037001F00370041006C006500720074003B000000"));

        node.Kind.Should().Be(RestrictionKind.Content);
        node.PropertyTag.Should().Be(0x0037001Fu);
        node.Value.Should().Be("Alert;");
        node.Detail.Should().Be("fuzzy=SUBSTRING|IGNORECASE");
    }

    /// <summary>
    /// Extended rules count AND/OR subrestrictions in 4 bytes; ROP restrictions in 2. Reading the
    /// narrow form against the wide one consumed half a count and treated the high half as the next
    /// opcode, surfacing as "unhandled restriction type 0x1F" far from the real mistake.
    /// </summary>
    [Fact]
    public void ExtendedRuleCondition_CountsAreFourBytesWide()
    {
        const uint SenderEmailAddress = 0x0C1F001F;

        var writer = new RopWriter();
        writer.UInt16(0);                 // NoOfNamedProps
        writer.UInt8(0x01);               // OR
        writer.UInt32(2);                 // ...counted in FOUR bytes here
        foreach (var address in new[] { "a@one.example", "b@two.example" })
        {
            writer.UInt8(0x03);           // CONTENT
            writer.UInt16(0x0000);        // FL_FULLSTRING
            writer.UInt16(0x0001);        // FL_IGNORECASE
            writer.UInt32(SenderEmailAddress);
            writer.UInt32(SenderEmailAddress);
            writer.UnicodeZ(address);
        }

        var node = Restriction.DecodeExtendedRuleCondition(writer.ToArray());

        node.Kind.Should().Be(RestrictionKind.Or);
        node.Children.Should().HaveCount(2);
        node.Children.Should().OnlyContain(c => c.Kind == RestrictionKind.Content && c.PropertyTag == SenderEmailAddress);
        node.Children.Select(c => c.Value).Should().Equal("a@one.example", "b@two.example");
    }

    /// <summary>
    /// Both plausible framing mistakes are silent here and loud somewhere unhelpful: RopSize counts
    /// itself, and RPC_HEADER_EXT's Size covers the payload after the header. Get either wrong and
    /// the server rejects at the transport layer, which looks like an auth or endpoint problem.
    /// </summary>
    [Fact]
    public void ExecuteFraming_SizeFieldsAgreeWithBytes()
    {
        var rop = RopLogon.BuildRequest("/o=Example/cn=Recipients/cn=test");
        var body = RopExecute.BuildExecuteBody(rop, [RopExecute.NullHandle]);

        var reader = new RopReader(body);
        reader.UInt32().Should().Be(0, "Flags");
        var ropBufferSize = reader.UInt32();
        var ropBuffer = reader.Bytes((int)ropBufferSize).ToArray();
        reader.UInt32();                                  // MaxRopOut
        reader.UInt32().Should().Be(0, "AuxiliaryBufferSize");
        reader.Remaining.Should().Be(0);

        var inner = new RopReader(ropBuffer);
        inner.UInt16().Should().Be(0x0000, "Version");
        inner.UInt16().Should().Be(0x0004, "Flags: Last");
        var size = inner.UInt16();
        var sizeActual = inner.UInt16();
        var ropSize = inner.UInt16();

        var expectedPayload = 2 + rop.Length + 4;         // RopSize + ROPs + one handle
        size.Should().Be((ushort)expectedPayload);
        sizeActual.Should().Be(size);
        ropSize.Should().Be((ushort)(rop.Length + 2));
        ropBufferSize.Should().Be((uint)(8 + expectedPayload));
    }

    /// <summary>
    /// MS-OXCRPC compresses with plain LZ77 (MS-XCA 2.1), not the LZ77+Huffman of 2.2. Reading it as
    /// Huffman fails on the code-length table, several layers away from the actual mistake.
    /// </summary>
    [Fact]
    public void Lz77_DecompressesRopHeader()
    {
        // Captured payload, immediately after RPC_HEADER_EXT (Flags 0x0005, SizeActual 1263):
        // flags 0x04000004 -> literals e3 04 15 02 00, then a match.
        var output = XpressLz77.Decompress(Convert.FromHexString("04000004e304150200000002"), 8);

        // RopSize 0x04e3 = 1251 (= 1263 - 12 for three handles), then RopQueryRows (0x15) on
        // handle index 2 with ReturnValue 0.
        Convert.ToHexString(output).ToLowerInvariant().Should().StartWith("e30415020000");
    }

    /// <summary>
    /// A complete captured compressed QueryRows response, which is what exercises the length escapes.
    /// The regression: a nibble of 15 escapes to a byte that ADDS to 15 rather than replacing it.
    /// Subtracting produced negative intermediates and the stream stayed superficially valid for
    /// another 80 bytes before failing somewhere unrelated.
    /// </summary>
    [Fact]
    public void Lz77_FullCompressedQueryRows_DecompressesAndParses()
    {
        var body = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Mapi", "TestData-CompressedQueryRows.bin"));

        var (rops, handles) = RopExecute.ParseExecuteResponse(body);
        var rows = RopFolder.ParseQueryRows(new RopReader(rops), PropertyTags.RuleColumns);

        handles.Should().HaveCount(3);
        rows.Should().HaveCount(10);

        var classes = rows.Select(r => r[0].AsString).ToList();
        classes.Should().Contain("IPM.ExtendedRule.Message");
        classes.Count(c => c!.StartsWith("IPM.Configuration.", StringComparison.Ordinal)).Should().Be(9);

        // The configuration items lack rule properties and say so per cell: a flagged PropertyRow
        // with PtypErrorCode MAPI_E_NOT_FOUND, not a parse failure.
        var configuration = rows.First(r => r[0].AsString!.StartsWith("IPM.Configuration.", StringComparison.Ordinal));
        configuration[1].Error.Should().Be(MapiRopException.NotFound);

        var junk = rows.Single(r => r[0].AsString == "IPM.ExtendedRule.Message");
        junk[1].AsString.Should().Be("JunkEmailRule");
        junk[3].AsUInt64.Should().NotBeNull("the Mid is what RopOpenMessage needs");
    }

    /// <summary>
    /// A non-zero Execute ErrorCode means what follows is a diagnostic, not ROPs. Reading on produced
    /// "buffer underrun at offset 0", which pointed nowhere.
    /// </summary>
    [Fact]
    public void ExecuteResponse_RpcError_IsNamedNotParsed()
    {
        var writer = new RopWriter();
        writer.UInt32(0);                          // StatusCode
        writer.UInt32(MapiRpcException.BufferTooSmall);
        writer.UInt32(0);                          // Flags
        writer.UInt32(4);                          // RopBufferSize
        writer.AsciiZ("Mic");                      // the start of a diagnostic string

        var act = () => RopExecute.ParseExecuteResponse(writer.ToArray());

        act.Should().Throw<MapiRpcException>().Which.ErrorCode.Should().Be(MapiRpcException.BufferTooSmall);
    }

    /// <summary>
    /// DONE is followed by more headers and a blank line before the buffer. Skipping only the blank
    /// line left "X-StartTime" at the head of the payload, where ErrorCode decoded as 0x54747261,
    /// which is ASCII "artT".
    /// </summary>
    [Fact]
    public void MetaTags_HeadersAfterDoneAreStripped()
    {
        var payload = "PROCESSING\r\nDONE\r\nX-ElapsedTime: 12\r\nX-StartTime: Wed\r\n\r\n"u8.ToArray()
            .Concat(new byte[] { 0x01, 0x02, 0x03 }).ToArray();

        var body = MapiHttpTransport.StripMetaTags(payload, out var tags);

        tags.Should().Equal("PROCESSING", "DONE");
        body.Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public void ReadStream_ByteCountEscapesAbove0xBABE()
    {
        var small = new RopReader(RopMessage.BuildReadStream(4, 24 * 1024));
        small.UInt8(); small.UInt8(); small.UInt8();
        small.UInt16().Should().Be(24 * 1024);
        small.Remaining.Should().Be(0);

        var large = new RopReader(RopMessage.BuildReadStream(4, 100_000));
        large.UInt8(); large.UInt8(); large.UInt8();
        large.UInt16().Should().Be(RopMessage.ByteCountEscape);
        large.UInt32().Should().Be(100_000);
    }

    [Fact]
    public void Autodiscover_WithoutMapiHttp_SaysSoDistinctly()
    {
        // An Exchange 2010 shape (or MAPI/HTTP disabled): the mailbox is identified, only EXCH/EXPR are offered.
        var xml = """
            <Autodiscover xmlns="http://schemas.microsoft.com/exchange/autodiscover/responseschema/2006">
              <Response xmlns="http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a">
                <User>
                  <DisplayName>Test User</DisplayName>
                  <LegacyDN>/o=Example/ou=Exchange Administrative Group (FYDIBOHF23SPDLT)/cn=Recipients/cn=abc-Test User</LegacyDN>
                </User>
                <Account>
                  <Protocol><Type>EXCH</Type><Server>EX01</Server></Protocol>
                  <Protocol><Type>EXPR</Type><Server>mail.example.com</Server></Protocol>
                </Account>
              </Response>
            </Autodiscover>
            """;

        var act = () => MapiAutodiscover.Parse(System.Xml.Linq.XDocument.Parse(xml));

        act.Should().Throw<MapiNotAdvertisedException>("the fallback to EWS keys on this exception, not on a generic format error");
    }

    [Fact]
    public void Autodiscover_ParsesMapiHttpProtocolAndLegacyDn()
    {
        var xml = """
            <Autodiscover xmlns="http://schemas.microsoft.com/exchange/autodiscover/responseschema/2006">
              <Response xmlns="http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a">
                <User>
                  <DisplayName>Test User</DisplayName>
                  <LegacyDN>/o=Example/ou=Exchange Administrative Group (FYDIBOHF23SPDLT)/cn=Recipients/cn=abc-Test User</LegacyDN>
                </User>
                <Account>
                  <Protocol Type="mapiHttp" Version="1">
                    <MailStore>
                      <InternalUrl>https://mail.internal/mapi/emsmdb/?MailboxId=11111111-2222-3333-4444-555555555555@example.com</InternalUrl>
                      <ExternalUrl>https://mail.example.com/mapi/emsmdb/?MailboxId=11111111-2222-3333-4444-555555555555@example.com</ExternalUrl>
                    </MailStore>
                    <AddressBook>
                      <ExternalUrl>https://mail.example.com/mapi/nspi/?MailboxId=11111111-2222-3333-4444-555555555555@example.com</ExternalUrl>
                    </AddressBook>
                  </Protocol>
                  <Protocol><Type>EXCH</Type><Server>EX01</Server></Protocol>
                </Account>
              </Response>
            </Autodiscover>
            """;

        var info = MapiAutodiscover.Parse(System.Xml.Linq.XDocument.Parse(xml));

        info.LegacyDn.Should().StartWith("/o=Example/");
        info.DisplayName.Should().Be("Test User");
        info.MailStoreUrl.Host.Should().Be("mail.example.com", "the external URL is preferred");
        info.MailStoreUrl.Query.Should().Contain("MailboxId=");
        info.AddressBookUrl!.AbsolutePath.Should().Be("/mapi/nspi/");
    }

    [Fact]
    public void Autodiscover_NamesTheArchiveAndPublicFolderMailboxes()
    {
        var xml = """
            <Autodiscover><Response><User><LegacyDN>/o=x/cn=y</LegacyDN></User>
            <Account>
              <Protocol Type="mapiHttp"><MailStore><ExternalUrl>https://mail.example.com/mapi/emsmdb/?MailboxId=1@example.com</ExternalUrl></MailStore></Protocol>
              <Protocol><Type>EXCH</Type><Server>EX01</Server></Protocol>
              <AlternativeMailbox><Type>Delegate</Type><SmtpAddress>shared@example.com</SmtpAddress></AlternativeMailbox>
              <AlternativeMailbox><Type>Archive</Type><DisplayName>Online Archive - Test</DisplayName><LegacyDN>/o=x/cn=archive</LegacyDN><SmtpAddress>guid-archive@example.com</SmtpAddress></AlternativeMailbox>
              <PublicFolderInformation><SmtpAddress>pfmailbox@example.com</SmtpAddress></PublicFolderInformation>
            </Account></Response></Autodiscover>
            """;

        var info = MapiAutodiscover.Parse(System.Xml.Linq.XDocument.Parse(xml));

        info.ArchiveSmtpAddress.Should().Be("guid-archive@example.com");
        info.ArchiveLegacyDn.Should().Be("/o=x/cn=archive");
        info.PublicFolderSmtpAddress.Should().Be("pfmailbox@example.com");

        var plain = """
            <Autodiscover><Response><User><LegacyDN>/o=x/cn=y</LegacyDN></User>
            <Account><Protocol Type="mapiHttp"><MailStore><ExternalUrl>https://m/mapi/emsmdb/</ExternalUrl></MailStore></Protocol></Account></Response></Autodiscover>
            """;
        var none = MapiAutodiscover.Parse(System.Xml.Linq.XDocument.Parse(plain));
        none.ArchiveSmtpAddress.Should().BeNull();
        none.PublicFolderSmtpAddress.Should().BeNull();
    }

    [Fact]
    public void Logon_PublicStore_HasNoEssdnAndParsesThePublicResponse()
    {
        var rop = RopLogon.BuildRequest("/o=x/cn=y", publicStore: true);
        rop[3].Should().Be(0, "LogonFlags without Private");
        BitConverter.ToUInt32(rop, 4).Should().Be(RopLogon.OpenFlagUsePerMdbReplidMapping | RopLogon.OpenFlagPublic);
        BitConverter.ToUInt16(rop, 12).Should().Be(0, "EssdnSize");
        rop.Length.Should().Be(14);

        var response = new RopWriter();
        response.UInt8(RopLogon.RopId); response.UInt8(0); response.UInt32(0);
        response.UInt8(0);                                          // LogonFlags: public
        for (var i = 0; i < 13; i++) response.UInt64((ulong)(0x100 + i));
        response.UInt16(7); response.Bytes(Guid.Empty.ToByteArray()); response.Bytes(Guid.NewGuid().ToByteArray());
        var parsed = RopLogon.ParseResponse(response.ToArray());
        parsed.IsPublicStore.Should().BeTrue();
        parsed.PublicIpmSubtreeFolderId.Should().Be(0x101);
        parsed.ReplId.Should().Be(7);

        var privateRop = RopLogon.BuildRequest("/o=x/cn=y");
        privateRop[3].Should().Be((byte)RopLogon.LogonFlags.Private);
        BitConverter.ToUInt16(privateRop, 12).Should().Be((ushort)("/o=x/cn=y".Length + 1));
    }

    /// <summary>
    /// A hierarchy row as Exchange returns it: a FLAGGED PropertyRow (0x01), because the deep table
    /// carries an error cell for anything a folder lacks. Live capture, reduced: the Calendar row from
    /// testing@ with a NOT_FOUND cell in the middle, which is exactly what tripped the first version
    /// (it asked for PidTagEntryId as a column; Exchange answers MAPI_E_NOT_FOUND for that).
    /// </summary>
    [Fact]
    public void HierarchyRow_FlaggedRowWithErrorCell_Parses()
    {
        uint[] columns = [PropertyTags.FolderId, PropertyTags.ParentFolderId, PropertyTags.DisplayName, PropertyTags.ContainerClass, PropertyTags.EntryId, PropertyTags.Subfolders, PropertyTags.ContentCount, PropertyTags.ContentUnreadCount];

        // 15 02 00000000 | 00 1b00 | 01 | 00 0100000000000d01 | 00 0100000000000801 | 00 "Calendar" | 00 "IPF.Appointment" | 0a 0f010480 | 00 01 | 00 02000000 | 00 00000000
        var writer = new RopWriter();
        writer.UInt8(RopFolder.RopQueryRows); writer.UInt8(2); writer.UInt32(0);
        writer.UInt8(0); writer.UInt16(1);
        writer.UInt8(0x01);                                                          // flagged row
        writer.UInt8(0x00); writer.UInt64(0x0D01000000000001);
        writer.UInt8(0x00); writer.UInt64(0x0801000000000001);
        writer.UInt8(0x00); writer.UnicodeZ("Calendar");
        writer.UInt8(0x00); writer.UnicodeZ("IPF.Appointment");
        writer.UInt8(0x0A); writer.UInt32(MapiRopException.NotFound);                // EntryId: error cell
        writer.UInt8(0x00); writer.UInt8(1);
        writer.UInt8(0x00); writer.UInt32(2);
        writer.UInt8(0x00); writer.UInt32(0);

        var row = RopFolder.ParseQueryRows(new RopReader(writer.ToArray()), columns).Single();

        row[0].AsUInt64.Should().Be(0x0D01000000000001);
        row[2].AsString.Should().Be("Calendar");
        row[4].Error.Should().Be(MapiRopException.NotFound);
        row[5].AsBoolean.Should().BeTrue();
        row[6].AsUInt32.Should().Be(2);
    }

    /// <summary>
    /// A hierarchy row with the real column set (no entry id), and the id conversion that replaces it:
    /// the EntryID built from a long-term id has the MS-OXCDATA 2.2.4.1 shape, 46 bytes.
    /// </summary>
    [Fact]
    public void HierarchyRow_AndConstructedEntryId()
    {
        var writer = new RopWriter();
        writer.UInt8(RopFolder.RopQueryRows); writer.UInt8(2); writer.UInt32(0);
        writer.UInt8(0); writer.UInt16(1);
        writer.UInt8(0x00);
        writer.UInt64(0x0C01000000000001);
        writer.UInt64(0x0801000000000001);
        writer.UnicodeZ("Inbox");
        writer.UnicodeZ("IPF.Note");
        writer.UInt8(1);
        writer.UInt32(1200);
        writer.UInt32(26);

        var folder = MapiFolderOperations.ToFolderInfo(RopFolder.ParseQueryRows(new RopReader(writer.ToArray()), PropertyTags.HierarchyColumns).Single())!;
        folder.DisplayName.Should().Be("Inbox");
        folder.IsMailFolder.Should().BeTrue();
        folder.UnreadCount.Should().Be(26);
        folder.EntryId.Should().BeNull("entry ids come from RopLongTermIdFromId, not the table");

        // RopLongTermIdFromId response: header, 22-byte long-term id, 2-byte padding; twice, batched.
        var dbGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var response = new RopWriter();
        for (var i = 0; i < 2; i++)
        {
            response.UInt8(RopIds.RopLongTermIdFromId); response.UInt8(0); response.UInt32(0);
            response.Bytes(dbGuid.ToByteArray()); response.Bytes([0, 0, 0, 0, 1, (byte)(0x0C + i)]); response.UInt16(0);
        }

        var longTermIds = RopIds.ParseLongTermIdFromIdBatch(response.ToArray(), 2);
        longTermIds.Should().HaveCount(2);
        longTermIds[0].Should().HaveCount(RopIds.LongTermIdLength);

        var mailboxGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var entryId = RopIds.BuildPrivateFolderEntryId(mailboxGuid, longTermIds[0]);
        entryId.Should().HaveCount(46);
        entryId.Take(4).Should().AllBeEquivalentTo((byte)0, "Flags");
        entryId.Skip(4).Take(16).Should().Equal(mailboxGuid.ToByteArray(), "ProviderUID is the mailbox GUID");
        entryId.Skip(20).Take(2).Should().Equal(new byte[] { 0x01, 0x00 }, "FolderType private");
        entryId.Skip(22).Take(22).Should().Equal(longTermIds[0]);
    }

    /// <summary>
    /// PidTagAdditionalRenEntryIds is multi-valued binary; in a ROP buffer its count is 16 bits, and the
    /// Junk entry id is the fifth value (MS-OXOSFLD 2.2.4).
    /// </summary>
    [Fact]
    public void GetPropertiesSpecific_ReadsMultipleBinaryWithSixteenBitCount()
    {
        uint[] tags = [PropertyTags.IpmDraftsEntryId, PropertyTags.AdditionalRenEntryIds];

        var writer = new RopWriter();
        writer.UInt8(RopProperties.RopGetPropertiesSpecific); writer.UInt8(1); writer.UInt32(0);
        writer.UInt8(0x00);                                                          // standard row
        writer.UInt16(2); writer.Bytes([0x01, 0x02]);                                // Drafts entry id
        writer.UInt16(5);                                                            // five entry ids
        for (var i = 0; i < 5; i++) { writer.UInt16(1); writer.UInt8((byte)(0x10 + i)); }

        var values = RopProperties.ParseGetPropertiesSpecific(new RopReader(writer.ToArray()), tags);

        values[0].AsBinary.Should().Equal(0x01, 0x02);
        var additional = values[1].Value as byte[][];
        additional.Should().HaveCount(5);
        additional![PropertyTags.AdditionalRenEntryIdsJunkIndex].Should().Equal(0x14);
    }

    [Fact]
    public void MergeHandles_KeepsSentSlotsTheServerDidNotFill()
    {
        var merged = MapiSession.MergeHandles([1, 2, RopExecute.NullHandle], [RopExecute.NullHandle, RopExecute.NullHandle, 3], slots: 5);

        merged.Should().Equal(1, 2, 3, RopExecute.NullHandle, RopExecute.NullHandle);
    }
}
