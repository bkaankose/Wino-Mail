using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// Address-first account setup (Windows AccountSetupPage, macOS AccountSetupPageViewController): one column with the
/// account so far, the step's title and description, the step (a <c>Gtk.Stack</c> over the six
/// <see cref="AccountSetupStep"/> values) and a footer with the setup links, Back and the primary button. Every rule
/// comes from the shared ViewModel; the page re-reads it once per main-loop turn (<see cref="OnboardingForm{T}"/>).
/// The welcome wizard and Settings &gt; Add account both open it in the onboarding window, as on macOS.
/// </summary>
internal sealed partial class AccountSetupView : WinoView<AccountSetupPageViewModel>
{
    private const int StepWidth = 480;
    private const int ReviewWidth = 600;

    private readonly Adw.ToolbarView _root;
    private readonly Adw.BreakpointBin _bin;
    private Gtk.Box? _footer;
    private readonly Adw.HeaderBar _header;
    private readonly Gtk.ScrolledWindow _scroller;
    private readonly Adw.Clamp _clamp;
    private readonly Gtk.Stack _steps;
    private readonly OnboardingForm<AccountSetupPageViewModel> _form;
    private readonly Gtk.Button _primary;
    private readonly Dictionary<string, Gtk.Editable> _fields = [];
    private AccountSetupStep? _shownStep;
    private IDisposable? _focus;

    public AccountSetupView(AccountSetupPageViewModel viewModel, WinoViewServices services) : base(viewModel, services)
    {
        var vm = ViewModel;
        _form = new OnboardingForm<AccountSetupPageViewModel>(vm, Bindings, Dispatcher, ReportError);
        _form.Track(vm.Capabilities);
        _form.TrackCollection(vm.KnownItems);
        _form.TrackCollection(vm.Checks);
        _form.Refreshed += OnRefreshed;
        Bindings.Own(new ActionDisposable(ReleaseSwatches));

        _steps = Gtk.Stack.New();
        _steps.SetTransitionType(Gtk.StackTransitionType.Crossfade);
        _steps.SetVhomogeneous(false);
        _steps.SetHhomogeneous(false);
        _steps.SetInterpolateSize(true);
        AddStep(AccountSetupStep.Address, BuildAddressStep());
        AddStep(AccountSetupStep.Discovery, BuildDiscoveryStep());
        AddStep(AccountSetupStep.SignInMethod, BuildSignInMethodStep());
        AddStep(AccountSetupStep.SignIn, BuildSignInStep());
        AddStep(AccountSetupStep.Review, BuildReviewStep());
        AddStep(AccountSetupStep.Progress, BuildProgressStep());

        var column = OnboardingUi.Column(20, BuildKnownRow(), BuildHeading(), _steps);
        column.SetMarginTop(24);
        column.SetMarginBottom(24);
        column.SetMarginStart(16);
        column.SetMarginEnd(16);
        _clamp = Adw.Clamp.New();
        _clamp.SetMaximumSize(StepWidth);
        _clamp.SetTighteningThreshold(StepWidth);
        _clamp.SetChild(column);
        _scroller = Gtk.ScrolledWindow.New();
        _scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        _scroller.SetVexpand(true);
        _scroller.SetChild(_clamp);

        _primary = _form.Button(Translator.ProviderSelection_ContinueButton, vm.PrimaryCommand, suggested: true);
        _header = Adw.HeaderBar.New();
        _header.SetShowTitle(false);
        _root = Adw.ToolbarView.New();
        _root.AddTopBar(_header);
        _root.SetContent(_scroller);
        _root.AddBottomBar(BuildFooter());
        _bin = OnboardingUi.NarrowBin(_root, Bindings, SetNarrow);
        Bindings.Own(new ActionDisposable(() => _focus?.Dispose()));
#if DEBUG
        Bindings.Own(DebugTrack());
#endif
    }

    public override Gtk.Widget Root => _bin;

