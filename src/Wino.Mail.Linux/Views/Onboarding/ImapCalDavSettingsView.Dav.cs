using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>The CalDAV and CardDAV sections, the POP3 server copies and the advanced options.</summary>
internal sealed partial class ImapCalDavSettingsView
{
    private Adw.ExpanderRow BuildCalDav()
    {
        var vm = ViewModel;
        var status = new ConnectionStatusView();
        _form.Apply(() => status.Apply(ViewModel.CalDavConnectionState));
        var expander = Expander(WinoIconGlyph.Calendar, status.Root, s => s.CalDavSectionTitle, s => s.CalDavSummary,
            nameof(vm.IsCalDavExpanded), s => s.IsCalDavExpanded, (s, v) => s.IsCalDavExpanded = v);
        _form.When(expander, s => s.IsCalDavSettingsVisible);

        var error = new WinoInfoBar(WinoInfoBarSeverity.Error, Translator.IMAPSetupDialog_ValidationFailed_Title);
        Bindings.Own(error);
        _form.Apply(() => error.Message = ViewModel.CalDavConnectionError);
        expander.AddRow(_form.When(WinoSettingsRows.Custom(error.Root), s => s.HasCalDavConnectionError));
        expander.AddRow(Field("caldav-url", _form.Entry(Translator.ImapCalDavSettingsPage_CalDavServiceUrl, nameof(vm.CalDavServiceUrl), s => s.CalDavServiceUrl, (s, v) => s.CalDavServiceUrl = v, Gtk.InputPurpose.Url)));
        expander.AddRow(_form.When(WinoSettingsRows.Action(Translator.ImapSetup_DavUsesSecureSignIn), s => s.IsCalDavOAuthSelected));
        AddDavCredentials(expander, s => s.IsDavCredentialsOptionVisible, s => s.IsDavCredentialsVisible);
        return expander;
    }

    private Adw.ExpanderRow BuildCardDav()
    {
        var vm = ViewModel;
        var expander = Expander(WinoIconGlyph.People, null, _ => Translator.ImapSetup_CardDavTitle, s => s.CardDavSummary,
            nameof(vm.IsCardDavExpanded), s => s.IsCardDavExpanded, (s, v) => s.IsCardDavExpanded = v);
        _form.When(expander, s => s.IsCardDavSettingsVisible);
        var url = Field("carddav-url", _form.Entry(Translator.ImapCalDavSettingsPage_CardDavServiceUrl, nameof(vm.CardDavServiceUrl), s => s.CardDavServiceUrl, (s, v) => s.CardDavServiceUrl = v, Gtk.InputPurpose.Url));
        expander.AddRow(url);
        WinoA11y.Description(url, Translator.ImapCalDavSettingsPage_CardDavDiscoveryPlaceholder);
        var help = WinoSettingsRows.Action(string.Empty);
        _form.Apply(() =>
        {
            var text = ViewModel.CardDavUrlDescription ?? string.Empty;
            if (help.GetTitle() != text) help.SetTitle(text);
        });
        expander.AddRow(_form.When(help, s => !string.IsNullOrEmpty(s.CardDavUrlDescription)));
        AddDavCredentials(expander, s => s.IsCardDavCredentialsOptionVisible, s => s.IsCardDavCredentialsVisible);
        return expander;
    }

    /// <summary>The "use the mail sign-in" option and the separate DAV sign-in it hides.</summary>
    private void AddDavCredentials(Adw.ExpanderRow expander, Func<ImapCalDavSettingsPageViewModel, bool> optionVisible, Func<ImapCalDavSettingsPageViewModel, bool> credentialsVisible)
    {
        var vm = ViewModel;
        expander.AddRow(_form.When(_form.Switch(Translator.ImapSetup_UseMailSignInForDav, null, nameof(vm.UseMailCredentialsForDav),
            s => s.UseMailCredentialsForDav, (s, v) => s.UseMailCredentialsForDav = v), optionVisible));
        expander.AddRow(_form.When(Field("dav-user", _form.Entry(Translator.ImapCalDavSettingsPage_DavUsername, nameof(vm.CalDavUsername), s => s.CalDavUsername, (s, v) => s.CalDavUsername = v)), credentialsVisible));
        expander.AddRow(_form.When(Field("dav-password", _form.Password(Translator.ImapCalDavSettingsPage_DavPassword, nameof(vm.CalDavPassword), s => s.CalDavPassword, (s, v) => s.CalDavPassword = v)), credentialsVisible));
    }

