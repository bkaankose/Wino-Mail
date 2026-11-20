using Android.Views;
using AndroidX.AppCompat.Widget;
using Google.Android.Material.MaterialSwitch;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using Wino.Mail.ViewModels.Common;
using Wino.Mail.ViewModels.Common.Data;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.WinoAccountSettings;

/// <summary>
/// Smart Labels and Daily Briefing (Windows MailClassificationPage, Mac MailClassificationPageViewController): the
/// provider access notice, Open daily briefing, then one row per mail account with its indexing switch; the row opens
/// that account's Wino Intelligence page.
/// </summary>
public sealed class MailClassificationPageFragment : SettingsPageFragment<WinoAccountManagementPageViewModel>
{
    private IPictureStorageService? _pictures;
    private WinoSettingsGroup? _mailboxes;
    private BindingScope? _rowScope;
    private bool _rebuildQueued;

    protected override void BuildPage()
    {
        var vm = VM;
        var services = MainApplication.Services;
        _pictures = services?.GetService<IPictureStorageService>();
        // The presenter receives "Open daily briefing"; it must exist before the request is sent.
        _ = services?.GetService<Extras.AndroidDailyBriefingPresenter>();
        _rebuildQueued = false;
        Bindings.Own(new ActionDisposable(() =>
        {
            _rowScope?.Dispose();
            _rowScope = null;
            _mailboxes = null;
        }));

        var dataError = InfoBar(WinoInfoBarSeverity.Error, null, null);
        Bind.Bind(vm, nameof(vm.IntelligenceDataError), s => s.IntelligenceDataError, text =>
        {
            dataError.Message = text;
            dataError.Visibility = string.IsNullOrWhiteSpace(text) ? ViewStates.Gone : ViewStates.Visible;
        });
        Add(dataError);

        var consent = InfoBar(WinoInfoBarSeverity.Informational, null, Translator.WinoAccount_IntelligenceConsentNotGranted);
        consent.ActionTitle = Translator.WinoIntelligence_Privacy_Title;
        EventHandler openPrivacy = (_, _) =>
        {
            try { if (vm.OpenIntelligencePageCommand.CanExecute(null)) vm.OpenIntelligencePageCommand.Execute(null); }
            catch (Exception exception) { ReportError(exception); }
        };
        consent.ActionInvoked += openPrivacy;
        Bindings.Own(new ActionDisposable(() => consent.ActionInvoked -= openPrivacy));
        Add(Bind.Visible(consent, vm, nameof(vm.IsConsentGranted), s => !s.IsConsentGranted));

        AddGroup(null, Card(Translator.WinoIntelligence_Briefing_OpenTitle, Translator.WinoIntelligence_Briefing_OpenDescription, WinoIconGlyph.DailyBriefing,
            Bind.Button(Translator.Buttons_Open, vm.OpenDailyBriefingCommand)));

        _mailboxes = AddGroup(Translator.WinoIntelligence_MailAccountsHeader);
        Bind.Collection(vm.IntelligenceMailboxes, QueueRebuild);
        QueueRebuild();
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        var scope = Bindings;
        Page.Post(() =>
        {
            _rebuildQueued = false;
            if (scope.IsDisposed) return;
            try { Rebuild(); }
            catch (Exception exception) { ReportError(exception); }
        });
    }

    private void Rebuild()
    {
        if (_mailboxes is not { } group) return;
        _rowScope?.Dispose();
        _rowScope = new BindingScope();
        var rows = Bind.Child(_rowScope);
        group.Clear();
        foreach (var item in VM.IntelligenceMailboxes.ToList()) group.Add(MailboxRow(item, rows));
        group.Visibility = group.Sources.Count > 0 ? ViewStates.Visible : ViewStates.Gone;
    }

    /// <summary>Account picture, address and its Smart Labels state, the indexing switch; a tap opens the account's page.</summary>
    private WinoSettingsCard MailboxRow(WinoIntelligenceMailboxItemViewModel item, SettingsBinder rows)
    {
        var context = Ctx;
        var toggle = WinoMaterialAccent.Tint(new MaterialSwitch(context) { ContentDescription = Translator.WinoIntelligence_MailClassification_ToggleName });
        if (!string.IsNullOrWhiteSpace(item.ToggleUnavailableTooltip)) TooltipCompat.SetTooltipText(toggle, item.ToggleUnavailableTooltip);
        rows.Bind(item, nameof(item.IsEnabled), i => i.IsEnabled, on => { if (toggle.Checked != on) toggle.Checked = on; });
        rows.Views.Enabled(toggle, item, nameof(item.CanChangeEnabled), i => i.CanChangeEnabled);
        rows.Views.Click(toggle, () =>
        {
            bool requested = toggle.Checked;
            // The command owns the state; the switch shows it again until the change lands.
            toggle.Checked = item.IsEnabled;
            var command = VM.ToggleIntelligenceMailboxCommand;
            if (requested != item.IsEnabled && command.CanExecute(item)) command.Execute(item);
        });

        var icon = new WinoAccountIconView(context)
        {
            Size = WinoTokens.Dp(context, Resource.Dimension.wino_winoaccount_mailbox_icon),
            DisplayName = string.IsNullOrWhiteSpace(item.Account?.Name) ? item.Address : item.Account.Name,
            Account = item.Account is not null && _pictures is not null
                ? MailAccountIconInfoFactory.Create(item.Account, _pictures)
                : MailAccountIconInfoFactory.CreateProviderFallback(item.ProviderType, item.SpecialProvider)
        };
        var card = Card(item.Address, item.SmartLabelsDescription, WinoIconGlyph.None, toggle);
        card.LeadingView = icon;
        rows.Bind(item, nameof(item.SmartLabelsDescription), i => i.SmartLabelsDescription, text => card.Description = text);
        if (item.CanManage && item.OpenCommand is { } open)
        {
            card.IsClickable = true;
            EventHandler handler = (_, _) =>
            {
                try { if (open.CanExecute(item)) open.Execute(item); }
                catch (Exception exception) { ReportError(exception); }
            };
            card.Activated += handler;
            _rowScope?.Own(new ActionDisposable(() => card.Activated -= handler));
        }
        return card;
    }
}
