using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class MailFilterService(IDatabaseService databaseService) : BaseDatabaseService(databaseService), IMailFilterService
{
    public async Task<List<MailFilter>> GetFiltersAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filters = await Connection.QueryAsync<MailFilter>(
            $"SELECT * FROM {nameof(MailFilter)} WHERE {nameof(MailFilter.MailAccountId)} = ? " +
            $"ORDER BY {nameof(MailFilter.ManagementType)}, {nameof(MailFilter.Sequence)}, {nameof(MailFilter.Name)} COLLATE NOCASE",
            accountId).ConfigureAwait(false);

        await HydrateAsync(filters, cancellationToken).ConfigureAwait(false);
        return filters;
    }

    public async Task<MailFilter> GetFilterAsync(Guid filterId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filter = await Connection.FindAsync<MailFilter>(filterId).ConfigureAwait(false);
        if (filter != null)
            await HydrateAsync([filter], cancellationToken).ConfigureAwait(false);

        return filter;
    }

    public async Task<MailFilter> CreateFilterAsync(MailFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();
        Normalize(filter, isNew: true);
        Validate(filter);

        await PersistAggregateAsync(filter, insert: true).ConfigureAwait(false);
        return filter;
    }

    public async Task UpdateFilterAsync(MailFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();
        Normalize(filter, isNew: false);
        Validate(filter);

        await PersistAggregateAsync(filter, insert: false).ConfigureAwait(false);
    }

    public async Task DeleteFilterAsync(Guid filterId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Connection.RunInTransactionAsync(connection =>
        {
            connection.Execute($"DELETE FROM {nameof(MailFilterCondition)} WHERE {nameof(MailFilterCondition.MailFilterId)} = ?", filterId);
            connection.Execute($"DELETE FROM {nameof(MailFilterAction)} WHERE {nameof(MailFilterAction.MailFilterId)} = ?", filterId);
            connection.Execute($"DELETE FROM {nameof(MailFilterExecution)} WHERE {nameof(MailFilterExecution.MailFilterId)} = ?", filterId);
            connection.Delete<MailFilter>(filterId);
        }).ConfigureAwait(false);
    }

    public async Task DeleteFiltersForAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = await Connection.QueryScalarsAsync<Guid>(
            $"SELECT {nameof(MailFilter.Id)} FROM {nameof(MailFilter)} WHERE {nameof(MailFilter.MailAccountId)} = ?",
            accountId).ConfigureAwait(false);

        foreach (var id in ids)
            await DeleteFilterAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceProviderFiltersAsync(
        Guid accountId,
        IReadOnlyCollection<MailFilter> providerFilters,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var incoming = providerFilters?.Where(filter => filter != null).ToList() ?? [];
        var existing = (await GetFiltersAsync(accountId, cancellationToken).ConfigureAwait(false))
            .Where(filter => filter.ManagementType == MailFilterManagementType.Provider)
            .ToList();
        var existingByRemoteId = existing
            .Where(filter => !string.IsNullOrWhiteSpace(filter.RemoteId))
            .ToDictionary(filter => filter.RemoteId, StringComparer.OrdinalIgnoreCase);
        var incomingRemoteIds = incoming
            .Where(filter => !string.IsNullOrWhiteSpace(filter.RemoteId))
            .Select(filter => filter.RemoteId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var stale in existing.Where(filter => !incomingRemoteIds.Contains(filter.RemoteId)))
            await DeleteFilterAsync(stale.Id, cancellationToken).ConfigureAwait(false);

        foreach (var filter in incoming)
        {
            filter.MailAccountId = accountId;
            filter.ManagementType = MailFilterManagementType.Provider;

            if (existingByRemoteId.TryGetValue(filter.RemoteId ?? string.Empty, out var current))
            {
                filter.Id = current.Id;
                filter.IsWinoCreated = current.IsWinoCreated;
                filter.IsReadOnly = current.IsWinoCreated ? false : filter.IsReadOnly;
                filter.Name = current.IsWinoCreated ? current.Name : filter.Name;
                filter.CreatedAtUtc = current.CreatedAtUtc;
                await UpdateFilterAsync(filter, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                filter.IsWinoCreated = false;
                await CreateFilterAsync(filter, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<List<MailFilter>> GetExecutableFiltersAsync(
        Guid accountId,
        string sourceRemoteFolderId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filters = await Connection.QueryAsync<MailFilter>(
            $"SELECT * FROM {nameof(MailFilter)} " +
            $"WHERE {nameof(MailFilter.MailAccountId)} = ? " +
            $"AND {nameof(MailFilter.ManagementType)} = ? " +
            $"AND {nameof(MailFilter.IsEnabled)} = 1 " +
            $"AND {nameof(MailFilter.SourceRemoteFolderId)} = ? " +
            $"ORDER BY {nameof(MailFilter.Sequence)}, {nameof(MailFilter.CreatedAtUtc)}",
            accountId,
            MailFilterManagementType.WinoLocal,
            sourceRemoteFolderId).ConfigureAwait(false);

        await HydrateAsync(filters, cancellationToken).ConfigureAwait(false);
        return filters;
    }

    public Task<bool> HasExecutionAsync(
        Guid filterId,
        string remoteMessageId,
        string sourceRemoteFolderId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Connection.ExecuteScalarAsync<bool>(
            $"SELECT EXISTS(SELECT 1 FROM {nameof(MailFilterExecution)} " +
            $"WHERE {nameof(MailFilterExecution.MailFilterId)} = ? " +
            $"AND {nameof(MailFilterExecution.RemoteMessageId)} = ? " +
            $"AND {nameof(MailFilterExecution.SourceRemoteFolderId)} = ?)",
            filterId,
            remoteMessageId,
            sourceRemoteFolderId);
    }

    public async Task CreateExecutionAsync(MailFilterExecution execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        cancellationToken.ThrowIfCancellationRequested();
        execution.Id = execution.Id == Guid.Empty ? Guid.NewGuid() : execution.Id;
        execution.CreatedAtUtc = execution.CreatedAtUtc == default ? DateTime.UtcNow : execution.CreatedAtUtc;
        await Connection.InsertAsync(execution, typeof(MailFilterExecution)).ConfigureAwait(false);
    }

    private async Task HydrateAsync(IReadOnlyCollection<MailFilter> filters, CancellationToken cancellationToken)
    {
        foreach (var filter in filters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            filter.Conditions = await Connection.QueryAsync<MailFilterCondition>(
                $"SELECT * FROM {nameof(MailFilterCondition)} WHERE {nameof(MailFilterCondition.MailFilterId)} = ? ORDER BY [{nameof(MailFilterCondition.Order)}]",
                filter.Id).ConfigureAwait(false);
            filter.Actions = await Connection.QueryAsync<MailFilterAction>(
                $"SELECT * FROM {nameof(MailFilterAction)} WHERE {nameof(MailFilterAction.MailFilterId)} = ? ORDER BY [{nameof(MailFilterAction.Order)}]",
                filter.Id).ConfigureAwait(false);
        }
    }

    private async Task PersistAggregateAsync(MailFilter filter, bool insert)
    {
        await Connection.RunInTransactionAsync(connection =>
        {
            if (insert)
                connection.Insert(filter, typeof(MailFilter));
            else
                connection.Update(filter, typeof(MailFilter));

            connection.Execute($"DELETE FROM {nameof(MailFilterCondition)} WHERE {nameof(MailFilterCondition.MailFilterId)} = ?", filter.Id);
            connection.Execute($"DELETE FROM {nameof(MailFilterAction)} WHERE {nameof(MailFilterAction.MailFilterId)} = ?", filter.Id);

            foreach (var condition in filter.Conditions)
                connection.Insert(condition, typeof(MailFilterCondition));

            foreach (var action in filter.Actions)
                connection.Insert(action, typeof(MailFilterAction));
        }).ConfigureAwait(false);
    }

    private static void Normalize(MailFilter filter, bool isNew)
    {
        var now = DateTime.UtcNow;
        filter.Id = filter.Id == Guid.Empty ? Guid.NewGuid() : filter.Id;
        filter.Name = filter.Name?.Trim();
        filter.SourceRemoteFolderId = filter.SourceRemoteFolderId?.Trim();
        filter.RemoteId = filter.RemoteId?.Trim();
        filter.CreatedAtUtc = isNew || filter.CreatedAtUtc == default ? now : filter.CreatedAtUtc;
        filter.UpdatedAtUtc = now;
        filter.Conditions ??= [];
        filter.Actions ??= [];

        for (var index = 0; index < filter.Conditions.Count; index++)
        {
            var condition = filter.Conditions[index];
            condition.Id = condition.Id == Guid.Empty ? Guid.NewGuid() : condition.Id;
            condition.MailFilterId = filter.Id;
            condition.Order = index;
            condition.Value = condition.Value?.Trim();
        }

        for (var index = 0; index < filter.Actions.Count; index++)
        {
            var action = filter.Actions[index];
            action.Id = action.Id == Guid.Empty ? Guid.NewGuid() : action.Id;
            action.MailFilterId = filter.Id;
            action.Order = index;
            action.TargetRemoteFolderId = action.TargetRemoteFolderId?.Trim();
        }
    }

    private static void Validate(MailFilter filter)
    {
        if (filter.MailAccountId == Guid.Empty)
            throw new ArgumentException("A mail filter must belong to an account.", nameof(filter));
        if (string.IsNullOrWhiteSpace(filter.Name))
            throw new ArgumentException("A mail filter must have a name.", nameof(filter));
        if (filter.ManagementType == MailFilterManagementType.WinoLocal && string.IsNullOrWhiteSpace(filter.SourceRemoteFolderId))
            throw new ArgumentException("A Wino filter must have a source folder.", nameof(filter));
        if (filter.Actions.Count == 0 && filter.ManagementType == MailFilterManagementType.WinoLocal)
            throw new ArgumentException("A Wino filter must contain an action.", nameof(filter));

        foreach (var condition in filter.Conditions)
        {
            if (string.IsNullOrWhiteSpace(condition.Value))
                throw new ArgumentException("Mail filter condition values cannot be empty.", nameof(filter));
            if (condition.Field is MailFilterConditionField.HasAttachments or MailFilterConditionField.Importance
                && condition.Operator is not MailFilterConditionOperator.Equals
                    and not MailFilterConditionOperator.NotEquals)
            {
                throw new ArgumentException(
                    $"{condition.Field} supports only equals and does-not-equal operators.",
                    nameof(filter));
            }
        }

        var terminalActions = filter.Actions.Count(action => IsTerminal(action.Type));
        if (terminalActions > 1)
            throw new ArgumentException("A mail filter can contain only one folder-changing action.", nameof(filter));
        if (filter.ManagementType == MailFilterManagementType.WinoLocal
            && terminalActions == 1
            && filter.Actions.Count > 1)
        {
            throw new ArgumentException(
                "A Wino filter cannot combine a folder-changing action with other actions.",
                nameof(filter));
        }

        foreach (var action in filter.Actions.Where(action =>
                     filter.ManagementType == MailFilterManagementType.WinoLocal
                     && RequiresTargetFolder(action.Type)))
        {
            if (string.IsNullOrWhiteSpace(action.TargetRemoteFolderId))
                throw new ArgumentException($"{action.Type} requires a target folder.", nameof(filter));
        }
    }

    public static bool IsTerminal(MailFilterActionType action)
        => action is MailFilterActionType.Move
            or MailFilterActionType.Archive
            or MailFilterActionType.MoveToJunk
            or MailFilterActionType.MarkAsNotJunk
            or MailFilterActionType.SoftDelete
            or MailFilterActionType.HardDelete;

    public static bool RequiresTargetFolder(MailFilterActionType action)
        => action is MailFilterActionType.Move
            or MailFilterActionType.Archive
            or MailFilterActionType.MoveToJunk
            or MailFilterActionType.MarkAsNotJunk
            or MailFilterActionType.SoftDelete;
}
