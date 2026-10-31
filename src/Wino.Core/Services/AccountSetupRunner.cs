#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Authorization;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Client.Accounts;

namespace Wino.Core.Services;

/// <summary>
/// Saves a new account and runs its first synchronization. The awaits deliberately keep the caller's context: a page
/// calls it on the UI thread, and the step updates and the certificate prompt must arrive there in order.
/// </summary>
public sealed class AccountSetupRunner : IAccountSetupRunner
{
    private const string OperationAccountSetup = "AccountSetup";

    private readonly IAccountService _accountService;
    private readonly ISpecialImapProviderConfigResolver _specialImapProviderConfigResolver;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IAuthenticationProvider _authenticationProvider;
    private readonly ICalDavClient _calDavClient;
    private readonly IWinoLogger _winoLogger;
    private readonly IOAuthTokenService? _oauthTokenService;
    private readonly IAccountOAuthProfileService? _accountOAuthProfileService;
    private readonly IDavOAuthAuthenticationProvider? _davOAuthAuthenticationProvider;
    private readonly IGoogleMailboxProfileService? _googleMailboxProfileService;

    public AccountSetupRunner(
        IAccountService accountService,
        ISpecialImapProviderConfigResolver specialImapProviderConfigResolver,
        ISynchronizationManager synchronizationManager,
        IAuthenticationProvider authenticationProvider,
        ICalDavClient calDavClient,
        IWinoLogger winoLogger,
        IOAuthTokenService? oauthTokenService = null,
        IAccountOAuthProfileService? accountOAuthProfileService = null,
        IDavOAuthAuthenticationProvider? davOAuthAuthenticationProvider = null,
        IGoogleMailboxProfileService? googleMailboxProfileService = null)
    {
        _googleMailboxProfileService = googleMailboxProfileService;
        _accountService = accountService;
        _specialImapProviderConfigResolver = specialImapProviderConfigResolver;
        _synchronizationManager = synchronizationManager;
        _authenticationProvider = authenticationProvider;
        _calDavClient = calDavClient;
        _winoLogger = winoLogger;
        _oauthTokenService = oauthTokenService;
        _accountOAuthProfileService = accountOAuthProfileService;
        _davOAuthAuthenticationProvider = davOAuthAuthenticationProvider;
    }

    public IReadOnlyList<AccountSetupStepDescriptor> GetSteps(AccountSetupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var steps = new List<AccountSetupStepDescriptor>();
        var shouldSetupMail = plan.IsMailAccessEnabled;
        var shouldSetupCalendar = plan.IsCalendarAccessEnabled;

        void Add(AccountSetupStepKind kind) => steps.Add(new AccountSetupStepDescriptor(kind, GetStepTitle(plan, kind)));

        switch (plan.Kind)
        {
            case AccountSetupKind.Native:
                Add(AccountSetupStepKind.Authenticating);
                Add(AccountSetupStepKind.SavingAccount);

                if (shouldSetupMail)
                {
                    Add(AccountSetupStepKind.FetchingProfile);
                    Add(AccountSetupStepKind.SyncingFolders);

                    if (plan.ProviderType == MailProviderType.Outlook)
                        Add(AccountSetupStepKind.SyncingCategories);

                    Add(AccountSetupStepKind.SyncingAliases);
                }

                if (shouldSetupCalendar)
                    Add(AccountSetupStepKind.FetchingCalendarMetadata);

                break;

            case AccountSetupKind.SpecialImap:
                if (shouldSetupMail)
                    Add(AccountSetupStepKind.TestingMailAuth);

                if (shouldSetupCalendar && plan.CalendarSupportMode == ImapCalendarSupportMode.CalDav)
                {
                    Add(AccountSetupStepKind.DiscoveringCalDav);
                    Add(AccountSetupStepKind.TestingCalendarAuth);
                }

                Add(AccountSetupStepKind.SavingAccount);

                if (shouldSetupMail)
                    Add(AccountSetupStepKind.SyncingFolders);

                if (shouldSetupCalendar && plan.CalendarSupportMode != ImapCalendarSupportMode.Disabled)
                    Add(AccountSetupStepKind.FetchingCalendarMetadata);

                break;

            default:
                Add(AccountSetupStepKind.SavingAccount);

                if (shouldSetupMail)
                    Add(AccountSetupStepKind.SyncingFolders);

                var imap = plan.Imap;
                if (imap?.IsCalendarAccessGranted == true &&
                    imap.ServerInformation?.CalendarSupportMode == ImapCalendarSupportMode.CalDav)
                {
                    Add(AccountSetupStepKind.DiscoveringCalDav);
                    Add(AccountSetupStepKind.TestingCalendarAuth);
                }

                if (imap?.IsCalendarAccessGranted == true)
                    Add(AccountSetupStepKind.FetchingCalendarMetadata);

                break;
        }

        Add(AccountSetupStepKind.Finalizing);
        return steps.ToArray();
    }

