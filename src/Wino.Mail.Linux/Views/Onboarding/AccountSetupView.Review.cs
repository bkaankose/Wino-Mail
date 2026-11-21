using Wino.Core.Domain;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>The sign-in method choice and the review (Windows ChooseImapOption and Review templates).</summary>
internal sealed partial class AccountSetupView
{
    private readonly List<AccountSetupChoiceCard> _choiceCards = [];
    private readonly List<AccountSetupAppRowView> _appRowViews = [];
    private Gtk.Box _choices = null!;
    private Adw.PreferencesGroup _appRows = null!;
    private BindingScope? _choiceScope;
    private BindingScope? _appRowScope;
    private List<AccountSetupChoice>? _shownChoices;
    private List<AccountSetupAppRow>? _shownAppRows;

    private Gtk.Widget BuildSignInMethodStep()
    {
        _choices = OnboardingUi.Column(6);
        _choices.AccessibleRole = Gtk.AccessibleRole.RadioGroup;
        WinoA11y.Label(_choices, Translator.AccountSetup_SignInOptionsHeader);
        _form.Apply(RenderChoices);
        var caption = _form.Text(OnboardingUi.Caption(), s => s.StepCaption);
        _form.When(caption, s => s.HasStepCaption);
        return OnboardingUi.Column(8, caption, _choices);
    }

    private void RenderChoices()
    {
        var vm = ViewModel;
        if (!ReferenceEquals(vm.StepChoices, _shownChoices))
        {
            _shownChoices = vm.StepChoices;
            _choiceScope?.Dispose();
            var scope = _choiceScope = Bindings.Own(new BindingScope());
            foreach (var card in _choiceCards) _choices.Remove(card.Toggle);
            _choiceCards.Clear();
            Gtk.ToggleButton? group = null;
            foreach (var choice in vm.StepChoices)
            {
                var card = new AccountSetupChoiceCard(choice, group);
                group ??= card.Toggle;
                var selected = card;
                scope.Own(GtkSignals.OnToggled(card.Toggle, () =>
                {
                    if (selected.Toggle.GetActive() && !ReferenceEquals(ViewModel.SelectedChoice, selected.Choice)) ViewModel.SelectedChoice = selected.Choice;
                }));
                _choiceCards.Add(card);
                _choices.Append(card.Toggle);
            }
        }
        foreach (var card in _choiceCards)
        {
            var active = ReferenceEquals(card.Choice, vm.SelectedChoice);
            if (card.Toggle.GetActive() != active) card.Toggle.SetActive(active);
        }
    }

    private Gtk.Widget? SelectedChoiceCard()
        => (_choiceCards.FirstOrDefault(card => ReferenceEquals(card.Choice, ViewModel.SelectedChoice)) ?? _choiceCards.FirstOrDefault())?.Toggle;

    /// <summary>Every app in one place. The IMAP route arrives here signed in; Gmail, Outlook and Exchange sign in from the footer.</summary>
    private Gtk.Widget BuildReviewStep()
    {
        var vm = ViewModel;
        var signedIn = new WinoInfoBar(WinoInfoBarSeverity.Success, null, Translator.AccountSetup_Review_SignedIn);
        Bindings.Own(signedIn);
        _form.When(signedIn.Root, s => s.IsReviewSignedInVisible);
        var pop3 = new WinoInfoBar(WinoInfoBarSeverity.Informational, Translator.POP3Setup_LimitationsTitle, Translator.POP3Setup_LimitationsMessage);
        Bindings.Own(pop3);
        _form.When(pop3.Root, s => s.IsPop3NoticeVisible);

        var (nameGroup, name) = BuildNameRow();
        Field("review-name", name);
        var nameError = _form.Text(OnboardingUi.Error(), s => s.AccountNameError);
        _form.When(nameError, s => s.HasAccountNameError);

        var sendAs = _form.Entry(Translator.AccountSetup_SendAsHeader, nameof(vm.SenderName), s => s.SenderName, (s, v) => s.SenderName = v);
        Field("review-sender", sendAs);
        var sendAsGroup = OnboardingUi.Group(null, Translator.AccountSetup_SendAsDescription);
        sendAsGroup.Add(sendAs);
        _form.When(sendAsGroup, s => s.IsReviewSendAsVisible);

        _appRows = OnboardingUi.Group();
        _form.Apply(RenderAppRows);
        Bindings.Own(new ActionDisposable(DetachAppRows));

        var useMailSignIn = _form.Check(Translator.AccountSetup_UseMailSignIn, nameof(vm.UseMailSignInForDav), s => s.UseMailSignInForDav, (s, v) => s.UseMailSignInForDav = v);
        _form.When(useMailSignIn, s => s.IsUseMailSignInForDavVisible);

        var davUser = Field("dav-user", _form.Entry(Translator.AccountSetup_DavUserNameHeader, nameof(vm.DavUserName), s => s.DavUserName, (s, v) => s.DavUserName = v));
        var davPassword = Field("dav-password", _form.Password(Translator.AccountSetup_DavPasswordHeader, nameof(vm.DavPassword), s => s.DavPassword, (s, v) => s.DavPassword = v));
        var davGroup = OnboardingUi.Group(null, Translator.AccountSetup_Review_DavSignInDescription);
        davGroup.Add(davUser);
        davGroup.Add(davPassword);
        _form.When(davGroup, s => s.IsReviewDavSignInVisible);

        var featuresError = _form.Text(OnboardingUi.Error(), s => s.FeaturesError);
        _form.When(featuresError, s => s.HasFeaturesError);
        var caption = _form.Text(OnboardingUi.Caption(), s => s.ReviewCaption);
        _form.When(caption, s => s.HasReviewCaption);

        var spinner = OnboardingUi.Spinner();
        var statusText = _form.Text(OnboardingUi.Body(), s => s.ReviewStatusText);
        var status = _form.When(OnboardingUi.Centered(spinner, statusText), s => s.IsReviewStatusVisible);

        return OnboardingUi.Column(12, signedIn.Root, pop3.Root, OnboardingUi.Column(4, nameGroup, nameError), sendAsGroup, _appRows,
            useMailSignIn, davGroup, BuildServerDetails(), featuresError, caption, status, BuildFailureBar(offersAnotherOption: false));
    }

