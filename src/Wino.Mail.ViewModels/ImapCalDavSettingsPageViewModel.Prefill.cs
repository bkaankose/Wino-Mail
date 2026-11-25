using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Advanced setup opened from the address-first setup page: the page starts on the Servers step with what that page
/// found.
/// </summary>
public partial class ImapCalDavSettingsPageViewModel
{
    private void ApplyPrefill(ImapSetupPrefill prefill)
    {
        if (!string.IsNullOrWhiteSpace(prefill.EmailAddress))
            EmailAddress = prefill.EmailAddress.Trim();

        if (!string.IsNullOrWhiteSpace(prefill.SenderName))
        {
            DisplayName = prefill.SenderName.Trim();
        }
        else if (EmailAddress?.IndexOf('@') is int atIndex && atIndex > 0)
        {
            // The default here is the account name, which is the domain when the user typed none. The Servers step has
            // no name field, and the Sign in step would offer the domain, so mail would go out as
            // "example.com" <user@example.com>.
            DisplayName = EmailAddress[..atIndex];
        }
        else
        {
            // Without an address the account name is only a placeholder such as "IMAP", never a sender name.
            DisplayName = string.Empty;
        }

        if (prefill.SpecialImapProvider != SpecialImapProvider.None)
        {
            _editingSpecialImapProvider = prefill.SpecialImapProvider;
            _specialImapLoginAddress = prefill.LoginAddress;
        }

        if (prefill.ServerInformation != null)
            ApplyServerInformation(prefill.ServerInformation);

        if (!string.IsNullOrWhiteSpace(prefill.CalDavServiceUrl))
            CalDavServiceUrl = prefill.CalDavServiceUrl.Trim();

        if (!string.IsNullOrWhiteSpace(prefill.CardDavServiceUrl))
            CardDavServiceUrl = prefill.CardDavServiceUrl.Trim();

        if (!string.IsNullOrEmpty(prefill.Password))
            Password = prefill.Password;

        if (!prefill.StartOnServers)
            return;

        var isFound = !IsMailSupportEnabled || (!string.IsNullOrWhiteSpace(IncomingServer) && !string.IsNullOrWhiteSpace(OutgoingServer));

        RememberDiscoveredAuthenticationMethods();

        if (prefill.OAuthSession != null && _oauthTokenService != null && !IsPop3)
        {
            _oauthSession = prefill.OAuthSession;
            _isOAuthSessionHandedOff = false;
            SelectOAuthAuthenticationMethods();
        }

        _lastDiscoveredAddress = EmailAddress?.Trim();
        DiscoveryState = isFound ? ServerDiscoveryState.Found : ServerDiscoveryState.NotFound;
        IsIncomingExpanded = !isFound && IsMailSupportEnabled;
        IsOutgoingExpanded = !isFound && IsMailSupportEnabled;
        IsCalDavExpanded = IsCalDavSettingsVisible && string.IsNullOrWhiteSpace(CalDavServiceUrl);
        IsCardDavExpanded = IsCardDavSettingsVisible && string.IsNullOrWhiteSpace(CardDavServiceUrl);
        SignInStage = ImapSignInStage.Method;
        CurrentSetupStep = ImapSetupStep.Servers;
    }
}
