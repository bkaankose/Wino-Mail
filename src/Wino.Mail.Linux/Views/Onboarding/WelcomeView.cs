using System.Windows.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Platform.Linux.Security;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// The first-run page (macOS WelcomePageV2): app mark, title, one-line description and the import status in the centre,
/// a footer with the two restore links at the start and Get started at the end. Get started opens the address-first
/// setup in the same window; a finished import or a created account replaces this page with the shell.
/// </summary>
internal sealed class WelcomeView : WinoView<WelcomePageV2ViewModel>
{
    private readonly Adw.ToolbarView _root;
    private readonly Adw.BreakpointBin _bin;
    private readonly Gtk.Button _getStarted;

    public WelcomeView(WelcomePageV2ViewModel viewModel, WinoViewServices services, LinuxProtectionKeySetup keySetup) : base(viewModel, services)
    {
        var vm = ViewModel;
        // Every way out of this page stores credentials, so each first creates the keyring key (and may prompt).
        ICommand Gated(ICommand command) => new KeyringGatedCommand(command, keySetup);

        var mark = Gtk.Image.NewFromIconName(Program.ApplicationId);
        mark.SetPixelSize(96);
        mark.SetHalign(Gtk.Align.Center);

        var title = OnboardingUi.Title(Translator.WelcomeWindow_Title);
        title.AccessibleRole = Gtk.AccessibleRole.Heading;
        var description = OnboardingUi.Subtitle(Translator.WelcomeWindow_GetStartedDescription);
        description.SetMaxWidthChars(52);

        var spinner = OnboardingUi.Spinner();
        var progress = Gtk.Label.New(Translator.WelcomeWindow_ImportInProgress);
        progress.AddCssClass("dim-label");
        var busy = OnboardingUi.Centered(spinner, progress);
        Bind.BindVisible(busy, nameof(vm.IsImportInProgress), static s => s.IsImportInProgress);

        var status = OnboardingUi.Subtitle();
        status.SetMaxWidthChars(52);
        Bind.BindText(status, nameof(vm.ImportStatusMessage), static s => s.ImportStatusMessage);
        Bind.BindVisible(status, nameof(vm.HasImportStatus), static s => s.HasImportStatus);

        var heading = OnboardingUi.Column(6, title, description);
        var hero = OnboardingUi.Column(16, mark, heading, busy, status);
        hero.SetValign(Gtk.Align.Center);
        hero.SetVexpand(true);
        hero.SetMarginTop(40);
        hero.SetMarginBottom(24);
        hero.SetMarginStart(24);
        hero.SetMarginEnd(24);

        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(560);
        clamp.SetChild(hero);
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetVexpand(true);
        scroller.SetChild(clamp);

        var importAccount = OnboardingUi.Link(Translator.WelcomeWindow_ImportFromWinoAccount);
        Bind.BindCommand(importAccount, Gated(vm.ImportFromWinoAccountCommand));
        WinoA11y.Rename(importAccount, Translator.WelcomeWindow_ImportFromWinoAccount);
        var importFile = OnboardingUi.Link(Translator.WelcomeWindow_ImportFromJsonFile);
        Bind.BindCommand(importFile, Gated(vm.ImportFromJsonCommand));
        WinoA11y.Rename(importFile, Translator.WelcomeWindow_ImportFromJsonFile);

        // The links wrap under each other before Get started would be pushed out of a narrow window.
        var links = Adw.WrapBox.New();
        links.SetChildSpacing(4);
        links.SetLineSpacing(4);
        links.SetHexpand(true);
        links.SetValign(Gtk.Align.Center);
        links.Append(importAccount);
        links.Append(importFile);

        _getStarted = OnboardingUi.Button(Translator.WelcomeWindow_GetStartedButton, suggested: true);
        _getStarted.AddCssClass("pill");
        _getStarted.SetValign(Gtk.Align.Center);
        Bind.BindCommand(_getStarted, Gated(vm.GetStartedCommand));
        WinoA11y.Rename(_getStarted, Translator.WelcomeWindow_GetStartedButton);

        var footer = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        footer.SetMarginTop(12);
        footer.SetMarginBottom(12);
        footer.SetMarginStart(16);
        footer.SetMarginEnd(16);
        footer.Append(links);
        footer.Append(_getStarted);

        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        content.Append(scroller);
        content.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        content.Append(footer);

        var header = Adw.HeaderBar.New();
        header.SetShowTitle(false);
        _root = Adw.ToolbarView.New();
        _root.AddTopBar(header);
        _root.SetContent(content);

        if (_getStarted.GetChild() is Gtk.Label getStartedLabel) getStartedLabel.SetEllipsize(Pango.EllipsizeMode.End);
        _bin = OnboardingUi.NarrowBin(_root, Bindings, narrow => footer.SetOrientation(narrow ? Gtk.Orientation.Vertical : Gtk.Orientation.Horizontal));
#if DEBUG
        _debugCurrent = new WeakReference<WelcomeView>(this);
#endif
    }

    public override Gtk.Widget Root => _bin;

    public override bool HasPendingWork => base.HasPendingWork || ViewModel.IsImportInProgress;

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        if (_root.GetRoot() is Gtk.Window window) window.SetTitle(AppActions.ApplicationName);
        _getStarted.GrabFocus();
        ViewModel.OnNavigatedTo(mode, parameter);
        return Task.CompletedTask;
    }

#if DEBUG
    private static WeakReference<WelcomeView>? _debugCurrent;

    public static WelcomeView? DebugCurrent => _debugCurrent is not null && _debugCurrent.TryGetTarget(out var view) && !view.IsReleased ? view : null;

    public string DebugDescribe()
        => $"busy={ViewModel.IsImportInProgress} status={ViewModel.ImportStatusMessage} getStarted={_getStarted.GetSensitive()}\n";
#endif
}
