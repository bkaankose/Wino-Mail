using Wino.Intelligence.ConsoleApp;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.Contracts.SemanticIndex;
using Xunit;

namespace Wino.Intelligence.Console.Tests;

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
    [InlineData(MailProviderType.Gmail, false)]
    [InlineData(MailProviderType.IMAP4, false)]
    public void OnlyOutlookAccountsAreSupported(MailProviderType providerType, bool expected)
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
        var state = new SemanticIndexAccountState(false, null, null, 1, 0, false, false, false);

        Assert.True(Program.HasIntelligence(state));
    }

    [Fact]
    public void MinimalServerStatusCountsAsExistingIntelligence()
    {
        var now = DateTimeOffset.UtcNow;
        var status = new SemanticMailboxIndexStateDto(
            Guid.NewGuid(), "profile", "model", 768, now.AddMonths(-1), now, 0, 1024, 0, now);
        var state = new SemanticIndexAccountState(true, status.MailboxId, status, 0, 0, true, true, false);

        Assert.True(Program.HasIntelligence(state));
    }

    [Fact]
    public void WaitingMessagesCountAsIncompleteIntelligence()
    {
        var state = new SemanticIndexAccountState(true, Guid.NewGuid(), null, 0, 12, true, false, false);

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
