using System.ComponentModel;
using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Core.Layout;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Onboarding;

/// <summary>The six step templates of the Windows page (AccountSetupStepTemplateSelector), built once and shown one at a time.</summary>
public sealed partial class AccountSetupPageViewController
{
    private readonly AccountSetupFlowView _shelf = new(centered: true, spacing: 8, lineSpacing: 8);
    private readonly List<AccountSetupShelfItemView> _shelfItems = new();
    private readonly UIStackView _choices = AccountSetupUi.Column(8);
    private readonly List<AccountSetupChoiceCard> _choiceCards = new();
    private readonly UIStackView _appRows = AccountSetupUi.Column(10);
    private readonly List<AccountSetupAppRowView> _appRowViews = new();
    private readonly UIStackView _resultRows = AccountSetupUi.Column(0);
    private UITextField _email = null!;
    private UITextField _localName = null!;
    private UITextField _reviewName = null!;
    private UITextField? _password;
    private UITextField? _exchangePassword;
    private List<AccountSetupProviderShelfItem>? _shownShelf;
    private List<AccountSetupChoice>? _shownChoices;
    private List<AccountSetupAppRow>? _shownAppRows;
    private List<AccountSetupResultRow>? _shownResultRows;
    private List<KnownImapProviderRegion>? _shownRegions;
    private KnownImapProviderRegion? _shownRegion;

    #region Address

    /// <summary>The providers Wino connects to, the email field with the recognised provider's mark, or a local account's name.</summary>
    private UIView BuildAddressStep()
    {
        _shelf.AccessibilityLabel = Translator.AccountSetup_Shelf_Header;
        When(_shelf, s => s.IsProviderShelfVisible);
        _appliers.Add(RenderShelf);

        var email = BuildEmailField();
        var shelfCaption = AccountSetupUi.Caption();
        Text(shelfCaption, s => s.ProviderShelfCaption);
        When(shelfCaption, s => !s.HasIdentityError && !string.IsNullOrEmpty(s.ProviderShelfCaption));
        var identityError = AccountSetupUi.Error();
        Text(identityError, s => s.IdentityError);
        When(identityError, s => s.HasIdentityError);
        var emailGroup = AccountSetupUi.Column(6, AccountSetupUi.Header(Translator.AccountSetup_EmailHeader), email, shelfCaption, identityError);
        When(emailGroup, s => s.IsEmailFieldVisible);

        var (nameRow, name) = BuildNameRow();
        _localName = name;
        SubmitOnReturn(name);
        var nameError = AccountSetupUi.Error();
        Text(nameError, s => s.AccountNameError);
        When(nameError, s => s.HasAccountNameError);
        var localGroup = AccountSetupUi.Column(6, AccountSetupUi.Header(Translator.AccountSetup_AccountNameHeader), nameRow, nameError);
        When(localGroup, s => s.IsLocalOnlyMode);

        return AccountSetupUi.Column(16, _shelf, emailGroup, localGroup);
    }

    private void RenderShelf()
    {
        var items = ViewModel.ProviderShelfItems;
        if (ReferenceEquals(items, _shownShelf)) return;
        _shownShelf = items;
        if (_shelfItems.Count != items.Count)
        {
            foreach (var view in _shelfItems) ViewTreeDisposal.DisposeTree(view);
            _shelfItems.Clear();
            foreach (var _ in items)
            {
                var view = new AccountSetupShelfItemView();
                _shelfItems.Add(view);
                _shelf.AddItem(view);
            }
        }
        for (int index = 0; index < items.Count; index++) _shelfItems[index].Apply(items[index]);
        _shelf.Reflow();
    }

