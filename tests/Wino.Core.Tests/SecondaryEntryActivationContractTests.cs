using FluentAssertions;
using Wino.Core.Activation;
using Wino.Core.Domain.Enums;
using Xunit;

namespace Wino.Core.Tests;

public sealed class SecondaryEntryActivationContractTests
{
    [Theory]
    [InlineData("--wino-calendar", WinoApplicationMode.Calendar)]
    [InlineData("--wino-contacts", WinoApplicationMode.Contacts)]
    [InlineData("--wino-people", WinoApplicationMode.Contacts)]
    public void LaunchClassification_AcceptsCalendarPeopleAndLegacyContactsTokens(
        string argument,
        WinoApplicationMode expectedMode)
    {
        var created = SecondaryEntryActivationContract.TryCreateLaunch(argument, null, null, out var activation);

        created.Should().BeTrue();
        activation!.Kind.Should().Be(PendingBootstrapActivationKind.Launch);
        activation.Mode.Should().Be(expectedMode);
    }

    [Theory]
    [InlineData("webcal://example.com/event.ics", true)]
    [InlineData("webcals://example.com/event.ics", true)]
    [InlineData("mailto:person@example.com", false)]
    public void ProtocolClassification_PreservesOnlyExistingCalendarProtocols(string value, bool expected)
    {
        SecondaryEntryActivationContract.TryCreateProtocol(new Uri(value), out var activation)
            .Should().Be(expected);

        if (expected)
            activation!.Mode.Should().Be(WinoApplicationMode.Calendar);
    }

    [Fact]
    public void FileClassification_UsesFirstSupportedTypeAndPreservesItsActivationOrder()
    {
        var created = SecondaryEntryActivationContract.TryCreateFiles(
            ["ignored.txt", "first.vcf", "event.ics", "FIRST.VCF", "second.vcf"],
            out var activation);

        created.Should().BeTrue();
        activation!.Kind.Should().Be(PendingBootstrapActivationKind.File);
        activation.Mode.Should().Be(WinoApplicationMode.Contacts);
        activation.FilePaths.Should().Equal("first.vcf", "second.vcf");
    }

    [Theory]
    [InlineData("invite.ics", WinoApplicationMode.Calendar)]
    [InlineData("person.vcf", WinoApplicationMode.Contacts)]
    public void FileRouting_MapsAssociationsToTheirCreateModes(string path, WinoApplicationMode expectedMode)
    {
        SecondaryEntryActivationContract.TryResolveFileMode(path, out var mode).Should().BeTrue();
        mode.Should().Be(expectedMode);
    }

    [Fact]
    public void PendingActivationSerialization_RoundTripsEveryField()
    {
        var createdAt = new DateTimeOffset(2026, 8, 25, 12, 30, 0, TimeSpan.Zero);
        var original = new PendingBootstrapActivation
        {
            Kind = PendingBootstrapActivationKind.File,
            Mode = WinoApplicationMode.Contacts,
            LaunchArguments = "--wino-contacts",
            TileId = "ContactsApp",
            ProtocolUri = "webcal://example.com/event.ics",
            FilePaths = ["first.vcf", "second.vcf"],
            CreatedAtUtc = createdAt
        };

        var serialized = SecondaryEntryActivationContract.Serialize(original);
        var deserialized = SecondaryEntryActivationContract.TryDeserialize(
            serialized.ToDictionary(pair => pair.Key, pair => (string?)pair.Value),
            out var result);

        deserialized.Should().BeTrue();
        result.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void PendingActivationDeserialization_RejectsInvalidOrEmptyFilePayload()
    {
        var serialized = SecondaryEntryActivationContract.Serialize(new PendingBootstrapActivation
        {
            Kind = PendingBootstrapActivationKind.File,
            Mode = WinoApplicationMode.Calendar,
            FilePaths = []
        });

        SecondaryEntryActivationContract.TryDeserialize(
            serialized.ToDictionary(pair => pair.Key, pair => (string?)pair.Value),
            out _).Should().BeFalse();
    }
}
