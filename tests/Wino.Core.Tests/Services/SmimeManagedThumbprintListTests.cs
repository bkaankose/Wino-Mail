using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class SmimeManagedThumbprintListTests
{
    private const string First = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678";
    private const string Second = "00112233445566778899AABBCCDDEEFF00112233";

    [Fact]
    public void Add_NormalizesSeparatorsAndCase()
    {
        var list = new SmimeManagedThumbprintList();

        list.Add("a1:b2:c3:d4 e5f6-0718293a4b5c6d7e8f9012345678", SmimeCertificatePurpose.Personal).Should().BeTrue();

        list.Entries.Should().ContainSingle().Which.Should().Be(new SmimeManagedThumbprint(First, SmimeCertificatePurpose.Personal));
        list.Contains(First.ToLowerInvariant(), SmimeCertificatePurpose.Personal).Should().BeTrue();
    }

    [Fact]
    public void Add_SameThumbprintAndPurpose_IsIgnored()
    {
        var list = new SmimeManagedThumbprintList();

        list.Add(First, SmimeCertificatePurpose.Personal).Should().BeTrue();
        list.Add(First.ToLowerInvariant(), SmimeCertificatePurpose.Personal).Should().BeFalse();

        list.Count.Should().Be(1);
    }

    [Fact]
    public void SameCertificate_CanBeListedForBothPurposes_AndRemovalKeepsTheOther()
    {
        var list = new SmimeManagedThumbprintList();
        list.Add(First, SmimeCertificatePurpose.Personal);
        list.Add(First, SmimeCertificatePurpose.Recipient);

        list.Remove(First, SmimeCertificatePurpose.Personal).Should().BeTrue();

        list.Contains(First, SmimeCertificatePurpose.Personal).Should().BeFalse();
        list.Contains(First, SmimeCertificatePurpose.Recipient).Should().BeTrue();
        list.IsReferenced(First).Should().BeTrue();

        list.Remove(First, SmimeCertificatePurpose.Recipient).Should().BeTrue();
        list.IsReferenced(First).Should().BeFalse();
    }

    [Fact]
    public void Remove_UnknownOrInvalidThumbprint_ReturnsFalse()
    {
        var list = new SmimeManagedThumbprintList();
        list.Add(First, SmimeCertificatePurpose.Personal);

        list.Remove(Second, SmimeCertificatePurpose.Personal).Should().BeFalse();
        list.Remove("not-a-thumbprint", SmimeCertificatePurpose.Personal).Should().BeFalse();
        list.Remove(First, SmimeCertificatePurpose.Recipient).Should().BeFalse();

        list.Count.Should().Be(1);
    }

    [Fact]
    public void GetThumbprints_FiltersByPurpose_InInsertionOrder()
    {
        var list = new SmimeManagedThumbprintList();
        list.Add(Second, SmimeCertificatePurpose.Personal);
        list.Add(First, SmimeCertificatePurpose.Recipient);
        list.Add(First, SmimeCertificatePurpose.Personal);

        list.GetThumbprints(SmimeCertificatePurpose.Personal).Should().Equal(Second, First);
        list.GetThumbprints(SmimeCertificatePurpose.Recipient).Should().Equal(First);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("XYZ")]
    [InlineData("ABC")]
    public void Normalize_RejectsValuesThatAreNotHexPairs(string value)
    {
        var normalize = () => SmimeManagedThumbprintList.Normalize(value);

        normalize.Should().Throw<ArgumentException>();
        SmimeManagedThumbprintList.TryNormalize(value, out var normalized).Should().BeFalse();
        normalized.Should().BeEmpty();
    }

    [Fact]
    public void SerializeAndParse_RoundTripsEntries()
    {
        var list = new SmimeManagedThumbprintList();
        list.Add(First, SmimeCertificatePurpose.Personal);
        list.Add(Second, SmimeCertificatePurpose.Recipient);
        list.Add(First, SmimeCertificatePurpose.Recipient);

        var restored = SmimeManagedThumbprintList.Parse(list.Serialize());

        restored.Entries.Should().Equal(list.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Personal A1B2C3D4E5F60718293A4B5C6D7E8F9012345678")]
    [InlineData("some other format v9\nPersonal A1B2C3D4E5F60718293A4B5C6D7E8F9012345678")]
    public void Parse_WithoutTheVersionHeader_ReturnsAnEmptyList(string? text)
        => SmimeManagedThumbprintList.Parse(text).Count.Should().Be(0);

    [Fact]
    public void Parse_SkipsMalformedLinesAndDuplicates()
    {
        var text = string.Join('\n',
            "wino-smime-managed v1",
            $"Personal {First}",
            $"Personal {First.ToLowerInvariant()}",
            "Recipient nothex",
            "Unknown 00112233445566778899AABBCCDDEEFF00112233",
            "0 00112233445566778899AABBCCDDEEFF00112233",
            "Recipient",
            "",
            $"Recipient {Second}\r");

        var list = SmimeManagedThumbprintList.Parse(text);

        list.Entries.Should().Equal(
            new SmimeManagedThumbprint(First, SmimeCertificatePurpose.Personal),
            new SmimeManagedThumbprint(Second, SmimeCertificatePurpose.Recipient));
    }
}