    public override bool HasPendingWork => base.HasPendingWork || ViewModel.Phase == AccountSetupPhase.Adding;

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _header.SetVisible(!OnboardingUi.HasWindowHeader(_root));
        ViewModel.IsSecureSignInSupportedByHost = true;
        ViewModel.OnNavigatedTo(mode, parameter);
        // The palette is loaded by OnNavigatedTo and never raises a change of its own.
        ApplyPalette();
        _form.Refresh();
        return Task.CompletedTask;
    }

    protected override Task DeactivateAsync()
    {
        ViewModel.OnNavigatedFrom(NavigationMode.New, null);
        return Task.CompletedTask;
    }

    /// <summary>Names an entry for the Debug setter; nothing else reads it.</summary>
    private T Field<T>(string key, T editable) where T : Gtk.Editable
    {
        _fields[key] = editable;
        return editable;
    }

    private void AddStep(AccountSetupStep step, Gtk.Widget content) => _steps.AddNamed(content, step.ToString());

    private static string AddressAwareTitle(AccountSetupPageViewModel vm)
        => vm.Step == AccountSetupStep.Address && vm.IsSettingsHost && !vm.IsLocalOnlyMode ? Translator.AccountSetup_Title_Address : vm.StepTitle;

    private Gtk.Widget BuildHeading()
    {
        var title = OnboardingUi.Title();
        title.AccessibleRole = Gtk.AccessibleRole.Heading;
        _form.Text(title, AddressAwareTitle);
        _form.When(title, vm => !string.IsNullOrEmpty(AddressAwareTitle(vm)));
        var subtitle = OnboardingUi.Subtitle();
        _form.Text(subtitle, vm => vm.StepSubtitle);
        _form.When(subtitle, vm => vm.HasStepSubtitle);
        return OnboardingUi.Column(6, title, subtitle);
    }

    private Gtk.Widget BuildFooter()
    {
        var vm = ViewModel;
        var advanced = _form.Link(Translator.AccountSetup_AdvancedSetup, vm.OpenAdvancedSetupCommand);
        var local = _form.Link(Translator.AccountSetup_LocalAccountSetup, vm.StartLocalOnlyCommand);
        var info = Gtk.MenuButton.New();
        info.SetChild(WinoIcons.Label(WinoIconGlyph.Info, 14));
        info.AddCssClass("flat");
        info.SetValign(Gtk.Align.Center);
        WinoA11y.LabelWithTooltip(info, Translator.AccountSetup_LocalAccountSetup);
        var infoText = OnboardingUi.Wrapping(Translator.AccountSetup_LocalAccountInfo_Device);
        infoText.SetMaxWidthChars(40);
        infoText.SetMarginTop(8);
        infoText.SetMarginBottom(8);
        infoText.SetMarginStart(8);
        infoText.SetMarginEnd(8);
        var popover = Gtk.Popover.New();
        popover.SetChild(infoText);
        info.SetPopover(popover);
        var localGroup = Gtk.Box.New(Gtk.Orientation.Horizontal, 2);
        localGroup.Append(local);
        localGroup.Append(info);
        var links = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        links.Append(advanced);
        links.Append(localGroup);
        _form.When(links, s => s.IsFooterVisible);

        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);

        var back = _form.When(_form.Button(Translator.AccountSetup_Back, vm.BackCommand), s => s.IsBackVisible);
        var cancelDiscovery = _form.When(_form.Button(Translator.AccountSetup_SetUpManually, vm.CancelDiscoveryToAdvancedCommand), s => s.IsResolvingVisible);

        var nativeGlyph = new WinoIcon(WinoIconGlyph.None, 16, WinoIconMode.Colorful);
        var nativeText = Gtk.Label.New(string.Empty);
        var nativeContent = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        nativeContent.Append(nativeGlyph.Root);
        nativeContent.Append(nativeText);
        var nativeSpinner = OnboardingUi.Spinner();
        nativeContent.Append(nativeSpinner);
        var native = Gtk.Button.New();
        native.SetChild(nativeContent);
        native.AddCssClass("suggested-action");
        _form.Command(native, vm.SignInWithProviderCommand);
        _form.When(native, s => s.IsFooterSocialSignInVisible);
        _form.When(nativeSpinner, s => s.IsOAuthWaiting);
        _form.Apply(() =>
        {
            var text = ViewModel.NativeSignInButtonText ?? string.Empty;
            if (nativeText.GetText() != text)
            {
                nativeText.SetText(text);
                WinoA11y.Rename(native, text);
            }
            var glyph = MailProviderGlyphs.GetGlyph(ViewModel.RouteProviderType, ViewModel.RouteSpecialImapProvider);
            nativeGlyph.SetIcon(glyph);
            nativeGlyph.Root.SetVisible(glyph != WinoIconGlyph.None);
        });

        _primary.SetSizeRequest(120, -1);
        _form.When(_primary, s => s.IsPrimaryVisible);
        _form.Apply(() =>
        {
            if (_primary.GetLabel() == ViewModel.PrimaryText) return;
            _primary.SetLabel(ViewModel.PrimaryText);
            WinoA11y.Rename(_primary, ViewModel.PrimaryText);
        });

        var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        buttons.SetHexpand(true);
        buttons.Append(spacer);
        buttons.Append(back);
        buttons.Append(cancelDiscovery);
        buttons.Append(native);
        buttons.Append(_primary);

        _footer = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        _footer.AddCssClass("toolbar");
        _footer.SetMarginTop(6);
        _footer.SetMarginBottom(6);
        _footer.SetMarginStart(12);
        _footer.SetMarginEnd(12);
        _footer.Append(links);
        _footer.Append(buttons);
        return _footer;
    }

    private void SetNarrow(bool narrow) => _footer?.SetOrientation(narrow ? Gtk.Orientation.Vertical : Gtk.Orientation.Horizontal);

    private void OnRefreshed()
    {
        var step = ViewModel.Step;
        if (_shownStep == step) return;
        _shownStep = step;
        _steps.SetVisibleChildName(step.ToString());
        var width = step == AccountSetupStep.Review ? ReviewWidth : StepWidth;
        _clamp.SetMaximumSize(width);
        _clamp.SetTighteningThreshold(width);
        _scroller.GetVadjustment().SetValue(0);
        QueueFocus();
    }

    /// <summary>After a step appears, focus goes to its first visible field, the selected choice or the sign-in button.</summary>
    private void QueueFocus()
    {
        _focus?.Dispose();
        _focus = MainLoop.Delay(1, () =>
        {
            _focus = null;
            if (IsReleased) return;
            if (FocusTarget() is { } target && target.IsDrawable()) target.GrabFocus();
        });
    }

    private Gtk.Widget? FocusTarget() => ViewModel.Step switch
    {
        AccountSetupStep.Address => ViewModel.IsLocalOnlyMode ? _localName : _email,
        AccountSetupStep.SignInMethod => SelectedChoiceCard(),
        AccountSetupStep.SignIn => SignInFocusTarget(),
        _ => null,
    };
}
