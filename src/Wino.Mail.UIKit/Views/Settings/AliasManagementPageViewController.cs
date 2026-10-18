using System.Security.Cryptography.X509Certificates;
using System.Windows.Input;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Aliases of one account (Mac AliasManagementPageViewController, Windows AliasManagementPage): Add and Sync as
/// navigation bar buttons, the summary line, then one expander per alias with its source, reply-to and send status. Copy address, Set primary
/// and Delete sit in the row's 44pt actions menu at phone width (inline buttons on the Mac). With S/MIME available the
/// nested rows hold the encryption switch and the signing certificate menu (enabled while encryption is on).
/// </summary>
public sealed class AliasManagementPageViewController(AliasManagementPageViewModel viewModel, IClipboardService clipboard, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AliasManagementPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private readonly WinoSettingsGroup _aliases = new();
    private BindingScope? _rowScope;

    public string? PageTitle => Translator.SettingsManageAliases_Title;
    /// <summary>The title is fixed.</summary>
    public event EventHandler? PageTitleChanged { add { } remove { } }

    protected override void BuildPage()
    {
        var vm = ViewModel;
        // Add and Synchronize are navigation bar buttons.
        BarButton(WinoIconGlyph.Add, Translator.Buttons_AddNewAlias, vm.AddNewAliasCommand);
        var sync = BarButton(WinoIconGlyph.Sync, Translator.Buttons_SyncAliases, vm.SyncAliasesCommand);
        Bind.Bind(vm, nameof(vm.CanSynchronizeAliases), s => s.CanSynchronizeAliases, visible => SetBarItemVisible(sync, visible));
        var syncing = Card(Translator.AccountAlias_Synchronizing, null, WinoIconGlyph.None, new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { TranslatesAutoresizingMaskIntoConstraints = false });
        if (syncing.Content is UIActivityIndicatorView spinner) spinner.StartAnimating();
        Bind.Visible(syncing, vm, nameof(vm.IsSynchronizing), s => s.IsSynchronizing);
        Add(syncing);

        // Both Windows InfoBars on this page are IsClosable="True".
        var info = InfoBar(WinoInfoBarSeverity.Informational, Translator.AccountAlias_Info_Title, Translator.AccountAlias_Info_Message);
        info.IsClosable = true;
        Add(info);

        var error = InfoBar(WinoInfoBarSeverity.Error, Translator.AccountAlias_SyncErrorTitle, null);
        error.IsClosable = true;
        Bind.Bind(vm, nameof(vm.SynchronizationError), s => s.SynchronizationError, text => { error.Message = text; error.Hidden = string.IsNullOrEmpty(text); });
        Add(error);

        var summary = Bind.Label(vm, nameof(vm.AliasSummary), s => s.AliasSummary, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        Bind.Visible(Add(summary), vm, nameof(vm.HasAliases), s => s.HasAliases);

        var loading = Caption(Translator.AccountAlias_Loading);
        Bind.Visible(Add(loading), vm, nameof(vm.IsLoading), s => s.IsLoading);

        var empty = Card(Translator.AccountAlias_Empty_Title, Translator.AccountAlias_Empty_Description, WinoIconGlyph.Mail);
        Bind.Visible(Add(empty), vm, nameof(vm.IsEmptyStateVisible), s => s.IsEmptyStateVisible);

        Add(_aliases);
        Bind.Bind(vm, nameof(vm.AccountAliases), s => s.AliasItems, Rebuild);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeAsync(mode, parameter);

    private void Rebuild(IReadOnlyList<AliasManagementItem>? items)
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        _aliases.Clear();

        foreach (var item in items ?? [])
        {
            var header = item.IsPrimary ? $"{item.AliasAddress}  ·  {Translator.AccountAlias_PrimaryBadge}" : item.AliasAddress;
            var description = item.HasStatusDetail ? $"{item.DescriptionText}\n{item.StatusDetailText}" : item.DescriptionText;
            var status = Caption(item.StatusText);
            status.TextColor = item.IsCapabilityDenied ? WinoStyle.Critical : item.IsCapabilityUnknown ? WinoStyle.Caution : WinoStyle.SecondaryText;
            WinoLayout.Priorities(status, UILayoutConstraintAxis.Horizontal, 750, 250);
            var controls = Row(status, ActionsButton(item));
            var icon = item.IsPrimary ? WinoIconGlyph.Star : WinoIconGlyph.Mail;
            var expander = new WinoSettingsExpander(header, description, icon, controls);
            expander.HeaderCard.AccessibilityLabel = item.RowAutomationName;
            if (item.IsSmimeAvailable) AddSmimeRows(expander, rows, item);

            // Read-only, as on Windows: the reply-to address is set where the alias itself is created.
            var replyTo = WinoStyle.Label(item.ReplyToText, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
            expander.Add(new WinoSettingsCard(Translator.AccountAlias_ReplyTo_Title, Translator.AccountAlias_ReplyTo_Description, WinoIconGlyph.Reply) { BottomContent = replyTo });
            _aliases.Add(expander);
        }
        _aliases.Hidden = _aliases.RowCount == 0;
    }

    /// <summary>A 44pt glyph button whose menu holds Copy address, Set primary and Delete (the Mac row buttons).</summary>
    private UIButton ActionsButton(AliasManagementItem item)
    {
        var elements = new List<UIMenuElement>
        {
            UIAction.Create(Translator.AccountAlias_CopyAddressAction, WinoIcons.Image(WinoIconGlyph.Copy, 17), null, _ => Run(CopyAddressAsync(item.AliasAddress)))
        };
        if (item.CanSetPrimary)
            elements.Add(UIAction.Create(Translator.AccountAlias_SetPrimaryAction, WinoIcons.Image(WinoIconGlyph.Star, 17), null, _ => Execute(ViewModel.SetAliasPrimaryCommand, item.Alias)));
        if (item.CanDelete)
        {
            var delete = UIAction.Create(Translator.AccountAlias_DeleteAction, WinoIcons.Image(WinoIconGlyph.Delete, 17, WinoStyle.Critical), null, _ => Execute(ViewModel.DeleteAliasCommand, item.Alias));
            delete.Attributes = UIMenuElementAttributes.Destructive;
            elements.Add(delete);
        }

        var button = new UIButton(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false, ShowsMenuAsPrimaryAction = true };
        button.SetImage(WinoIcons.Image(WinoIconGlyph.More, 17, WinoStyle.SecondaryText), UIControlState.Normal);
        button.Menu = UIMenu.Create([.. elements]);
        WinoLayout.Size(button, 44, 44);
        WinoAccessibility.Label(button, $"{Translator.More}, {item.AliasAddress}");
        return button;
    }

    /// <summary>Windows AliasManagementPage encryption card and signing certificate card for one alias.</summary>
    private void AddSmimeRows(WinoSettingsExpander expander, SettingsBinder rows, AliasManagementItem item)
    {
        var encryption = new WinoLabeledSwitch { IsOn = item.IsSmimeEncryptionEnabled, ShowsLabel = false };
        WinoAccessibility.Label(encryption.Switch, $"{Translator.AccountAlias_Encryption_Title}, {item.AliasAddress}");
        rows.OnActivated(encryption.Switch, () =>
        {
            if (encryption.IsOn == item.IsSmimeEncryptionEnabled) return;
            Run(ViewModel.SetAliasSmimeEncryption(item.Alias, encryption.IsOn));
        }, UIControlEvent.ValueChanged);
        expander.Add(new WinoSettingsCard(Translator.AccountAlias_Encryption_Title, Translator.AccountAlias_Encryption_Description, WinoIconGlyph.LockClosed, encryption));

        // The ViewModel keeps a blank first entry: it is the "None" choice.
        var certificates = item.Certificates.ToList();
        var selected = item.SelectedSigningCertificate is null ? 0 : certificates.FindIndex(certificate => certificate?.Thumbprint == item.SelectedSigningCertificate.Thumbprint);
        selected = Math.Max(0, selected);
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        var signing = new UIButton { Configuration = configuration, ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false };
        signing.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        WinoAccessibility.Label(signing, $"{Translator.SettingsSignatureAndEncryption_SigningCertificate}, {item.AliasAddress}");

        void Render(int chosen)
        {
            var actions = new UIMenuElement[certificates.Count];
            for (int index = 0; index < certificates.Count; index++)
            {
                var certificate = certificates[index];
                var captured = index;
                var action = UIAction.Create(CertificateText(certificate), null, null, _ =>
                {
                    if (certificate?.Thumbprint == item.SelectedSigningCertificate?.Thumbprint) return;
                    Render(captured);
                    Run(ViewModel.SetSelectedSigningCertificate(item.Alias, certificate));
                });
                action.State = index == chosen ? UIMenuElementState.On : UIMenuElementState.Off;
                actions[index] = action;
            }
            signing.Menu = UIMenu.Create(actions);
            if (signing.Configuration is { } current)
            {
                current.Title = chosen >= 0 && chosen < certificates.Count ? CertificateText(certificates[chosen]) : string.Empty;
                signing.Configuration = current;
            }
        }
        Render(selected);

        var signingCard = new WinoSettingsCard(Translator.SettingsSignatureAndEncryption_SigningCertificate, item.CertificateDescriptionText, WinoIconGlyph.Certificate)
        {
            BottomContent = signing
        };
        signingCard.IsEnabled = item.IsSmimeEncryptionEnabled && item.HasCertificates;
        expander.Add(signingCard);
    }

    private static string CertificateText(X509Certificate2? certificate)
        => certificate is null ? Translator.SettingsSignatureAndEncryption_SigningCertificatePlaceholder : CertificateTitle(certificate);

    private static string CertificateTitle(X509Certificate2 certificate)
    {
        var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
        if (string.IsNullOrWhiteSpace(name)) name = certificate.Subject;
        return $"{name} ({certificate.NotAfter:d})";
    }

    private async Task CopyAddressAsync(string address)
        => (await clipboard.CopyTextAsync(address)).ThrowIfNotSucceeded();

    private void Execute(ICommand command, object? parameter)
    {
        try
        {
            if (command.CanExecute(parameter)) command.Execute(parameter);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (Exception exception) { ReportError(exception); }
    }
}
