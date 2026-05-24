using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.SyncHost;

namespace Wino.Mail.WinUI.Services.SyncHost;

public sealed class RemoteWinoSynchronizer : IWinoSynchronizerBase
{
    private readonly SyncHostPipeClient _pipeClient;

    public RemoteWinoSynchronizer(MailAccount account, SyncHostPipeClient pipeClient)
    {
        Account = account;
        _pipeClient = pipeClient;
    }

    public MailAccount Account { get; }

    public AccountSynchronizerState State
        => IsAccountSynchronizing(Account.Id)
            ? AccountSynchronizerState.Synchronizing
            : AccountSynchronizerState.Idle;

    public void QueueRequest(IRequestBase request)
    {
        var serializedRequest = SyncHostSynchronizationManagerProxy.SerializeRequest(request);
        var payload = new QueueRequestsPayload(Account.Id, false, [serializedRequest]);

        _pipeClient.SendAsync(SyncHostProtocol.Commands.QueueRequests, payload)
            .GetAwaiter()
            .GetResult();
    }

    public bool HasPendingOperation(Guid mailUniqueId)
        => GetPendingOperationUniqueIds().Contains(mailUniqueId);

    public IReadOnlyCollection<Guid> GetPendingOperationUniqueIds()
        => _pipeClient
            .SendAsync<AccountIdPayload, IReadOnlyCollection<Guid>>(
                SyncHostProtocol.Commands.GetPendingMailOperationIds,
                new AccountIdPayload(Account.Id))
            .GetAwaiter()
            .GetResult() ?? [];

    public bool HasPendingCalendarOperation(Guid calendarItemId)
        => GetPendingCalendarOperationIds().Contains(calendarItemId);

    public IReadOnlyCollection<Guid> GetPendingCalendarOperationIds()
        => _pipeClient
            .SendAsync<AccountIdPayload, IReadOnlyCollection<Guid>>(
                SyncHostProtocol.Commands.GetPendingCalendarOperationIds,
                new AccountIdPayload(Account.Id))
            .GetAwaiter()
            .GetResult() ?? [];

    public Task<ProfileInformation> GetProfileInformationAsync()
        => Task.FromException<ProfileInformation>(
            new NotSupportedException("Profile synchronization must be requested through SynchronizationManager."));

    public Task<MailSynchronizationResult> SynchronizeMailsAsync(
        MailSynchronizationOptions options,
        CancellationToken cancellationToken = default)
        => _pipeClient.SendAsync<MailSynchronizationOptions, MailSynchronizationResult>(
            SyncHostProtocol.Commands.SynchronizeMail,
            options,
            cancellationToken)!;

    public Task<CalendarSynchronizationResult> SynchronizeCalendarEventsAsync(
        CalendarSynchronizationOptions options,
        CancellationToken cancellationToken = default)
        => _pipeClient.SendAsync<CalendarSynchronizationOptions, CalendarSynchronizationResult>(
            SyncHostProtocol.Commands.SynchronizeCalendar,
            options,
            cancellationToken)!;

    public async Task DownloadMissingMimeMessageAsync(
        MailCopy mailItem,
        ITransferProgress transferProgress,
        CancellationToken cancellationToken = default)
        => await _pipeClient.SendAsync(
            SyncHostProtocol.Commands.DownloadMimeMessage,
            new DownloadMimeMessagePayload(mailItem, Account.Id),
            cancellationToken).ConfigureAwait(false);

    public Task KillSynchronizerAsync()
        => _pipeClient.SendAsync(
            SyncHostProtocol.Commands.DestroySynchronizer,
            new AccountIdPayload(Account.Id));

    public Task<List<MailCopy>> OnlineSearchAsync(
        string queryText,
        List<IMailItemFolder> folders,
        CancellationToken cancellationToken = default)
    {
        var concreteFolders = folders?.OfType<MailItemFolder>().ToList();

        return _pipeClient.SendAsync<OnlineSearchPayload, List<MailCopy>>(
            SyncHostProtocol.Commands.OnlineSearch,
            new OnlineSearchPayload(Account.Id, queryText, concreteFolders),
            cancellationToken)!;
    }

    public Task DownloadCalendarAttachmentAsync(
        CalendarItem calendarItem,
        CalendarAttachment attachment,
        string localFilePath,
        CancellationToken cancellationToken)
        => _pipeClient.SendAsync(
            SyncHostProtocol.Commands.DownloadCalendarAttachment,
            new DownloadCalendarAttachmentPayload(calendarItem, attachment, localFilePath),
            cancellationToken);

    private bool IsAccountSynchronizing(Guid accountId)
        => _pipeClient.SendAsync<AccountIdPayload, bool>(
                SyncHostProtocol.Commands.IsAccountSynchronizing,
                new AccountIdPayload(accountId))
            .GetAwaiter()
            .GetResult();
}
