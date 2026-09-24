using FluentAssertions;
using Wino.Core.Domain.Models.Contacts;
using Xunit;

namespace Wino.Core.Tests.Models;

public class RecipientAddressHeuristicsTests
{
    [Theory]
    [InlineData("a23asd21asdju12398asdf9nfg9hwe@google.com", null)]
    [InlineData("3f2a9c1e7b4d4c6e9a1f0b2c3d4e5f6a@mail.example.com", null)]
    [InlineData("3f2a9c1e-7b4d-4c6e-9a1f-0b2c3d4e5f6a@example.com", null)]
    [InlineData("x7k9q2m4p8w3z6n1v5b0@example.com", null)]
    [InlineData("reply+ABCDEFGH1234@reply.github.com", null)]
    [InlineData("bounces+12345-abcd-user=example.com@sendgrid.net", null)]
    [InlineData("noreply@accounts.google.com", "Google")]
    [InlineData("no-reply@example.com", null)]
    [InlineData("donotreply@bank.example", "Your Bank")]
    [InlineData("do-not-reply@example.com", null)]
    [InlineData("MAILER-DAEMON@mx.example.org", "Mail Delivery System")]
    [InlineData("postmaster@example.com", null)]
    [InlineData("notifications@github.com", "[owner/repo] Issue #42")]
    [InlineData("notifications@github.com", "Jane Doe")]
    [InlineData("newsletter@shop.example", "Shop News")]
    [InlineData("unsubscribe@lists.example.com", null)]
    [InlineData("support@vendor.example", "Support")]
    [InlineData("info@vendor.example", "Vendor Team")]
    [InlineData("12345@em.dropbox.com", "Dropbox")]
    [InlineData("", null)]
    [InlineData("not-an-address", null)]
    [InlineData("@example.com", null)]
    [InlineData("someone@", null)]
    public void AutomatedSenders_AreRejected(string address, string displayName)
        => RecipientAddressHeuristics.IsAutomatedAddress(address, displayName).Should().BeTrue();

    [Theory]
    [InlineData("alice@example.com", "Alice")]
    [InlineData("alice.smith@example.co.uk", "Alice Smith")]
    [InlineData("a.smith2@example.com", null)]
    [InlineData("john.doe1985@gmail.com", "John Doe")]
    [InlineData("christopher.montgomery@example.com", null)]
    [InlineData("annabelle.schwarzenegger@example.com", null)]
    [InlineData("firstname.lastname.consulting@example.com", null)]
    [InlineData("alice+work@example.com", null)]
    [InlineData("alice+shopping@example.com", null)]
    [InlineData("info@joesplumbing.example", "Joe Martinez")]
    [InlineData("mike@mail.example.com", null)]
    [InlineData("bkaankose@outlook.com", "Burak Kaan Köse")]
    [InlineData("jane.smith.2019@example.com", null)]
    public void People_AreKept(string address, string displayName)
        => RecipientAddressHeuristics.IsAutomatedAddress(address, displayName).Should().BeFalse();

    [Theory]
    [InlineData("Joe Martinez", true)]
    [InlineData("Anne-Marie O'Neil", true)]
    [InlineData("Support", false)]
    [InlineData("Acme Support", false)]
    [InlineData("GitHub Notifications", false)]
    [InlineData("[owner/repo] Issue #42", false)]
    [InlineData("Order 12345", false)]
    [InlineData("alice@example.com", false)]
    [InlineData("", false)]
    public void HumanDisplayNames_AreRecognized(string displayName, bool expected)
        => RecipientAddressHeuristics.LooksHumanDisplayName(displayName).Should().Be(expected);

    [Fact]
    public void MachineGeneratedLocalPart_WithAHumanName_IsStillKept()
    {
        // A person may genuinely use an odd address; their name is the stronger signal.
        RecipientAddressHeuristics.IsAutomatedAddress("x7k9q2m4p8w3z6n1v5b0@example.com", "Jane Doe").Should().BeFalse();
    }
}
