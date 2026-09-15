using Wino.Mapi.Rops;
using Wino.Mapi.Wire;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>The recipient table carried by a RopOpenMessage response (MS-OXCDATA 2.8.3.2 rows).</summary>
public class MapiRecipientRowTests
{
    [Fact]
    public void OpenMessageResponse_ParsesX500AndSmtpRecipientsWithProperties()
    {
        uint[] columns = [PropertyTags.SmtpAddress, PropertyTags.RecipientTrackStatus, PropertyTags.RecipientFlags];

        var w = new RopWriter();
        w.UInt8(RopMessage.RopOpenMessage); w.UInt8(1); w.UInt32(0);
        w.UInt8(0);                                          // HasNamedProperties
        w.UInt8(0x00);                                       // SubjectPrefix: none
        w.UInt8(0x03); w.AsciiZ("Standup");                  // NormalizedSubject as UnicodeReduced (one byte per char), as the server sends it
        w.UInt16(2);                                         // RecipientCount
        w.UInt16((ushort)columns.Length); foreach (var c in columns) w.UInt32(c);
        w.UInt8(2);                                          // RowCount

        // Row 1: the organizer, X500DN (the spec's worked example: S, D, Type=X500DN, I, U; wire bytes 51 06), To.
        var r1 = new RopWriter();
        r1.UInt16(0x0651);                                   // bytes 51 06 on the wire, as the example prints them
        r1.UInt8(0x5A); r1.UInt8(0x00); r1.AsciiZ("/o=Org/ou=Ex/cn=Recipients/cn=user2");
        r1.UnicodeZ("user2");                                // DisplayName (D)
        r1.UnicodeZ("user2");                                // SimpleDisplayName (I)
        r1.UInt16(3); r1.UInt8(0x00);                        // standard property row
        r1.UnicodeZ("user2@example.com"); r1.UInt32(1); r1.UInt32(0x3);
        var row1 = r1.ToArray();
        w.UInt8(0x01); w.UInt16(0x0FFF); w.UInt16(0); w.UInt16((ushort)row1.Length); w.Bytes(row1);

        // Row 2: an SMTP attendee (E, D, Type=SMTP, U), Cc, accepted, flagged row lacking the flags column.
        var r2 = new RopWriter();
        r2.UInt16(0x0003 | 0x0008 | 0x0010 | 0x0200);
        r2.UnicodeZ("guest@example.org"); r2.UnicodeZ("Guest");
        r2.UInt16(3); r2.UInt8(0x01);
        r2.UInt8(0x00); r2.UnicodeZ("guest@example.org"); r2.UInt8(0x00); r2.UInt32(3); r2.UInt8(0x0A); r2.UInt32(0x8004010F);
        var row2 = r2.ToArray();
        w.UInt8(0x02); w.UInt16(0x0FFF); w.UInt16(0); w.UInt16((ushort)row2.Length); w.Bytes(row2);

        var response = RopMessage.ParseOpenMessageResponse(new RopReader(w.ToArray()));

        response.NormalizedSubject.Should().Be("Standup");
        response.Recipients.Should().HaveCount(2);
        response.Recipients[0].SmtpAddress.Should().Be("user2@example.com");
        response.Recipients[0].Name.Should().Be("user2");
        response.Recipients[0].IsOrganizer.Should().BeTrue();
        response.Recipients[0].IsOptional.Should().BeFalse();
        response.Recipients[1].SmtpAddress.Should().Be("guest@example.org");
        response.Recipients[1].Name.Should().Be("Guest");
        response.Recipients[1].IsOptional.Should().BeTrue();
        response.Recipients[1].TrackStatus.Should().Be(3);
        response.Recipients[1].IsOrganizer.Should().BeFalse();
    }
}
