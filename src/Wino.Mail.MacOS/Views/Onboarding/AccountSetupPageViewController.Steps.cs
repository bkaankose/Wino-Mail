using System.ComponentModel;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Onboarding;

/// <summary>The six step templates of the Windows page (AccountSetupStepTemplateSelector), built once and shown one at a time.</summary>
public sealed partial class AccountSetupPageViewController
{
    private readonly List<AccountColorButton> _colorButtons = new();
    private readonly NSStackView _shelf = WinoLayout.HStack(12);
    private readonly List<AccountSetupShelfItemView> _shelfItems = new();
    private readonly NSStackView _choices = AccountSetupUi.Column(4);
    private readonly List<AccountSetupChoiceCard> _choiceCards = new();
    private readonly NSStackView _appRows = AccountSetupUi.Column(4);
    private readonly List<AccountSetupAppRowView> _appRowViews = new();
    private readonly NSStackView _resultRows = AccountSetupUi.Column(0);
    private NSTextField _email = null!;
    private NSTextField _localName = null!;
    private NSButton? _browserSignIn;
    private NSSecureTextField? _password;
    private NSSecureTextField? _exchangePassword;
    private List<AccountSetupProviderShelfItem>? _shownShelf;
    private List<AccountSetupChoice>? _shownChoices;
    private List<AccountSetupAppRow>? _shownAppRows;
    private List<AccountSetupResultRow>? _shownResultRows;
    private List<KnownImapProviderRegion>? _shownRegions;

    #region Address

    /// <summary>The providers Wino connects to, the email field with the recognised provider's mark, or a local account's name.</summary>
    private NSView BuildAddressStep()
    {
        var shelfHost = Centered(_shelf);
        WinoAccessibility.Label(_shelf, Translator.AccountSetup_Shelf_Header);
        When(shelfHost, s => s.IsProviderShelfVisible);
        _appliers.Add(RenderShelf);

        var emailBox = BuildEmailField();
        var shelfCaption = Wrap(AccountSetupUi.Caption());
        Text(shelfCaption, s => s.ProviderShelfCaption);
        When(shelfCaption, s => !s.HasIdentityError);
        var identityError = Wrap(AccountSetupUi.Error());
        Text(identityError, s => s.IdentityError);
        When(identityError, s => s.HasIdentityError);
        var emailGroup = AccountSetupUi.Column(6, AccountSetupUi.Header(Translator.AccountSetup_EmailHeader), emailBox, shelfCaption, identityError);
        When(emailGroup, s => s.IsEmailFieldVisible);

        var (nameRow, name) = BuildNameRow();
        _localName = name;
        var nameError = Wrap(AccountSetupUi.Error());
        Text(nameError, s => s.AccountNameError);
        When(nameError, s => s.HasAccountNameError);
        var localGroup = AccountSetupUi.Column(4, AccountSetupUi.Header(Translator.AccountSetup_AccountNameHeader), nameRow, nameError);
        When(localGroup, s => s.IsLocalOnlyMode);

        return AccountSetupUi.Column(16, shelfHost, emailGroup, localGroup);
    }

    private void RenderShelf()
    {
        var items = ViewModel.ProviderShelfItems;
        if (ReferenceEquals(items, _shownShelf)) return;
        _shownShelf = items;
        if (_shelfItems.Count != items.Count)
        {
            foreach (var view in _shelfItems)
            {
                _shelf.RemoveArrangedSubview(view);
                ViewTreeDisposal.DisposeTree(view);
            }
            _shelfItems.Clear();
            foreach (var _ in items)
            {
                var view = new AccountSetupShelfItemView();
                _shelfItems.Add(view);
                _shelf.AddArrangedSubview(view);
            }
        }
        for (int index = 0; index < items.Count; index++) _shelfItems[index].Apply(items[index]);
    }