    /// <summary>POP3 only: whether downloaded messages stay on the server, and when they leave it.</summary>
    private Gtk.Widget BuildPop3ServerCopies()
    {
        var vm = ViewModel;
        var group = OnboardingUi.Group(Translator.POP3Setup_ServerCopiesTitle, Translator.POP3Setup_ServerCopiesDescription);
        group.Add(_form.Switch(Translator.POP3Setup_LeaveOnServerTitle, Translator.POP3Setup_LeaveOnServerDescription, nameof(vm.Pop3LeaveOnServer),
            s => s.Pop3LeaveOnServer, (s, v) => s.Pop3LeaveOnServer = v));
        var days = _form.Combo(Translator.POP3Setup_DeleteAfterDaysTitle, s => s.Pop3DeleteAfterDaysLabels, nameof(vm.SelectedPop3DeleteAfterDaysIndex),
            s => s.SelectedPop3DeleteAfterDaysIndex, (s, v) => s.SelectedPop3DeleteAfterDaysIndex = v);
        days.SetSubtitle(Translator.POP3Setup_DeleteAfterDaysDescription);
        group.Add(_form.Enabled(days, s => s.Pop3LeaveOnServer));
        group.Add(_form.Enabled(_form.Switch(Translator.POP3Setup_DeleteWithLocalTitle, Translator.POP3Setup_DeleteWithLocalDescription, nameof(vm.Pop3DeleteWithLocal),
            s => s.Pop3DeleteWithLocal, (s, v) => s.Pop3DeleteWithLocal = v), s => s.Pop3LeaveOnServer));
        return _form.When(group, s => s.IsPop3);
    }

    private Gtk.Widget BuildAdvanced()
    {
        var vm = ViewModel;
        var expander = Expander(WinoIconGlyph.Settings, null, _ => Translator.ImapSetup_AdvancedTitle, _ => Translator.ImapSetup_AdvancedDescription,
            nameof(vm.IsAdvancedExpanded), s => s.IsAdvancedExpanded, (s, v) => s.IsAdvancedExpanded = v);
        var proxy = Field("proxy-host", _form.Entry(Translator.ImapSetup_ProxyTitle, nameof(vm.ProxyServer), s => s.ProxyServer, (s, v) => s.ProxyServer = v, Gtk.InputPurpose.Url));
        WinoA11y.Description(proxy, Translator.ImapSetup_ProxyDescription);
        expander.AddRow(proxy);
        expander.AddRow(Field("proxy-port", _form.Entry(Translator.IMAPSetupDialog_IncomingMailServerPort, nameof(vm.ProxyServerPort), s => s.ProxyServerPort, (s, v) => s.ProxyServerPort = v, Gtk.InputPurpose.Digits)));
        expander.AddRow(_form.When(_form.Rows.Number(vm, Translator.ImapSetup_ConcurrencyTitle, Translator.ImapSetup_ConcurrencyDescription, WinoIconGlyph.None,
            nameof(vm.MaxConcurrentClients), s => s.MaxConcurrentClients, (s, v) => s.MaxConcurrentClientsValue = v, 1, 20), s => s.IsMailAdvancedOptionsVisible));
        expander.AddRow(_form.When(_form.Switch(Translator.SettingsAccountManagementAppendMessage_Title, Translator.SettingsAccountManagementAppendMessage_Description,
            nameof(vm.ShouldAppendMessagesToSentFolder), s => s.ShouldAppendMessagesToSentFolder, (s, v) => s.ShouldAppendMessagesToSentFolder = v), s => s.IsMailAdvancedOptionsVisible));
        var group = OnboardingUi.Group();
        group.Add(expander);
        return group;
    }
}
