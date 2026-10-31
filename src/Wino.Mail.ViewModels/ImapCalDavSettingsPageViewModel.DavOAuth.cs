#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authorization;
using Wino.Core.Domain.Models.CardDav;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// CalDAV and CardDAV with the account's OAuth token. Sign-in requests the calendars and contacts scopes for the modes
/// the user chose, and <see cref="DavOAuthRule"/> decides per service whether the token or a DAV password is used.
/// </summary>
public partial class ImapCalDavSettingsPageViewModel
{
    private readonly IDavOAuthAuthenticationProvider? _davOAuthAuthenticationProvider;

    private IReadOnlyCollection<string> _davGrantedScopes = Array.Empty<string>();
    private IReadOnlyList<string> _editSupportedScopes = Array.Empty<string>();
    private bool _initialCalDavEnabled;
    private bool _initialCardDavEnabled;
    private int _davScopeCheckVersion;
    private bool _isDavModeTrackingEnabled;

    /// <summary>
    /// The chosen calendar or contacts mode needs a scope the server supports but the last sign-in did not request.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDavScopeSignInRequiredForSetupVisible))]
    [NotifyPropertyChangedFor(nameof(IsDavScopeSignInRequiredForEditVisible))]
    public partial bool IsDavScopeSignInRequired { get; set; }

    public bool IsCalDavOAuthSelected => UsesDavToken(DavServiceKind.CalDav, CurrentIncomingMethod, CalDavServiceUrl, IncomingServer);
    public bool IsCardDavOAuthSelected => UsesDavToken(DavServiceKind.CardDav, CurrentIncomingMethod, CardDavServiceUrl, IncomingServer);

    public bool IsDavScopeSignInRequiredForSetupVisible => IsOAuthSignedInVisible && IsDavScopeSignInRequired;
    public bool IsDavScopeSignInRequiredForEditVisible => IsEditOAuthSignInVisible && IsDavScopeSignInRequired;

    public string CardDavUrlDescription => IsCardDavOAuthSelected
        ? Translator.ImapSetup_DavUsesSecureSignIn
        : IsCalDavSettingsVisible && IsCalDavOAuthSelected
            ? string.Empty
            : Translator.ImapSetup_CardDavSharedSignIn;

    private bool IsCalDavModeSelected => !IsPop3 && IsCalendarSupportEnabled && SelectedCalendarSupportMode == ImapCalendarSupportMode.CalDav;

    private ImapAuthenticationMethod CurrentIncomingMethod => GetAuthenticationMethodByIndex(SelectedIncomingServerAuthenticationMethodIndex);

    private AccountOAuthProfile? CurrentOAuthProfile => IsCreateMode ? _oauthSession?.Profile : _storedOAuthProfile;

    private string? CurrentOAuthIssuer => CurrentOAuthProfile?.Issuer;

    private OAuthDavSettings GetDavSettings(AccountOAuthProfile? profile) => _knownImapProviderCatalog.GetOAuthDavSettings(profile);

    private Guid? CurrentOAuthTokenKey => IsCreateMode
        ? _oauthSession?.Key
        : _storedOAuthProfile != null ? _editingAccountId : null;

    private bool UsesDavToken(DavServiceKind kind, ImapAuthenticationMethod incomingMethod, string? davUrl, string? imapHost)
        => incomingMethod == ImapAuthenticationMethod.OAuth2
           && _davOAuthAuthenticationProvider != null
           && CurrentOAuthTokenKey != null
           && DavOAuthRule.UsesToken(incomingMethod, _davGrantedScopes, kind, davUrl, imapHost, CurrentOAuthIssuer, GetDavSettings(CurrentOAuthProfile));

    private bool UsesDavToken(CustomServerInformation serverInformation, DavServiceKind kind)
        => UsesDavToken(
            kind,
            serverInformation.IncomingAuthenticationMethod,
            kind == DavServiceKind.CalDav ? serverInformation.CalDavServiceUrl : serverInformation.CardDavServiceUrl,
            serverInformation.IncomingServer);

    private async Task<DavAuthenticationProfile?> TryCreateDavBearerAsync(CustomServerInformation serverInformation, DavServiceKind kind, Uri serviceUri)
    {
        var provider = _davOAuthAuthenticationProvider;
        var tokenKey = CurrentOAuthTokenKey;
        if (provider == null || tokenKey == null || !UsesDavToken(serverInformation, kind))
            return null;

        return await provider.TryCreateAsync(tokenKey.Value, serverInformation, CurrentOAuthProfile, kind, serviceUri);
    }

    /// <summary>A DAV password is not stored when the token covers every DAV service the account uses.</summary>
    private void ApplyDavTokenCredentialPolicy(CustomServerInformation serverInformation)
    {
        var usesCalDav = serverInformation.CalendarSupportMode == ImapCalendarSupportMode.CalDav;
        var usesCardDav = !string.IsNullOrWhiteSpace(serverInformation.CardDavServiceUrl);
        if (!usesCalDav && !usesCardDav)
            return;

        if ((!usesCalDav || UsesDavToken(serverInformation, DavServiceKind.CalDav)) &&
            (!usesCardDav || UsesDavToken(serverInformation, DavServiceKind.CardDav)))
        {
            serverInformation.CalDavPassword = string.Empty;
        }
    }

    private AccountOAuthProfile PrepareProfileForSelectedModes(
        AccountOAuthProfile profile,
        IReadOnlyCollection<string> supportedScopes,
        IEnumerable<string>? registeredScopes)
        => OAuthSignInProfile.PrepareForModes(profile, supportedScopes, registeredScopes, IsCalDavModeSelected, _isCardDavEnabled, GetDavSettings(profile));

