using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Contacts;
using Xunit;

namespace Wino.Core.Tests.Models;

/// <summary>
/// Covers <see cref="RecipientSuggestionMerge.Merge"/>, the pure merge of local-contact and GAL recipient
/// suggestions used by To/Cc/Bcc and attendee autocomplete. Local contacts win on a duplicate address.
/// </summary>
public class RecipientSuggestionMergeTests
{
    private static AccountContact Contact(string address, string? name = null)
        => new() { Address = address, Name = name ?? address };

    [Fact]
    public void Merge_PutsLocalFirstThenDirectory()
    {
        var local = new List<AccountContact> { Contact("a@x.com", "Local A") };
        var gal = new List<AccountContact> { Contact("b@x.com", "Gal B") };

        var merged = RecipientSuggestionMerge.Merge(local, gal);

        merged.Should().HaveCount(2);
        merged[0].Address.Should().Be("a@x.com");
        merged[1].Address.Should().Be("b@x.com");
    }

    [Fact]
    public void Merge_DropsDirectoryDuplicateOfLocal_CaseInsensitive_LocalWins()
    {
        var local = new List<AccountContact> { Contact("Dup@X.com", "Local Dup") };
        var gal = new List<AccountContact> { Contact("dup@x.com", "Gal Dup"), Contact("c@x.com", "Gal C") };

        var merged = RecipientSuggestionMerge.Merge(local, gal);

        merged.Should().HaveCount(2);
        merged[0].Name.Should().Be("Local Dup");
        merged[1].Address.Should().Be("c@x.com");
    }

    [Fact]
    public void Merge_DedupesWithinDirectory()
    {
        var gal = new List<AccountContact> { Contact("d@x.com", "First"), Contact("d@x.com", "Second") };

        var merged = RecipientSuggestionMerge.Merge(Array.Empty<AccountContact>(), gal);

        merged.Should().ContainSingle().Which.Name.Should().Be("First");
    }

    [Fact]
    public void Merge_EmptyDirectory_ReturnsLocalOnly()
    {
        var local = new List<AccountContact> { Contact("a@x.com") };

        RecipientSuggestionMerge.Merge(local, Array.Empty<AccountContact>()).Should().ContainSingle().Which.Address.Should().Be("a@x.com");
    }

    [Fact]
    public void Merge_EmptyLocal_ReturnsDirectoryOnly()
    {
        var gal = new List<AccountContact> { Contact("b@x.com") };

        RecipientSuggestionMerge.Merge(Array.Empty<AccountContact>(), gal).Should().ContainSingle().Which.Address.Should().Be("b@x.com");
    }

    [Fact]
    public void Merge_ToleratesNullInputs()
    {
        RecipientSuggestionMerge.Merge(null, null).Should().BeEmpty();
        RecipientSuggestionMerge.Merge(null, new List<AccountContact> { Contact("b@x.com") }).Should().ContainSingle();
    }
}
