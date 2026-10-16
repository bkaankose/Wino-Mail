using System.Security.Cryptography.X509Certificates;
using Android.Views;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.Accounts;

/// <summary>
/// Aliases of one account (iOS AliasManagementPageViewController, Windows AliasManagementPage): Add and Sync buttons,
/// the summary line, then one expander per alias with its source, reply-to and send status. Copy address, Set primary
/// and Delete sit in the row's 48 dp actions menu at phone width. With S/MIME available the nested rows hold the
/// encryption switch and the signing certificate menu (enabled while encryption is on).
/// </summary>
public sealed class AliasManagementPageFragment : SettingsPageFragment<AliasManagementPageViewModel>, ISettingsPageTitleSource
{
    private WinoSettingsGroup? _aliases;
    private BindingScope? _rowScope;

    public string? PageTitle => Translator.SettingsManageAliases_Title;

    protected override void BuildPage()
    {
        var vm = VM;
        Bindings.Own(new ActionDisposable(() => _rowScope?.Dispose()));
        var sync = ActionCard(Translator.Buttons_SyncAliases, vm.SyncAliasesCommand);
        Bind.Visible(sync, vm, nameof(vm.CanSynchronizeAliases), s => s.CanSynchronizeAliases);
        var syncing = Card(Translator.AccountAlias_Synchronizing, null);
        Bind.Visible(syncing, vm, nameof(vm.IsSynchronizing), s => s.IsSynchronizing);
        AddGroup(null, ActionCard(Translator.Buttons_AddNewAlias, vm.AddNewAliasCommand), sync, syncing);

        // Both Windows InfoBars on this page are IsClosable="True".
        var info = InfoBar(WinoInfoBarSeverity.Informational, Translator.AccountAlias_Info_Title, Translator.AccountAlias_Info_Message);
        info.IsClosable = true;
        Add(info);

        var error = InfoBar(WinoInfoBarSeverity.Error, Translator.AccountAlias_SyncErrorTitle, null);
        error.IsClosable = true;
        Bind.Bind(vm, nameof(vm.SynchronizationError), s => s.SynchronizationError, text =>
        {
            error.Message = text;
            error.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
        });
        Add(error);

        var summary = Bind.Label(vm, nameof(vm.AliasSummary), s => s.AliasSummary, WinoTextStyle.Description, maximumLines: 0);
        Bind.Visible(Add(summary), vm, nameof(vm.HasAliases), s => s.HasAliases);

        var loading = Caption(Translator.AccountAlias_Loading);
        Bind.Visible(Add(loading), vm, nameof(vm.IsLoading), s => s.IsLoading);

        var empty = Card(Translator.AccountAlias_Empty_Title, Translator.AccountAlias_Empty_Description, WinoIconGlyph.Mail);
        Bind.Visible(Add(empty), vm, nameof(vm.IsEmptyStateVisible), s => s.IsEmptyStateVisible);

        _aliases = Add(Group(null));
        Bind.Bind(vm, nameof(vm.AccountAliases), s => s.AliasItems, Rebuild);
    }

