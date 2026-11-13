using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// The expansion every mode pane shows for each account group, shared by all modes. A group's state is read from
/// the database the first time a pane shows it and is then kept here, so a menu rebuild from a freshly loaded
/// layout cannot bring back a value that is still being saved.
/// </summary>
public sealed class AccountGroupExpansionStore
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, bool> _isExpanded = [];
    private readonly IAccountService _accountService;

    public AccountGroupExpansionStore(IAccountService accountService)
    {
        _accountService = accountService;
    }

    /// <summary>Raised on the thread that changed the state.</summary>
    public event Action<Guid, bool>? ExpansionChanged;

    public bool IsExpanded(MergedInbox group)
    {
        lock (_gate)
        {
            if (!_isExpanded.TryGetValue(group.Id, out var isExpanded))
            {
                isExpanded = !group.IsCollapsed;
                _isExpanded[group.Id] = isExpanded;
            }

            return isExpanded;
        }
    }

    public void SetExpanded(Guid groupId, bool isExpanded)
    {
        lock (_gate)
        {
            if (_isExpanded.TryGetValue(groupId, out var current) && current == isExpanded)
                return;

            _isExpanded[groupId] = isExpanded;
        }

        ExpansionChanged?.Invoke(groupId, isExpanded);

        _ = SaveAsync(groupId);
    }

    private async Task SaveAsync(Guid groupId)
    {
        // Each write stores the state at write time, so quick toggles cannot be saved out of order.
        await _saveGate.WaitAsync().ConfigureAwait(false);

        try
        {
            bool isExpanded;

            lock (_gate)
            {
                isExpanded = _isExpanded[groupId];
            }

            await _accountService.SetAccountGroupExpandedAsync(groupId, isExpanded).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save the expansion of account group {GroupId}.", groupId);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
