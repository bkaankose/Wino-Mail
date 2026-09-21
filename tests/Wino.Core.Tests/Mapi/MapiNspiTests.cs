using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Synchronizers.Mapi;
using Wino.Mapi;
using Wino.Mapi.AddressBook;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>NSPI over MAPI/HTTP (MS-OXCMAPIHTTP 2.2.5): request bodies and the AddressBookPropertyRow codec.</summary>
public class MapiNspiTests
{
    [Fact]
    public void Stat_IsAnsiGalAtTableStart()
    {
        var stat = NspiClient.Stat();
        stat.Should().HaveCount(36);
        BitConverter.ToUInt32(stat, 24).Should().Be(1252, "an ANSI page: NspiBind refuses CP_WINUNICODE with MAPI_E_UNKNOWN_CPID");
        BitConverter.ToUInt32(stat, 28).Should().Be(1033);
        stat.Take(24).Should().AllBeEquivalentTo((byte)0, "GAL container, beginning of table, no deltas");
    }

    [Fact]
    public void Bind_AndUnbind_Bodies()
    {
        var bind = NspiClient.BuildBind();
        bind.Should().HaveCount(4 + 1 + 36 + 4);
        bind[4].Should().Be(1, "HasState");
        BitConverter.ToUInt32(bind, bind.Length - 4).Should().Be(0, "no auxiliary buffer");

        NspiClient.BuildUnbind().Should().Equal(new byte[8]);
    }

    [Fact]
    public void GetMatches_CarriesAnrRestrictionAndColumns()
    {
        var body = NspiClient.BuildGetMatches("mat", [PropertyTags.DisplayName, PropertyTags.SmtpAddress], 50, NspiClient.FilterKind.AnrUnicode);
        var r = new RopReader(body);
        r.UInt32().Should().Be(0, "Reserved");
        r.UInt8().Should().Be(1, "HasState");
        r.Bytes(36);
        r.UInt8().Should().Be(0, "HasMinimalIds");
        r.UInt32().Should().Be(0, "InterfaceOptionFlags");
        r.UInt8().Should().Be(1, "HasFilter");
        r.UInt8().Should().Be(0x04, "RES_PROPERTY");
        r.UInt8().Should().Be(0x04, "RELOP_EQ");
        r.UInt32().Should().Be(NspiClient.Anr);
        r.UInt8().Should().Be(0xFF, "the nullable property value is present");
        r.UInt32().Should().Be(NspiClient.Anr, "the tagged value repeats the tag");
        r.UInt8().Should().Be(0xFF, "HasValue");
        r.UnicodeZ().Should().Be("mat");
        r.UInt8().Should().Be(0, "HasPropertyName");
        r.UInt32().Should().Be(50, "Rows");
        r.UInt8().Should().Be(1, "HasColumns");
        r.UInt32().Should().Be(2);
        r.UInt32().Should().Be(PropertyTags.DisplayName);
        r.UInt32().Should().Be(PropertyTags.SmtpAddress);
        r.UInt32().Should().Be(0, "AuxiliaryBufferSize");
        r.Remaining.Should().Be(0);

        var prefix = new RopReader(NspiClient.BuildGetMatches("mat", [PropertyTags.DisplayName], 10, NspiClient.FilterKind.DisplayNamePrefix));
        prefix.Bytes(4 + 1 + 36 + 1 + 4 + 1);
        prefix.UInt8().Should().Be(0x03, "RES_CONTENT");
        prefix.UInt16().Should().Be(0x0002, "FL_PREFIX");
        prefix.UInt16().Should().Be(0x0001, "FL_IGNORECASE");
        prefix.UInt32().Should().Be(PropertyTags.DisplayName);
        prefix.UInt8().Should().Be(0xFF);
        prefix.UInt32().Should().Be(PropertyTags.DisplayName);
        prefix.UInt8().Should().Be(0xFF);
        prefix.UnicodeZ().Should().Be("mat");
    }

