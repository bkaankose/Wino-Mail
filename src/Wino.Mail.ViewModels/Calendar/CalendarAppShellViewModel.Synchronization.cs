using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;

namespace Wino.Calendar.ViewModels;

/// <summary>
/// Calendar's half of the shell title bar synchronization button. Scoped to every account
/// that has calendars in the pane, with the progress read out of
/// <see cref="SynchronizationManager"/> rather than mirrored here.
/// </summary>
public partial class CalendarAppShellViewModel
{
    public bool IsSynchronizationSupported => true;

    public bool CanSynchronize => CalendarAccountIds.Count > 0 && !SynchronizationState.IsSynchronizing;

    public ShellSynchronizationState SynchronizationState
        => ShellSynchronizationStateReader.Read(CalendarAccountIds, SynchronizationProgressCategory.Calendar);

    public string SynchronizationDescription => Translator.TitleBar_Sync_CalendarDescription;

    public string SynchronizationToolTip => Translator.TitleBar_Sync_CalendarToolTip;

    public Task SynchronizeAsync() => Sync();

    private IReadOnlyList<Guid> CalendarAccountIds
        => AccountCalendarStateService.GroupedAccountCalendars?
               .Select(group => group.Account.Id)
               .Distinct()
               .ToList()
           ?? (IReadOnlyList<Guid>)Array.Empty<Guid>();

    /// <summary>
    /// Nothing is stored, so a refresh is only a notification. Raised alongside the pane's
    /// own <see cref="CanSynchronizeCalendars"/> update.
    /// </summary>
    internal void RefreshShellSynchronizationState()
    {
        OnPropertyChanged(nameof(CanSynchronize));
        OnPropertyChanged(nameof(SynchronizationState));
        OnPropertyChanged(nameof(SynchronizationDescription));
        OnPropertyChanged(nameof(SynchronizationToolTip));
    }
}
