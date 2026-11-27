using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Onboarding;

/// <summary>
/// Address-first account setup (Windows AccountSetupPage): one column with the account pill, the step's question and
/// description, the step, and a pinned footer with the setup links, Back and the primary button. Every step and rule comes from the
/// shared <see cref="AccountSetupPageViewModel"/>; the page re-reads it once per run-loop turn after any change, because
/// most of its state is computed and raised in groups. The welcome wizard and Settings > Add account both run in the
/// onboarding window, so both centre the column and show the address step's title.
/// </summary>
public sealed partial class AccountSetupPageViewController(AccountSetupPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<AccountSetupPageViewModel>(viewModel, dispatcher, logger)
{
    private const double StepWidth = 440;
    private const double ReviewWidth = 560;

    private readonly List<Action> _appliers = new();
    private readonly List<(NSTextField Label, double Inset)> _wrapping = new();
    private readonly Dictionary<AccountSetupStep, NSView> _sections = new();
    private FormBinder<AccountSetupPageViewModel> _form = null!;
    private int _refreshQueued;
    private AccountSetupStep? _shownStep;
    private double _columnWidth = StepWidth;

    private NSScrollView _scroll = null!;
    private NSLayoutConstraint _columnWidthConstraint = null!;
    private NSView _logo = null!;
    private NSTextField _title = null!;
    private NSTextField _subtitle = null!;
    private NSButton _primary = null!;

    public override bool HasPendingWork => base.HasPendingWork || ViewModel.Phase == AccountSetupPhase.Adding;

    public override void LoadView()
    {
        var vm = ViewModel;
        _form = new FormBinder<AccountSetupPageViewModel>(vm, Bindings, Dispatcher, ReportError);

        var header = BuildHeader();
        var known = BuildKnownRow();
        var steps = AccountSetupUi.Column(0);
        AddSection(steps, AccountSetupStep.Address, BuildAddressStep());
        AddSection(steps, AccountSetupStep.Discovery, BuildDiscoveryStep());
        AddSection(steps, AccountSetupStep.SignInMethod, BuildSignInMethodStep());
        AddSection(steps, AccountSetupStep.SignIn, BuildSignInStep());
        AddSection(steps, AccountSetupStep.Review, BuildReviewStep());
        AddSection(steps, AccountSetupStep.Progress, BuildProgressStep());

        var column = AccountSetupUi.Column(20, known, header, steps);
        View = BuildPage(column, BuildFooter());
        SubscribeToViewModel();
    }

    private static AccountSetupStep SectionOf(AccountSetupStep step)
        => step == AccountSetupStep.ServerKind ? AccountSetupStep.SignInMethod : step;

    private void AddSection(NSStackView host, AccountSetupStep step, NSView section)
    {
        _sections[step] = section;
        section.Hidden = true;
        host.AddArrangedSubview(section);
        section.WidthAnchor.ConstraintEqualTo(host.WidthAnchor).Active = true;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.IsSecureSignInSupportedByHost = true;
        ViewModel.OnNavigatedTo(mode, parameter!);
        // The palette is loaded by OnNavigatedTo and never raises a change of its own.
        var palette = ViewModel.AvailableColors.Select(color => color.Hex).ToList();
        foreach (var button in _colorButtons) button.Colors = palette;
        Refresh();
#if DEBUG
        RegisterDebugCommands();
#endif
        return Task.CompletedTask;
    }

    public override void ViewDidAppear()
    {
        base.ViewDidAppear();
        if (View.Window is { } window) window.AutorecalculatesKeyViewLoop = true;
        QueueFocus();
    }

    #region Page layout

    /// <summary>The scrolling column over a pinned footer, like the other onboarding pages.</summary>
    private NSView BuildPage(NSStackView column, NSView footer)
    {
        var document = new FlippedView();
        document.AddSubview(column);
        // The column takes its step's width; the window's minimum width always leaves room for it.
        _columnWidthConstraint = column.WidthAnchor.ConstraintEqualTo((nfloat)StepWidth);
        _columnWidthConstraint.Priority = 999;
        var center = column.CenterYAnchor.ConstraintEqualTo(document.CenterYAnchor);
        center.Priority = 740;
        var compact = document.HeightAnchor.ConstraintEqualTo(0);
        compact.Priority = 1;
        NSLayoutConstraint.ActivateConstraints(
        [
            column.TopAnchor.ConstraintGreaterThanOrEqualTo(document.TopAnchor, 24),
            column.BottomAnchor.ConstraintLessThanOrEqualTo(document.BottomAnchor, -24),
            column.CenterXAnchor.ConstraintEqualTo(document.CenterXAnchor),
            column.WidthAnchor.ConstraintLessThanOrEqualTo(document.WidthAnchor, 1, -48),
            _columnWidthConstraint, center, compact
        ]);

        _scroll = new NSScrollView
        {
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            // The scroll view starts below the transparent title bar (safe area), so it needs no title-bar inset of its own.
            AutomaticallyAdjustsContentInsets = false,
            DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false,
            DocumentView = document
        };
        NSLayoutConstraint.ActivateConstraints(
        [
            document.WidthAnchor.ConstraintEqualTo(_scroll.ContentView.WidthAnchor),
            document.TopAnchor.ConstraintEqualTo(_scroll.ContentView.TopAnchor),
            document.LeadingAnchor.ConstraintEqualTo(_scroll.ContentView.LeadingAnchor),
            document.HeightAnchor.ConstraintGreaterThanOrEqualTo(_scroll.ContentView.HeightAnchor)
        ]);

        var separator = new WinoSeparator();
        var page = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        // The onboarding window takes the page's frame when the page arrives; a zero frame would shrink it to the footer.
        page.Frame = new CGRect(0, 0, 720, 640);
        page.AddSubview(_scroll);
        page.AddSubview(separator);
        page.AddSubview(footer);
        NSLayoutConstraint.ActivateConstraints(
        [
            _scroll.TopAnchor.ConstraintEqualTo(page.SafeAreaLayoutGuide.TopAnchor),
            _scroll.LeadingAnchor.ConstraintEqualTo(page.LeadingAnchor),
            _scroll.TrailingAnchor.ConstraintEqualTo(page.TrailingAnchor),
            _scroll.BottomAnchor.ConstraintEqualTo(separator.TopAnchor),
            separator.LeadingAnchor.ConstraintEqualTo(page.LeadingAnchor),
            separator.TrailingAnchor.ConstraintEqualTo(page.TrailingAnchor),
            separator.BottomAnchor.ConstraintEqualTo(footer.TopAnchor),
            footer.LeadingAnchor.ConstraintEqualTo(page.LeadingAnchor),
            footer.TrailingAnchor.ConstraintEqualTo(page.TrailingAnchor),
            footer.BottomAnchor.ConstraintEqualTo(page.BottomAnchor)
        ]);
        return page;
    }

    private static string AddressAwareTitle(AccountSetupPageViewModel vm)
        => vm.Step == AccountSetupStep.Address && vm.IsSettingsHost && !vm.IsLocalOnlyMode ? Translator.AccountSetup_Title_Address : vm.StepTitle;

    /// <summary>The address step shows the Wino Mail mark; every step has its title and, on some, a subtitle.</summary>
    private NSView BuildHeader()
    {
        var icon = new NSImageView
        {
            Image = NSApplication.SharedApplication.ApplicationIconImage,
            ImageScaling = NSImageScale.ProportionallyUpOrDown,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(icon, 64, 64);
        _logo = Centered(icon);

        _title = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(22, NSFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        _subtitle = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        Wrap(_title);
        Wrap(_subtitle);
        // The shared ViewModel leaves the address title to the Windows Settings page header; on the Mac both entry points
        // open the onboarding window, which has no header of its own.
        Text(_title, AddressAwareTitle);
        When(_title, vm => !string.IsNullOrEmpty(AddressAwareTitle(vm)));
        Text(_subtitle, vm => vm.StepSubtitle);
        When(_subtitle, vm => vm.HasStepSubtitle);
        _title.AccessibilityRole = NSAccessibilityRoles.StaticTextRole;

        _title.Alignment = NSTextAlignment.Center;
        _subtitle.Alignment = NSTextAlignment.Center;
        // Standard line breaking keeps the last word from sitting alone on its own line.
        _title.LineBreakStrategy = NSLineBreakStrategy.Standard;
        _subtitle.LineBreakStrategy = NSLineBreakStrategy.Standard;
        var header = AccountSetupUi.Column(6, _logo, _title, _subtitle);
        header.SetCustomSpacing(8, _logo);
        return header;
    }

    private NSView BuildFooter()
    {
        var vm = ViewModel;

        var advanced = Link(Translator.AccountSetup_AdvancedSetup, vm.ChooseServerKindCommand);
        var local = Link(Translator.AccountSetup_LocalAccountSetup, vm.StartLocalOnlyCommand);
        var info = new NSButton
        {
            Bordered = false,
            Image = WinoIcons.Image(WinoIconGlyph.Info, 14, WinoStyle.SecondaryText, Translator.AccountSetup_LocalAccountSetup),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ToolTip = Translator.AccountSetup_LocalAccountInfo_Device,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(info, 22, 22);
        WinoAccessibility.Label(info, Translator.AccountSetup_LocalAccountSetup);
        WinoAccessibility.Help(info, Translator.AccountSetup_LocalAccountInfo_Device);
        info.Activated += (_, _) => ShowLocalInfo(info);
        var localGroup = WinoLayout.HStack(2, local, info);
        var links = WinoLayout.HStack(WinoStyle.Space3, advanced, localGroup);
        When(links, s => s.IsFooterVisible);

        var back = Command(Translator.AccountSetup_Back, vm.BackCommand);
        When(back, s => s.IsBackVisible);
        back.WidthAnchor.ConstraintGreaterThanOrEqualTo(72).Active = true;

        var skipImported = Command(string.Empty, vm.SkipImportedAccountCommand);
        When(skipImported, s => s.IsSkipImportedAccountVisible);
        _appliers.Add(() => { if (skipImported.Title != ViewModel.SkipImportedAccountText) skipImported.Title = ViewModel.SkipImportedAccountText; });

        var cancelDiscovery = Command(Translator.AccountSetup_SetUpManually, vm.CancelDiscoveryToAdvancedCommand);
        When(cancelDiscovery, s => s.IsResolvingVisible);

        var nativeSignIn = Command(string.Empty, vm.SignInWithProviderCommand);
        nativeSignIn.ImagePosition = NSCellImagePosition.ImageLeading;
        When(nativeSignIn, s => s.IsFooterSocialSignInVisible);
        var nativeGlyph = WinoIconGlyph.None;
        _appliers.Add(() =>
        {
            var text = ViewModel.NativeSignInButtonText;
            if (nativeSignIn.AccessibilityLabel != text) { nativeSignIn.Title = " " + text; WinoAccessibility.Label(nativeSignIn, text); }
            var glyph = MailProviderGlyphs.GetGlyph(ViewModel.RouteProviderType, ViewModel.RouteSpecialImapProvider);
            if (glyph == nativeGlyph) return;
            nativeGlyph = glyph;
            nativeSignIn.Image = glyph == WinoIconGlyph.None ? null : WinoIcons.ColorfulImage(glyph, 16);
        });

        _primary = Command(Translator.ProviderSelection_ContinueButton, vm.PrimaryCommand);
        _primary.KeyEquivalent = "\r";
        _primary.WidthAnchor.ConstraintGreaterThanOrEqualTo(128).Active = true;
        When(_primary, s => s.IsPrimaryVisible);
        _appliers.Add(() => { if (_primary.Title != ViewModel.PrimaryText) _primary.Title = ViewModel.PrimaryText; });

        var footer = WinoLayout.HStack(WinoStyle.Space2, links, WinoLayout.Spacer(), back, skipImported, cancelDiscovery, nativeSignIn, _primary);
        footer.EdgeInsets = new NSEdgeInsets(14, 24, 18, 24);
        return footer;
    }

    private NSPopover? _localInfo;

    private void ShowLocalInfo(NSView anchor)
    {
        if (_localInfo is null)
        {
            var text = WinoStyle.Label(Translator.AccountSetup_LocalAccountInfo_Device, WinoStyle.Body, WinoStyle.PrimaryText, 0);
            text.PreferredMaxLayoutWidth = 280;
            var content = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
            WinoLayout.Fill(text, content, 12);
            content.LayoutSubtreeIfNeeded();
            _localInfo = new NSPopover
            {
                Behavior = NSPopoverBehavior.Transient,
                ContentViewController = new NSViewController { View = content, PreferredContentSize = content.FittingSize }
            };
        }
        if (_localInfo.Shown) { _localInfo.Close(); return; }
        _localInfo.Show(anchor.Bounds, anchor, NSRectEdge.MaxYEdge);
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
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
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
        foreach (var apply in _appliers) apply();
    }

    private void ShowStep(AccountSetupStep step)
    {
        _shownStep = step;
        var shownSection = SectionOf(step);
        foreach (var (key, section) in _sections) section.Hidden = key != shownSection;
        _logo.Hidden = step != AccountSetupStep.Address;
        _columnWidth = step == AccountSetupStep.Review ? ReviewWidth : StepWidth;
        _columnWidthConstraint.Constant = (nfloat)_columnWidth;
        foreach (var (label, inset) in _wrapping) label.PreferredMaxLayoutWidth = (nfloat)(_columnWidth - inset);
        foreach (var row in _appRowViews) row.SetWrapWidth(_columnWidth);
        foreach (var card in _choiceCards) card.SetWrapWidth(_columnWidth);
        if (_scroll.DocumentView is { } document) document.ScrollPoint(CGPoint.Empty);
        QueueFocus();
    }

    /// <summary>After a step appears, focus goes to its first visible field, the selected choice or the sign-in button.</summary>
    private void QueueFocus()
    {
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (Bindings.IsDisposed || View.Window is not { } window) return;
            var target = FocusTarget();
            if (target is not null && IsShown(target)) window.MakeFirstResponder(target);
        });
    }

    private NSView? FocusTarget() => ViewModel.Step switch
    {
        AccountSetupStep.Address => ViewModel.IsLocalOnlyMode ? _localName : _email,
        AccountSetupStep.ServerKind or AccountSetupStep.SignInMethod => _choiceCards.FirstOrDefault(card => card.IsSelected) ?? _choiceCards.FirstOrDefault(),
        AccountSetupStep.SignIn => new NSView?[] { _browserSignIn, _password, _exchangePassword }.FirstOrDefault(view => view is not null && IsShown(view)),
        _ => null
    };

    private static bool IsShown(NSView view)
    {
        for (NSView? current = view; current is not null; current = current.Superview)
            if (current.Hidden) return false;
        return true;
    }

    #endregion

    #region Binding helpers

    private void When(NSView view, Func<AccountSetupPageViewModel, bool> visible)
        => _appliers.Add(() =>
        {
            var hidden = !visible(ViewModel);
            if (view.Hidden != hidden) view.Hidden = hidden;
        });

    private void Text(NSTextField label, Func<AccountSetupPageViewModel, string?> read)
        => _appliers.Add(() =>
        {
            var value = read(ViewModel) ?? string.Empty;
            if (label.StringValue != value) label.StringValue = value;
        });

    private void Enabled(NSControl control, Func<AccountSetupPageViewModel, bool> enabled)
        => _appliers.Add(() =>
        {
            var value = enabled(ViewModel);
            if (control.Enabled != value) control.Enabled = value;
        });

    private T Wrap<T>(T label, double inset = 0) where T : NSTextField
    {
        label.PreferredMaxLayoutWidth = (nfloat)(StepWidth - inset);
        _wrapping.Add((label, inset));
        return label;
    }

    /// <summary>A push button that runs a command with an optional parameter and follows its CanExecute.</summary>
    private NSButton Command(string title, ICommand command, Func<object?>? parameter = null)
    {
        var button = new NSButton { Title = title, BezelStyle = NSBezelStyle.Rounded, TranslatesAutoresizingMaskIntoConstraints = false };
        var binding = Bindings.Own(new CommandBinding(command, parameter ?? (() => null), enabled => button.Enabled = enabled, Dispatcher, ReportError));
        EventHandler handler = (_, _) => binding.Execute();
        button.Activated += handler;
        Bindings.Own(new ActionDisposable(() => button.Activated -= handler));
        return button;
    }

    private NSButton Link(string title, ICommand command, Func<object?>? parameter = null, double fontSize = 13)
    {
        var link = AccountSetupUi.Link(title, fontSize);
        var binding = Bindings.Own(new CommandBinding(command, parameter ?? (() => null), enabled => link.Enabled = enabled, Dispatcher, ReportError));
        EventHandler handler = (_, _) => binding.Execute();
        link.Activated += handler;
        Bindings.Own(new ActionDisposable(() => link.Activated -= handler));
        return link;
    }

    private NSButton ClickLink(string title, Action<NSButton> click)
    {
        var link = AccountSetupUi.Link(title);
        link.Activated += (_, _) => click(link);
        return link;
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

    private static NSView Centered(NSView view)
    {
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
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
    private static NSColor? ReadableOn(string? hex)
    {
        var value = hex?.TrimStart('#');
        if (value is not { Length: 6 } || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return null;
        return ReadableTextColor.PrefersDarkTextByLuminance((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF) ? NSColor.Black : NSColor.White;
    }

    #endregion

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _localInfo?.Close();
            _localInfo?.Dispose();
            _localInfo = null;
            _providerPicker?.Close();
            _providerPicker?.Dispose();
            _providerPicker = null;
            if (_changeMenu is not null) WinoMenus.DisposeTree(_changeMenu);
            _changeMenu = null;
        }
        base.Dispose(disposing);
    }

    private sealed class FlippedView : NSView
    {
        public FlippedView() => TranslatesAutoresizingMaskIntoConstraints = false;
        public override bool IsFlipped => true;
    }
}
