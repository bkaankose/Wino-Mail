using FluentAssertions;
using Wino.Services.CardDav;
using Xunit;

namespace Wino.Core.Tests.CardDav;

public sealed class VCardCodecTests
{
    private readonly VCardCodec _codec = new();

    [Fact]
    public void ParseAndSerialize_PreservesUnknownGroupedProperties()
    {
        const string source = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:person-1\r\nFN:Jane Doe\r\nitem1.EMAIL;TYPE=INTERNET:jane@example.com\r\nitem1.X-ABLabel:_$!<Work>!$_\r\nX-CUSTOM;X-VALUE=kept:opaque\\,value\r\nEND:VCARD\r\n";

        var document = _codec.Parse(source);
        var contact = _codec.Project(document);
        contact.DisplayName = "Jane Q. Doe";

        _codec.Patch(document, contact);
        var serialized = _codec.Serialize(document);

        serialized.Should().Contain("item1.X-ABLabel:_$!<Work>!$_");
        serialized.Should().Contain("X-CUSTOM;X-VALUE=kept:opaque\\,value");
        serialized.Should().Contain("FN:Jane Q. Doe");
    }

    [Fact]
    public void Parse_SupportsFoldedUtf8AndRfc6868Parameters()
    {
        const string source = "BEGIN:VCARD\nVERSION:4.0\nFN:Zoë Example\nEMAIL;TYPE=\"work^'team^nprimary\":zoe@example.com\nNOTE:Long text that is \n folded\nEND:VCARD\n";

        var document = _codec.Parse(source);
        var contact = _codec.Project(document);

        contact.DisplayName.Should().Be("Zoë Example");
        contact.Notes.Should().Be("Long text that is folded");
        document.Properties.Single(property => property.Name == "EMAIL")
            .Parameters.Single().Values.Single().Should().Be("work\"team\nprimary");
    }

    [Fact]
    public void ExistingVCard3_RemainsVersion3AfterPatch()
    {
        const string source = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:person-1\r\nFN:Before\r\nN:Before;;;;\r\nEND:VCARD\r\n";
        var document = _codec.Parse(source);
        var contact = _codec.Project(document);
        contact.DisplayName = "After";

        _codec.Patch(document, contact);
        var serialized = _codec.Serialize(document);

        serialized.Should().Contain("VERSION:3.0");
        serialized.Should().NotContain("VERSION:4.0");
    }

    [Fact]
    public void Create_UsesRequestedVersionAndStableUid()
    {
        var contact = new Wino.Core.Domain.Entities.Shared.AccountContact
        {
            DisplayName = "New person"
        };

        var document = _codec.Create(contact, "4.0", "stable-uid");
        var serialized = _codec.Serialize(document);

        serialized.Should().Contain("VERSION:4.0");
        serialized.Should().Contain("UID:stable-uid");
    }
}
