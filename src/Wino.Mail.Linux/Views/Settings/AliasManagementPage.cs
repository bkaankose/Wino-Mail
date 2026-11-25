using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// The aliases of one account (Windows AliasManagementPage, macOS order): Add and Synchronize, the information and the
/// synchronisation error bars, the summary, then one expander per alias with its source, reply-to and send status, Copy,
/// Set primary and Delete. The reply-to address is read-only, as on Windows; it is set when the alias is created. With
/// S/MIME each alias also has the encryption switch and the signing certificate (macOS: the certificate row is enabled
/// only while encryption is on and the alias has a certificate). The navigation parameter is the account id.
/// </summary>
internal sealed class AliasManagementPage(AliasManagementPageViewModel viewModel, WinoViewServices services, IClipboardService clipboard)
    : SettingsPageView<AliasManagementPageViewModel>(viewModel, services), ISettingsPageTitleSource
{
    private readonly List<(Guid Id, Adw.ExpanderRow Row)> _expanders = [];
    private WinoSettingsGroup? _aliases;
    private SettingsBinder? _rows;

#if DEBUG
    internal static AliasManagementPage? DebugCurrent { get; private set; }
#endif

    public string? PageTitle => Translator.SettingsManageAliases_Title;

    public string? PageDescription => ViewModel.Account?.Address;

    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
#if DEBUG
        DebugCurrent = this;
        Settings.Own(new ActionDisposable(() =>
        {
            if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
        }));
#endif
        Settings.Bind(vm, nameof(vm.Account), static s => s.Account, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));

        var info = new WinoInfoBar(WinoInfoBarSeverity.Informational, Translator.AccountAlias_Info_Title, Translator.AccountAlias_Info_Message) { IsClosable = true };
        Settings.Own(info);
        var error = new WinoInfoBar(WinoInfoBarSeverity.Error, Translator.AccountAlias_SyncErrorTitle) { IsClosable = true };
        Settings.Own(error);
        Settings.Bind(vm, nameof(vm.SynchronizationError), static s => s.SynchronizationError, text =>
        {
            error.Message = text ?? string.Empty;
            error.IsOpen = !string.IsNullOrEmpty(text);
        });
        var bars = Page.AddGroup();
        bars.HidesWhenEmpty = false;
        var stack = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        stack.Append(info.Root);
        stack.Append(error.Root);
        bars.Root.Add(stack);

        var add = Settings.Button(Translator.Buttons_AddNewAlias, vm.AddNewAliasCommand, suggested: true, icon: WinoIconGlyph.Add);
        var sync = Settings.Visible(Settings.Button(Translator.Buttons_SyncAliases, vm.SyncAliasesCommand, icon: WinoIconGlyph.Sync),
            vm, nameof(vm.CanSynchronizeAliases), static s => s.CanSynchronizeAliases);
        var syncing = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        syncing.Append(Adw.Spinner.New());
        var syncingText = Gtk.Label.New(Translator.AccountAlias_Synchronizing);
        syncingText.AddCssClass("dim-label");
        syncing.Append(syncingText);
        Settings.Visible(syncing, vm, nameof(vm.IsSynchronizing), static s => s.IsSynchronizing);
        var commands = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        commands.Append(syncing);
        commands.Append(sync);
        commands.Append(add);

        var aliases = _aliases = Page.AddGroup(Translator.SettingsManageAliases_Title);
        aliases.HidesWhenEmpty = false;
        aliases.HeaderSuffix = commands;
        Settings.Bind(vm, nameof(vm.AliasSummary), static s => s.AliasSummary, summary => aliases.Description = summary);

        var loading = WinoSettingsRows.Action(Translator.AccountAlias_Loading);
        loading.AddPrefix(Adw.Spinner.New());
        var empty = Adw.StatusPage.New();
        empty.SetTitle(Translator.AccountAlias_Empty_Title);
        empty.SetDescription(Translator.AccountAlias_Empty_Description);
        empty.AddCssClass("compact");
        var states = Page.AddGroup(null, null, Settings.Visible(loading, vm, nameof(vm.IsLoading), static s => s.IsLoading),
            Settings.Visible(WinoSettingsRows.Custom(empty, Translator.AccountAlias_Empty_Title), vm, nameof(vm.IsEmptyStateVisible), static s => s.IsEmptyStateVisible));
        Settings.Bind(vm, nameof(vm.IsEmptyStateVisible), static s => s.IsLoading || s.IsEmptyStateVisible, states.Root.SetVisible);

        Settings.Bind(vm, nameof(vm.AccountAliases), static s => s.AliasItems, Rebuild);
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeAsync(mode, parameter);

    private void Rebuild(AliasManagementItemList? items)
    {
        if (_aliases is null || IsReleased) return;
        var expanded = _expanders.Where(static entry => entry.Row.GetExpanded()).Select(static entry => entry.Id).ToHashSet();
        _expanders.Clear();
        _rows?.Scope.Dispose();
        var rows = _rows = Settings.Child();
        _aliases.Clear();

        foreach (var item in items ?? Enumerable.Empty<AliasManagementItem>())
        {
            var address = item.AliasAddress ?? string.Empty;
            var description = item.HasStatusDetail ? $"{item.DescriptionText}\n{item.StatusDetailText}" : item.DescriptionText;
            var row = WinoSettingsRows.Expander(address, description, WinoIconGlyph.Mail);
            // An expander row lays its suffixes out from the end, so they are added in reverse reading order.
            if (item.CanDelete)
            {
                var delete = WinoSettingsRows.Suffix(row, WinoSettingsRows.GlyphButton(WinoIconGlyph.Delete, $"{Translator.AccountAlias_DeleteAction}, {address}"));
                delete.AddCssClass("flat");
                rows.Own(GtkSignals.OnClicked(delete, () =>
                {
                    var command = ViewModel.DeleteAliasCommand;
                    if (command.CanExecute(item.Alias)) command.Execute(item.Alias);
                }));
            }

            if (item.CanSetPrimary)
            {
                var primary = WinoSettingsRows.Suffix(row, rows.Button(Translator.AccountAlias_SetPrimaryAction, ViewModel.SetAliasPrimaryCommand, () => item.Alias, icon: WinoIconGlyph.Star));
                WinoA11y.Rename(primary, $"{Translator.AccountAlias_SetPrimaryAction}, {address}");
            }
            var copy = WinoSettingsRows.Suffix(row, WinoSettingsRows.GlyphButton(WinoIconGlyph.Copy, $"{Translator.AccountAlias_CopyAddressAction}, {address}"));
            copy.AddCssClass("flat");
            rows.Own(GtkSignals.OnClicked(copy, () => Observe(CopyAsync(address))));
            if (item.IsPrimary)
            {
                var badge = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
                badge.AddCssClass("wino-chip");
                badge.SetValign(Gtk.Align.Center);
                badge.Append(WinoIcons.Label(WinoIconGlyph.Star, 11));
                var badgeText = Gtk.Label.New(Translator.AccountAlias_PrimaryBadge);
                badgeText.AddCssClass("caption");
                badge.Append(badgeText);
                WinoSettingsRows.Suffix(row, badge);
            }
            var status = Gtk.Label.New(item.StatusText);
            status.AddCssClass("caption");
            status.AddCssClass(item.IsCapabilityDenied ? "error" : item.IsCapabilityUnknown ? "warning" : "dim-label");
            WinoSettingsRows.Suffix(row, status);

            if (item.IsSmimeAvailable) AddSmimeRows(row, rows, item);
            row.AddRow(WinoSettingsRows.Action(Translator.AccountAlias_ReplyTo_Title, item.ReplyToText, WinoIconGlyph.Reply));
            WinoA11y.Rename(row, item.RowAutomationName);
            row.SetExpanded(expanded.Contains(item.Alias.Id));
            _expanders.Add((item.Alias.Id, row));
            _aliases.Add(row);
        }
    }

    private void AddSmimeRows(Adw.ExpanderRow expander, SettingsBinder rows, AliasManagementItem item)
    {
        var address = item.AliasAddress ?? string.Empty;
        var encryption = WinoSettingsRows.Switch(Translator.AccountAlias_Encryption_Title, Translator.AccountAlias_Encryption_Description, WinoIconGlyph.LockClosed);
        encryption.SetActive(item.IsSmimeEncryptionEnabled);
        WinoA11y.Rename(encryption, $"{Translator.AccountAlias_Encryption_Title}, {address}");
        rows.Own(GtkSignals.OnNotify(encryption, "active", () =>
        {
            var on = encryption.GetActive();
            if (on != item.IsSmimeEncryptionEnabled) Observe(ViewModel.SetAliasSmimeEncryption(item.Alias, on));
        }));
        expander.AddRow(encryption);

        // The ViewModel keeps a blank first entry: it is the "None" choice.
        var certificates = item.Certificates.ToArray();
        var titles = certificates.Select(static certificate => certificate is null
            ? Translator.SettingsSignatureAndEncryption_SigningCertificatePlaceholder
            : CertificateTitle(certificate)).ToArray();
        var signing = WinoSettingsRows.Combo(Translator.SettingsSignatureAndEncryption_SigningCertificate, item.CertificateDescriptionText, WinoIconGlyph.Certificate);
        signing.SetModel(Gtk.StringList.New(titles));
        var current = item.SelectedSigningCertificate?.Thumbprint;
        signing.SetSelected((uint)Math.Max(0, current is null ? 0 : Array.FindIndex(certificates, certificate => certificate?.Thumbprint == current)));
        signing.SetSensitive(item.IsSmimeEncryptionEnabled && item.HasCertificates);
        WinoA11y.Rename(signing, $"{Translator.SettingsSignatureAndEncryption_SigningCertificate}, {address}");
        rows.Own(GtkSignals.OnNotify(signing, "selected", () =>
        {
            var index = (int)signing.GetSelected();
            if (index < 0 || index >= certificates.Length) return;
            var certificate = certificates[index];
            if (certificate?.Thumbprint != item.SelectedSigningCertificate?.Thumbprint) Observe(ViewModel.SetSelectedSigningCertificate(item.Alias, certificate));
        }));
        expander.AddRow(signing);
    }

    private static string CertificateTitle(X509Certificate2 certificate)
    {
        string name;
        try { name = certificate.GetNameInfo(X509NameType.SimpleName, false); }
        catch (CryptographicException) { name = string.Empty; }
        if (string.IsNullOrWhiteSpace(name)) name = certificate.Subject;
        return $"{name} ({certificate.NotAfter.ToString("d", CultureInfo.CurrentCulture)})";
    }

