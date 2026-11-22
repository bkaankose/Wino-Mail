using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>The server sections: discovery results, the connection test, then one expander per server with its status.</summary>
internal sealed partial class ImapCalDavSettingsView
{
    private Gtk.Widget BuildServers()
    {
        var vm = ViewModel;
        var found = new WinoInfoBar(WinoInfoBarSeverity.Success);
        Bindings.Own(found);
        _form.Apply(() => found.Message = ViewModel.DiscoveryFoundMessage);
        _form.When(found.Root, s => s.IsDiscoveryFoundVisible);
        var notFound = new WinoInfoBar(WinoInfoBarSeverity.Warning);
        Bindings.Own(notFound);
        _form.Apply(() => notFound.Message = ViewModel.DiscoveryNotFoundMessage);
        _form.When(notFound.Root, s => s.IsDiscoveryNotFoundVisible);
        var oauthServers = new WinoInfoBar(WinoInfoBarSeverity.Informational);
        Bindings.Own(oauthServers);
        _form.Apply(() => oauthServers.Message = ViewModel.OAuthServerSignInText);
        _form.When(oauthServers.Root, s => s.IsOAuthServerSignInVisible);

        var test = _form.Button(Translator.ImapSetup_TestConnections, vm.TestConnectionsCommand);
        var group = OnboardingUi.Group(Translator.ImapSetup_ConnectionsGroup);
        group.SetHeaderSuffix(test);
        group.Add(BuildIncoming());
        group.Add(BuildOutgoing());
        group.Add(BuildCalDav());
        group.Add(BuildCardDav());

        return _form.When(OnboardingUi.Column(12, found.Root, notFound.Root, oauthServers.Root, group, BuildUnusedHints(), BuildPop3ServerCopies(), BuildAdvanced()),
            s => s.IsServersStepVisible);
    }

    private Adw.ExpanderRow BuildIncoming()
    {
        var vm = ViewModel;
        var status = new ConnectionStatusView();
        _form.Apply(() => status.Apply(ViewModel.IncomingConnectionState));
        var expander = Expander(WinoIconGlyph.ArrowDownload, status.Root, s => s.IncomingSettingsTitle, s => s.IncomingSummary,
            nameof(vm.IsIncomingExpanded), s => s.IsIncomingExpanded, (s, v) => s.IsIncomingExpanded = v);
        _form.When(expander, s => s.IsMailSupportEnabled);

        var error = new WinoInfoBar(WinoInfoBarSeverity.Error);
        Bindings.Own(error);
        _form.Apply(() =>
        {
            error.Title = ViewModel.MailValidationFailedTitle;
            error.Message = ViewModel.IncomingConnectionError;
        });
        expander.AddRow(_form.When(WinoSettingsRows.Custom(error.Root), s => s.HasIncomingConnectionError));
        expander.AddRow(Field("incoming-host", _form.Entry(Translator.ImapSetup_ServerHeader, nameof(vm.IncomingServer), s => s.IncomingServer, (s, v) => s.IncomingServer = v, Gtk.InputPurpose.Url)));
        expander.AddRow(Field("incoming-port", _form.Entry(Translator.IMAPSetupDialog_IncomingMailServerPort, nameof(vm.IncomingServerPort), s => s.IncomingServerPort, (s, v) => s.IncomingServerPort = v, Gtk.InputPurpose.Digits)));
        expander.AddRow(_form.Combo(Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader, s => s.AvailableConnectionSecurityDisplayNames,
            nameof(vm.SelectedIncomingServerConnectionSecurityIndex), s => s.SelectedIncomingServerConnectionSecurityIndex, (s, v) => s.SelectedIncomingServerConnectionSecurityIndex = v));
        expander.AddRow(_form.Combo(Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader, s => s.AvailableAuthenticationMethodDisplayNames,
            nameof(vm.SelectedIncomingServerAuthenticationMethodIndex), s => s.SelectedIncomingServerAuthenticationMethodIndex, (s, v) => s.SelectedIncomingServerAuthenticationMethodIndex = v));
        expander.AddRow(_form.When(Field("incoming-user", _form.Entry(Translator.IMAPSetupDialog_Username, nameof(vm.IncomingServerUsername), s => s.IncomingServerUsername, (s, v) => s.IncomingServerUsername = v)), s => s.IsIncomingPasswordSignInVisible));
        expander.AddRow(_form.When(Field("incoming-password", _form.Password(Translator.IMAPSetupDialog_Password, nameof(vm.IncomingServerPassword), s => s.IncomingServerPassword, (s, v) => s.IncomingServerPassword = v)), s => s.IsIncomingPasswordSignInVisible));
        return expander;
    }

