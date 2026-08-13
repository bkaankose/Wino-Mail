using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class MailFilterServiceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private MailFilterService _service = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _service = new MailFilterService(_databaseService);
    }

    public Task DisposeAsync() => _databaseService.DisposeAsync().AsTask();

    [Theory]
    [InlineData(MailFilterActionType.Move)]
    [InlineData(MailFilterActionType.Archive)]
    [InlineData(MailFilterActionType.MoveToJunk)]
    [InlineData(MailFilterActionType.MarkAsNotJunk)]
    [InlineData(MailFilterActionType.SoftDelete)]
    [InlineData(MailFilterActionType.HardDelete)]
    public async Task CreateFilterAsync_WinoLocalExclusiveActionWithAnotherAction_Throws(
        MailFilterActionType exclusiveAction)
    {
        var filter = CreateFilter(MailFilterManagementType.WinoLocal);
        filter.Actions =
        [
            new()
            {
                Type = exclusiveAction,
                TargetRemoteFolderId = exclusiveAction == MailFilterActionType.HardDelete
                    ? null
                    : "target-folder"
            },
            new() { Type = MailFilterActionType.SetFlag }
        ];

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateFilterAsync(filter));

        Assert.Contains("cannot combine", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFilterAsync_WinoLocalStateActions_AllowsCombination()
    {
        var filter = CreateFilter(MailFilterManagementType.WinoLocal);
        filter.Actions =
        [
            new() { Type = MailFilterActionType.SetFlag },
            new() { Type = MailFilterActionType.MarkRead }
        ];

        var created = await _service.CreateFilterAsync(filter);

        Assert.Equal(2, created.Actions.Count);
    }

    [Fact]
    public async Task CreateFilterAsync_ProviderMoveAndStateAction_AllowsCombination()
    {
        var filter = CreateFilter(MailFilterManagementType.Provider);
        filter.SourceRemoteFolderId = null;
        filter.Actions =
        [
            new()
            {
                Type = MailFilterActionType.Move,
                TargetRemoteFolderId = "target-folder"
            },
            new() { Type = MailFilterActionType.SetFlag }
        ];

        var created = await _service.CreateFilterAsync(filter);

        Assert.Equal(2, created.Actions.Count);
    }

    private static MailFilter CreateFilter(MailFilterManagementType managementType)
        => new()
        {
            MailAccountId = Guid.NewGuid(),
            Name = "Test filter",
            ManagementType = managementType,
            SourceRemoteFolderId = "inbox",
            Conditions = [],
            Actions = []
        };
}
