using System.Security.Cryptography.X509Certificates;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Aliases of one account (Manage accounts › account › Aliases): Add and Sync buttons, the summary
/// line, then one card per alias with its source, reply-to and send status, Set primary and Delete
/// (Windows AliasManagementPage). With S/MIME available each alias is an expander whose nested rows
/// hold the encryption switch and the signing certificate pop-up (enabled while encryption is on).
/// </summary>
public sealed class AliasManagementPageViewController(AliasManagementPageViewModel viewModel, IClipboardService clipboard, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AliasManagementPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private readonly WinoSettingsGroup _aliases = new();
    private BindingScope? _rowScope;

    public string? PageTitle => Translator.SettingsManageAliases_Title;
    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var add = Bind.Button(Translator.Buttons_AddNewAlias, vm.AddNewAliasCommand, primary: true, icon: WinoIconGlyph.Add);
        var sync = Bind.Button(Translator.Buttons_SyncAliases, vm.SyncAliasesCommand, icon: WinoIconGlyph.Sync);
        Bind.Visible(sync, vm, nameof(vm.CanSynchronizeAliases), s => s.CanSynchronizeAliases);
        var syncing = Caption(Translator.AccountAlias_Synchronizing);
        Bind.Visible(syncing, vm, nameof(vm.IsSynchronizing), s => s.IsSynchronizing);
        var buttons = Row(add, sync, syncing);
        buttons.EdgeInsets = new NSEdgeInsets(0, 0, 8, 0);
        Add(buttons);

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
        => ViewModel.InitializeAsync(mode, parameter!);

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
            var controls = Row(status);
            var copy = rows.Button(string.Empty, () => Run(CopyAddressAsync(item.AliasAddress)), icon: WinoIconGlyph.Copy);
            copy.ToolTip = Translator.AccountAlias_CopyAddressAction;
            WinoAccessibility.Label(copy, $"{Translator.AccountAlias_CopyAddressAction}, {item.AliasAddress}");
            controls.AddArrangedSubview(copy);
            if (item.CanSetPrimary) controls.AddArrangedSubview(rows.Button(Translator.AccountAlias_SetPrimaryAction, ViewModel.SetAliasPrimaryCommand, () => item.Alias, icon: WinoIconGlyph.Star));
            if (item.CanDelete)
            {
                var delete = rows.Button(string.Empty, ViewModel.DeleteAliasCommand, () => item.Alias, icon: WinoIconGlyph.Delete);
                delete.ToolTip = Translator.AccountAlias_DeleteAction;
                controls.AddArrangedSubview(delete);
            }
            var icon = item.IsPrimary ? WinoIconGlyph.Star : WinoIconGlyph.Mail;
            var expander = new WinoSettingsExpander(header, description, icon, controls);
            expander.HeaderCard.AccessibilityLabel = item.RowAutomationName;
            if (item.IsSmimeAvailable) AddSmimeRows(expander, rows, item);

            // Read-only, as on Windows: the reply-to address is set where the alias itself is created.
            var replyTo = WinoStyle.Label(item.ReplyToText, WinoStyle.Body, WinoStyle.SecondaryText);
            expander.Add(new WinoSettingsCard(Translator.AccountAlias_ReplyTo_Title, Translator.AccountAlias_ReplyTo_Description, WinoIconGlyph.Reply, replyTo));
            _aliases.Add(expander);
        }
        _aliases.Hidden = _aliases.RowCount == 0;
    }

    /// <summary>Windows AliasManagementPage encryption card and signing certificate card for one alias.</summary>
    private void AddSmimeRows(WinoSettingsExpander expander, SettingsBinder rows, AliasManagementItem item)
    {
        var encryption = new WinoLabeledSwitch { IsOn = item.IsSmimeEncryptionEnabled };
        WinoAccessibility.Label(encryption.Switch, $"{Translator.AccountAlias_Encryption_Title}, {item.AliasAddress}");
        rows.OnActivated(encryption.Switch, () =>
        {
            if (encryption.IsOn == item.IsSmimeEncryptionEnabled) return;
            Run(ViewModel.SetAliasSmimeEncryption(item.Alias, encryption.IsOn));
        });
        expander.Add(new WinoSettingsCard(Translator.AccountAlias_Encryption_Title, Translator.AccountAlias_Encryption_Description, WinoIconGlyph.LockClosed, encryption));

        // The ViewModel keeps a blank first entry: it is the "None" choice.
        var certificates = item.Certificates.ToList();
        var signing = new NSPopUpButton { TranslatesAutoresizingMaskIntoConstraints = false };
        foreach (var certificate in certificates)
            signing.Menu!.AddItem(new NSMenuItem(certificate is null ? Translator.SettingsSignatureAndEncryption_SigningCertificatePlaceholder : CertificateTitle(certificate)));
        var selected = item.SelectedSigningCertificate is null ? 0 : certificates.FindIndex(certificate => certificate?.Thumbprint == item.SelectedSigningCertificate.Thumbprint);
        signing.SelectItem(Math.Max(0, selected));
        signing.WidthAnchor.ConstraintLessThanOrEqualTo(320).Active = true;
        WinoAccessibility.Label(signing, $"{Translator.SettingsSignatureAndEncryption_SigningCertificate}, {item.AliasAddress}");
        rows.OnActivated(signing, () =>
        {
            var index = (int)signing.IndexOfSelectedItem;
            if (index < 0 || index >= certificates.Count) return;
            var certificate = certificates[index];
            if (certificate?.Thumbprint == item.SelectedSigningCertificate?.Thumbprint) return;
            Run(ViewModel.SetSelectedSigningCertificate(item.Alias, certificate!));
        });

        var signingCard = new WinoSettingsCard(Translator.SettingsSignatureAndEncryption_SigningCertificate, item.CertificateDescriptionText, WinoIconGlyph.Certificate, signing);
        signingCard.IsEnabled = item.IsSmimeEncryptionEnabled && item.HasCertificates;
        expander.Add(signingCard);
    }

    private static string CertificateTitle(X509Certificate2 certificate)
    {
        var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
        if (string.IsNullOrWhiteSpace(name)) name = certificate.Subject;
        return $"{name} ({certificate.NotAfter:d})";
    }

    private async Task CopyAddressAsync(string address)
        => (await clipboard.CopyTextAsync(address)).ThrowIfNotSucceeded();

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (Exception exception) { ReportError(exception); }
    }
}
