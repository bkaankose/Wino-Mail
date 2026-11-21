#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Authorization;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.CardDav;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Calendar, contacts and tasks on the IMAP route. After the mail sign-in Wino only asks each DAV service discovery
/// found whether it accepts that sign-in; no calendar, address book or task list is read before the account is added.
/// A server the user enters on the review step, or one that needs its own sign-in, is checked the same way on Add account.
/// </summary>
public partial class AccountSetupPageViewModel
{
    private AccountSetupDavAvailability _calDavAvailability;
    private AccountSetupDavAvailability _cardDavAvailability;
    private Uri? _foundCalDavUri;
    private Uri? _foundCardDavUri;
    private bool _isDavSignInDemanded;

    private void ResetDavState()
    {
        _calDavAvailability = AccountSetupDavAvailability.NotFound;
        _cardDavAvailability = AccountSetupDavAvailability.NotFound;
        _foundCalDavUri = null;
        _foundCardDavUri = null;
        _isDavSignInDemanded = false;
    }

    /// <summary>
    /// After the mail checks pass: asks the found DAV services whether they accept the sign-in, then opens the review
    /// with what is available. A DAV answer never fails the sign-in; it only decides what the review offers.
    /// </summary>
    private async Task ProbeDavAndReviewAsync(CustomServerInformation server, CancellationTokenSource operation)
    {
        _foundCalDavUri = ParseDavUri(server.CalDavServiceUrl) ?? _resolution?.Support.Calendar.ServiceUri;
        _foundCardDavUri = ParseDavUri(server.CardDavServiceUrl) ?? _resolution?.Support.Contacts.ServiceUri;
        _isDavSignInDemanded = false;

        var (user, password) = GetDavPasswordCredential(server);

        var calDavTask = ProbeDavAsync(DavServiceKind.CalDav, _foundCalDavUri, server, user, password, operation.Token);
        var cardDavTask = ProbeDavAsync(DavServiceKind.CardDav, _foundCardDavUri, server, user, password, operation.Token);
        await Task.WhenAll(calDavTask, cardDavTask);

        if (operation.IsCancellationRequested)
            return;

        _calDavAvailability = calDavTask.Result.Availability;
        _cardDavAvailability = cardDavTask.Result.Availability;

        var isCalDavOffered = _calDavAvailability is AccountSetupDavAvailability.Available or AccountSetupDavAvailability.NeedsSignIn;
        var isCardDavOffered = _cardDavAvailability is AccountSetupDavAvailability.Available or AccountSetupDavAvailability.NeedsSignIn;

        Capabilities.IsCalendarProviderModeAvailable = isCalDavOffered;
        Capabilities.IsContactProviderModeAvailable = isCardDavOffered;
        Capabilities.IsTaskProviderModeAvailable = isCalDavOffered;

        // Synced is the starting point only where the mail sign-in already works; a service that needs its own
        // sign-in starts on this device, so the review never opens asking for another password. The apps stay off
        // until the user turns them on.
        Capabilities.CalendarMode = _calDavAvailability == AccountSetupDavAvailability.Available ? AccountCapabilityMode.Provider : AccountCapabilityMode.Local;
        Capabilities.ContactMode = _cardDavAvailability == AccountSetupDavAvailability.Available ? AccountCapabilityMode.Provider : AccountCapabilityMode.Local;
        Capabilities.TaskMode = _calDavAvailability == AccountSetupDavAvailability.Available ? AccountCapabilityMode.Provider : AccountCapabilityMode.Local;
        Capabilities.TurnOffOptionalApps();

        _pendingServer = server;
        SetSignInValidated(true);
        IsDavPasswordRequired = false;
        BuildAppRows();
        Phase = AccountSetupPhase.Features;
    }