    private void RenderAppRows()
    {
        var vm = ViewModel;
        if (!ReferenceEquals(vm.AppRows, _shownAppRows))
        {
            DetachAppRows();
            _shownAppRows = vm.AppRows;
            var scope = _appRowScope = new BindingScope();
            foreach (var row in vm.AppRows)
            {
                var view = new AccountSetupAppRowView(row, scope);
                _form.Track(row, scope);
                _appRowViews.Add(view);
                _appRows.Add(view.Main);
                _appRows.Add(view.Server);
                _appRows.Add(view.Notes);
                Field("server-" + row.Kind.ToString().ToLowerInvariant(), view.ServerField);
            }
        }
        var enabled = !vm.IsReviewStatusVisible;
        foreach (var view in _appRowViews)
        {
            view.Apply();
            view.SetEnabled(enabled);
        }
    }

    private void DetachAppRows()
    {
        _appRowScope?.Dispose();
        _appRowScope = null;
        foreach (var view in _appRowViews)
        {
            _appRows.Remove(view.Main);
            _appRows.Remove(view.Server);
            _appRows.Remove(view.Notes);
        }
        _appRowViews.Clear();
        _shownAppRows = null;
    }

    /// <summary>The checked IMAP servers, folded away; Edit servers opens the advanced page.</summary>
    private Gtk.Widget BuildServerDetails()
    {
        var vm = ViewModel;
        var expander = WinoSettingsRows.Expander(Translator.AccountSetup_Review_ServerDetails);
        void Row(string title, Func<AccountSetupPageViewModel, string> read)
        {
            var row = WinoSettingsRows.Action(title);
            row.SetSubtitleSelectable(true);
            _form.Apply(() =>
            {
                var value = read(ViewModel) ?? string.Empty;
                if (row.GetSubtitle() != value) row.SetSubtitle(value);
            });
            expander.AddRow(row);
        }
        Row(Translator.AccountSetup_Check_Incoming, s => s.ServerDetailsIncoming);
        Row(Translator.AccountSetup_Check_Outgoing, s => s.ServerDetailsOutgoing);
        Row(Translator.ProviderSelection_SourceCalDav, s => s.ServerDetailsCalDav);
        Row(Translator.ProviderSelection_SourceCardDav, s => s.ServerDetailsCardDav);
        var edit = _form.Link(Translator.AccountSetup_Review_EditServers, vm.OpenAdvancedSetupCommand);
        expander.AddRow(WinoSettingsRows.Custom(edit));
        var group = OnboardingUi.Group();
        group.Add(expander);
        return _form.When(group, s => s.IsServerDetailsVisible);
    }

    /// <summary>The failure bar of the sign-in and review steps, with what can be done about it.</summary>
    private Gtk.Widget BuildFailureBar(bool offersAnotherOption)
    {
        var vm = ViewModel;
        var bar = new WinoInfoBar(WinoInfoBarSeverity.Error) { IsClosable = true };
        Bindings.Own(bar);
        bar.Closed += (_, _) => _form.Run(ViewModel.HideFailureCommand);
        _form.Apply(() =>
        {
            bar.Title = ViewModel.FailureTitle;
            bar.Message = ViewModel.FailureMessage;
            bar.Severity = OnboardingUi.ToSeverity(ViewModel.FailureSeverity);
        });

        var actions = new List<Gtk.Widget> { _form.When(_form.Button(Translator.AccountSetup_Retry, vm.RetryCommand), s => s.IsRetryVisible) };
        if (offersAnotherOption)
            actions.Add(_form.When(_form.Button(Translator.AccountSetup_UseAnotherOption, vm.UseAnotherOptionCommand), s => s.IsUseAnotherOptionVisible));
        var change = OnboardingUi.Button(Translator.AccountSetup_ChangeProvider);
        Bindings.Own(GtkSignals.OnClicked(change, ShowProviderPicker));
        actions.Add(_form.When(change, s => s.IsChangeProviderVisible));
        actions.Add(_form.Link(Translator.AccountSetup_AdvancedSetup, vm.OpenAdvancedSetupCommand));
        actions.Add(_form.When(_form.Link(Translator.AccountSetup_ShowDetails, vm.ShowFailureDetailsCommand), s => s.HasFailureDetails));
        var buttons = Adw.WrapBox.New();
        buttons.SetChildSpacing(8);
        buttons.SetLineSpacing(6);
        foreach (var action in actions) buttons.Append(action);
        bar.Content = buttons;

        var host = OnboardingUi.Column(0, bar.Root);
        _form.Apply(() =>
        {
            var shown = ViewModel.IsFailureOpen;
            OnboardingUi.SetVisible(host, shown);
            // The bar hides itself when its close button is used; showing the failure again shows the bar too.
            if (shown && !bar.IsOpen) bar.IsOpen = true;
        });
        return host;
    }
}