    /// <summary>A recognised address shows its provider's mark inside the field, on the leading edge.</summary>
    private UITextField BuildEmailField()
    {
        var vm = ViewModel;
        var field = _form.Text(nameof(vm.EmailAddress), s => s.EmailAddress, (s, v) => s.EmailAddress = v,
            Translator.AccountSetup_EmailPlaceholder, Translator.AccountSetup_EmailHeader);
        field.KeyboardType = UIKeyboardType.EmailAddress;
        field.TextContentType = UITextContentType.EmailAddress;
        SubmitOnReturn(field);
        _email = field;

        var mark = new WinoIconView(WinoIconGlyph.Mail, 18);
        mark.TranslatesAutoresizingMaskIntoConstraints = true;
        mark.Frame = new CGRect(10, 0, 18, 18);
        var slot = new UIView(new CGRect(0, 0, 32, 18)) { UserInteractionEnabled = false };
        slot.AddSubview(mark);
        field.LeftView = slot;
        field.LeftViewMode = UITextFieldViewMode.Never;

        _appliers.Add(() =>
        {
            var matched = ViewModel.HasMatchedProvider;
            mark.Icon = ViewModel.MatchedProviderGlyph;
            var mode = matched ? UITextFieldViewMode.Always : UITextFieldViewMode.Never;
            if (field.LeftViewMode != mode) field.LeftViewMode = mode;
        });
        return field;
    }

    /// <summary>The account name with the account colour button beside it (local setup and the review).</summary>
    private (UIView Row, UITextField Field) BuildNameRow()
    {
        var vm = ViewModel;
        var name = _form.Text(nameof(vm.AccountName), s => s.AccountName, (s, v) => s.AccountName = v,
            Translator.AccountSetup_AccountNamePlaceholder, Translator.AccountSetup_AccountNameHeader);
        name.AutocapitalizationType = UITextAutocapitalizationType.Words;

        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 0, 0, 0);
        var colors = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(colors, 48, 44);
        colors.AccessibilityLabel = Translator.AccountDetailsPage_ColorPicker_Title;
        colors.AccessibilityHint = Translator.AccountDetailsPage_ColorPicker_Description;
        EventHandler pick = (_, _) => ShowColorPicker(colors);
        colors.PrimaryActionTriggered += pick;
        Bindings.Own(new ActionDisposable(() => colors.PrimaryActionTriggered -= pick));
        string? shownHex = "\0";
        _appliers.Add(() =>
        {
            var hex = ViewModel.SelectedColor?.Hex;
            if (string.Equals(hex, shownHex, StringComparison.OrdinalIgnoreCase) || colors.Configuration is not { } current) return;
            shownHex = hex;
            current.Image = WinoStyle.FromHexString(hex) is { } color
                ? Swatch(color, 18)
                : WinoIcons.Image(WinoIconGlyph.PaintBrush, 18, null, Translator.AccountDetailsPage_ColorPicker_Title);
            colors.Configuration = current;
        });

