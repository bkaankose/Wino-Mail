using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Services;

public class MailFilterExecutor(
    IMailFilterService mailFilterService,
    IMailService mailService,
    IFolderService folderService,
    IWinoRequestProcessor requestProcessor,
    IWinoRequestDelegator requestDelegator) : IMailFilterExecutor
{
    private readonly ILogger _logger = Log.ForContext<MailFilterExecutor>();

    public async Task<bool> ShouldSuppressNewMessageAsync(
        Guid accountId,
        string sourceRemoteFolderId,
        MailCopy message,
        CancellationToken cancellationToken = default)
    {
        if (message == null || string.IsNullOrWhiteSpace(sourceRemoteFolderId))
            return false;

        var filters = await mailFilterService
            .GetExecutableFiltersAsync(accountId, sourceRemoteFolderId, cancellationToken)
            .ConfigureAwait(false);

        if (filters.Count == 0)
            return false;

        var folders = await folderService.GetFoldersAsync(accountId).ConfigureAwait(false);
        var availableRemoteFolderIds = folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder.RemoteFolderId))
            .Select(folder => folder.RemoteFolderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var filter in filters)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Matches(filter, message))
                continue;

            if (await mailFilterService
                .HasExecutionAsync(filter.Id, message.Id, sourceRemoteFolderId, cancellationToken)
                .ConfigureAwait(false))
            {
                continue;
            }

            var actionableActions = filter.Actions
                .Where(action => ToMailOperation(action.Type) != MailOperation.None)
                .ToList();

            if (actionableActions.Count == 0)
                continue;

            var hasMissingTarget = actionableActions.Any(action =>
                !string.IsNullOrWhiteSpace(action.TargetRemoteFolderId)
                && !availableRemoteFolderIds.Contains(action.TargetRemoteFolderId));

            if (!hasMissingTarget)
                return true;
        }

        return false;
    }

    public async Task<IReadOnlySet<string>> ProcessNewMessagesAsync(
        Guid accountId,
        IEnumerable<string> remoteMessageIds,
        CancellationToken cancellationToken = default)
    {
        var ids = remoteMessageIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];

        if (ids.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var messages = await mailService.GetMailItemsAsync(ids).ConfigureAwait(false);
        var folders = await folderService.GetFoldersAsync(accountId).ConfigureAwait(false);
        var foldersById = folders.ToDictionary(folder => folder.Id);
        var foldersByRemoteId = folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder.RemoteFolderId))
            .GroupBy(folder => folder.RemoteFolderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var requests = new List<IRequestBase>();
        var executions = new List<MailFilterExecution>();
        var suppressedIds = new HashSet<string>(StringComparer.Ordinal);
        var terminalMessageIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in messages.Where(message => message != null))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!foldersById.TryGetValue(message.FolderId, out var sourceFolder))
                continue;

            message.AssignedFolder = sourceFolder;
            if (message.AssignedAccount == null)
            {
                _logger.Warning(
                    "Skipping mail filters for message {MessageId}; account {AccountId} could not be hydrated.",
                    message.Id,
                    accountId);
                continue;
            }

            var filters = await mailFilterService
                .GetExecutableFiltersAsync(accountId, sourceFolder.RemoteFolderId, cancellationToken)
                .ConfigureAwait(false);

            foreach (var filter in filters)
            {
                if (terminalMessageIds.Contains(message.Id))
                    break;

                if (!Matches(filter, message))
                    continue;

                if (await mailFilterService
                    .HasExecutionAsync(filter.Id, message.Id, sourceFolder.RemoteFolderId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    continue;
                }

                var filterRequests = new List<IRequestBase>();
                var terminalActionQueued = false;

                foreach (var action in filter.Actions.OrderBy(action => action.Order))
                {
                    var operation = ToMailOperation(action.Type);
                    if (operation == MailOperation.None)
                        continue;

                    MailItemFolder targetFolder = null;
                    if (!string.IsNullOrWhiteSpace(action.TargetRemoteFolderId)
                        && !foldersByRemoteId.TryGetValue(action.TargetRemoteFolderId, out targetFolder))
                    {
                        _logger.Warning(
                            "Skipping mail filter {FilterId}; target folder {RemoteFolderId} no longer exists for account {AccountId}.",
                            filter.Id,
                            action.TargetRemoteFolderId,
                            accountId);
                        filterRequests.Clear();
                        break;
                    }

                    var preparation = new MailOperationPreperationRequest(
                        operation,
                        message,
                        moveTargetFolder: targetFolder,
                        ignoreHardDeleteProtection: true);
                    var prepared = await requestProcessor.PrepareRequestsAsync(preparation).ConfigureAwait(false);
                    if (prepared?.Count > 0)
                        filterRequests.AddRange(prepared);

                    terminalActionQueued |= IsTerminal(action.Type);
                }

                if (filterRequests.Count == 0)
                    continue;

                requests.AddRange(filterRequests);
                executions.Add(new MailFilterExecution
                {
                    MailFilterId = filter.Id,
                    MailAccountId = accountId,
                    RemoteMessageId = message.Id,
                    SourceRemoteFolderId = sourceFolder.RemoteFolderId,
                    State = MailFilterExecutionState.Pending
                });

                suppressedIds.Add(message.Id);
                if (terminalActionQueued)
                    terminalMessageIds.Add(message.Id);
                if (filter.StopProcessing)
                    break;
            }
        }

        if (requests.Count > 0)
        {
            await requestDelegator.ExecuteAsync(accountId, requests).ConfigureAwait(false);
            foreach (var execution in executions)
            {
                await mailFilterService.CreateExecutionAsync(execution, cancellationToken).ConfigureAwait(false);
            }
        }

        return suppressedIds;
    }

    internal static bool Matches(MailFilter filter, MailCopy message)
    {
        if (filter.Conditions.Count == 0)
            return true;

        var results = filter.Conditions
            .OrderBy(condition => condition.Order)
            .Select(condition => Matches(condition, message));
        return filter.MatchMode == MailFilterMatchMode.All ? results.All(result => result) : results.Any(result => result);
    }

    private static bool Matches(MailFilterCondition condition, MailCopy message)
    {
        if (condition.Field == MailFilterConditionField.HasAttachments)
        {
            var expected = bool.TryParse(condition.Value, out var value) && value;
            return condition.Operator == MailFilterConditionOperator.NotEquals
                ? message.HasAttachments != expected
                : message.HasAttachments == expected;
        }

        if (condition.Field == MailFilterConditionField.Importance)
        {
            var expected = Enum.TryParse<MailImportance>(condition.Value, true, out var value)
                ? value
                : MailImportance.Normal;
            return condition.Operator == MailFilterConditionOperator.NotEquals
                ? message.Importance != expected
                : message.Importance == expected;
        }

        var actual = condition.Field switch
        {
            MailFilterConditionField.FromAddress => message.FromAddress,
            MailFilterConditionField.FromName => message.FromName,
            MailFilterConditionField.Subject => message.Subject,
            MailFilterConditionField.PreviewText => message.PreviewText,
            _ => string.Empty
        } ?? string.Empty;
        var expectedText = condition.Value ?? string.Empty;

        return condition.Operator switch
        {
            MailFilterConditionOperator.Equals => string.Equals(actual, expectedText, StringComparison.OrdinalIgnoreCase),
            MailFilterConditionOperator.NotEquals => !string.Equals(actual, expectedText, StringComparison.OrdinalIgnoreCase),
            MailFilterConditionOperator.Contains => actual.Contains(expectedText, StringComparison.OrdinalIgnoreCase),
            MailFilterConditionOperator.NotContains => !actual.Contains(expectedText, StringComparison.OrdinalIgnoreCase),
            MailFilterConditionOperator.StartsWith => actual.StartsWith(expectedText, StringComparison.OrdinalIgnoreCase),
            MailFilterConditionOperator.EndsWith => actual.EndsWith(expectedText, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static MailOperation ToMailOperation(MailFilterActionType action)
        => action switch
        {
            MailFilterActionType.MarkRead => MailOperation.MarkAsRead,
            MailFilterActionType.MarkUnread => MailOperation.MarkAsUnread,
            MailFilterActionType.SetFlag => MailOperation.SetFlag,
            MailFilterActionType.ClearFlag => MailOperation.ClearFlag,
            MailFilterActionType.Move => MailOperation.Move,
            MailFilterActionType.Archive => MailOperation.Archive,
            MailFilterActionType.MoveToJunk => MailOperation.MoveToJunk,
            MailFilterActionType.MarkAsNotJunk => MailOperation.MarkAsNotJunk,
            MailFilterActionType.SoftDelete => MailOperation.SoftDelete,
            MailFilterActionType.HardDelete => MailOperation.HardDelete,
            _ => MailOperation.None
        };

    private static bool IsTerminal(MailFilterActionType action)
        => action is MailFilterActionType.Move
            or MailFilterActionType.Archive
            or MailFilterActionType.MoveToJunk
            or MailFilterActionType.MarkAsNotJunk
            or MailFilterActionType.SoftDelete
            or MailFilterActionType.HardDelete;
}