    /// <summary>Discovery registers its client with every supported scope, so a sign-in with that client can request any of them.</summary>
    private IEnumerable<string>? GetSetupRegisteredScopes(AccountOAuthProfile profile)
        => _oauthDiscovery.Profile is { } discovered &&
           !string.IsNullOrWhiteSpace(discovered.ClientId) &&
           string.Equals(discovered.ClientId, profile.ClientId, StringComparison.Ordinal)
            ? OAuthScopes.Split(discovered.Scopes)
            : null;

    private async Task RefreshDavGrantedScopesAsync()
    {
        var tokenService = _oauthTokenService;
        var tokenKey = CurrentOAuthTokenKey;
        IReadOnlyCollection<string> scopes = Array.Empty<string>();

        if (tokenService != null && tokenKey != null)
        {
            try
            {
                scopes = await tokenService.GetGrantedScopesAsync(tokenKey.Value);
            }
            catch (Exception ex)
            {
                Log.Warning("The granted OAuth scopes could not be read ({ErrorType}).", ex.GetType().Name);
            }
        }

        _davGrantedScopes = scopes;
        NotifyDavSignInLayoutChanged();
    }

    private void RememberInitialDavModes()
    {
        _initialCalDavEnabled = IsCalDavModeSelected;
        _initialCardDavEnabled = _isCardDavEnabled;
        _editSupportedScopes = Array.Empty<string>();
    }

    private void ResetDavOAuthState()
    {
        _davScopeCheckVersion++;
        _davGrantedScopes = Array.Empty<string>();
        IsDavScopeSignInRequired = false;
        NotifyDavSignInLayoutChanged();
    }

    /// <summary>
    /// Runs when the calendar or contacts mode changes. While adding an account, a session that did not request a now
    /// needed scope sends the user back to its sign-in card. While editing, only a mode the user turns on asks for a new
    /// sign-in, so accounts that already use DAV passwords are left alone.
    /// </summary>
    private void OnDavModesChanged()
    {
        NotifyDavSignInLayoutChanged();

        if (!_isDavModeTrackingEnabled)
            return;

        if (IsCreateMode)
        {
            UpdateSetupDavScopeRequirement();
            return;
        }

        _ = UpdateEditDavScopeRequirementAsync();
    }

    private void UpdateSetupDavScopeRequirement()
    {
        var session = _oauthSession;
        var isRequired = session != null &&
                         IsOAuthMethodChosen &&
                         DavOAuthRule.GetMissingScopes(OAuthScopes.Split(session.Profile.Scopes), _oauthDiscovery.SupportedScopes,
                             IsCalDavModeSelected, _isCardDavEnabled, GetDavSettings(session.Profile)).Count > 0;

        IsDavScopeSignInRequired = isRequired;

        if (isRequired && CurrentSetupStep == ImapSetupStep.Servers)
        {
            CurrentSetupStep = ImapSetupStep.SignIn;
            SignInStage = ImapSignInStage.Method;
        }

        NotifyDavSignInLayoutChanged();
    }

    private async Task UpdateEditDavScopeRequirementAsync()
    {
        var version = ++_davScopeCheckVersion;
        var profile = _storedOAuthProfile;
        var isCalDavNewlyEnabled = IsCalDavModeSelected && !_initialCalDavEnabled;
        var isCardDavNewlyEnabled = _isCardDavEnabled && !_initialCardDavEnabled;

        if (!IsEditMode || profile == null || !IsIncomingOAuthSelected || (!isCalDavNewlyEnabled && !isCardDavNewlyEnabled))
        {
            IsDavScopeSignInRequired = false;
            NotifyDavSignInLayoutChanged();
            return;
        }

        try
        {
            var supportedScopes = await GetEditSupportedScopesAsync(profile);
            if (version != _davScopeCheckVersion)
                return;

            IsDavScopeSignInRequired = DavOAuthRule.GetMissingScopes(OAuthScopes.Split(profile.Scopes), supportedScopes,
                isCalDavNewlyEnabled, isCardDavNewlyEnabled, GetDavSettings(profile)).Count > 0;
        }
        catch (Exception ex)
        {
            Log.Information("Checking whether calendar and contacts need a new OAuth sign-in failed ({ErrorType}).", ex.GetType().Name);
            if (version == _davScopeCheckVersion)
                IsDavScopeSignInRequired = false;
        }

        NotifyDavSignInLayoutChanged();
    }

    /// <summary>Reads the issuer's supported scopes again, so a server that does not offer a DAV scope keeps DAV passwords.</summary>
    private async Task<IReadOnlyList<string>> GetEditSupportedScopesAsync(AccountOAuthProfile profile)
    {
        if (_editSupportedScopes.Count > 0 || _oauthIssuerDiscoveryService == null)
            return _editSupportedScopes;

        _editSupportedScopes = await _oauthIssuerDiscoveryService.GetSupportedScopesAsync(profile);
        return _editSupportedScopes;
    }

    private async Task EnsureDavScopesSignedInAsync()
    {
        if (IsEditMode)
            await UpdateEditDavScopeRequirementAsync();

        if (IsDavScopeSignInRequired)
            throw new InvalidOperationException(Translator.ImapSetup_OAuthDavScopeSignInRequired);
    }

    private void NotifyDavSignInLayoutChanged()
    {
        OnPropertyChanged(nameof(IsCalDavOAuthSelected));
        OnPropertyChanged(nameof(IsCardDavOAuthSelected));
        OnPropertyChanged(nameof(IsDavCredentialsVisible));
        OnPropertyChanged(nameof(IsCardDavCredentialsVisible));
        OnPropertyChanged(nameof(CardDavUrlDescription));
        OnPropertyChanged(nameof(IsDavScopeSignInRequiredForSetupVisible));
        OnPropertyChanged(nameof(IsDavScopeSignInRequiredForEditVisible));
    }
}
