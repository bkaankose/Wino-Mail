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
/// The first-run page (Windows WelcomePageV2): app mark, title and description, Get started right below them, then
/// one wrapping row of links to restore a Wino Account or a backup file or to import accounts from other mail clients,
/// and the import status. Get started opens the address-first setup in the same window; a finished import or a
/// created account replaces this page with the shell.
/// </summary>
internal sealed class WelcomeView : WinoView<WelcomePageV2ViewModel>
{
    private readonly Adw.ToolbarView _root;
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
        var description = OnboardingUi.Subtitle(Translator.Linux_Welcome_AppDescription);
        description.SetMaxWidthChars(52);

        _getStarted = OnboardingUi.Button(Translator.WelcomeWindow_GetStartedButton, suggested: true);
        _getStarted.AddCssClass("pill");
        _getStarted.SetHalign(Gtk.Align.Center);
        _getStarted.SetSizeRequest(240, -1);
        if (_getStarted.GetChild() is Gtk.Label getStartedLabel) getStartedLabel.SetEllipsize(Pango.EllipsizeMode.End);
        Bind.BindCommand(_getStarted, Gated(vm.GetStartedCommand));
        WinoA11y.Rename(_getStarted, Translator.WelcomeWindow_GetStartedButton);

        var caption = OnboardingUi.Caption(Translator.WelcomeWindow_RestoreOrImportCaption);
        caption.SetJustify(Gtk.Justification.Center);
        caption.SetXalign(0.5f);

        var links = Adw.WrapBox.New();
        links.SetChildSpacing(4);
        links.SetLineSpacing(0);
        links.SetHalign(Gtk.Align.Center);
        links.SetAlign(0.5f);
        links.Append(Link(Translator.WelcomeWindow_RestoreLinkWinoAccount, Translator.WelcomeWindow_ImportFromWinoAccount, Gated(vm.ImportFromWinoAccountCommand)));
        links.Append(Link(Translator.WelcomeWindow_BackupFileLink, Translator.WelcomeWindow_ImportFromJsonFile, Gated(vm.ImportFromJsonCommand)));
        links.Append(Link(Translator.WelcomeWindow_RestoreLinkOtherClients, Translator.MailClientImport_Title, Gated(vm.ImportFromOtherMailClientsCommand)));

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
        var restore = OnboardingUi.Column(2, caption, links);
        restore.SetMarginTop(8);
        var hero = OnboardingUi.Column(16, mark, heading, _getStarted, restore, busy, status);
        hero.SetValign(Gtk.Align.Center);
        hero.SetVexpand(true);
        hero.SetMarginTop(40);
        hero.SetMarginBottom(40);
        hero.SetMarginStart(24);
        hero.SetMarginEnd(24);

        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(560);
        clamp.SetChild(hero);
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetVexpand(true);
        scroller.SetChild(clamp);

        var header = Adw.HeaderBar.New();
        header.SetShowTitle(false);
        _root = Adw.ToolbarView.New();
        _root.AddTopBar(header);
        _root.SetContent(scroller);
#if DEBUG
        _debugCurrent = new WeakReference<WelcomeView>(this);
#endif
    }

    private Gtk.Button Link(string text, string accessibleName, ICommand command)
    {
        var link = OnboardingUi.Link(text);
        Bind.BindCommand(link, command);
        WinoA11y.Rename(link, accessibleName);
        return link;
    }

    public override Gtk.Widget Root => _root;

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