    [Fact]
    public void GetMatchesResponse_DecodesPlainAndFlaggedRows()
    {
        uint[] columns = [PropertyTags.DisplayName, PropertyTags.SmtpAddress, NspiClient.DisplayType];
        var w = new RopWriter();
        w.UInt32(0); w.UInt32(0x00040380);                  // StatusCode, ErrorCode: partial completion is fine
        w.UInt8(1); w.Bytes(NspiClient.Stat());             // HasState + STAT
        w.UInt8(1); w.UInt32(2); w.UInt32(0x10); w.UInt32(0x11);   // minimal ids
        w.UInt8(1);                                          // HasColsAndRows
        w.UInt32(3); foreach (var c in columns) w.UInt32(c);
        w.UInt32(2);                                         // RowCount
        // Row 0: plain values.
        w.UInt8(0x00);
        w.UInt8(0xFF); w.UnicodeZ("Matthew Johnson");
        w.UInt8(0xFF); w.UnicodeZ("matt@example.com");
        w.UInt32(0);
        // Row 1: flagged: name present, SMTP absent, display type errored.
        w.UInt8(0x01);
        w.UInt8(0x0); w.UInt8(0xFF); w.UnicodeZ("All Staff");
        w.UInt8(0x1);
        w.UInt8(0xA); w.UInt32(0x8004010F);
        w.UInt32(0);                                         // AuxiliaryBufferSize

        var rows = NspiClient.ParseGetMatches(w.ToArray(), columns);

        rows.Should().HaveCount(2);
        rows[0][0].AsString.Should().Be("Matthew Johnson");
        rows[0][1].AsString.Should().Be("matt@example.com");
        rows[0][2].AsUInt32.Should().Be(0);
        rows[1][0].AsString.Should().Be("All Staff");
        rows[1][1].IsPresent.Should().BeFalse();
        rows[1][2].Error.Should().Be(0x8004010F);
    }

    [Fact]
    public void GetMatchesResponse_NoRowsAndErrors()
    {
        var w = new RopWriter();
        w.UInt32(0); w.UInt32(0); w.UInt8(0); w.UInt8(0); w.UInt8(0); w.UInt32(0);
        NspiClient.ParseGetMatches(w.ToArray(), [PropertyTags.DisplayName]).Should().BeEmpty();

        var failed = new RopWriter();
        failed.UInt32(0); failed.UInt32(0x80004005); failed.UInt8(0); failed.UInt8(0); failed.UInt8(0); failed.UInt32(0);
        var act = () => NspiClient.ParseGetMatches(failed.ToArray(), [PropertyTags.DisplayName]);
        act.Should().Throw<MapiRopException>();
    }

    [Fact]
    public void GalRows_MapToSmtpContactsOnly_Deduped()
    {
        PropertyValue[] Row(string name, string? smtp, string? email, string? type, string? company = null, string? title = null, string? department = null) =>
        [
            PropertyValue.Of(PropertyTags.DisplayName, name), smtp is null ? PropertyValue.Absent(PropertyTags.SmtpAddress) : PropertyValue.Of(PropertyTags.SmtpAddress, smtp),
            PropertyValue.Of(PropertyTags.EmailAddress, email), PropertyValue.Of(PropertyTags.AddressType, type),
            PropertyValue.Of(NspiClient.CompanyName, company), PropertyValue.Of(NspiClient.Title, title),
            department is null ? PropertyValue.Absent(NspiClient.DepartmentName) : PropertyValue.Of(NspiClient.DepartmentName, department),
            PropertyValue.Of(NspiClient.DisplayType, 0u),
        ];

        var rows = new List<PropertyValue[]>
        {
            Row("Matthew Johnson", "matt@example.com", "/o=X/ou=Y/cn=Recipients/cn=matt", "EX", "MTEC", "Engineer", "Ops"),
            Row("Matt Again", "MATT@example.com", null, "EX"),
            Row("Contact", null, "ext@example.com", "SMTP"),
            Row("Legacy only", null, "/o=X/cn=legacy", "EX"),
            Row("", "blank@example.com", null, "EX"),
        };

        var accountId = Guid.NewGuid();
        var contacts = MapiExchangeSynchronizer.MapGalRows(rows, 10, accountId);

        contacts.Select(c => c.Address).Should().Equal("matt@example.com", "ext@example.com", "blank@example.com");
        contacts[0].Name.Should().Be("Matthew Johnson");
        contacts[0].CompanyName.Should().Be("MTEC");
        contacts[0].JobTitle.Should().Be("Engineer");
        contacts[0].Department.Should().Be("Ops");
        contacts[0].MailAccountId.Should().Be(accountId);
        contacts[0].SourceKind.Should().Be(ContactSourceKind.Exchange);
        contacts[2].Name.Should().Be("blank@example.com", "no display name falls back to the address");
        MapiExchangeSynchronizer.MapGalRows(rows, 1).Should().HaveCount(1);
    }
}
