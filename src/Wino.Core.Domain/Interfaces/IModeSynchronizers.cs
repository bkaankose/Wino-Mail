using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.Interfaces;

/// <param name="CanRememberRecipients">
/// Whether the provider keeps a list of the addresses this mailbox has written to, which the composer
/// offers ahead of the address book. Optional so a provider that has no such thing says nothing.
/// </param>
public sealed record MailSynchronizerCapabilities(bool CanSynchronize, bool CanExecuteRequests, bool CanRememberRecipients = false);
public sealed record CalendarSynchronizerCapabilities(bool CanSynchronize, bool CanExecuteRequests);
public sealed record ContactSynchronizerCapabilities(bool CanSynchronize, bool CanExecuteRequests, bool CanManageAddressBooks);
public sealed record TaskSynchronizerCapabilities(bool CanSynchronize, bool CanExecuteRequests, bool CanManageLists, bool CanManageSteps);

public interface IModeSynchronizer
{
    MailAccount Account { get; }
    bool IsAvailable { get; }
    string UnavailableReason { get; }
}

public interface IMailSynchronizer : IModeSynchronizer
{
    MailSynchronizerCapabilities Capabilities { get; }
    Task<MailSynchronizationResult> SynchronizeAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default);
    Task<MailSynchronizationResult> ExecuteRequestsAsync(IReadOnlyList<IMailActionRequest> requests, CancellationToken cancellationToken = default);

    /// <summary>
    /// The addresses this mailbox has written to before, heaviest first, or empty when the provider
    /// keeps no such list.
    /// </summary>
    Task<IReadOnlyList<RememberedRecipient>> GetRememberedRecipientsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds these addresses to the remembered list and saves it.
    ///
    /// Deliberately not "write this list back": whatever the provider stores is usually richer than
    /// the neutral shape above - another client's list can carry per-entry data this code does not
    /// interpret, and losing it would damage that client's file. So the synchronizer keeps what it
    /// read, applies the additions to it, and writes that. Nothing untranslatable crosses this seam.
    /// </summary>
    Task RememberRecipientsAsync(IReadOnlyList<RememberedRecipient> recipients, CancellationToken cancellationToken = default);
}

public interface ICalendarSynchronizer : IModeSynchronizer
{
    CalendarSynchronizerCapabilities Capabilities { get; }
    Task<CalendarSynchronizationResult> SynchronizeAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default);
    Task<CalendarSynchronizationResult> ExecuteRequestsAsync(IReadOnlyList<ICalendarActionRequest> requests, CancellationToken cancellationToken = default);
}

public interface IContactSynchronizer : IModeSynchronizer
{
    ContactSynchronizerCapabilities Capabilities { get; }
    Task<ContactSynchronizationResult> SynchronizeAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken = default);
    Task<ContactSynchronizationResult> ExecuteRequestsAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken = default);
}

public interface ITaskSynchronizer : IModeSynchronizer
{
    TaskSynchronizerCapabilities Capabilities { get; }
    Task<TaskSynchronizationResult> SynchronizeAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken = default);
    Task<TaskSynchronizationResult> ExecuteRequestsAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken = default);
}

public interface IMailSynchronizerFactory
{
    Task<IMailSynchronizer> GetSynchronizerAsync(Guid accountId);
}

public interface ICalendarSynchronizerFactory
{
    Task<ICalendarSynchronizer> GetSynchronizerAsync(Guid accountId);
}

public interface IContactSynchronizerFactory
{
    Task<IContactSynchronizer> GetSynchronizerAsync(Guid accountId);
}

public interface ITaskSynchronizerFactory
{
    Task<ITaskSynchronizer> GetSynchronizerAsync(Guid accountId);
}