        var row = WinoLayout.HStack(8, name, colors);
        row.Alignment = UIStackViewAlignment.Center;
        return (row, name);
    }

    private static UIImage Swatch(UIColor color, double size)
    {
        var renderer = new UIGraphicsImageRenderer(new CGSize(size, size));
        return renderer.CreateImage(context =>
        {
            color.SetFill();
            UIBezierPath.FromOval(new CGRect(0, 0, size, size)).Fill();
        }).ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
    }

    /// <summary>The account colours in a popover at the colour button, with Clear colour under them once one is chosen.</summary>
    private void ShowColorPicker(UIView anchor)
    {
        var picker = new WinoColorSwatchPicker
        {
            Colors = ViewModel.AvailableColors.Select(color => color.Hex).ToList(),
            SelectedHex = ViewModel.SelectedColor?.Hex
        };
        picker.AccessibilityLabel = Translator.AccountDetailsPage_ColorPicker_Title;
        picker.SelectionChanged += (_, hex) =>
        {
            var color = ViewModel.AvailableColors.FirstOrDefault(item => string.Equals(item.Hex, hex, StringComparison.OrdinalIgnoreCase));
            if (color is not null) ViewModel.SelectedColor = color;
            ClosePopover();
        };
        var clear = AccountSetupUi.Link(Translator.ProviderSelection_ClearColor);
        clear.HorizontalAlignment = UIControlContentHorizontalAlignment.Center;
        clear.Hidden = !ViewModel.IsColorSelected;
        clear.PrimaryActionTriggered += (_, _) =>
        {
            Run(ViewModel.SelectColorCommand);
            ClosePopover();
        };
        var stack = AccountSetupUi.Column(8, picker, clear);
        var controller = new UIViewController { ModalPresentationStyle = UIModalPresentationStyle.Popover };
        controller.View = new UIView();
        WinoLayout.Fill(stack, controller.View, 14);
        const double pickerWidth = 232;
        var pickerHeight = WinoSwatchGridLayout.Height(picker.Colors.Count, pickerWidth);
        picker.WidthAnchor.ConstraintEqualTo((nfloat)pickerWidth).Active = true;
        picker.HeightAnchor.ConstraintEqualTo((nfloat)pickerHeight).Active = true;
        var clearHeight = clear.Hidden ? 0 : clear.SystemLayoutSizeFittingSize(UIView.UILayoutFittingCompressedSize).Height + 8;
        controller.PreferredContentSize = new CGSize(pickerWidth + 28, pickerHeight + clearHeight + 28);
        PresentPopover(controller, anchor);
    }

    #endregion

    #region Discovery

    private UIView BuildDiscoveryStep()
    {
        var vm = ViewModel;
        var slow = AccountSetupUi.Caption(vm.DiscoverySlowText);
        When(slow, s => s.IsResolveSlow);

        var bar = new WinoInfoBar(WinoInfoBarSeverity.Warning);
        _appliers.Add(() =>
        {
            bar.Title = ViewModel.NotFoundTitle;
            bar.Message = ViewModel.NotFoundMessage;
        });
        var manual = CommandButton(Translator.AccountSetup_SetUpManually, vm.OpenAdvancedSetupCommand, primary: true);
        _appliers.Add(() =>
        {
            if (manual.Configuration is not { } configuration || configuration.Title == ViewModel.NotFoundManualText) return;
            configuration.Title = ViewModel.NotFoundManualText;
            manual.Configuration = configuration;
        });
        var retry = CommandButton(Translator.AccountSetup_Retry, vm.ResolveAgainCommand);
        var choose = ProviderMenuButton(Translator.AccountSetup_ChooseProvider, link: true);
        When(choose, s => s.IsChooseProviderVisible);
        var notFound = new AccountSetupActionBar(bar, manual, retry, choose);
        _flows.Add(notFound.Actions);
        When(notFound, s => s.IsNotFoundVisible);

        return AccountSetupUi.Column(12, slow, notFound);
    }

    #endregion

    #region Sign-in method and server kind

    /// <summary>The sign-in method and server kind steps share this list, as on Windows (AccountSetupStepTemplateSelector).</summary>
    private UIView BuildSignInMethodStep()
    {
        _choices.AccessibilityLabel = Translator.AccountSetup_SignInOptionsHeader;
        _appliers.Add(RenderChoices);
        var caption = AccountSetupUi.Caption();
        Text(caption, s => s.StepCaption);
        When(caption, s => s.HasStepCaption);
        return AccountSetupUi.Column(10, caption, _choices);
    }

    private void RenderChoices()
    {
        var vm = ViewModel;
        if (!ReferenceEquals(vm.StepChoices, _shownChoices))
        {
            _shownChoices = vm.StepChoices;
            foreach (var card in _choiceCards) ViewTreeDisposal.DisposeTree(card);
            _choiceCards.Clear();
            foreach (var choice in vm.StepChoices)
            {
                var card = new AccountSetupChoiceCard(choice);
                card.Clicked += (sender, _) =>
                {
                    if (sender is AccountSetupChoiceCard pressed) ViewModel.SelectedChoice = pressed.Choice;
                };
                _choiceCards.Add(card);
                _choices.AddArrangedSubview(card);
            }
        }
        foreach (var card in _choiceCards) card.IsSelected = ReferenceEquals(card.Choice, vm.SelectedChoice);
    }

    #endregion

    #region Sign in

    /// <summary>
    /// IMAP signs in here (browser or password) and goes on to the review; an Exchange account that uses a password
    /// signs in here after its review. Gmail and Outlook never show this step.
    /// </summary>
    private UIView BuildSignInStep()
    {
        var vm = ViewModel;

        var issuerSwitch = new UISwitch { TranslatesAutoresizingMaskIntoConstraints = false };
        var issuerBinding = Bindings.Own(new PropertyBinding<AccountSetupPageViewModel, bool>(vm, nameof(vm.IsIssuerConfirmed),
            s => s.IsIssuerConfirmed, value => { if (issuerSwitch.On != value) issuerSwitch.SetState(value, true); }, Dispatcher, ReportError,
            (s, value) => s.IsIssuerConfirmed = value));
        EventHandler issuerChanged = (_, _) => issuerBinding.UpdateSource(issuerSwitch.On);
        issuerSwitch.ValueChanged += issuerChanged;
        Bindings.Own(new ActionDisposable(() => issuerSwitch.ValueChanged -= issuerChanged));
        WinoLayout.Priorities(issuerSwitch, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        var issuerText = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        Text(issuerText, s => s.IssuerConfirmationText);
        _appliers.Add(() => issuerSwitch.AccessibilityLabel = ViewModel.IssuerConfirmationText);
        var issuerCaption = AccountSetupUi.Caption(Translator.ImapSetup_OAuthConfirmIssuerDescription);
        var issuerLabels = AccountSetupUi.Column(2, issuerText, issuerCaption);
        issuerLabels.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        var issuerGroup = WinoLayout.HStack(WinoStyle.Space3, issuerLabels, issuerSwitch);
        issuerGroup.Alignment = UIStackViewAlignment.Center;
        When(issuerGroup, s => s.IsIssuerConfirmationVisible);

        var browser = AccountSetupUi.ProviderButton();
        BindCommand(browser, vm.SignInWithBrowserCommand);
        string? browserText = null;
        var browserGlyph = WinoIconGlyph.None;
        _appliers.Add(() =>
        {
            var text = ViewModel.BrowserSignInButtonText;
            var glyph = BrowserSignInMark(ViewModel.RouteSpecialImapProvider);
            if (text == browserText && glyph == browserGlyph) return;
            browserText = text;
            browserGlyph = glyph;
            AccountSetupUi.SetContent(browser, text, glyph == WinoIconGlyph.Globe ? WinoIcons.Image(glyph, 18) : WinoIcons.ColorfulImage(glyph, 18));
        });
        var browserRow = Centered(browser);
        When(browserRow, s => s.IsBrowserSignInButtonVisible);

        var exchange = BuildExchangePassword();
        var sendAs = Field(nameof(vm.SenderName), s => s.SenderName, (s, v) => s.SenderName = v, Translator.AccountSetup_SendAsHeader, out var sendAsField,
            description: Translator.AccountSetup_SendAsDescription);
        sendAsField.AutocapitalizationType = UITextAutocapitalizationType.Words;
        sendAsField.TextContentType = UITextContentType.Name;
        SubmitOnReturn(sendAsField);
        When(sendAs, s => s.IsSendAsVisible);

        return AccountSetupUi.Column(14, issuerGroup, browserRow, BuildPasswordPanel(), exchange, sendAs,
            BuildConnectionStatus(), BuildFailureBar(offersAnotherOption: true));
    }

    /// <summary>The provider's mark on the browser sign-in button when Wino has one, otherwise a globe.</summary>
    private static WinoIconGlyph BrowserSignInMark(SpecialImapProvider provider)
    {
        var glyph = MailProviderGlyphs.GetGlyph(MailProviderType.IMAP4, provider);
        return glyph is WinoIconGlyph.IMAP or WinoIconGlyph.None ? WinoIconGlyph.Globe : glyph;
    }

    private UIView BuildPasswordPanel()
    {
        var vm = ViewModel;

        var hint = AccountSetupUi.Caption();
        Text(hint, s => s.SignInCaption);
        var help = Link(string.Empty, vm.OpenHelpLinkCommand, () => ViewModel.HelpUrl, WinoStyle.Description);
        string? helpText = null;
        _appliers.Add(() =>
        {
            if (helpText == ViewModel.HelpLinkText) return;
            helpText = ViewModel.HelpLinkText;
            AccountSetupUi.SetTitle(help, helpText, WinoStyle.Description);
        });
        var helpRow = AccountSetupUi.Leading(help);
        When(helpRow, s => s.IsHelpLinkVisible);
        var hintGroup = AccountSetupUi.Column(2, hint, helpRow);
        When(hintGroup, s => s.IsSetupHintCaptionVisible);

        var region = new WinoPopUpButton(Translator.ProviderSelection_RegionHeader);
        region.Chosen = index =>
        {
            if (index >= 0 && index < ViewModel.Regions.Count) ViewModel.SelectedRegion = ViewModel.Regions[index];
        };
        _appliers.Add(() =>
        {
            var regions = ViewModel.Regions;
            if (ReferenceEquals(regions, _shownRegions) && ReferenceEquals(ViewModel.SelectedRegion, _shownRegion)) return;
            _shownRegions = regions;
            _shownRegion = ViewModel.SelectedRegion;
            // A pull-down that changes its selection needs at least one item; without regions the group is hidden.
            if (regions.Count == 0) region.Menu = null;
            else region.SetItems(regions.Select(item => item.DisplayName).ToArray(), ViewModel.SelectedRegion is null ? -1 : regions.IndexOf(ViewModel.SelectedRegion));
        });
        var regionDescription = AccountSetupUi.Caption();
        Text(regionDescription, s => s.RegionDescription);
        var regionGroup = AccountSetupUi.Column(4, AccountSetupUi.Header(Translator.ProviderSelection_RegionHeader), AccountSetupUi.Leading(region), regionDescription);
        When(regionGroup, s => s.IsRegionVisible);

        var login = Field(nameof(vm.LoginAddress), s => s.LoginAddress, (s, v) => s.LoginAddress = v, string.Empty, out var loginField,
            headerRead: s => s.LoginAddressHeader, descriptionRead: s => s.LoginAddressDescription);
        loginField.KeyboardType = UIKeyboardType.EmailAddress;
        loginField.TextContentType = UITextContentType.Username;
        When(login, s => s.IsLoginAddressVisible);

        var userName = Field(nameof(vm.ImapUserName), s => s.ImapUserName, (s, v) => s.ImapUserName = v, Translator.AccountSetup_UserNameHeader, out var userNameField,
            description: Translator.AccountSetup_UserNameDescription);
        userNameField.TextContentType = UITextContentType.Username;
        SubmitOnReturn(userNameField);
        _appliers.Add(() => userNameField.Placeholder = ViewModel.ImapUserNamePlaceholder);
        When(userName, s => s.IsImapUserNameVisible);

        var passwordHeader = AccountSetupUi.Header(string.Empty);
        Text(passwordHeader, s => s.PasswordHeader);
        _password = _form.Secure(nameof(vm.Password), s => s.Password, (s, v) => s.Password = v);
        SubmitOnReturn(_password);
        _appliers.Add(() => _password.AccessibilityLabel = ViewModel.PasswordHeader);
        Enabled(_password, s => !s.IsConnectionStatusBusy);
        var passwordGroup = AccountSetupUi.Column(6, passwordHeader, _password);

        var otherUserName = Link(Translator.AccountSetup_UseDifferentUserName, vm.ShowImapUserNameCommand, font: WinoStyle.Description);
        var otherUserNameRow = AccountSetupUi.Leading(otherUserName);
        When(otherUserNameRow, s => s.IsImapUserNameLinkVisible);

        var panel = AccountSetupUi.Column(14, hintGroup, regionGroup, login, userName, passwordGroup, otherUserNameRow);
        When(panel, s => s.IsPasswordPanelVisible);
        return panel;
    }

    /// <summary>Exchange without modern auth: the sign-in name when it is not the address, then the password.</summary>
    private UIView BuildExchangePassword()
    {
        var vm = ViewModel;
        var caption = AccountSetupUi.Caption();
        Text(caption, s => s.ExchangePasswordCaption);
        var userName = Field(nameof(vm.ExchangeUserName), s => s.ExchangeUserName, (s, v) => s.ExchangeUserName = v, Translator.ExchangeSetup_UserNameHeader, out var userNameField,
            description: Translator.ExchangeSetup_UserNameDescription);
        userNameField.TextContentType = UITextContentType.Username;
        SubmitOnReturn(userNameField);
        _appliers.Add(() => userNameField.Placeholder = ViewModel.ExchangeUserNamePlaceholder);
        Enabled(userNameField, s => !s.IsConnectionStatusBusy);
        _exchangePassword = _form.Secure(nameof(vm.Password), s => s.Password, (s, v) => s.Password = v, Translator.ProviderSelection_PasswordHeader_Account);
        SubmitOnReturn(_exchangePassword);
        Enabled(_exchangePassword, s => !s.IsConnectionStatusBusy);
        var password = AccountSetupUi.Column(6, AccountSetupUi.Header(Translator.ProviderSelection_PasswordHeader_Account), _exchangePassword);
        var group = AccountSetupUi.Column(14, caption, userName, password);
        When(group, s => s.IsExchangePasswordVisible);
        return group;
    }

    /// <summary>One status line: waiting or checking, then signed in or all connections work.</summary>
    private UIView BuildConnectionStatus()
    {
        var spinner = AccountSetupUi.Spinner();
        var check = new WinoIconView(WinoIconGlyph.CheckmarkCircle, 18, WinoStyle.Success) { Colorful = false };
        var slot = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(slot, 20, 20);
        WinoLayout.Size(check, 18, 18);
        slot.AddSubview(spinner);
        slot.AddSubview(check);
        NSLayoutConstraint.ActivateConstraints(
        [
            spinner.CenterXAnchor.ConstraintEqualTo(slot.CenterXAnchor),
            spinner.CenterYAnchor.ConstraintEqualTo(slot.CenterYAnchor),
            check.CenterXAnchor.ConstraintEqualTo(slot.CenterXAnchor),
            check.CenterYAnchor.ConstraintEqualTo(slot.CenterYAnchor)
        ]);
        var text = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        Text(text, s => s.ConnectionStatusText);
        _appliers.Add(() =>
        {
            AccountSetupUi.SetSpinning(spinner, ViewModel.IsConnectionStatusBusy);
            check.Hidden = !ViewModel.IsConnectionStatusConnected;
        });
        var row = WinoLayout.HStack(8, slot, text);
        row.Alignment = UIStackViewAlignment.Center;
        row.IsAccessibilityElement = true;
        _appliers.Add(() => row.AccessibilityLabel = ViewModel.ConnectionStatusText);
        var host = Centered(row);
        When(host, s => s.IsConnectionStatusVisible);
        return host;
    }

    /// <summary>The failure InfoBar of the sign-in and review steps, with what can be done about it.</summary>
    private UIView BuildFailureBar(bool offersAnotherOption)
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

        var actions = new List<UIView>();
        var retry = CommandButton(Translator.AccountSetup_Retry, vm.RetryCommand);
        When(retry, s => s.IsRetryVisible);
        actions.Add(retry);
        if (offersAnotherOption)
        {
            var another = CommandButton(Translator.AccountSetup_UseAnotherOption, vm.UseAnotherOptionCommand);
            When(another, s => s.IsUseAnotherOptionVisible);
            actions.Add(another);
        }
        var change = ProviderMenuButton(Translator.AccountSetup_ChangeProvider, link: false);
        When(change, s => s.IsChangeProviderVisible);
        actions.Add(change);
        actions.Add(Link(Translator.AccountSetup_AdvancedSetup, vm.OpenAdvancedSetupCommand));
        var details = Link(Translator.AccountSetup_ShowDetails, vm.ShowFailureDetailsCommand);
        When(details, s => s.HasFailureDetails);
        actions.Add(details);

        var failure = new AccountSetupActionBar(bar, actions.ToArray());
        _flows.Add(failure.Actions);
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
    private UIView BuildReviewStep()
    {
        var vm = ViewModel;
        var signedIn = new WinoInfoBar(WinoInfoBarSeverity.Success, null, Translator.AccountSetup_Review_SignedIn);
        When(signedIn, s => s.IsReviewSignedInVisible);
        var pop3 = new WinoInfoBar(WinoInfoBarSeverity.Informational, Translator.POP3Setup_LimitationsTitle, Translator.POP3Setup_LimitationsMessage);
        When(pop3, s => s.IsPop3NoticeVisible);

        var (nameRow, nameField) = BuildNameRow();
        _reviewName = nameField;
        SubmitOnReturn(nameField);
        var nameError = AccountSetupUi.Error();
        Text(nameError, s => s.AccountNameError);
        When(nameError, s => s.HasAccountNameError);
        var nameGroup = AccountSetupUi.Column(6, AccountSetupUi.Header(Translator.AccountSetup_AccountNameHeader), nameRow, nameError);

        var reviewSendAs = Field(nameof(vm.SenderName), s => s.SenderName, (s, v) => s.SenderName = v, Translator.AccountSetup_SendAsHeader, out var reviewSendAsField,
            description: Translator.AccountSetup_SendAsDescription);
        reviewSendAsField.AutocapitalizationType = UITextAutocapitalizationType.Words;
        reviewSendAsField.TextContentType = UITextContentType.Name;
        SubmitOnReturn(reviewSendAsField);
        When(reviewSendAs, s => s.IsReviewSendAsVisible);

        _appliers.Add(RenderAppRows);

        var useMailSignInSwitch = _form.Check(Translator.AccountSetup_UseMailSignIn, nameof(vm.UseMailSignInForDav), s => s.UseMailSignInForDav, (s, v) => s.UseMailSignInForDav = v);
        When(useMailSignInSwitch, s => s.IsUseMailSignInForDavVisible);

        var davCaption = AccountSetupUi.Caption(Translator.AccountSetup_Review_DavSignInDescription);
        var davUser = Field(nameof(vm.DavUserName), s => s.DavUserName, (s, v) => s.DavUserName = v, Translator.AccountSetup_DavUserNameHeader, out var davUserField);
        davUserField.TextContentType = UITextContentType.Username;
        var davPassword = _form.Secure(nameof(vm.DavPassword), s => s.DavPassword, (s, v) => s.DavPassword = v, Translator.AccountSetup_DavPasswordHeader);
        SubmitOnReturn(davPassword);
        var davGroup = AccountSetupUi.Column(10, davCaption, davUser,
            AccountSetupUi.Column(6, AccountSetupUi.Header(Translator.AccountSetup_DavPasswordHeader), davPassword));
        When(davGroup, s => s.IsReviewDavSignInVisible);

        var featuresError = AccountSetupUi.Error();
        Text(featuresError, s => s.FeaturesError);
        When(featuresError, s => s.HasFeaturesError);
        var caption = AccountSetupUi.Caption();
        Text(caption, s => s.ReviewCaption);
        When(caption, s => s.HasReviewCaption);

        var spinner = AccountSetupUi.Spinner();
        var statusText = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        Text(statusText, s => s.ReviewStatusText);
        var status = WinoLayout.HStack(8, spinner, statusText);
        status.Alignment = UIStackViewAlignment.Center;
        _appliers.Add(() => AccountSetupUi.SetSpinning(spinner, ViewModel.IsReviewStatusVisible));
        var statusRow = Centered(status);
        When(statusRow, s => s.IsReviewStatusVisible);

        return AccountSetupUi.Column(14, signedIn, pop3, nameGroup, reviewSendAs, _appRows, useMailSignInSwitch, davGroup, BuildServerDetails(),
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
                SubmitOnReturn(view.ServerField);
                row.PropertyChanged += OnAppRowChanged;
                _appRowViews.Add(view);
                _appRows.AddArrangedSubview(view);
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
            ViewTreeDisposal.DisposeTree(view);
        }
        _appRowViews.Clear();
        _shownAppRows = null;
    }

    /// <summary>The checked IMAP servers, folded away (Windows Expander); Edit servers opens the advanced page.</summary>
    private UIView BuildServerDetails()
    {
        var vm = ViewModel;
        var rows = AccountSetupUi.Column(8);
        void Row(string label, Func<AccountSetupPageViewModel, string> read)
        {
            var value = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.PrimaryText, 0);
            Text(value, read);
            rows.AddArrangedSubview(AccountSetupUi.Column(2, AccountSetupUi.Caption(label), value));
        }
        Row(Translator.AccountSetup_Check_Incoming, s => s.ServerDetailsIncoming);
        Row(Translator.AccountSetup_Check_Outgoing, s => s.ServerDetailsOutgoing);
        Row(Translator.ProviderSelection_SourceCalDav, s => s.ServerDetailsCalDav);
        Row(Translator.ProviderSelection_SourceCardDav, s => s.ServerDetailsCardDav);
        var edit = AccountSetupUi.Leading(Link(Translator.AccountSetup_Review_EditServers, vm.OpenAdvancedSetupCommand));
        var body = AccountSetupUi.Column(8, rows, edit);
        body.Hidden = true;

        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(12, 0, 12, 0);
        configuration.ImagePlacement = NSDirectionalRectEdge.Trailing;
        configuration.BaseForegroundColor = WinoStyle.PrimaryText;
        var toggle = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        toggle.HorizontalAlignment = UIControlContentHorizontalAlignment.Fill;
        void ApplyToggle()
        {
            if (toggle.Configuration is not { } current) return;
            current.Title = Translator.AccountSetup_Review_ServerDetails;
            current.Image = WinoIcons.Image(body.Hidden ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronUp, 14, WinoStyle.SecondaryText);
            toggle.Configuration = current;
            toggle.AccessibilityValue = body.Hidden ? null : Translator.AccountSetup_Review_ServerDetails;
        }
        ApplyToggle();
        EventHandler toggled = (_, _) =>
        {
            body.Hidden = !body.Hidden;
            ApplyToggle();
        };
        toggle.PrimaryActionTriggered += toggled;
        Bindings.Own(new ActionDisposable(() => toggle.PrimaryActionTriggered -= toggled));

        var stack = AccountSetupUi.Column(0, toggle, body);
        var card = new WinoSurfaceView { Fill = WinoSettingsStyle.GroupFill, Stroke = WinoSettingsStyle.GroupStroke, StrokeWidth = 1, CornerRadius = 12 };
        card.TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Fill(stack, card, 0, 14, 8, 14);
        When(card, s => s.IsServerDetailsVisible);
        return card;
    }

    #endregion

    #region Progress

    /// <summary>Adding, Added and Failed: the setup steps, then the result or the failure with its actions.</summary>
    private UIView BuildProgressStep()
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
        var tryAgain = CommandButton(Translator.AccountSetup_TryAgainButton, vm.TryAgainCommand, primary: true);
        var goBack = CommandButton(Translator.AccountSetup_GoBackButton, vm.GoBackFromFailureCommand);
        var details = Link(Translator.AccountSetup_ShowDetails, vm.ShowSetupFailureDetailsCommand);
        When(details, s => s.HasSetupFailureDetails);
        var failed = new AccountSetupActionBar(failedBar, tryAgain, goBack, details);
        _flows.Add(failed.Actions);
        ShowActionBar(failed, s => s.IsSetupFailedVisible);

        return AccountSetupUi.Column(14, Centered(steps), added, resultHost, failed);
    }

    private void RenderResultRows()
    {
        var rows = ViewModel.ResultRows;
        if (ReferenceEquals(rows, _shownResultRows)) return;
        _shownResultRows = rows;
        foreach (var view in _resultRows.ArrangedSubviews) ViewTreeDisposal.DisposeTree(view);
        foreach (var row in rows) _resultRows.AddArrangedSubview(new AccountSetupResultRowView(row));
    }

    #endregion

    #region Fields

    /// <summary>A labelled text field with an optional caption under it, bound two-way to one ViewModel property.</summary>
    private UIView Field(string property, Func<AccountSetupPageViewModel, string?> read, Action<AccountSetupPageViewModel, string> write, string header,
        out UITextField field, Func<AccountSetupPageViewModel, string>? headerRead = null, string? description = null,
        Func<AccountSetupPageViewModel, string>? descriptionRead = null)
    {
        var label = AccountSetupUi.Header(header);
        if (headerRead is not null) Text(label, headerRead);
        field = _form.Text(property, read, write, null, header);
        var captured = field;
        if (headerRead is not null) _appliers.Add(() => captured.AccessibilityLabel = headerRead(ViewModel));
        var views = new List<UIView> { label, field };
        if (description is not null || descriptionRead is not null)
        {
            var caption = AccountSetupUi.Caption(description);
            if (descriptionRead is not null) Text(caption, descriptionRead);
            views.Add(caption);
        }
        return AccountSetupUi.Column(6, views.ToArray());
    }

    #endregion
}