    /// <summary>A recognised address shows its provider's mark inside the field, on the leading edge.</summary>
    private NSView BuildEmailField()
    {
        var vm = ViewModel;
        var field = new NSTextField { TranslatesAutoresizingMaskIntoConstraints = false };
        var original = field.Cell;
        var cell = new AccountSetupAddressCell
        {
            Editable = true,
            Selectable = true,
            Bezeled = true,
            BezelStyle = original.BezelStyle,
            DrawsBackground = original.DrawsBackground,
            BackgroundColor = original.BackgroundColor,
            TextColor = original.TextColor,
            Font = original.Font,
            Scrollable = true,
            Wraps = false,
            UsesSingleLineMode = true,
            LineBreakMode = NSLineBreakMode.TruncatingTail,
            PlaceholderString = Translator.AccountSetup_EmailPlaceholder
        };
        field.Cell = cell;
        field.ContentType = NSTextContentType.EmailAddress;
        field.AccessibilityLabel = Translator.AccountSetup_EmailHeader;
        _email = field;

        var binding = Bindings.Own(new PropertyBinding<AccountSetupPageViewModel, string>(vm, nameof(vm.EmailAddress),
            s => s.EmailAddress ?? string.Empty, value => { if (field.StringValue != value) field.StringValue = value; },
            Dispatcher, ReportError, (s, value) => s.EmailAddress = value));
        EventHandler changed = (_, _) => binding.UpdateSource(field.StringValue);
        field.Changed += changed;
        Bindings.Own(new ActionDisposable(() => field.Changed -= changed));

        var mark = new WinoIconView(WinoIconGlyph.Mail, 16);
        WinoLayout.Size(mark, 16, 16);
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(field, host);
        host.AddSubview(mark);
        NSLayoutConstraint.ActivateConstraints(
        [
            mark.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor, 8),
            mark.CenterYAnchor.ConstraintEqualTo(field.CenterYAnchor)
        ]);