    public MailAccount CreateTransientAccount(AccountSetupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var account = BuildAccount(plan);
        account.Address = plan.EmailAddress?.Trim() ?? string.Empty;
        return account;
    }

    public async Task<TokenInformationEx> SignInTransientAccountAsync(MailAccount transientAccount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transientAccount);

        if (transientAccount.ProviderType is not (MailProviderType.Gmail or MailProviderType.Outlook))
            throw new ArgumentException("Only Gmail and Outlook accounts sign in through their provider.", nameof(transientAccount));

        var authenticator = _authenticationProvider.GetAuthenticator(transientAccount.ProviderType);
        IReadOnlyCollection<ProviderFeature> requestedFeatures = transientAccount.IsMailAccessGranted
            ? (ProviderFeature[])[ProviderFeature.MailFilters]
            : Array.Empty<ProviderFeature>();

        return await authenticator.GenerateTokenInformationAsync(transientAccount, requestedFeatures, cancellationToken);
    }

    public async Task<string> ConfirmTransientAddressAsync(MailAccount transientAccount, TokenInformationEx tokenInformation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transientAccount);
        ArgumentNullException.ThrowIfNull(tokenInformation);

        string? address = transientAccount.ProviderType == MailProviderType.Gmail
            ? _googleMailboxProfileService == null
                ? null
                : await _googleMailboxProfileService.GetMailboxAddressAsync(tokenInformation.AccessToken, cancellationToken)
            : tokenInformation.AccountAddress;

        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException(Translator.AccountSetup_Failure_ProfileUnavailable);

        return address.Trim();
    }

    public async Task DiscardTransientSignInAsync(MailAccount transientAccount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transientAccount);

        if (transientAccount.ProviderType != MailProviderType.Gmail)
            return;

        try
        {
            await _authenticationProvider
                .GetAuthenticator(MailProviderType.Gmail)
                .DeleteTokenInformationAsync(transientAccount, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "The Google token of an abandoned account setup could not be deleted.");
        }
    }

    public async Task<AccountSetupRunResult> RunAsync(
        AccountSetupPlan plan,
        IProgress<AccountSetupStepUpdate>? progress = null,
        Func<string, byte[], Task<bool>>? confirmCertificateTrust = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Rejected before the run starts, so an account that would serve nothing is neither saved nor reported as a failure.
        if (!plan.HasAnyCapability)
            return new AccountSetupRunResult { Error = new InvalidOperationException(Translator.ProviderSelection_CapabilityValidationMessage) };

        var run = new SetupRun(plan, progress);

        try
        {
            switch (plan.Kind)
            {
                case AccountSetupKind.Native:
                    await RunNativeAsync(run, cancellationToken);
                    break;
                case AccountSetupKind.SpecialImap:
                    await RunSpecialImapAsync(run, confirmCertificateTrust, cancellationToken);
                    break;
                default:
                    await RunImapAsync(run, cancellationToken);
                    break;
            }

            run.Start(AccountSetupStepKind.Finalizing);
            run.Succeed();

            return new AccountSetupRunResult { Account = run.Account, IsAccountSaved = run.IsSaved, IsCompleted = true };
        }
        catch (AccountSetupCanceledException)
        {
            return new AccountSetupRunResult { Account = run.Account, IsAccountSaved = run.IsSaved, IsCanceled = true };
        }
        catch (Exception ex) when (ex.Message.Contains(nameof(AccountSetupCanceledException)))
        {
            return new AccountSetupRunResult { Account = run.Account, IsAccountSaved = run.IsSaved, IsCanceled = true };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AccountSetupRunResult { Account = run.Account, IsAccountSaved = run.IsSaved, IsCanceled = true };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Account setup failed.");
            CaptureAccountSetupException(ex, run);

            run.Fail(ex.Message);

            // Once authentication and persistence have succeeded, the account is valid even if a provider endpoint
            // is temporarily unavailable. It is kept so retry can enter the shell without authenticating again or
            // attempting a duplicate insert; normal synchronization recovers the remaining data.
            return new AccountSetupRunResult { Account = run.Account, IsAccountSaved = run.IsSaved, Error = ex };
        }
    }

    private async Task RunNativeAsync(SetupRun run, CancellationToken cancellationToken)
    {
        var plan = run.Plan;
        var account = BuildAccount(plan);
        run.Account = account;

        run.Start(AccountSetupStepKind.Authenticating);

        TokenInformationEx tokenInformation;
        if (plan.NativeSignIn is { } signIn)
        {
            // The sign-in ran against the transient account; its id keys the stored Google token and its client id
            // picks the MSAL cache, so the saved account takes both.
            account.Id = signIn.TransientAccount.Id;
            account.OutlookClientId = signIn.TransientAccount.OutlookClientId;
            tokenInformation = signIn.TokenInformation;
        }
        else
        {
            tokenInformation = await _synchronizationManager.HandleAuthorizationAsync(
                plan.ProviderType,
                account,
                forceInteractive: true,
                requestedFeatures: account.IsMailAccessGranted
                    ? (ProviderFeature[])[ProviderFeature.MailFilters]
                    : Array.Empty<ProviderFeature>());
        }

        account.AuthenticationAddress = tokenInformation.AuthenticationAddress;
        account.Address = tokenInformation.AccountAddress;
        run.Succeed();

        run.Start(AccountSetupStepKind.SavingAccount);
        await _accountService.CreateAccountAsync(account, null, enableMailFilters: account.IsMailAccessGranted);
        WeakReferenceMessenger.Default.Send(new AccountSetupInProgressMessage(account.Id));
        run.IsSaved = true;
        run.Succeed();

        if (account.IsMailAccessGranted)
        {
            run.Start(AccountSetupStepKind.FetchingProfile);
            var profileResult = await _synchronizationManager.SynchronizeProfileAsync(account.Id, cancellationToken);
            if (profileResult.CompletedState != SynchronizationCompletedState.Success)
                throw new Exception(Translator.Exception_FailedToSynchronizeProfileInformation);

            if (profileResult.ProfileInformation != null)
            {
                account.SenderName = profileResult.ProfileInformation.SenderName;
                if (!string.IsNullOrEmpty(profileResult.ProfileInformation.AccountAddress))
                {
                    if (await _accountService.AccountAddressExistsAsync(profileResult.ProfileInformation.AccountAddress, account.Id))
                        throw new InvalidOperationException(Translator.DialogMessage_AccountAddressExistsMessage);

                    account.Address = profileResult.ProfileInformation.AccountAddress;
                }

                await _accountService.UpdateProfileInformationAsync(account.Id, profileResult.ProfileInformation);
            }

            run.Succeed();

            await SynchronizeFoldersAsync(run, cancellationToken);

            if (account.IsCategorySyncSupported)
            {
                run.Start(AccountSetupStepKind.SyncingCategories);
                await TrySynchronizeCategoriesAsync(account, cancellationToken);
                run.Succeed();
            }
        }

        if (account.IsCalendarAccessGranted)
            await SynchronizeCalendarMetadataAsync(run, cancellationToken);

        if (account.IsMailAccessGranted)
        {
            run.Start(AccountSetupStepKind.SyncingAliases);

            if (account.IsAliasSyncSupported)
            {
                var aliasResult = await _synchronizationManager.SynchronizeAliasesAsync(account.Id, cancellationToken);
                if (aliasResult.CompletedState != SynchronizationCompletedState.Success)
                    throw new Exception(Translator.Exception_FailedToSynchronizeAliases);
            }
            else
            {
                await _accountService.CreateRootAliasAsync(account.Id, account.Address);
            }

            run.Succeed();
        }
    }

    private async Task RunSpecialImapAsync(SetupRun run, Func<string, byte[], Task<bool>>? confirmCertificateTrust, CancellationToken cancellationToken)
    {
        var plan = run.Plan;
        var account = BuildAccount(plan);
        run.Account = account;

        var dialogResult = plan.SpecialImap
            ?? throw new InvalidOperationException("The provider sign-in details are missing.");

        var serverInformation = _specialImapProviderConfigResolver.GetServerInformation(account, dialogResult)
            ?? throw new Exception("Failed to resolve server information.");

        serverInformation.Id = Guid.NewGuid();
        serverInformation.AccountId = account.Id;

        account.Address = plan.EmailAddress ?? string.Empty;
        account.SenderName = plan.DisplayName ?? string.Empty;
        account.IsMailAccessGranted = dialogResult.IsMailAccessGranted;
        account.ImapKnownFolderBootstrapState = ImapKnownFolderBootstrap.GetInitialState(account.ProviderType, dialogResult.IsMailAccessGranted);
        account.IsCalendarAccessEnabled = plan.IsCalendarAccessEnabled;
        account.IsCalendarAccessGranted = serverInformation.CalendarSupportMode != ImapCalendarSupportMode.Disabled;
        account.ServerInformation = serverInformation;

        if (account.IsMailAccessGranted)
        {
            run.Start(AccountSetupStepKind.TestingMailAuth);
            await ValidateImapConnectivityAsync(serverInformation, confirmCertificateTrust);
            run.Succeed();
        }

        if (serverInformation.CalendarSupportMode == ImapCalendarSupportMode.CalDav)
        {
            run.Start(AccountSetupStepKind.DiscoveringCalDav);
            run.Succeed();

            run.Start(AccountSetupStepKind.TestingCalendarAuth);
            await ValidateCalDavConnectivityAsync(serverInformation, cancellationToken);
            run.Succeed();
        }

        run.Start(AccountSetupStepKind.SavingAccount);
        await _accountService.CreateAccountAsync(account, serverInformation, true);
        WeakReferenceMessenger.Default.Send(new AccountSetupInProgressMessage(account.Id));
        run.IsSaved = true;
        run.Succeed();

        if (account.IsMailAccessGranted)
            await SynchronizeFoldersAsync(run, cancellationToken);

        if (account.IsCalendarAccessGranted)
            await SynchronizeCalendarMetadataAsync(run, cancellationToken);

        if (account.IsMailAccessGranted)
            await _accountService.CreateRootAliasAsync(account.Id, account.Address);
    }

    private async Task RunImapAsync(SetupRun run, CancellationToken cancellationToken)
    {
        var plan = run.Plan;
        var account = BuildAccount(plan);
        run.Account = account;

        var setupData = plan.Imap ?? throw new Exception("IMAP setup was not completed.");
        var serverInformation = setupData.ServerInformation ?? throw new Exception("Server information is missing.");

        // The sign-in session key becomes the account id, so the tokens it holds in memory are written under the
        // account they belong to.
        var oauthSession = setupData.OAuthSession;
        if (oauthSession != null)
            account.Id = oauthSession.Key;

        serverInformation.Id = Guid.NewGuid();
        serverInformation.AccountId = account.Id;

        account.Address = setupData.EmailAddress ?? string.Empty;
        account.SenderName = setupData.DisplayName ?? string.Empty;
        account.IsMailAccessGranted = setupData.IsMailAccessGranted;
        account.ImapKnownFolderBootstrapState = ImapKnownFolderBootstrap.GetInitialState(account.ProviderType, setupData.IsMailAccessGranted);
        account.IsCalendarAccessEnabled = plan.IsCalendarAccessEnabled;
        account.IsCalendarAccessGranted = setupData.IsCalendarAccessGranted;
        account.ServerInformation = serverInformation;

        run.Start(AccountSetupStepKind.SavingAccount);
        await _accountService.CreateAccountAsync(account, serverInformation, setupData.ShouldAppendMessagesToSentFolder);
        WeakReferenceMessenger.Default.Send(new AccountSetupInProgressMessage(account.Id));
        run.IsSaved = true;

        if (oauthSession != null)
            await PersistOAuthSessionAsync(oauthSession, account.Id, cancellationToken);

        run.Succeed();

        if (account.IsMailAccessGranted)
            await SynchronizeFoldersAsync(run, cancellationToken);

        if (setupData.IsCalendarAccessGranted && serverInformation.CalendarSupportMode == ImapCalendarSupportMode.CalDav)
        {
            run.Start(AccountSetupStepKind.DiscoveringCalDav);
            run.Succeed();

            run.Start(AccountSetupStepKind.TestingCalendarAuth);
            await ValidateCalDavConnectivityAsync(serverInformation, cancellationToken);
            run.Succeed();
        }

        if (setupData.IsCalendarAccessGranted)
            await SynchronizeCalendarMetadataAsync(run, cancellationToken);

        if (account.IsMailAccessGranted)
            await _accountService.CreateRootAliasAsync(account.Id, account.Address);
    }

    private static MailAccount BuildAccount(AccountSetupPlan plan)
    {
        var isProviderApi = plan.ProviderType is MailProviderType.Outlook or MailProviderType.Gmail;

        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            ProviderType = plan.ProviderType,
            Name = plan.AccountName ?? string.Empty,
            SpecialImapProvider = plan.SpecialImapProvider,
            AccountColorHex = plan.AccountColorHex ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            InitialSynchronizationRange = plan.InitialSynchronizationRange,
            IsMailAccessGranted = plan.IsMailAccessEnabled,
            IsCalendarAccessEnabled = plan.IsCalendarAccessEnabled,
            IsCalendarAccessGranted = plan.IsCalendarAccessEnabled && plan.CalendarIntegrationSource != AccountIntegrationSource.Local,
            CalendarIntegrationSource = plan.CalendarIntegrationSource,
            IsContactAccessEnabled = plan.IsContactAccessEnabled,
            IsContactAccessGranted = plan.IsContactAccessEnabled && plan.ContactIntegrationSource != AccountIntegrationSource.Local,
            ContactIntegrationSource = plan.ContactIntegrationSource,
            IsTaskAccessEnabled = plan.IsTaskAccessEnabled,
            IsTaskAccessGranted = isProviderApi && plan.IsTaskAccessEnabled && plan.TaskIntegrationSource == AccountIntegrationSource.Provider,
            TaskIntegrationSource = plan.TaskIntegrationSource
        };

        if (plan.ProviderType == MailProviderType.Outlook)
            account.OutlookClientId = WinoAuthenticationClientIds.Outlook.Current;

        return account;
    }

    private async Task SynchronizeFoldersAsync(SetupRun run, CancellationToken cancellationToken)
    {
        run.Start(AccountSetupStepKind.SyncingFolders);

        var folderResult = await _synchronizationManager.SynchronizeFoldersAsync(run.RequireAccount().Id, cancellationToken);
        if (folderResult == null || folderResult.CompletedState != SynchronizationCompletedState.Success)
            throw new Exception(Translator.Exception_FailedToSynchronizeFolders);

        run.Succeed();
    }

    private async Task SynchronizeCalendarMetadataAsync(SetupRun run, CancellationToken cancellationToken)
    {
        run.Start(AccountSetupStepKind.FetchingCalendarMetadata);

        var calendarResult = await _synchronizationManager.SynchronizeCalendarAsync(new CalendarSynchronizationOptions
        {
            AccountId = run.RequireAccount().Id,
            Type = CalendarSynchronizationType.CalendarMetadata
        }, cancellationToken);

        if (calendarResult == null || calendarResult.CompletedState != SynchronizationCompletedState.Success)
            throw new Exception(Translator.Exception_FailedToSynchronizeCalendarMetadata);

        run.Succeed();
    }

    /// <summary>
    /// Writes the OAuth profile and the in-memory tokens right after the account row, before the first folder
    /// synchronization needs them. The session's profile carries any client id that dynamic registration created
    /// during sign-in.
    /// </summary>
    private async Task PersistOAuthSessionAsync(OAuthSetupSession session, Guid accountId, CancellationToken cancellationToken)
    {
        if (_oauthTokenService == null || _accountOAuthProfileService == null)
            throw new InvalidOperationException("Secure sign-in is not available on this device.");

        var profile = session.Profile.Clone();
        profile.AccountId = accountId;

        await _accountOAuthProfileService.SaveAsync(profile);
        await _oauthTokenService.PersistSessionAsync(session, accountId, cancellationToken);
    }

    private async Task TrySynchronizeCategoriesAsync(MailAccount account, CancellationToken cancellationToken)
    {
        try
        {
            var categoryResult = await _synchronizationManager.SynchronizeCategoriesAsync(account.Id, cancellationToken);

            if (categoryResult?.CompletedState == SynchronizationCompletedState.Success)
                return;

            var exception = categoryResult?.Exception
                ?? new InvalidOperationException(Translator.Exception_FailedToSynchronizeCategories);

            Log.Warning(exception, "Category synchronization failed during account setup for provider {ProviderType}. Setup will continue.",
                account.ProviderType);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Category synchronization threw during account setup for provider {ProviderType}. Setup will continue.",
                account.ProviderType);
        }
    }

    private async Task ValidateImapConnectivityAsync(CustomServerInformation serverInformation, Func<string, byte[], Task<bool>>? confirmCertificateTrust)
    {
        while (true)
        {
            var connectivityResult = await _synchronizationManager.TestImapConnectivityAsync(serverInformation);

            if (!connectivityResult.IsCertificateUIRequired)
            {
                if (!connectivityResult.IsSuccess)
                    throw new ImapValidationException(connectivityResult.FailedReason ?? Translator.IMAPSetupDialog_ConnectionFailedMessage, connectivityResult.ProtocolLog);
                return;
            }

            var failure = connectivityResult.CertificateFailure;
            if (failure?.CanTrust != true || confirmCertificateTrust == null)
                throw new InvalidOperationException(connectivityResult.FailedReason ?? Translator.IMAPSetupDialog_CertificateCannotBeTrusted);

            var allowCertificate = await confirmCertificateTrust(MailServerCertificateMessage.Build(failure), failure.CertificateRawData);
            if (!allowCertificate)
                throw new InvalidOperationException(Translator.IMAPSetupDialog_CertificateDenied);

            serverInformation.PendingCertificateTrusts.RemoveAll(item => item.Protocol == failure.Protocol &&
                string.Equals(item.Host, failure.Host, StringComparison.OrdinalIgnoreCase) && item.Port == failure.Port);
            serverInformation.PendingCertificateTrusts.Add(failure.CreateTrust(serverInformation.AccountId));
        }
    }

    private async Task ValidateCalDavConnectivityAsync(CustomServerInformation serverInformation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverInformation.CalDavServiceUrl))
            throw new InvalidOperationException(Translator.ImapCalDavSettingsPage_CalDavUrlRequired);

        var serviceUri = new Uri(serverInformation.CalDavServiceUrl, UriKind.Absolute);

        // The generic IMAP path persists the OAuth tokens before this test, so the account id is the token key.
        var bearer = _davOAuthAuthenticationProvider == null
            ? null
            : await _davOAuthAuthenticationProvider.TryCreateForAccountAsync(serverInformation.AccountId, serverInformation, DavServiceKind.CalDav, serviceUri, cancellationToken);

        var settings = new CalDavConnectionSettings
        {
            ServiceUri = serviceUri,
            Username = serverInformation.CalDavUsername,
            Password = serverInformation.CalDavPassword ?? string.Empty,
            Authentication = bearer
        };

        await _calDavClient.DiscoverCalendarsAsync(settings);
    }

    private void CaptureAccountSetupException(Exception exception, SetupRun run)
    {
        var plan = run.Plan;
        var operationName = run.CurrentStep is { } step ? GetOperationName(step) : OperationAccountSetup;

        var properties = new Dictionary<string, string>
        {
            ["error_origin"] = "AccountSetup",
            ["setup_operation"] = operationName,
            ["setup_step"] = run.CurrentStep is { } currentStep ? GetStepTitle(plan, currentStep) : string.Empty,
            ["provider_type"] = plan.ProviderType.ToString(),
            ["is_oauth_provider"] = (plan.ProviderType is MailProviderType.Outlook or MailProviderType.Gmail).ToString(),
            ["is_mail_access_enabled"] = plan.IsMailAccessEnabled.ToString(),
            ["is_calendar_access_enabled"] = plan.IsCalendarAccessEnabled.ToString(),
            ["is_contact_access_enabled"] = plan.IsContactAccessEnabled.ToString(),
            ["is_task_access_enabled"] = plan.IsTaskAccessEnabled.ToString(),
            ["is_presigned_in"] = (plan.NativeSignIn != null).ToString()
        };

        if (run.Account is { } account)
        {
            properties["account_id"] = account.Id.ToString();
            properties["account_provider_type"] = account.ProviderType.ToString();
            properties["account_mail_access_granted"] = account.IsMailAccessGranted.ToString();
            properties["account_calendar_access_granted"] = account.IsCalendarAccessGranted.ToString();
        }

        _winoLogger.CaptureException(exception, operationName, properties);
    }

    private static string GetOperationName(AccountSetupStepKind kind) => kind switch
    {
        AccountSetupStepKind.Authenticating => "Authentication",
        AccountSetupStepKind.SavingAccount => "SaveAccount",
        AccountSetupStepKind.FetchingProfile => "ProfileSync",
        AccountSetupStepKind.SyncingFolders => "FolderSync",
        AccountSetupStepKind.SyncingCategories => "CategorySync",
        AccountSetupStepKind.FetchingCalendarMetadata => "CalendarMetadataSync",
        AccountSetupStepKind.SyncingAliases => "AliasSync",
        AccountSetupStepKind.TestingMailAuth => "MailAuthTest",
        AccountSetupStepKind.DiscoveringCalDav => "CalDavDiscovery",
        AccountSetupStepKind.TestingCalendarAuth => "CalendarAuthTest",
        AccountSetupStepKind.Finalizing => "Finalizing",
        _ => OperationAccountSetup
    };

    private static string GetStepTitle(AccountSetupPlan plan, AccountSetupStepKind kind) => kind switch
    {
        AccountSetupStepKind.Authenticating => string.Format(Translator.AccountSetup_Step_Authenticating, plan.ProviderName),
        AccountSetupStepKind.TestingMailAuth => Translator.AccountSetup_Step_TestingMailAuth,
        AccountSetupStepKind.DiscoveringCalDav => Translator.AccountSetup_Step_DiscoveringCalDav,
        AccountSetupStepKind.TestingCalendarAuth => Translator.AccountSetup_Step_TestingCalendarAuth,
        AccountSetupStepKind.SavingAccount => Translator.AccountSetup_Step_SavingAccount,
        AccountSetupStepKind.FetchingProfile => Translator.AccountSetup_Step_FetchingProfile,
        AccountSetupStepKind.SyncingFolders => Translator.AccountSetup_Step_SyncingFolders,
        AccountSetupStepKind.SyncingCategories => Translator.AccountSetup_Step_SyncingCategories,
        AccountSetupStepKind.SyncingAliases => Translator.AccountSetup_Step_SyncingAliases,
        AccountSetupStepKind.FetchingCalendarMetadata => Translator.AccountSetup_Step_FetchingCalendarMetadata,
        _ => Translator.AccountSetup_Step_Finalizing
    };

    private sealed class SetupRun(AccountSetupPlan plan, IProgress<AccountSetupStepUpdate>? progress)
    {
        public AccountSetupPlan Plan { get; } = plan;
        public MailAccount? Account { get; set; }
        public bool IsSaved { get; set; }
        public AccountSetupStepKind? CurrentStep { get; private set; }

        public MailAccount RequireAccount()
            => Account ?? throw new InvalidOperationException("The account has not been built yet.");

        public void Start(AccountSetupStepKind kind)
        {
            CurrentStep = kind;
            progress?.Report(new AccountSetupStepUpdate(kind, AccountSetupStepStatus.InProgress));
        }

        public void Succeed()
        {
            if (CurrentStep is { } kind)
                progress?.Report(new AccountSetupStepUpdate(kind, AccountSetupStepStatus.Succeeded));
        }

        public void Fail(string errorMessage)
        {
            if (CurrentStep is { } kind)
                progress?.Report(new AccountSetupStepUpdate(kind, AccountSetupStepStatus.Failed, errorMessage));
        }
    }
}
