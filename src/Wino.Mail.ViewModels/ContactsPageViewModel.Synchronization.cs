using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;

namespace Wino.Mail.ViewModels;

/// <summary>
/// People's half of the shell title bar synchronization button. Scoped to the accounts that
/// granted contact access, with the progress read out of <see cref="SynchronizationManager"/>
/// rather than mirrored here.
/// </summary>
public partial class ContactsPageViewModel
{
    public bool IsSynchronizationSupported => true;

    public bool CanSynchronize => ContactAccountIds.Count > 0 && !SynchronizationState.IsSynchronizing;

    public ShellSynchronizationState SynchronizationState
        => ShellSynchronizationStateReader.Read(ContactAccountIds, SynchronizationProgressCategory.Contacts);

    public string SynchronizationDescription => Translator.TitleBar_Sync_ContactsDescription;

    public string SynchronizationToolTip => Translator.TitleBar_Sync_ContactsToolTip;

    /// <summary>
    /// Reuses the page's own refresh so the title bar and the page's refresh affordance
    /// cannot diverge in what they do.
    /// </summary>
    public Task SynchronizeAsync() => RefreshContactsAsync();

    /// <summary>
    /// Mirrors the account filter <see cref="RefreshContactsAsync"/> applies.
    /// </summary>
    private IReadOnlyList<Guid> ContactAccountIds
        => _accounts.Values
            .Where(account => account.IsContactAccessGranted)
            .Select(account => account.Id)
            .ToList();

    internal void RefreshShellSynchronizationState()
    {
        OnPropertyChanged(nameof(CanSynchronize));
        OnPropertyChanged(nameof(SynchronizationState));
        OnPropertyChanged(nameof(SynchronizationDescription));
        OnPropertyChanged(nameof(SynchronizationToolTip));
    }
}