        _appliers.Add(() =>
        {
            var matched = ViewModel.HasMatchedProvider;
            mark.Hidden = !matched;
            mark.Icon = ViewModel.MatchedProviderGlyph;
            var inset = (nfloat)(matched ? 22 : 0);
            if (cell.LeadingInset == inset) return;
            cell.LeadingInset = inset;
            field.NeedsDisplay = true;
            // The field editor keeps the frame it started with; restart it so the text clears the mark.
            if (field.CurrentEditor is NSTextView editor && field.Window is { } window)
            {
                var selection = editor.SelectedRange;
                window.EndEditingFor(field);
                window.MakeFirstResponder(field);
                if (field.CurrentEditor is NSTextView restarted) restarted.SelectedRange = selection;
            }
        });
        return host;
    }

    /// <summary>The account name with the account colour button beside it (local setup and the review).</summary>
    private (NSView Row, NSTextField Field) BuildNameRow()
    {
        var vm = ViewModel;
        var name = _form.Text(nameof(vm.AccountName), s => s.AccountName, (s, v) => s.AccountName = v,
            Translator.AccountSetup_AccountNamePlaceholder, Translator.AccountSetup_AccountNameHeader);
        var colors = new AccountColorButton();
        WinoLayout.Size(colors, 40);
        colors.ColorSelected += (_, hex) =>
        {
            var color = ViewModel.AvailableColors.FirstOrDefault(item => string.Equals(item.Hex, hex, StringComparison.OrdinalIgnoreCase));
            if (color is not null) ViewModel.SelectedColor = color;
        };
        colors.Cleared += (_, _) => Run(ViewModel.SelectColorCommand);
        _colorButtons.Add(colors);
        _appliers.Add(() =>
        {
            var hex = ViewModel.SelectedColor?.Hex;
            if (!string.Equals(colors.SelectedHex, hex, StringComparison.OrdinalIgnoreCase)) colors.SelectedHex = hex;
        });
        var row = WinoLayout.HStack(8, name, colors);
        return (row, name);
    }

    #endregion

    #region Discovery

    private NSView BuildDiscoveryStep()
    {
        var vm = ViewModel;
        var slow = Wrap(AccountSetupUi.Caption(vm.DiscoverySlowText));
        When(slow, s => s.IsResolveSlow);

        var bar = new WinoInfoBar(WinoInfoBarSeverity.Warning);
        _appliers.Add(() =>
        {
            bar.Title = ViewModel.NotFoundTitle;
            bar.Message = ViewModel.NotFoundMessage;
        });
        var manual = Command(Translator.AccountSetup_SetUpManually, vm.OpenAdvancedSetupCommand);
        manual.BezelColor = WinoStyle.Accent;
        _appliers.Add(() => { if (manual.Title != ViewModel.NotFoundManualText) manual.Title = ViewModel.NotFoundManualText; });
        var retry = Command(Translator.AccountSetup_Retry, vm.ResolveAgainCommand);
        var choose = ClickLink(Translator.AccountSetup_ChooseProvider, ShowProviderPicker);
        When(choose, s => s.IsChooseProviderVisible);
        var notFound = new AccountSetupActionBar(bar, manual, retry, choose);
        When(notFound, s => s.IsNotFoundVisible);

        return AccountSetupUi.Column(12, slow, notFound);
    }

    #endregion

    #region Sign-in method

    /// <summary>The sign-in method and the server kind (Advanced setup) steps share this list of choice cards.</summary>
    private NSView BuildSignInMethodStep()
    {
        _choices.AccessibilityRole = NSAccessibilityRoles.RadioGroupRole;
        _appliers.Add(() =>
        {
            var label = ViewModel.Step == AccountSetupStep.ServerKind ? ViewModel.StepTitle : Translator.AccountSetup_SignInOptionsHeader;
            if (_choices.AccessibilityLabel != label) WinoAccessibility.Label(_choices, label);
        });
        _appliers.Add(RenderChoices);
        var caption = Wrap(AccountSetupUi.Caption());
        Text(caption, s => s.StepCaption);
        When(caption, s => s.HasStepCaption);
        return AccountSetupUi.Column(8, caption, _choices);
    }

    private void RenderChoices()
    {
        var vm = ViewModel;
        if (!ReferenceEquals(vm.StepChoices, _shownChoices))
        {
            _shownChoices = vm.StepChoices;
            foreach (var card in _choiceCards)
            {
                _choices.RemoveArrangedSubview(card);
                ViewTreeDisposal.DisposeTree(card);
            }
            _choiceCards.Clear();
            foreach (var choice in vm.StepChoices)
            {
                var card = new AccountSetupChoiceCard(choice);
                card.SetWrapWidth(_columnWidth);
                card.Pressed += (_, _) => ViewModel.SelectedChoice = card.Choice;
                card.Moved += (_, delta) => MoveChoice(card, delta);
                _choiceCards.Add(card);
                _choices.AddArrangedSubview(card);
                card.WidthAnchor.ConstraintEqualTo(_choices.WidthAnchor).Active = true;
            }
        }
        foreach (var card in _choiceCards) card.IsSelected = ReferenceEquals(card.Choice, vm.SelectedChoice);
    }

    private void MoveChoice(AccountSetupChoiceCard from, int delta)
    {
        var index = _choiceCards.IndexOf(from) + delta;
        if (index < 0 || index >= _choiceCards.Count) return;
        var target = _choiceCards[index];
        ViewModel.SelectedChoice = target.Choice;
        View.Window?.MakeFirstResponder(target);
    }

    #endregion

    #region Sign in

    /// <summary>
    /// IMAP signs in here (browser or password) and goes on to the review; an Exchange account that uses a password
    /// signs in here after its review. Gmail and Outlook never show this step.
    /// </summary>
    private NSView BuildSignInStep()
    {
        var vm = ViewModel;

        var issuer = _form.Check(string.Empty, nameof(vm.IsIssuerConfirmed), s => s.IsIssuerConfirmed, (s, v) => s.IsIssuerConfirmed = v);
        _appliers.Add(() => { if (issuer.Title != ViewModel.IssuerConfirmationText) issuer.Title = ViewModel.IssuerConfirmationText; });
        var issuerCaption = Wrap(AccountSetupUi.Caption(Translator.ImapSetup_OAuthConfirmIssuerDescription), 20);
        var issuerGroup = AccountSetupUi.Column(2, issuer, Indent(issuerCaption, 20));
        When(issuerGroup, s => s.IsIssuerConfirmationVisible);

        var browser = Command(string.Empty, vm.SignInWithBrowserCommand);
        browser.ImagePosition = NSCellImagePosition.ImageLeading;
        browser.ControlSize = NSControlSize.Large;
        _browserSignIn = browser;
        var browserGlyph = WinoIconGlyph.None;
        _appliers.Add(() =>
        {
            var text = ViewModel.BrowserSignInButtonText;
            if (browser.AccessibilityLabel != text) { browser.Title = " " + text; WinoAccessibility.Label(browser, text); }
            var glyph = BrowserSignInMark(ViewModel.RouteSpecialImapProvider);
            if (glyph == browserGlyph) return;
            browserGlyph = glyph;
            browser.Image = glyph == WinoIconGlyph.Globe ? WinoIcons.Image(glyph, 16) : WinoIcons.ColorfulImage(glyph, 16);
        });
        var browserRow = Centered(browser);
        When(browserRow, s => s.IsBrowserSignInButtonVisible);

        var exchange = BuildExchangePassword();
        var sendAs = Field(nameof(vm.SenderName), s => s.SenderName, (s, v) => s.SenderName = v, Translator.AccountSetup_SendAsHeader, out _,
            description: Translator.AccountSetup_SendAsDescription);
        When(sendAs, s => s.IsSendAsVisible);

        return AccountSetupUi.Column(12, issuerGroup, browserRow, BuildPasswordPanel(), exchange, sendAs,
            BuildConnectionStatus(), BuildFailureBar(offersAnotherOption: true));
    }

    /// <summary>The provider's mark on the browser sign-in button when Wino has one, otherwise a globe.</summary>
    private static WinoIconGlyph BrowserSignInMark(SpecialImapProvider provider)
    {
        var glyph = MailProviderGlyphs.GetGlyph(MailProviderType.IMAP4, provider);
        return glyph is WinoIconGlyph.IMAP or WinoIconGlyph.None ? WinoIconGlyph.Globe : glyph;
    }

    private NSView BuildPasswordPanel()
    {
        var vm = ViewModel;

        var hint = Wrap(AccountSetupUi.Caption());
        Text(hint, s => s.SignInCaption);
        var help = Link(string.Empty, vm.OpenHelpLinkCommand, () => ViewModel.HelpUrl, 12);
        _appliers.Add(() => { if (help.Title != ViewModel.HelpLinkText) AccountSetupUi.SetLinkTitle(help, ViewModel.HelpLinkText, 12); });
        var helpRow = AccountSetupUi.Buttons(help);
        When(helpRow, s => s.IsHelpLinkVisible);
        var hintGroup = AccountSetupUi.Column(2, hint, helpRow);
        When(hintGroup, s => s.IsSetupHintCaptionVisible);

        var region = new NSPopUpButton { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(region, Translator.ProviderSelection_RegionHeader);
        region.WidthAnchor.ConstraintGreaterThanOrEqualTo(200).Active = true;
        region.Activated += (_, _) =>
        {
            var index = (int)region.IndexOfSelectedItem;
            if (index >= 0 && index < ViewModel.Regions.Count) ViewModel.SelectedRegion = ViewModel.Regions[index];
        };
        _appliers.Add(() =>
        {
            var regions = ViewModel.Regions;
            if (!ReferenceEquals(regions, _shownRegions))
            {
                _shownRegions = regions;
                region.RemoveAllItems();
                region.AddItems(regions.Select(item => item.DisplayName).ToArray());
            }
            var index = ViewModel.SelectedRegion is null ? -1 : regions.IndexOf(ViewModel.SelectedRegion);
            if (index >= 0 && region.IndexOfSelectedItem != index) region.SelectItem(index);
        });
        var regionDescription = Wrap(AccountSetupUi.Caption());
        Text(regionDescription, s => s.RegionDescription);
        var regionGroup = AccountSetupUi.Column(4, AccountSetupUi.Header(Translator.ProviderSelection_RegionHeader), AccountSetupUi.Buttons(region), regionDescription);
        When(regionGroup, s => s.IsRegionVisible);

        var login = Field(nameof(vm.LoginAddress), s => s.LoginAddress, (s, v) => s.LoginAddress = v, string.Empty, out var loginField,
            headerRead: s => s.LoginAddressHeader, descriptionRead: s => s.LoginAddressDescription);
        loginField.ContentType = NSTextContentType.EmailAddress;
        When(login, s => s.IsLoginAddressVisible);

        var userName = Field(nameof(vm.ImapUserName), s => s.ImapUserName, (s, v) => s.ImapUserName = v, Translator.AccountSetup_UserNameHeader, out var userNameField,
            description: Translator.AccountSetup_UserNameDescription);
        _appliers.Add(() => userNameField.PlaceholderString = ViewModel.ImapUserNamePlaceholder);
        When(userName, s => s.IsImapUserNameVisible);

        var passwordHeader = AccountSetupUi.Header(string.Empty);
        Text(passwordHeader, s => s.PasswordHeader);
        _password = _form.Secure(nameof(vm.Password), s => s.Password, (s, v) => s.Password = v);
        _appliers.Add(() => _password.AccessibilityLabel = ViewModel.PasswordHeader);
        Enabled(_password, s => !s.IsConnectionStatusBusy);
        var passwordGroup = AccountSetupUi.Column(4, passwordHeader, _password);

        var otherUserName = Link(Translator.AccountSetup_UseDifferentUserName, vm.ShowImapUserNameCommand, fontSize: 12);
        var otherUserNameRow = AccountSetupUi.Buttons(otherUserName);
        When(otherUserNameRow, s => s.IsImapUserNameLinkVisible);

        var panel = AccountSetupUi.Column(12, hintGroup, regionGroup, login, userName, passwordGroup, otherUserNameRow);
        When(panel, s => s.IsPasswordPanelVisible);
        return panel;
    }

    /// <summary>Exchange without modern auth: the sign-in name when it is not the address, then the password.</summary>
    private NSView BuildExchangePassword()
    {
        var vm = ViewModel;
        var caption = Wrap(AccountSetupUi.Caption());
        Text(caption, s => s.ExchangePasswordCaption);
        var userName = Field(nameof(vm.ExchangeUserName), s => s.ExchangeUserName, (s, v) => s.ExchangeUserName = v, Translator.ExchangeSetup_UserNameHeader, out var userNameField,
            description: Translator.ExchangeSetup_UserNameDescription);
        _appliers.Add(() => userNameField.PlaceholderString = ViewModel.ExchangeUserNamePlaceholder);
        Enabled(userNameField, s => !s.IsConnectionStatusBusy);
        _exchangePassword = _form.Secure(nameof(vm.Password), s => s.Password, (s, v) => s.Password = v, Translator.ProviderSelection_PasswordHeader_Account);
        Enabled(_exchangePassword, s => !s.IsConnectionStatusBusy);
        var password = AccountSetupUi.Column(4, AccountSetupUi.Header(Translator.ProviderSelection_PasswordHeader_Account), _exchangePassword);
        var group = AccountSetupUi.Column(12, caption, userName, password);
        When(group, s => s.IsExchangePasswordVisible);
        return group;
    }

    /// <summary>One status line: waiting or checking, then signed in or all connections work.</summary>
    private NSView BuildConnectionStatus()
    {
        var spinner = AccountSetupUi.Spinner();
        var check = new WinoIconView(WinoIconGlyph.CheckmarkCircle, 16, WinoStyle.Success) { Colorful = false };
        WinoLayout.Size(check, 16, 16);
        var slot = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(slot, 16, 16);
        slot.AddSubview(spinner);
        slot.AddSubview(check);
        NSLayoutConstraint.ActivateConstraints(
        [
            spinner.CenterXAnchor.ConstraintEqualTo(slot.CenterXAnchor),
            spinner.CenterYAnchor.ConstraintEqualTo(slot.CenterYAnchor),
            check.CenterXAnchor.ConstraintEqualTo(slot.CenterXAnchor),
            check.CenterYAnchor.ConstraintEqualTo(slot.CenterYAnchor)
        ]);
        var text = Wrap(WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText, 0), 24);
        Text(text, s => s.ConnectionStatusText);
        _appliers.Add(() =>
        {
            AccountSetupUi.SetSpinning(spinner, ViewModel.IsConnectionStatusBusy);
            check.Hidden = !ViewModel.IsConnectionStatusConnected;
        });
        var row = WinoLayout.HStack(8, slot, text);
        row.Alignment = NSLayoutAttribute.CenterY;
        var host = Centered(row);
        When(host, s => s.IsConnectionStatusVisible);
        return host;
    }

    /// <summary>The failure InfoBar of the sign-in and review steps, with what can be done about it.</summary>
    private NSView BuildFailureBar(bool offersAnotherOption)
    {
        var vm = ViewModel;
        var bar = new WinoInfoBar(WinoInfoBarSeverity.Error) { IsClosable = true };
        EventHandler<WinoInfoBarClosedEventArgs> closed = (_, _) => Run(ViewModel.HideFailureCommand);
        bar.Closed += closed;
        Bindings.Own(new ActionDisposable(() => bar.Closed -= closed));
        _appliers.Add(() =>
        {
            bar.Title = ViewModel.FailureTitle;
            bar.Message = ViewModel.FailureMessage;
            bar.Severity = ToSeverity(ViewModel.FailureSeverity);
        });

        var actions = new List<NSView>();
        var retry = Command(Translator.AccountSetup_Retry, vm.RetryCommand);
        When(retry, s => s.IsRetryVisible);
        actions.Add(retry);
        if (offersAnotherOption)
        {
            var another = Command(Translator.AccountSetup_UseAnotherOption, vm.UseAnotherOptionCommand);
            When(another, s => s.IsUseAnotherOptionVisible);
            actions.Add(another);
        }
        var change = new NSButton { Title = Translator.AccountSetup_ChangeProvider, BezelStyle = NSBezelStyle.Rounded, TranslatesAutoresizingMaskIntoConstraints = false };
        change.Activated += (_, _) => ShowProviderPicker(change);
        When(change, s => s.IsChangeProviderVisible);
        actions.Add(change);
        actions.Add(Link(Translator.AccountSetup_AdvancedSetup, vm.OpenAdvancedSetupCommand));
        var details = Link(Translator.AccountSetup_ShowDetails, vm.ShowFailureDetailsCommand);
        When(details, s => s.HasFailureDetails);
        actions.Add(details);

        var failure = new AccountSetupActionBar(bar, actions.ToArray());
        ShowActionBar(failure, s => s.IsFailureOpen);
        return failure;
    }

    /// <summary>The bar hides itself when its close button is used; showing the group again shows the bar too.</summary>
    private void ShowActionBar(AccountSetupActionBar actionBar, Func<AccountSetupPageViewModel, bool> visible)
        => _appliers.Add(() =>
        {
            var shown = visible(ViewModel);
            if (actionBar.Hidden == shown) actionBar.Hidden = !shown;
            if (shown && actionBar.Bar.Hidden) actionBar.Bar.Hidden = false;
        });

    #endregion

    #region Review

    /// <summary>Every app in one place. The IMAP route arrives here signed in; Gmail, Outlook and Exchange sign in from the footer.</summary>
    private NSView BuildReviewStep()
    {
        var vm = ViewModel;
        var signedIn = new WinoInfoBar(WinoInfoBarSeverity.Success, null, Translator.AccountSetup_Review_SignedIn);
        When(signedIn, s => s.IsReviewSignedInVisible);
        var pop3 = new WinoInfoBar(WinoInfoBarSeverity.Informational, Translator.POP3Setup_LimitationsTitle, Translator.POP3Setup_LimitationsMessage);
        When(pop3, s => s.IsPop3NoticeVisible);

        var (nameRow, _) = BuildNameRow();
        var nameError = Wrap(AccountSetupUi.Error(), 0);
        Text(nameError, s => s.AccountNameError);
        When(nameError, s => s.HasAccountNameError);
        var nameGroup = AccountSetupUi.Column(4, AccountSetupUi.Header(Translator.AccountSetup_AccountNameHeader), nameRow, nameError);

        var reviewSendAs = Field(nameof(vm.SenderName), s => s.SenderName, (s, v) => s.SenderName = v, Translator.AccountSetup_SendAsHeader, out _,
            description: Translator.AccountSetup_SendAsDescription);
        When(reviewSendAs, s => s.IsReviewSendAsVisible);

        _appliers.Add(RenderAppRows);

        var useMailSignIn = _form.Check(Translator.AccountSetup_UseMailSignIn, nameof(vm.UseMailSignInForDav), s => s.UseMailSignInForDav, (s, v) => s.UseMailSignInForDav = v);
        When(useMailSignIn, s => s.IsUseMailSignInForDavVisible);

        var davCaption = Wrap(AccountSetupUi.Caption(Translator.AccountSetup_Review_DavSignInDescription));
        var davUser = Field(nameof(vm.DavUserName), s => s.DavUserName, (s, v) => s.DavUserName = v, Translator.AccountSetup_DavUserNameHeader, out _);
        var davPassword = _form.Secure(nameof(vm.DavPassword), s => s.DavPassword, (s, v) => s.DavPassword = v, Translator.AccountSetup_DavPasswordHeader);
        var davGroup = AccountSetupUi.Column(8, davCaption, davUser,
            AccountSetupUi.Column(4, AccountSetupUi.Header(Translator.AccountSetup_DavPasswordHeader), davPassword));
        When(davGroup, s => s.IsReviewDavSignInVisible);

        var featuresError = Wrap(AccountSetupUi.Error());
        Text(featuresError, s => s.FeaturesError);
        When(featuresError, s => s.HasFeaturesError);
        var caption = Wrap(AccountSetupUi.Caption());
        Text(caption, s => s.ReviewCaption);
        When(caption, s => s.HasReviewCaption);

        var spinner = AccountSetupUi.Spinner();
        var statusText = Wrap(WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText, 0), 24);
        Text(statusText, s => s.ReviewStatusText);
        var status = WinoLayout.HStack(8, spinner, statusText);
        _appliers.Add(() => AccountSetupUi.SetSpinning(spinner, ViewModel.IsReviewStatusVisible));
        var statusRow = Centered(status);
        When(statusRow, s => s.IsReviewStatusVisible);

        return AccountSetupUi.Column(12, signedIn, pop3, nameGroup, reviewSendAs, _appRows, useMailSignIn, davGroup, BuildServerDetails(),
            featuresError, caption, statusRow, BuildFailureBar(offersAnotherOption: false));
    }

    private void RenderAppRows()
    {
        var vm = ViewModel;
        if (!ReferenceEquals(vm.AppRows, _shownAppRows))
        {
            DetachAppRows();
            _shownAppRows = vm.AppRows;
            foreach (var row in vm.AppRows)
            {
                var view = new AccountSetupAppRowView(row);
                view.SetWrapWidth(_columnWidth);
                row.PropertyChanged += OnAppRowChanged;
                _appRowViews.Add(view);
                _appRows.AddArrangedSubview(view);
                view.WidthAnchor.ConstraintEqualTo(_appRows.WidthAnchor).Active = true;
            }
        }
        var enabled = !vm.IsReviewStatusVisible;
        foreach (var view in _appRowViews)
        {
            view.Apply();
            view.SetEnabled(enabled);
        }
    }

    private void OnAppRowChanged(object? sender, PropertyChangedEventArgs args) => QueueRefresh();

    private void DetachAppRows()
    {
        foreach (var view in _appRowViews)
        {
            view.Row.PropertyChanged -= OnAppRowChanged;
            _appRows.RemoveArrangedSubview(view);
            ViewTreeDisposal.DisposeTree(view);
        }
        _appRowViews.Clear();
        _shownAppRows = null;
    }

    /// <summary>The checked IMAP servers, folded away; Edit servers opens the advanced page.</summary>
    private NSView BuildServerDetails()
    {
        var vm = ViewModel;
        var grid = new NSGridView { TranslatesAutoresizingMaskIntoConstraints = false, RowSpacing = 4, ColumnSpacing = 16 };
        void Row(string label, Func<AccountSetupPageViewModel, string> read)
        {
            var value = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.PrimaryText, 0);
            value.Selectable = true;
            value.PreferredMaxLayoutWidth = 320;
            Text(value, read);
            grid.AddRow([AccountSetupUi.Caption(label), value]);
        }
        Row(Translator.AccountSetup_Check_Incoming, s => s.ServerDetailsIncoming);
        Row(Translator.AccountSetup_Check_Outgoing, s => s.ServerDetailsOutgoing);
        Row(Translator.ProviderSelection_SourceCalDav, s => s.ServerDetailsCalDav);
        Row(Translator.ProviderSelection_SourceCardDav, s => s.ServerDetailsCardDav);
        var edit = Link(Translator.AccountSetup_Review_EditServers, vm.OpenAdvancedSetupCommand);
        var body = WinoLayout.VStack(8, grid, edit);

        var expander = new WinoSettingsExpander(Translator.AccountSetup_Review_ServerDetails);
        expander.Add(body, 12, WinoSettingsStyle.CardPadding, 12, WinoSettingsStyle.CardPadding);
        When(expander, s => s.IsServerDetailsVisible);
        return expander;
    }

    #endregion

    #region Progress

    /// <summary>Adding, Added and Failed: the setup steps, then the result or the failure with its actions.</summary>
    private NSView BuildProgressStep()
    {
        var vm = ViewModel;
        var steps = new AccountSetupStepList();
        Bindings.Own(steps.Bind(vm.Steps, Dispatcher, ReportError));

        var added = new WinoInfoBar(WinoInfoBarSeverity.Success);
        _appliers.Add(() => added.Message = ViewModel.AddedMessage);
        When(added, s => s.IsAddedVisible);

        _appliers.Add(RenderResultRows);
        var resultHost = Centered(_resultRows);
        When(resultHost, s => s.IsAddedVisible);

        var failedBar = new WinoInfoBar(WinoInfoBarSeverity.Error, null, Translator.AccountSetup_FailureMessage);
        var tryAgain = Command(Translator.AccountSetup_TryAgainButton, vm.TryAgainCommand);
        tryAgain.BezelColor = WinoStyle.Accent;
        var goBack = Command(Translator.AccountSetup_GoBackButton, vm.GoBackFromFailureCommand);
        var details = Link(Translator.AccountSetup_ShowDetails, vm.ShowSetupFailureDetailsCommand);
        When(details, s => s.HasSetupFailureDetails);
        var failed = new AccountSetupActionBar(failedBar, tryAgain, goBack, details);
        ShowActionBar(failed, s => s.IsSetupFailedVisible);

        return AccountSetupUi.Column(12, Centered(steps), added, resultHost, failed);
    }

    private void RenderResultRows()
    {
        var rows = ViewModel.ResultRows;
        if (ReferenceEquals(rows, _shownResultRows)) return;
        _shownResultRows = rows;
        foreach (var view in _resultRows.ArrangedSubviews)
        {
            _resultRows.RemoveArrangedSubview(view);
            ViewTreeDisposal.DisposeTree(view);
        }
        foreach (var row in rows)
        {
            var view = new AccountSetupResultRowView(row);
            _resultRows.AddArrangedSubview(view);
        }
    }

    #endregion

    #region Fields

    /// <summary>A labelled text field with an optional caption under it, bound two-way to one ViewModel property.</summary>
    private NSView Field(string property, Func<AccountSetupPageViewModel, string?> read, Action<AccountSetupPageViewModel, string> write, string header,
        out NSTextField field, Func<AccountSetupPageViewModel, string>? headerRead = null, string? description = null,
        Func<AccountSetupPageViewModel, string>? descriptionRead = null)
    {
        var label = AccountSetupUi.Header(header);
        if (headerRead is not null) Text(label, headerRead);
        field = _form.Text(property, read, write, null, header);
        var captured = field;
        if (headerRead is not null) _appliers.Add(() => captured.AccessibilityLabel = headerRead(ViewModel));
        var views = new List<NSView> { label, field };
        if (description is not null || descriptionRead is not null)
        {
            var caption = Wrap(AccountSetupUi.Caption(description));
            if (descriptionRead is not null) Text(caption, descriptionRead);
            views.Add(caption);
        }
        return AccountSetupUi.Column(4, views.ToArray());
    }

    private static NSView Indent(NSView view, double leading)
    {
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(view, host, 0, leading, 0, 0);
        return host;
    }

    #endregion
}
