using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Onboarding;

/// <summary>
/// Address-first account setup (Windows AccountSetupPage, Mac AccountSetupPageViewController): one column with the account
/// pill, the step's question and description, the step and the setup links, over a footer pinned above the keyboard with Back and the step's
/// action. Every step and rule comes from the shared <see cref="AccountSetupPageViewModel"/>; the page re-reads it once
/// per run-loop turn after any change, because most of its state is computed and raised in groups. Both hosts centre the
/// column horizontally; the welcome wizard also centres it vertically and Settings › Add account starts it at the top.
/// </summary>
public sealed partial class AccountSetupPageViewController(AccountSetupPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<AccountSetupPageViewModel>(viewModel, dispatcher, logger)
{
    private const double StepWidth = 440;
    private const double ReviewWidth = 560;

    private readonly List<Action> _appliers = new();
    private readonly List<AccountSetupFlowView> _flows = new();
    private readonly Dictionary<AccountSetupStep, UIView> _sections = new();
    private FormBinder<AccountSetupPageViewModel> _form = null!;
    private int _refreshQueued;
    private AccountSetupStep? _shownStep;
    private bool _shownLocalOnly;

    private UIScrollView _scroll = null!;
    private NSLayoutConstraint _columnWidth = null!;
    private NSLayoutConstraint _wizardCenter = null!;
    private NSLayoutConstraint _settingsTop = null!;
    private UIView _logo = null!;
    private UILabel _title = null!;
    private UILabel _subtitle = null!;
    private UIButton _primary = null!;

    public override bool HasPendingWork => base.HasPendingWork || ViewModel.Phase == AccountSetupPhase.Adding;

    public override void LoadView()
    {
        _form = new FormBinder<AccountSetupPageViewModel>(ViewModel, Bindings, Dispatcher, ReportError);

        var header = BuildHeader();
        var known = BuildKnownRow();
        var steps = AccountSetupUi.Column(0);
        AddSection(steps, AccountSetupStep.Address, BuildAddressStep());
        AddSection(steps, AccountSetupStep.Discovery, BuildDiscoveryStep());
        AddSection(steps, AccountSetupStep.SignInMethod, BuildSignInMethodStep());
        AddSection(steps, AccountSetupStep.SignIn, BuildSignInStep());
        AddSection(steps, AccountSetupStep.Review, BuildReviewStep());
        AddSection(steps, AccountSetupStep.Progress, BuildProgressStep());

        var column = AccountSetupUi.Column(20, known, header, steps, BuildSetupLinks());
        View = BuildPage(column, BuildFooter());
        SubscribeToViewModel();
    }

    private void AddSection(UIStackView host, AccountSetupStep step, UIView section)
    {
        _sections[step] = section;
        section.Hidden = true;
        host.AddArrangedSubview(section);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.IsSecureSignInSupportedByHost = true;
        ViewModel.OnNavigatedTo(mode, parameter);
        ApplyHostLayout(ViewModel.IsSettingsHost);
        Refresh();
#if DEBUG
        RegisterDebugCommands();
#endif
        return Task.CompletedTask;
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        QueueFocus();
    }

    #region Page layout

    /// <summary>The scrolling column over a footer pinned above the keyboard, like the other onboarding pages.</summary>
    private UIView BuildPage(UIStackView column, UIView footer)
    {
        var page = new UIView { BackgroundColor = UIColor.SystemGroupedBackground };
        _scroll = new UIScrollView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive,
            AlwaysBounceVertical = true
        };
        var container = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        _scroll.AddSubview(container);
        container.AddSubview(column);

        var content = _scroll.ContentLayoutGuide;
        var frame = _scroll.FrameLayoutGuide;
        // The column takes its step's width, or the screen's on a phone.
        _columnWidth = column.WidthAnchor.ConstraintEqualTo((nfloat)StepWidth);
        _columnWidth.Priority = 750;
        _wizardCenter = column.CenterYAnchor.ConstraintEqualTo(container.CenterYAnchor);
        _wizardCenter.Priority = 740;
        _settingsTop = column.TopAnchor.ConstraintEqualTo(container.TopAnchor, 24);
        _settingsTop.Priority = 760;
        _settingsTop.Active = false;
        var snug = container.HeightAnchor.ConstraintEqualTo(frame.HeightAnchor);
        snug.Priority = 250;

        var bar = new WinoBarBackdropView { TranslatesAutoresizingMaskIntoConstraints = false };
        var separator = new WinoSeparator();
        page.AddSubview(_scroll);
        page.AddSubview(bar);
        page.AddSubview(separator);
        page.AddSubview(footer);

        var guide = page.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            _scroll.TopAnchor.ConstraintEqualTo(guide.TopAnchor),
            _scroll.LeadingAnchor.ConstraintEqualTo(page.LeadingAnchor),
            _scroll.TrailingAnchor.ConstraintEqualTo(page.TrailingAnchor),
            _scroll.BottomAnchor.ConstraintEqualTo(bar.TopAnchor),
            container.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            container.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            container.LeadingAnchor.ConstraintEqualTo(frame.LeadingAnchor),
            container.TrailingAnchor.ConstraintEqualTo(frame.TrailingAnchor),
            container.HeightAnchor.ConstraintGreaterThanOrEqualTo(frame.HeightAnchor),
            snug,
            column.TopAnchor.ConstraintGreaterThanOrEqualTo(container.TopAnchor, 24),
            column.BottomAnchor.ConstraintLessThanOrEqualTo(container.BottomAnchor, -24),
            column.CenterXAnchor.ConstraintEqualTo(container.CenterXAnchor),
            column.WidthAnchor.ConstraintLessThanOrEqualTo(container.WidthAnchor, 1, -32),
            _columnWidth, _wizardCenter,
            bar.LeadingAnchor.ConstraintEqualTo(page.LeadingAnchor),
            bar.TrailingAnchor.ConstraintEqualTo(page.TrailingAnchor),
            bar.BottomAnchor.ConstraintEqualTo(page.BottomAnchor),
            separator.TopAnchor.ConstraintEqualTo(bar.TopAnchor),
            separator.LeadingAnchor.ConstraintEqualTo(page.LeadingAnchor),
            separator.TrailingAnchor.ConstraintEqualTo(page.TrailingAnchor),
            footer.TopAnchor.ConstraintEqualTo(bar.TopAnchor, 12),
            footer.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, 16),
            footer.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -16),
            footer.BottomAnchor.ConstraintEqualTo(page.KeyboardLayoutGuide.TopAnchor, -12)
        ]);
        return page;
    }

    /// <summary>The address step shows the Wino Mail logo; every step has its title and, on some, a subtitle.</summary>
    private UIView BuildHeader()
    {
        var icon = new UIImageView(WelcomePageV2.LoadLogo())
        {
            ContentMode = UIViewContentMode.ScaleAspectFit,
            TranslatesAutoresizingMaskIntoConstraints = false,
            IsAccessibilityElement = false
        };
        WinoLayout.Size(icon, 64, 64);
        _logo = Centered(icon);

        _title = WinoStyle.Label(string.Empty, WinoStyle.Heading, WinoStyle.PrimaryText, 0);
        _title.AccessibilityTraits = UIAccessibilityTrait.Header;
        _subtitle = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        Text(_title, vm => vm.StepTitle);
        When(_title, vm => vm.HasStepTitle);
        Text(_subtitle, vm => vm.StepSubtitle);
        When(_subtitle, vm => vm.HasStepSubtitle);
        _title.TextAlignment = UITextAlignment.Center;
        _subtitle.TextAlignment = UITextAlignment.Center;

        var header = AccountSetupUi.Column(6, _logo, _title, _subtitle);
        header.SetCustomSpacing(8, _logo);
        return header;
    }

    /// <summary>The wizard centres the column vertically; Settings starts it at the top, so a changing step does not move it.</summary>
    private void ApplyHostLayout(bool isSettings)
    {
        _wizardCenter.Active = !isSettings;
        _settingsTop.Active = isSettings;
    }

    private UIViewController? _popover;

    /// <summary>Advanced setup and Local account setup with its info button, under the address step.</summary>
    private UIView BuildSetupLinks()
    {
        var vm = ViewModel;
        var advanced = Link(Translator.AccountSetup_AdvancedSetup, vm.OpenAdvancedSetupCommand);
        var local = Link(Translator.AccountSetup_LocalAccountSetup, vm.StartLocalOnlyCommand);
        var infoConfiguration = UIButtonConfiguration.PlainButtonConfiguration;
        infoConfiguration.Image = WinoIcons.Image(WinoIconGlyph.Info, 16, WinoStyle.SecondaryText, Translator.AccountSetup_LocalAccountSetup);
        infoConfiguration.ContentInsets = new NSDirectionalEdgeInsets(4, 4, 4, 4);
        var info = new UIButton { Configuration = infoConfiguration, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(info, 32, 32);
        info.AccessibilityLabel = Translator.AccountSetup_LocalAccountSetup;
        info.AccessibilityHint = Translator.AccountSetup_LocalAccountInfo_Device;
        EventHandler showInfo = (_, _) => ShowPopover(info, Translator.AccountSetup_LocalAccountInfo_Device);
        info.PrimaryActionTriggered += showInfo;
        Bindings.Own(new ActionDisposable(() => info.PrimaryActionTriggered -= showInfo));
        var localGroup = WinoLayout.HStack(0, local, info);
        localGroup.Alignment = UIStackViewAlignment.Center;

        var links = new AccountSetupFlowView(centered: false, spacing: 16, lineSpacing: 0);
        links.AddItem(advanced);
        links.AddItem(localGroup);
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(links);
        var fitted = links.WidthAnchor.ConstraintEqualTo(LinksWidth(advanced, localGroup));
        fitted.Priority = 500;
        NSLayoutConstraint.ActivateConstraints(
        [
            links.TopAnchor.ConstraintEqualTo(host.TopAnchor),
            links.BottomAnchor.ConstraintEqualTo(host.BottomAnchor),
            links.WidthAnchor.ConstraintLessThanOrEqualTo(host.WidthAnchor),
            fitted, links.CenterXAnchor.ConstraintEqualTo(host.CenterXAnchor)
        ]);
        When(host, s => s.IsFooterVisible);
        return host;
    }

    private static nfloat LinksWidth(UIView first, UIView second)
        => first.SystemLayoutSizeFittingSize(UIView.UILayoutFittingCompressedSize).Width
           + second.SystemLayoutSizeFittingSize(UIView.UILayoutFittingCompressedSize).Width + 17;

    /// <summary>Back on the leading side; Set up manually while discovering, the provider sign-in or the step's action on the trailing side.</summary>
    private UIView BuildFooter()
    {
        var vm = ViewModel;
        var back = CommandButton(Translator.AccountSetup_Back, vm.BackCommand);
        When(back, s => s.IsBackVisible);

        var cancelDiscovery = CommandButton(Translator.AccountSetup_SetUpManually, vm.CancelDiscoveryToAdvancedCommand);
        When(cancelDiscovery, s => s.IsResolvingVisible);

        var nativeSignIn = AccountSetupUi.ProviderButton();
        BindCommand(nativeSignIn, vm.SignInWithProviderCommand);
        When(nativeSignIn, s => s.IsFooterSocialSignInVisible);
        string? nativeText = null;
        var nativeGlyph = WinoIconGlyph.None;
        _appliers.Add(() =>
        {
            var text = ViewModel.NativeSignInButtonText;
            var glyph = MailProviderGlyphs.GetGlyph(ViewModel.RouteProviderType, ViewModel.RouteSpecialImapProvider);
            if (text == nativeText && glyph == nativeGlyph) return;
            nativeText = text;
            nativeGlyph = glyph;
            AccountSetupUi.SetContent(nativeSignIn, text, glyph == WinoIconGlyph.None ? null : WinoIcons.ColorfulImage(glyph, 18));
        });

        _primary = CommandButton(Translator.ProviderSelection_ContinueButton, vm.PrimaryCommand, primary: true);
        _primary.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        When(_primary, s => s.IsPrimaryVisible);
        string? primaryText = null;
        _appliers.Add(() =>
        {
            if (primaryText == ViewModel.PrimaryText || _primary.Configuration is not { } configuration) return;
            primaryText = ViewModel.PrimaryText;
            configuration.Title = primaryText;
            _primary.Configuration = configuration;
        });

        foreach (var button in new[] { back, cancelDiscovery })
            button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        foreach (var button in new[] { cancelDiscovery, nativeSignIn, _primary })
            WinoLayout.Priorities(button, UILayoutConstraintAxis.Horizontal, 750, 760);
        var footer = WinoLayout.HStack(WinoStyle.Space3, back, WinoLayout.Spacer(), cancelDiscovery, nativeSignIn, _primary);
        footer.Alignment = UIStackViewAlignment.Center;
        return footer;
    }

    /// <summary>A short text in a popover at <paramref name="anchor"/> (Windows Flyout), kept a popover on iPhone.</summary>
    private void ShowPopover(UIView anchor, string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        var controller = new UIViewController { ModalPresentationStyle = UIModalPresentationStyle.Popover };
        controller.View = new UIView();
        WinoLayout.Fill(label, controller.View, 14);
        var size = label.SystemLayoutSizeFittingSize(new CGSize(280, 0), 1000, 50);
        controller.PreferredContentSize = new CGSize(Math.Min(308, (double)size.Width + 28), size.Height + 28);
        PresentPopover(controller, anchor);
    }

    private void PresentPopover(UIViewController controller, UIView anchor)
    {
        if (PresentedViewController is not null) return;
        if (controller.PopoverPresentationController is { } popover)
        {
            popover.SourceView = anchor;
            popover.SourceRect = anchor.Bounds;
            popover.PermittedArrowDirections = UIPopoverArrowDirection.Up | UIPopoverArrowDirection.Down;
            popover.Delegate = KeepPopover.Instance;
        }
        _popover = controller;
        PresentViewController(controller, true, null);
    }

    private void ClosePopover()
    {
        var popover = _popover;
        _popover = null;
        if (popover?.PresentingViewController is not null) popover.DismissViewController(true, null);
    }

    /// <summary>Keeps popovers popovers on iPhone instead of the adaptive sheet.</summary>
    private sealed class KeepPopover : UIPopoverPresentationControllerDelegate
    {
        public static readonly KeepPopover Instance = new();

        public override UIModalPresentationStyle GetAdaptivePresentationStyle(UIPresentationController controller, UITraitCollection traitCollection)
            => UIModalPresentationStyle.None;
    }

    #endregion

    #region Refresh

    private void SubscribeToViewModel()
    {
        var vm = ViewModel;
        PropertyChangedEventHandler changed = (_, _) => QueueRefresh();
        NotifyCollectionChangedEventHandler collectionChanged = (_, _) => QueueRefresh();
        vm.PropertyChanged += changed;
        vm.Capabilities.PropertyChanged += changed;
        vm.KnownItems.CollectionChanged += collectionChanged;
        vm.Checks.CollectionChanged += collectionChanged;
        Bindings.Own(new ActionDisposable(() =>
        {
            vm.PropertyChanged -= changed;
            vm.Capabilities.PropertyChanged -= changed;
            vm.KnownItems.CollectionChanged -= collectionChanged;
            vm.Checks.CollectionChanged -= collectionChanged;
            DetachAppRows();
        }));
    }

    /// <summary>Coalesces a burst of ViewModel changes into one refresh on the main thread.</summary>
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        BeginInvokeOnMainThread(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (Bindings.IsDisposed) return;
            try { Refresh(); }
            catch (Exception exception) { ReportError(exception); }
        });
    }

    private void Refresh()
    {
        var step = ViewModel.Step;
        if (_shownStep != step) ShowStep(step);
        else if (_shownLocalOnly != ViewModel.IsLocalOnlyMode)
        {
            // Local account setup swaps the address for the name on the same step; the keyboard follows the field.
            View.EndEditing(true);
            QueueFocus();
        }
        _shownLocalOnly = ViewModel.IsLocalOnlyMode;
        foreach (var apply in _appliers)
        {
            // One failing view must not leave the rest of the page on the previous step.
            try { apply(); }
            catch (Exception exception) { ReportError(exception); }
        }
        foreach (var flow in _flows) flow.Reflow();
    }

    private void ShowStep(AccountSetupStep step)
    {
        var previous = _shownStep;
        _shownStep = step;
        foreach (var (key, section) in _sections) section.Hidden = key != step;
        _logo.Hidden = step != AccountSetupStep.Address;
        _columnWidth.Constant = (nfloat)(step == AccountSetupStep.Review ? ReviewWidth : StepWidth);
        ClosePopover();
        if (previous is not null)
        {
            // A new step starts without the previous step's keyboard.
            View.EndEditing(true);
            _scroll.SetContentOffset(new CGPoint(0, -_scroll.AdjustedContentInset.Top), false);
        }
        QueueFocus();
    }

    /// <summary>After a step appears, the keyboard goes to its first field: the address, a local account's name or the password.</summary>
    private void QueueFocus()
    {
        BeginInvokeOnMainThread(() =>
        {
            if (Bindings.IsDisposed || View.Window is null || PresentedViewController is not null) return;
            var target = FocusTarget();
            if (target is not null && IsShown(target) && target.Enabled) target.BecomeFirstResponder();
        });
    }

    private UITextField? FocusTarget() => ViewModel.Step switch
    {
        AccountSetupStep.Address => ViewModel.IsLocalOnlyMode ? _localName : _email,
        AccountSetupStep.SignIn => new UITextField?[] { _password, _exchangePassword }.FirstOrDefault(field => field is not null && IsShown(field)),
        _ => null
    };

    private static bool IsShown(UIView view)
    {
        for (UIView? current = view; current is not null; current = current.Superview)
            if (current.Hidden) return false;
        return true;
    }

    #endregion

    #region Binding helpers

    private void When(UIView view, Func<AccountSetupPageViewModel, bool> visible)
        => _appliers.Add(() =>
        {
            var hidden = !visible(ViewModel);
            if (view.Hidden != hidden) view.Hidden = hidden;
        });

    private void Text(UILabel label, Func<AccountSetupPageViewModel, string?> read)
        => _appliers.Add(() =>
        {
            var value = read(ViewModel) ?? string.Empty;
            if (label.Text != value) label.Text = value;
        });

    private void Enabled(UIControl control, Func<AccountSetupPageViewModel, bool> enabled)
        => _appliers.Add(() =>
        {
            var value = enabled(ViewModel);
            if (control.Enabled != value) control.Enabled = value;
        });

    /// <summary>Runs a command from a button and follows its CanExecute.</summary>
    private void BindCommand(UIButton button, ICommand command, Func<object?>? parameter = null)
    {
        var binding = Bindings.Own(new CommandBinding(command, parameter ?? (() => null), enabled => button.Enabled = enabled, Dispatcher, ReportError));
        EventHandler handler = (_, _) => binding.Execute();
        button.PrimaryActionTriggered += handler;
        Bindings.Own(new ActionDisposable(() => button.PrimaryActionTriggered -= handler));
    }

    private UIButton Link(string title, ICommand command, Func<object?>? parameter = null, UIFont? font = null)
    {
        var link = AccountSetupUi.Link(title, font);
        BindCommand(link, command, parameter);
        return link;
    }

    /// <summary>Return in a field runs the step's primary action, as Enter does in the Windows fields.</summary>
    private void SubmitOnReturn(UITextField field)
    {
        field.ReturnKeyType = UIReturnKeyType.Go;
        field.ShouldReturn = _ =>
        {
            ExecutePrimary();
            return false;
        };
    }

    private void ExecutePrimary()
    {
        if (ViewModel.IsPrimaryVisible) Run(ViewModel.PrimaryCommand);
    }

    private void Run(ICommand command, object? parameter = null)
    {
        if (!command.CanExecute(parameter)) return;
        if (command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncCommand)
            _ = RunAsync(() => asyncCommand.ExecuteAsync(parameter));
        else command.Execute(parameter);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { ReportError(exception); }
    }

    private static UIView Centered(UIView view)
    {
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(view);
        NSLayoutConstraint.ActivateConstraints(
        [
            view.TopAnchor.ConstraintEqualTo(host.TopAnchor),
            view.BottomAnchor.ConstraintEqualTo(host.BottomAnchor),
            view.CenterXAnchor.ConstraintEqualTo(host.CenterXAnchor),
            view.LeadingAnchor.ConstraintGreaterThanOrEqualTo(host.LeadingAnchor),
            view.TrailingAnchor.ConstraintLessThanOrEqualTo(host.TrailingAnchor)
        ]);
        return host;
    }

    private static WinoInfoBarSeverity ToSeverity(InfoBarMessageType type) => type switch
    {
        InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
        InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
        InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
        _ => WinoInfoBarSeverity.Informational
    };

    /// <summary>Black or white, whichever reads on the account colour (Windows GetReadableTextColor).</summary>
    private static UIColor? ReadableOn(string? hex)
    {
        var value = hex?.TrimStart('#');
        if (value is not { Length: 6 } || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return null;
        return ReadableTextColor.PrefersDarkTextByLuminance((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF) ? UIColor.Black : UIColor.White;
    }

    #endregion

    protected override void Dispose(bool disposing)
    {
        if (disposing) ClosePopover();
        base.Dispose(disposing);
    }
}