    private void Rebuild(IReadOnlyList<AliasManagementItem>? items)
    {
        if (_aliases is null) return;
        _rowScope?.Dispose();
        _rowScope = new BindingScope();
        var rows = Bind.Child(_rowScope);
        _aliases.Clear();

        foreach (var item in items ?? [])
        {
            var header = item.IsPrimary ? $"{item.AliasAddress}  ·  {Translator.AccountAlias_PrimaryBadge}" : item.AliasAddress;
            var description = item.HasStatusDetail ? $"{item.DescriptionText}\n{item.StatusDetailText}" : item.DescriptionText;
            var status = Caption(item.StatusText);
            status.SetMaxLines(2);
            status.SetTextColor(item.IsCapabilityDenied ? WinoColors.Critical(Ctx) : item.IsCapabilityUnknown ? WinoColors.Caution(Ctx) : WinoColors.SecondaryText(Ctx));
            var controls = Row(status, ActionsButton(rows, item));
            var icon = item.IsPrimary ? WinoIconGlyph.Star : WinoIconGlyph.Mail;
            var expander = new WinoSettingsExpander(Ctx, header, description, icon, controls);
            expander.HeaderCard.ContentDescription = item.RowAutomationName;
            if (item.IsSmimeAvailable) AddSmimeRows(expander, rows, item);

            // Read-only, as on Windows: the reply-to address is set where the alias itself is created.
            expander.Add(new WinoSettingsCard(Ctx, Translator.AccountAlias_ReplyTo_Title, Translator.AccountAlias_ReplyTo_Description, WinoIconGlyph.Reply)
            {
                BottomContent = Text(item.ReplyToText, WinoTextStyle.Description)
            });
            _aliases.Add(expander);
        }
        _aliases.Visibility = _aliases.RowCount == 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>A 48 dp glyph button whose menu holds Copy address, Set primary and Delete (the Mac row buttons).</summary>
    private View ActionsButton(SettingsBinder rows, AliasManagementItem item)
    {
        var entries = new List<SettingsMenuEntry>
        {
            new(Translator.AccountAlias_CopyAddressAction, () => AccountSettingsParts.Run(CopyAddressAsync(item.AliasAddress), ReportError))
        };
        if (item.CanSetPrimary)
            entries.Add(new(Translator.AccountAlias_SetPrimaryAction, () => AccountSettingsParts.Execute(VM.SetAliasPrimaryCommand, item.Alias, ReportError)));
        if (item.CanDelete)
            entries.Add(new(Translator.AccountAlias_DeleteAction, () => AccountSettingsParts.Execute(VM.DeleteAliasCommand, item.Alias, ReportError), IsDestructive: true));
        return AccountSettingsParts.MoreButton(Ctx, rows, $"{Translator.More}, {item.AliasAddress}", entries);
    }

    /// <summary>Windows AliasManagementPage encryption card and signing certificate card for one alias.</summary>
    private void AddSmimeRows(WinoSettingsExpander expander, SettingsBinder rows, AliasManagementItem item)
    {
        var encryption = WinoMaterialAccent.Tint(new Google.Android.Material.MaterialSwitch.MaterialSwitch(Ctx)
        {
            Checked = item.IsSmimeEncryptionEnabled,
            ContentDescription = $"{Translator.AccountAlias_Encryption_Title}, {item.AliasAddress}"
        });
        EventHandler<global::Android.Widget.CompoundButton.CheckedChangeEventArgs> toggled = (_, args) =>
        {
            if (args.IsChecked == item.IsSmimeEncryptionEnabled) return;
            AccountSettingsParts.Run(VM.SetAliasSmimeEncryption(item.Alias, args.IsChecked), ReportError);
        };
        encryption.CheckedChange += toggled;
        rows.Scope.Own(new ActionDisposable(() => encryption.CheckedChange -= toggled));
        expander.Add(new WinoSettingsCard(Ctx, Translator.AccountAlias_Encryption_Title, Translator.AccountAlias_Encryption_Description, WinoIconGlyph.LockClosed, encryption));

        // The ViewModel keeps a blank first entry: it is the "None" choice.
        var certificates = item.Certificates.ToList();
        var selected = item.SelectedSigningCertificate is null ? 0 : certificates.FindIndex(certificate => certificate?.Thumbprint == item.SelectedSigningCertificate.Thumbprint);
        selected = Math.Max(0, selected);
        var signing = SettingsBinder.CreateDropdown(Ctx, 0);
        signing.ContentDescription = $"{Translator.SettingsSignatureAndEncryption_SigningCertificate}, {item.AliasAddress}";

        void Render(int chosen) => signing.Text = chosen >= 0 && chosen < certificates.Count ? CertificateText(certificates[chosen]) : string.Empty;
        Render(selected);
        rows.Views.Click(signing, () =>
        {
            var entries = new List<SettingsMenuEntry>();
            for (int index = 0; index < certificates.Count; index++)
            {
                var certificate = certificates[index];
                var captured = index;
                entries.Add(new(CertificateText(certificate), () =>
                {
                    if (certificate?.Thumbprint == item.SelectedSigningCertificate?.Thumbprint) return;
                    selected = captured;
                    Render(captured);
                    AccountSettingsParts.Run(VM.SetSelectedSigningCertificate(item.Alias, certificate), ReportError);
                }, IsChecked: index == selected));
            }
            AccountSettingsParts.ShowMenu(Ctx, signing, entries, ReportError);
        });

        var signingCard = new WinoSettingsCard(Ctx, Translator.SettingsSignatureAndEncryption_SigningCertificate, item.CertificateDescriptionText, WinoIconGlyph.Certificate)
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

    private static async Task CopyAddressAsync(string address)
    {
        if (MainApplication.Services?.GetService<IClipboardService>() is not { } clipboard) return;
        (await clipboard.CopyTextAsync(address)).ThrowIfNotSucceeded();
    }
}
