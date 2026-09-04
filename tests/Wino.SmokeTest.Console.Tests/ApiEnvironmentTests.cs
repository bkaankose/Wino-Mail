using Wino.SmokeTest.ConsoleApp;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.Contracts.Intelligence;
using Xunit;

namespace Wino.SmokeTest.Console.Tests;

public sealed class ApiEnvironmentTests
{
    [Fact]
    public void Local_UsesLocalhostEndpoint()
    {
        var uri = Program.GetApiUri(ApiEnvironment.Local);

        Assert.Equal(new Uri("https://localhost:7204/"), uri);
    }

    [Fact]
    public void Production_UsesProductionEndpoint()
    {
        var uri = Program.GetApiUri(ApiEnvironment.Production);

        Assert.Equal(new Uri("https://api.winomail.app/"), uri);
    }

    [Theory]
    [InlineData("https://localhost:7204/", true)]
    [InlineData("https://127.0.0.1:7204/", true)]
    [InlineData("https://api.winomail.app/", false)]
    [InlineData("https://example.test/", false)]
    public void Local_BypassesCertificatesOnlyForLoopback(string address, bool expected)
    {
        Assert.Equal(expected, Program.ShouldBypassCertificate(ApiEnvironment.Local, new Uri(address)));
    }

    [Theory]
    [InlineData("https://localhost:7204/")]
    [InlineData("https://127.0.0.1:7204/")]
    [InlineData("https://api.winomail.app/")]
    public void Production_NeverBypassesCertificates(string address)
    {
        Assert.False(Program.ShouldBypassCertificate(ApiEnvironment.Production, new Uri(address)));
    }

    [Theory]
    [InlineData(MailProviderType.Outlook, true)]
    [InlineData(MailProviderType.Gmail, true)]
    [InlineData(MailProviderType.IMAP4, true)]
    [InlineData(MailProviderType.POP3, false)]
    public void AccountsWithSemanticBodySynchronizersAreSupported(MailProviderType providerType, bool expected)
    {
        Assert.Equal(expected, Program.IsSupportedAccount(new MailAccount { ProviderType = providerType }));
    }

    [Theory]
    [InlineData(null, SemanticIndexRangePreset.OneWeek)]
    [InlineData("", SemanticIndexRangePreset.OneWeek)]
    [InlineData("1", SemanticIndexRangePreset.OneWeek)]
    [InlineData("2", SemanticIndexRangePreset.OneMonth)]
    [InlineData("3", SemanticIndexRangePreset.OneYear)]
    public void RangeSelectionMapsToSupportedPresets(string? selection, SemanticIndexRangePreset expected)
    {
        Assert.Equal(expected, Program.ParseRangeSelection(selection));
    }

    [Fact]
    public void LocalImportedRevisionCountsAsExistingIntelligence()
    {
        var localState = new LocalIntelligenceMailboxState(
            Guid.NewGuid(),
            Guid.NewGuid(),
            WinoIntelligenceVersions.V1,
            Guid.NewGuid(),
            1);
        var state = new SemanticIndexAccountState(false, null, null, localState, 0, false, false, false);

        Assert.True(Program.HasIntelligence(state));
    }

    [Fact]
    public void MinimalServerStatusCountsAsExistingIntelligence()
    {
        var now = DateTimeOffset.UtcNow;
        var head = new MailboxIntelligenceHeadDto(
            Guid.NewGuid(),
            WinoIntelligenceVersions.V1,
            Guid.NewGuid(),
            1,
            1,
            1024,
            now.AddMonths(-1),
            now,
            now,
            now);
        var state = new SemanticIndexAccountState(true, head.MailboxId, head, null, 0, true, true, false);

        Assert.True(Program.HasIntelligence(state));
    }

    [Fact]
    public void WaitingMessagesCountAsIncompleteIntelligence()
    {
        var state = new SemanticIndexAccountState(true, Guid.NewGuid(), null, null, 12, true, false, false);

        Assert.True(Program.HasIncompleteIntelligence(state));
    }

    [Fact]
    public void SemanticSearchSelectionReturnsTrimmedCustomQuery()
    {
        var result = Program.ReadSemanticSearchSelection("1", () => "  my own meaning  ");

        Assert.NotNull(result);
        Assert.Equal("my own meaning", result.Query);
        Assert.False(result.UseQueryPlanner);
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("not-a-choice", "")]
    public void SemanticSearchSelectionHandlesBackAndInvalidInput(string selection, string? expectedQuery)
    {
        var result = Program.ReadSemanticSearchSelection(selection, () => throw new InvalidOperationException());

        Assert.Equal(expectedQuery, result?.Query);
    }
}