    private async Task<(AccountSetupDavAvailability Availability, string Message)> ProbeDavAsync(
        DavServiceKind kind,
        Uri? uri,
        CustomServerInformation server,
        string user,
        string password,
        CancellationToken cancellationToken)
    {
        if (uri == null)
            return (AccountSetupDavAvailability.NotFound, string.Empty);

        DavAuthenticationProfile? bearer = null;
        if (_oauthSession != null && _davOAuthAuthenticationProvider != null && UsesDavToken(kind, uri, server))
        {
            try
            {
                bearer = await _davOAuthAuthenticationProvider.TryCreateAsync(_oauthSession.Key, server, _oauthSession.Profile, kind, uri, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Information("The calendar or contacts token could not be prepared ({ErrorType}).", ex.GetType().Name);
            }
        }

        if (bearer == null && string.IsNullOrEmpty(password))
            return (AccountSetupDavAvailability.NeedsSignIn, string.Empty);

        try
        {
            if (kind == DavServiceKind.CalDav)
            {
                await _calDavClient.VerifyAccessAsync(new CalDavConnectionSettings
                {
                    ServiceUri = uri,
                    Username = user,
                    Password = password,
                    Authentication = bearer
                }, cancellationToken);
            }
            else if (_cardDavClient != null)
            {
                await _cardDavClient.VerifyAccessAsync(new CardDavConnectionSettings
                {
                    ServiceUri = uri,
                    AccountAddress = EmailAddress?.Trim() ?? string.Empty,
                    Authentication = bearer ?? new DavAuthenticationProfile
                    {
                        Kind = DavAuthenticationKind.Basic,
                        Username = user,
                        Password = password
                    }
                }, cancellationToken);
            }

            return (AccountSetupDavAvailability.Available, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (AccountSetupDavAvailability.NotFound, string.Empty);
        }
        catch (DavRequestException ex) when (ex.StatusCode is 401 or 403)
        {
            Log.Information("{Service:l} at {Host} refused the sign-in ({StatusCode}).", kind.ToString(), uri.Host, ex.StatusCode);
            return (AccountSetupDavAvailability.NeedsSignIn, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Information("{Service:l} at {Host} did not answer the access check ({ErrorType}).", kind.ToString(), uri.Host, ex.GetType().Name);
            return (AccountSetupDavAvailability.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Add account on the IMAP review: checks every synced service that the sign-in step has not already confirmed,
    /// then writes the chosen servers and the DAV credential into the server information the account is saved with.
    /// A failure stays on its row; the user fixes it or keeps that app on this device.
    /// </summary>
    private async Task<bool> PrepareImapServerAsync()
    {
        var server = _pendingServer;
        if (server == null)
        {
            FeaturesError = Translator.AccountSetup_Failure_Title;
            return false;
        }

        var calDavUri = CalDavServiceUri;
        var cardDavUri = CardDavServiceUri;
        var (user, password) = GetDavPasswordCredential(server);

        var needsCalDavCheck = calDavUri != null && !IsConfirmed(calDavUri, _foundCalDavUri, _calDavAvailability);
        var needsCardDavCheck = cardDavUri != null && !IsConfirmed(cardDavUri, _foundCardDavUri, _cardDavAvailability);

        if (needsCalDavCheck || needsCardDavCheck)
        {
            var operation = StartOperation();
            IsReviewBusy = true;

            try
            {
                var calDav = needsCalDavCheck
                    ? await ProbeDavAsync(DavServiceKind.CalDav, calDavUri, server, user, password, operation.Token)
                    : (AccountSetupDavAvailability.Available, string.Empty);
                var cardDav = needsCardDavCheck
                    ? await ProbeDavAsync(DavServiceKind.CardDav, cardDavUri, server, user, password, operation.Token)
                    : (AccountSetupDavAvailability.Available, string.Empty);

                if (operation.IsCancellationRequested)
                    return false;

                var isCalDavAccepted = ApplyDavCheck(calDav, calDavUri, AccountSetupAppKind.Calendar, AccountSetupAppKind.Tasks);
                var isCardDavAccepted = ApplyDavCheck(cardDav, cardDavUri, AccountSetupAppKind.Contacts, null);

                if (!isCalDavAccepted || !isCardDavAccepted)
                    return false;
            }
            finally
            {
                IsReviewBusy = false;
            }
        }

        var usesDav = calDavUri != null || cardDavUri != null;

        server.CalendarSupportMode = CalendarSupportMode;
        server.CalDavServiceUrl = calDavUri?.ToString() ?? string.Empty;
        server.CardDavServiceUrl = cardDavUri?.ToString() ?? string.Empty;
        server.CalDavUsername = usesDav ? user : string.Empty;
        server.CalDavPassword = usesDav ? password : string.Empty;

        return true;
    }

    /// <summary>The sign-in step already confirmed the found server with the credential that will be saved.</summary>
    private bool IsConfirmed(Uri uri, Uri? foundUri, AccountSetupDavAvailability availability)
        => availability == AccountSetupDavAvailability.Available &&
           foundUri != null &&
           uri == foundUri &&
           !IsDavPasswordRequired;

    private bool ApplyDavCheck(
        (AccountSetupDavAvailability Availability, string Message) result,
        Uri? uri,
        AccountSetupAppKind kind,
        AccountSetupAppKind? sharedKind)
    {
        if (uri == null || result.Availability == AccountSetupDavAvailability.Available)
            return true;

        string message;

        if (result.Availability == AccountSetupDavAvailability.NeedsSignIn)
        {
            // The service wants a sign-in of its own: the fields appear under the rows, or the typed one was refused.
            var wasAsked = IsReviewDavSignInVisible && !string.IsNullOrEmpty(DavPassword);
            _isDavSignInDemanded = true;
            RefreshDavSignInRequirement();

            message = string.Format(
                wasAsked ? Translator.AccountSetup_Failure_PasswordRejected : Translator.AccountSetup_Review_DavSignInNeeded,
                uri.Host);
        }
        else
        {
            message = string.Format(Translator.AccountSetup_Failure_Dav, uri.Host, result.Message);
        }

        SetRowError(kind, message);

        if (sharedKind is { } shared)
            SetRowError(shared, message);

        return false;
    }

    /// <summary>
    /// The DAV sign-in fields are needed when a synced app uses the found server and that server refused the mail
    /// sign-in, or when an entered server refused it on Add account.
    /// </summary>
    private void RefreshDavSignInRequirement()
    {
        var isRequired = IsImapRoute &&
                         Phase == AccountSetupPhase.Features &&
                         ((UsesCalDav && !IsCalDavManualEntryVisible && _calDavAvailability == AccountSetupDavAvailability.NeedsSignIn) ||
                          (IsContactRemote && !IsCardDavManualEntryVisible && _cardDavAvailability == AccountSetupDavAvailability.NeedsSignIn) ||
                          (_isDavSignInDemanded && (UsesCalDav || IsContactRemote)));

        if (isRequired && string.IsNullOrWhiteSpace(DavUserName))
            DavUserName = EmailAddress?.Trim() ?? string.Empty;

        IsDavPasswordRequired = isRequired;
    }
}