    private Adw.ExpanderRow BuildOutgoing()
    {
        var vm = ViewModel;
        var status = new ConnectionStatusView();
        _form.Apply(() => status.Apply(ViewModel.OutgoingConnectionState));
        var expander = Expander(WinoIconGlyph.ArrowUpload, status.Root, _ => Translator.ImapSetup_OutgoingTitle, s => s.OutgoingSummary,
            nameof(vm.IsOutgoingExpanded), s => s.IsOutgoingExpanded, (s, v) => s.IsOutgoingExpanded = v);
        _form.When(expander, s => s.IsMailSupportEnabled);

        var error = new WinoInfoBar(WinoInfoBarSeverity.Error);
        Bindings.Own(error);
        _form.Apply(() => error.Message = ViewModel.OutgoingConnectionError);
        expander.AddRow(_form.When(WinoSettingsRows.Custom(error.Root), s => s.HasOutgoingConnectionError));

        expander.AddRow(Field("outgoing-host", _form.Entry(Translator.ImapSetup_ServerHeader, nameof(vm.OutgoingServer), s => s.OutgoingServer, (s, v) => s.OutgoingServer = v, Gtk.InputPurpose.Url)));
        expander.AddRow(Field("outgoing-port", _form.Entry(Translator.IMAPSetupDialog_OutgoingMailServerPort, nameof(vm.OutgoingServerPort), s => s.OutgoingServerPort, (s, v) => s.OutgoingServerPort = v, Gtk.InputPurpose.Digits)));
        expander.AddRow(_form.Combo(Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader, s => s.AvailableConnectionSecurityDisplayNames,
            nameof(vm.SelectedOutgoingServerConnectionSecurityIndex), s => s.SelectedOutgoingServerConnectionSecurityIndex, (s, v) => s.SelectedOutgoingServerConnectionSecurityIndex = v));
        expander.AddRow(_form.Combo(Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader, s => s.AvailableAuthenticationMethodDisplayNames,
            nameof(vm.SelectedOutgoingServerAuthenticationMethodIndex), s => s.SelectedOutgoingServerAuthenticationMethodIndex, (s, v) => s.SelectedOutgoingServerAuthenticationMethodIndex = v));
        expander.AddRow(_form.When(_form.Switch(Translator.ImapSetup_UseIncomingSignInForOutgoing, null, nameof(vm.UseIncomingCredentialsForOutgoing),
            s => s.UseIncomingCredentialsForOutgoing, (s, v) => s.UseIncomingCredentialsForOutgoing = v), s => s.IsOutgoingSharedSignInOptionVisible));
        expander.AddRow(_form.When(Field("outgoing-user", _form.Entry(Translator.IMAPSetupDialog_OutgoingMailServerUsername, nameof(vm.OutgoingServerUsername), s => s.OutgoingServerUsername, (s, v) => s.OutgoingServerUsername = v)), s => s.IsOutgoingCredentialsVisible));
        expander.AddRow(_form.When(Field("outgoing-password", _form.Password(Translator.IMAPSetupDialog_OutgoingMailServerPassword, nameof(vm.OutgoingServerPassword), s => s.OutgoingServerPassword, (s, v) => s.OutgoingServerPassword = v)), s => s.IsOutgoingCredentialsVisible));
        return expander;
    }

    /// <summary>A server section; its title, summary and expansion follow the ViewModel, which also folds sections after discovery.</summary>
    private Adw.ExpanderRow Expander(WinoIconGlyph glyph, Gtk.Widget? status, Func<ImapCalDavSettingsPageViewModel, string?> title, Func<ImapCalDavSettingsPageViewModel, string?> summary,
        string expandedProperty, Func<ImapCalDavSettingsPageViewModel, bool> read, Action<ImapCalDavSettingsPageViewModel, bool> write)
    {
        var expander = WinoSettingsRows.Expander(string.Empty, null, glyph);
        if (status is not null) expander.AddSuffix(status);
        _form.Apply(() =>
        {
            var text = title(ViewModel) ?? string.Empty;
            if (expander.GetTitle() != text) expander.SetTitle(text);
            var description = summary(ViewModel) ?? string.Empty;
            if (expander.GetSubtitle() != description) expander.SetSubtitle(description);
        });
        var applying = false;
        _form.Apply(() =>
        {
            var expanded = read(ViewModel);
            if (expander.GetExpanded() == expanded) return;
            applying = true;
            try { expander.SetExpanded(expanded); }
            finally { applying = false; }
        });
        Bindings.Own(GtkSignals.OnNotify(expander, "expanded", () =>
        {
            if (!applying && expander.GetExpanded() != read(ViewModel)) write(ViewModel, expander.GetExpanded());
        }));
        return expander;
    }
}
