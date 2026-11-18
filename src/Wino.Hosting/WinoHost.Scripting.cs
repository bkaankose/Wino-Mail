using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Messaging.UI;
using Wino.Mail.ViewModels.Common;
using Wino.Services.Interfaces;

namespace Wino.Hosting;

/// <summary>Short forms of what a script does most, each over the same code path an app takes.</summary>
public sealed partial class WinoHost
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(100);

    // Work hands over between the dispatcher, the runtime and the synchronizers with short gaps; idle must hold this long.
    private static readonly TimeSpan IdleSettleTime = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Adds a leased IMAP or POP3 account the way the account setup page does for a password account: the plan
    /// <c>AccountSetupPageViewModel.BuildFinalPlan</c> builds, run by <see cref="IAccountSetupRunner"/> on the UI thread.
    /// Without <paramref name="modes"/> the account has mail only.
    /// </summary>
    public async Task<MailAccount> AddImapAccountAsync(LabLeaseAccount leaseAccount, LabAccountModes? modes = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leaseAccount);
        cancellationToken.ThrowIfCancellationRequested();

        modes ??= LabAccountModes.MailOnly;
        if (leaseAccount.IsPop3 && modes.UsesDav)
            throw new ArgumentException("A POP3 account cannot sync calendars, contacts or tasks through DAV.", nameof(modes));
        if (modes.UsesDav && string.IsNullOrWhiteSpace(leaseAccount.DavUrl))
            throw new ArgumentException($"The lab server of {leaseAccount.Address} has no DAV service.", nameof(modes));

        var calendarSource = modes.Calendar == ImapCalendarSupportMode.CalDav ? AccountIntegrationSource.Dav : AccountIntegrationSource.Local;
        var contactSource = modes.Contacts && modes.ContactsThroughDav ? AccountIntegrationSource.Dav : AccountIntegrationSource.Local;
        var taskSource = modes.Tasks && modes.TasksThroughDav ? AccountIntegrationSource.Dav : AccountIntegrationSource.Local;
        var usesCalDav = modes.Calendar == ImapCalendarSupportMode.CalDav || taskSource == AccountIntegrationSource.Dav;
        var usesCardDav = contactSource == AccountIntegrationSource.Dav;
        var davUser = string.IsNullOrWhiteSpace(leaseAccount.DavUsername) ? leaseAccount.Address : leaseAccount.DavUsername;

        var server = new CustomServerInformation
        {
            Address = leaseAccount.Address,
            DisplayName = leaseAccount.Name,
            IncomingServerType = leaseAccount.IsPop3 ? CustomIncomingServerType.POP3 : CustomIncomingServerType.IMAP4,
            IncomingServer = leaseAccount.ImapHost,
            IncomingServerPort = leaseAccount.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IncomingServerUsername = leaseAccount.Address,
            IncomingServerPassword = leaseAccount.Password,
            IncomingServerSocketOption = leaseAccount.ImapSecurity,
            IncomingAuthenticationMethod = leaseAccount.ImapAuthentication,
            OutgoingServer = leaseAccount.SmtpHost,
            OutgoingServerPort = leaseAccount.SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OutgoingServerUsername = leaseAccount.Address,
            OutgoingServerPassword = leaseAccount.Password,
            OutgoingServerSocketOption = leaseAccount.SmtpSecurity,
            OutgoingAuthenticationMethod = leaseAccount.SmtpAuthentication,
            CalendarSupportMode = modes.Calendar,
            CalDavServiceUrl = usesCalDav ? leaseAccount.DavUrl : string.Empty,
            CardDavServiceUrl = usesCardDav ? leaseAccount.CardDavUrl : string.Empty,
            CalDavUsername = usesCalDav || usesCardDav ? davUser : string.Empty,
            CalDavPassword = usesCalDav || usesCardDav ? leaseAccount.Password : string.Empty,
            MaxConcurrentClients = 5,
            ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected,
            PendingCertificateTrusts = []
        };

        var plan = new AccountSetupPlan
        {
            Kind = AccountSetupKind.Imap,
            ProviderType = leaseAccount.IsPop3 ? MailProviderType.POP3 : MailProviderType.IMAP4,
            SpecialImapProvider = SpecialImapProvider.None,
            ProviderName = leaseAccount.IsPop3 ? "POP3" : "IMAP",
            AccountName = leaseAccount.Address,
            AccountColorHex = string.Empty,
            InitialSynchronizationRange = InitialSynchronizationRange.Everything,
            IsMailAccessEnabled = true,
            IsCalendarAccessEnabled = modes.Calendar != ImapCalendarSupportMode.Disabled,
            IsContactAccessEnabled = modes.Contacts,
            IsTaskAccessEnabled = modes.Tasks,
            CalendarIntegrationSource = calendarSource,
            ContactIntegrationSource = contactSource,
            TaskIntegrationSource = taskSource,
            CalendarSupportMode = modes.Calendar,
            EmailAddress = leaseAccount.Address,
            DisplayName = leaseAccount.Name,
            Imap = new ImapAccountSetupData
            {
                DisplayName = leaseAccount.Name,
                EmailAddress = leaseAccount.Address,
                IsMailAccessGranted = true,
                IsCalendarAccessGranted = modes.Calendar != ImapCalendarSupportMode.Disabled,
                ShouldAppendMessagesToSentFolder = !leaseAccount.IsPop3,
                ServerInformation = server
            }
        };

        var runner = Get<IAccountSetupRunner>();
        var dialogs = Get<IMailDialogService>();
        var result = await _dispatcher.InvokeAsync(() => runner.RunAsync(plan, null, dialogs.ShowServerCertificateTrustDialogAsync))
            .ConfigureAwait(false);

        if (!result.IsCompleted || result.Account is not { } account)
            throw new InvalidOperationException($"Adding {leaseAccount.Address} failed: {result.Error?.Message ?? "the setup did not complete"}.");

        // The setup page announces the account once the runner completes; the runtime, mode readiness and the
        // account list listen for it.
        await _dispatcher.InvokeAsync(() =>
        {
            Get<IMessenger>().Send(new AccountCreatedMessage(account));
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        return account;
    }

    /// <summary>
    /// Queues the answer the next <paramref name="dialogName"/> dialog returns instead of being dismissed, for example
    /// <c>AnswerNextDialog(nameof(IMailDialogService.ShowHardDeleteConfirmationAsync), true)</c>. Each answer is used once.
    /// </summary>
    public void AnswerNextDialog<T>(string dialogName, T answer) => Get<HostDialogAnswers>().Enqueue(dialogName, answer);

    /// <summary>
    /// Synchronizes every granted mode of the account with <see cref="IApplicationRuntime.SynchronizeCreatedAccountAsync"/>,
    /// the pass an app runs for a new account (mail with its full folder list), and returns when it finishes.
    /// </summary>
    public Task SynchronizeAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        return Get<IApplicationRuntime>().SynchronizeCreatedAccountAsync(account, cancellationToken);
    }

    /// <summary>
    /// Opens a ViewModel the way a page does, on the UI thread: resolves it, assigns <see cref="CoreBaseViewModel.Dispatcher"/>,
    /// then calls <see cref="CoreBaseViewModel.OnNavigatedTo"/> and <see cref="CoreBaseViewModel.OnPageLoaded"/>.
    /// Read its state and invoke its commands on <see cref="Dispatcher"/> too.
    /// </summary>
    public Task<TViewModel> OpenAsync<TViewModel>(object? parameter = null, NavigationMode mode = NavigationMode.New)
        where TViewModel : CoreBaseViewModel
        => _dispatcher.InvokeAsync(() =>
        {
            var viewModel = Get<TViewModel>();
            viewModel.Dispatcher = _dispatcher;
            viewModel.OnNavigatedTo(mode, parameter);
            viewModel.OnPageLoaded();
            return Task.FromResult(viewModel);
        });

    /// <summary>
    /// Completes when no synchronizer has queued or running work, no request waits in an undo grace period, the runtime
    /// runs no requested synchronization, and the UI thread queue is empty, all held for a short settle time.
    /// </summary>
    public async Task IdleAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var manager = Get<ISynchronizationManager>();
        var runtime = Get<IApplicationRuntime>();
        var accounts = Get<IAccountService>();
        var deadline = DateTime.UtcNow + (timeout ?? DefaultIdleTimeout);
        DateTime? idleSince = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsIdleAsync(manager, runtime, accounts).ConfigureAwait(false))
            {
                idleSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - idleSince >= IdleSettleTime)
                    return;
            }
            else
            {
                idleSince = null;
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The host did not become idle within {(timeout ?? DefaultIdleTimeout).TotalSeconds:0} seconds.");

            await Task.Delay(IdlePollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> IsIdleAsync(ISynchronizationManager manager, IApplicationRuntime runtime, IAccountService accounts)
    {
        if (!_dispatcher.IsIdle || runtime.HasRunningWork)
            return false;

        foreach (var synchronizer in manager.GetAllSynchronizers())
        {
            if (synchronizer.State != AccountSynchronizerState.Idle || synchronizer.HasQueuedRequests())
                return false;
        }

        // A request in its undo grace period has not reached a synchronizer yet, and may belong to an account without one.
        foreach (var account in await accounts.GetAccountsAsync().ConfigureAwait(false))
        {
            if (manager.HasPendingUndoAction(account.Id))
                return false;
        }

        return true;
    }
}
