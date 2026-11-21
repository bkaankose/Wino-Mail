#if DEBUG
using System.Text;
using System.Text.Json;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>Hooks for <c>ImapSetupDebugActions</c>: the open form, its state without secrets, and the lab fill that types into the widgets.</summary>
internal sealed partial class ImapCalDavSettingsView
{
    private static WeakReference<ImapCalDavSettingsView>? _debugCurrent;

    public static ImapCalDavSettingsView? DebugCurrent => _debugCurrent is not null && _debugCurrent.TryGetTarget(out var view) && !view.IsReleased ? view : null;

    public ImapCalDavSettingsPageViewModel DebugViewModel => ViewModel;

    private IDisposable DebugTrack()
    {
        _debugCurrent = new WeakReference<ImapCalDavSettingsView>(this);
        return new Presentation.Linux.ActionDisposable(() =>
        {
            if (_debugCurrent is not null && _debugCurrent.TryGetTarget(out var view) && ReferenceEquals(view, this)) _debugCurrent = null;
        });
    }

    public string DebugDescribe()
    {
        var vm = ViewModel;
        var text = new StringBuilder();
        text.AppendLine($"mode create={vm.IsCreateMode} edit={vm.IsEditMode} wizard={vm.IsWizardMode} step={vm.CurrentSetupStep} stage={vm.SignInStage} busy={vm.IsBusy} busyText='{vm.BusyText}'");
        text.AppendLine($"visible signIn={vm.IsSignInStepVisible} identity={vm.IsSignInIdentityStageVisible} method={vm.IsSignInMethodStageVisible} password={vm.IsPasswordMethodVisible} oauthChoice={vm.IsOAuthChoiceVisible} oauthSignedIn={vm.IsOAuthSignedInVisible} organization={vm.IsOrganizationSignInOptionVisible}/{vm.IsOrganizationSignInFieldsVisible} servers={vm.IsServersStepVisible} editSignIn={vm.IsEditSignInVisible}");
        text.AppendLine($"primary={(vm.IsPrimaryActionVisible ? vm.PrimaryActionText : "hidden")} pageBar={(vm.IsPageInfoBarOpen ? $"{vm.PageInfoBarTitle}: {vm.PageInfoBarMessage}" : "closed")} oauthBar={(vm.IsOAuthInfoBarOpen ? vm.OAuthInfoBarMessage : "closed")}");
        text.AppendLine($"mail={vm.IsMailSupportEnabled} calendar={vm.IsCalendarSupportEnabled} caldavVisible={vm.IsCalDavSettingsVisible} carddavVisible={vm.IsCardDavSettingsVisible} pop3={vm.IsPop3}");
        text.AppendLine($"incoming '{vm.IncomingSummary}' expanded={vm.IsIncomingExpanded} security={vm.SelectedIncomingServerConnectionSecurityIndex} auth={vm.SelectedIncomingServerAuthenticationMethodIndex}");
        text.AppendLine($"outgoing '{vm.OutgoingSummary}' expanded={vm.IsOutgoingExpanded} security={vm.SelectedOutgoingServerConnectionSecurityIndex} auth={vm.SelectedOutgoingServerAuthenticationMethodIndex} shared={vm.UseIncomingCredentialsForOutgoing}");
        text.AppendLine($"caldav '{vm.CalDavSummary}' expanded={vm.IsCalDavExpanded} carddav '{vm.CardDavSummary}' expanded={vm.IsCardDavExpanded} useMail={vm.UseMailCredentialsForDav}");
        text.AppendLine($"state mail={vm.MailConnectionState} error='{vm.MailConnectionError}' caldav={vm.CalDavConnectionState} error='{vm.CalDavConnectionError}'");
        text.AppendLine($"discovery found={vm.IsDiscoveryFoundVisible} notFound={vm.IsDiscoveryNotFoundVisible} address='{vm.EmailAddress}' name='{vm.DisplayName}'");
        return text.ToString();
    }

    /// <summary>Types the lab account's settings into the form's widgets (drop-downs through the ViewModel index); logs field names only.</summary>
    public List<string> DebugFill(string leasePath, string key)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(leasePath));
        var root = document.RootElement;
        JsonElement account = default;
        foreach (var list in new[] { "accounts", "pop3Accounts", "discoveryAccounts", "oauthAccounts", "extraAccounts" })
        {
            if (!root.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("key", out var itemKey) && itemKey.GetString() == key) account = item;
            if (account.ValueKind != JsonValueKind.Undefined) break;
        }
        if (account.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException($"No lab account {key}.");
        string Read(string name) => account.TryGetProperty(name, out var value) ? value.ToString()
            : name == "password" && root.TryGetProperty("password", out var shared) ? shared.ToString() : string.Empty;

        var password = Read("password");
        var address = Read("address");
        var set = new List<string>();
        void Type(string field, string value)
        {
            if (value.Length == 0 || !_fields.TryGetValue(field, out var editable)) return;
            editable.SetText(value);
            set.Add(field);
        }
        Type("name", Read("name"));
        Type("address", address);
        Type("password", password);
        Type("incoming-host", Read("imapHost"));
        Type("incoming-port", Read("imapPort"));
        Type("incoming-user", address);
        Type("incoming-password", password);
        Type("outgoing-host", Read("smtpHost"));
        Type("outgoing-port", Read("smtpPort"));
        Type("outgoing-user", Read("smtpUsername") is { Length: > 0 } login ? login : address);
        Type("outgoing-password", password);
        Type("caldav-url", Read("davUrl"));
        Type("carddav-url", Read("cardDavUrl"));
        Type("dav-user", Read("davUsername"));
        Type("dav-password", password);
        var vm = ViewModel;
        int Security(string name) => Enum.TryParse<ImapConnectionSecurity>(Read(name), out var security)
            ? vm.AvailableConnectionSecurities.FindIndex(item => item.ImapConnectionSecurity == security) : -1;
        if (Security("imapSecurity") is >= 0 and var incoming) { vm.SelectedIncomingServerConnectionSecurityIndex = incoming; set.Add("incoming-security"); }
        if (Security("smtpSecurity") is >= 0 and var outgoing) { vm.SelectedOutgoingServerConnectionSecurityIndex = outgoing; set.Add("outgoing-security"); }
        return set;
    }

    public bool DebugType(string field, string text)
    {
        if (!_fields.TryGetValue(field, out var editable)) return false;
        editable.SetText(text);
        return true;
    }
}
#endif