#if DEBUG
    internal string DebugSmime()
    {
        var aliases = ViewModel.AliasItems;
        return string.Join(" | ", aliases.Select(static item =>
            $"{item.AliasAddress} encrypt={item.IsSmimeEncryptionEnabled} certs={item.Certificates.Count(static c => c is not null)} " +
            $"selected={item.SelectedSigningCertificate?.Thumbprint ?? "-"} stored={item.Alias.SelectedSigningCertificateThumbprint ?? "-"}"));
    }

    internal Task DebugSetEncryptionAsync(string address, bool on)
        => ViewModel.AccountAliases.FirstOrDefault(alias => string.Equals(alias.AliasAddress, address, StringComparison.OrdinalIgnoreCase)) is { } alias
            ? ViewModel.SetAliasSmimeEncryption(alias, on)
            : throw new InvalidOperationException("no alias " + address);

    internal Task DebugSetCertificateAsync(string address, int index)
    {
        var alias = ViewModel.AccountAliases.FirstOrDefault(alias => string.Equals(alias.AliasAddress, address, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("no alias " + address);
        return ViewModel.SetSelectedSigningCertificate(alias, index >= 0 && index < alias.Certificates.Count ? alias.Certificates[index] : null);
    }
#endif

    private async Task CopyAsync(string address) => (await clipboard.CopyTextAsync(address)).ThrowIfNotSucceeded();
}
