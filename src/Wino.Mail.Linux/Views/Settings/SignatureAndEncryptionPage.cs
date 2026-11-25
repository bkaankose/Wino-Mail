using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;
using Wino.Services.Interfaces;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Signature and encryption (macOS SignatureAndEncryptionPageViewController): my certificates and recipient
/// certificates, each a multi-selection list with Import, Export, Remove and View certificate. Import asks for a PKCS#12
/// password in a password field; the selection is mirrored into the ViewModel's lists its Remove commands read.
/// </summary>
internal sealed class SignatureAndEncryptionPage(SignatureAndEncryptionPageViewModel viewModel, WinoViewServices services,
    GtkDialogService dialogs, ISmimeCertificateService certificates)
    : SettingsPageView<SignatureAndEncryptionPageViewModel>(viewModel, services)
{
    private readonly List<CertificateSection> _sections = [];

#if DEBUG
    internal static SignatureAndEncryptionPage? DebugCurrent { get; private set; }

    internal CertificateSection DebugSection(SmimeCertificatePurpose purpose) => _sections.First(section => section.Purpose == purpose);

    internal Task DebugImportAsync(SmimeCertificatePurpose purpose) => ImportAsync(purpose);

    internal Task DebugExportAsync(SmimeCertificatePurpose purpose) => ExportAsync(DebugSection(purpose).Selected);
#endif

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Page.AddGroup(null, Translator.SettingsSignatureAndEncryption_Description);
        if (!vm.IsSmimeAvailable) return;

        AddSection(SmimeCertificatePurpose.Personal, Translator.SettingsSignatureAndEncryption_MyCertificatesHeader,
            Translator.SettingsSignatureAndEncryption_MyCertificatesDescription, vm.PersonalCertificates, vm.SelectedPersonalCertificates, vm.RemovePersonalCertificatesCommand);
        AddSection(SmimeCertificatePurpose.Recipient, Translator.SettingsSignatureAndEncryption_RecipientCertificatesHeader,
            Translator.SettingsSignatureAndEncryption_RecipientCertificatesDescription, vm.RecipientCertificates, vm.SelectedRecipientCertificates, vm.RemoveRecipientCertificatesCommand);
#if DEBUG
        DebugCurrent = this;
#endif
    }

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
#endif
        return base.DeactivateAsync();
    }

    private void AddSection(SmimeCertificatePurpose purpose, string header, string description, ObservableCollection<X509Certificate2> items,
        List<X509Certificate2> selection, CommunityToolkit.Mvvm.Input.IAsyncRelayCommand remove)
    {
        var section = new CertificateSection(purpose, items, selection, header);
        _sections.Add(section);

        var import = Settings.Button(Translator.Buttons_Import, () => Observe(ImportAsync(purpose)), suggested: purpose == SmimeCertificatePurpose.Personal, icon: WinoIconGlyph.ArrowDownload);
        var export = Settings.Button(Translator.Buttons_Export, () => Observe(ExportAsync(section.Selected)), icon: WinoIconGlyph.ArrowUpload);
        var delete = Settings.Button(Translator.Buttons_Remove, () => Observe(remove.ExecuteAsync(null)), icon: WinoIconGlyph.Delete);
        var details = Settings.Button(Translator.IMAPSetupDialog_CertificateView, () => Observe(ShowDetailsAsync(section.Selected.FirstOrDefault())), icon: WinoIconGlyph.Info);
        foreach (var button in (Gtk.Button[])[import, export, delete, details])
        {
            if (button.GetLabel() is { Length: > 0 } label) WinoA11y.Rename(button, label);
        }

        void UpdateButtons()
        {
            var count = section.Selected.Count;
            export.SetSensitive(count > 0);
            delete.SetSensitive(count > 0);
            details.SetSensitive(count == 1);
        }

        Settings.Own(GtkSignals.Connect<Gtk.ListBox>(h => section.List.OnSelectedRowsChanged += h, h => section.List.OnSelectedRowsChanged -= h,
            (_, _) =>
            {
                section.MirrorSelection();
                UpdateButtons();
            }));
        Settings.Own(GtkSignals.Connect<Gtk.ListBox, Gtk.ListBox.RowActivatedSignalArgs>(h => section.List.OnRowActivated += h, h => section.List.OnRowActivated -= h,
            (_, args) => Observe(ShowDetailsAsync(section.ItemOf(args.Row)))));

        var actions = Adw.WrapBox.New();
        actions.SetChildSpacing(6);
        actions.SetLineSpacing(6);
        actions.SetHexpand(true);
        actions.Append(import);
        actions.Append(export);
        actions.Append(delete);
        details.SetValign(Gtk.Align.Start);
        var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        buttons.Append(actions);
        buttons.Append(details);
        buttons.SetMarginTop(8);

        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        content.Append(section.List);
        content.Append(buttons);
        var group = Page.AddGroup(header, description);
        group.HidesWhenEmpty = false;
        group.Root.Add(content);

        Settings.Collection(items, () =>
        {
            section.Rebuild();
            UpdateButtons();
        });
    }

    private async Task ImportAsync(SmimeCertificatePurpose purpose)
    {
        var files = await dialogs.PickFilesAsync(".pfx", ".p12", ".cer", ".crt", ".pem");
        if (files.Count == 0) return;
        var failures = new List<string>();
        var imported = 0;
        foreach (var file in files)
        {
            var extension = file.FileExtension ?? string.Empty;
            string? password = null;
            if (extension is ".pfx" or ".p12")
            {
                password = await dialogs.ShowSmimePasswordDialogAsync(file.FileName);
                if (password is null) continue;
            }
            try
            {
                await Task.Run(() => certificates.ImportCertificate(extension, file.Data, password, purpose));
                imported++;
            }
            catch (Exception exception)
            {
                failures.Add($"{file.FileName}: {exception.Message}");
            }
        }
        if (imported > 0)
        {
            await Dispatcher.ExecuteOnUIThread(ReloadViewModel);
            dialogs.InfoBarMessage(Translator.Smime_ImportCertificates_Success, Translator.GeneralTitle_Info, InfoBarMessageType.Success);
        }
        if (failures.Count > 0)
            await dialogs.ShowMessageAsync(string.Format(Translator.Smime_ImportCertificates_Error, "\n\n" + string.Join("\n", failures)),
                Translator.GeneralTitle_Warning, WinoCustomMessageDialogIcon.Warning);
    }

    /// <summary>One DER .cer per certificate, written to the path the save dialog returned (never the private key).</summary>
    private async Task ExportAsync(IReadOnlyList<X509Certificate2> selected)
    {
        var failures = new List<string>();
        var exported = 0;
        foreach (var certificate in selected.ToList())
        {
            var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
            var path = await dialogs.PickFilePathAsync(SafeFileName(string.IsNullOrWhiteSpace(name) ? certificate.Thumbprint : name) + ".cer");
            if (string.IsNullOrEmpty(path)) continue;
            try
            {
                await File.WriteAllBytesAsync(path, certificate.RawData);
                exported++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{certificate.Subject}: {exception.Message}");
            }
        }
        if (exported > 0) dialogs.InfoBarMessage(Translator.Smime_ExportCertificates_Success, Translator.GeneralTitle_Info, InfoBarMessageType.Success);
        if (failures.Count > 0)
            await dialogs.ShowMessageAsync($"{Translator.Smime_ExportCertificates_Error}\n\n{string.Join("\n", failures)}",
                Translator.GeneralTitle_Warning, WinoCustomMessageDialogIcon.Warning);
    }

    private async Task ShowDetailsAsync(X509Certificate2? certificate)
    {
        if (certificate is null) return;
        var details = string.Format(CultureInfo.CurrentCulture, Translator.Smime_CertificateDetails, certificate.Subject, certificate.Issuer,
            certificate.NotBefore, certificate.NotAfter, certificate.Thumbprint);
        await dialogs.ShowMessageAsync(details, Translator.GeneralTitle_Info, WinoCustomMessageDialogIcon.Information);
    }

    // The ViewModel reloads both lists when it navigates to an empty state (macOS does the same after an import).
    private void ReloadViewModel()
    {
        ViewModel.OnNavigatedFrom(NavigationMode.Refresh, null);
        ViewModel.OnNavigatedTo(NavigationMode.Refresh, null);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. value.Select(character => invalid.Contains(character) || character == ':' ? '_' : character)]).Trim();
        return cleaned.Length == 0 ? "certificate" : cleaned;
    }

    /// <summary>The list of one store; rows are rebuilt from the ViewModel's collection, and the selection is written back to it.</summary>
    internal sealed class CertificateSection
    {
        private readonly ObservableCollection<X509Certificate2> _items;
        private readonly List<X509Certificate2> _selection;
        private readonly List<(Adw.ActionRow Row, X509Certificate2 Certificate)> _rows = [];

        public CertificateSection(SmimeCertificatePurpose purpose, ObservableCollection<X509Certificate2> items, List<X509Certificate2> selection, string name)
        {
            Purpose = purpose;
            _items = items;
            _selection = selection;
            List.SetSelectionMode(Gtk.SelectionMode.Multiple);
            List.AddCssClass("boxed-list");
            WinoA11y.Label(List, name);
        }

        public SmimeCertificatePurpose Purpose { get; }

        public Gtk.ListBox List { get; } = Gtk.ListBox.New();

        public IReadOnlyList<X509Certificate2> Selected => _selection;

        public void Rebuild()
        {
            List.RemoveAll();
            _rows.Clear();
            _selection.Clear();
            if (_items.Count == 0)
            {
                var empty = Adw.ActionRow.New();
                empty.SetUseMarkup(false);
                empty.SetTitle(Translator.ToDoPage_ListEmptyTitle);
                empty.SetSelectable(false);
                empty.SetActivatable(false);
                empty.AddCssClass("dim-label");
                List.Append(empty);
                return;
            }
            foreach (var certificate in _items)
            {
                var row = Adw.ActionRow.New();
                row.SetUseMarkup(false);
                var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
                row.SetTitle(string.IsNullOrWhiteSpace(name) ? certificate.Subject : name);
                var email = certificate.GetNameInfo(X509NameType.EmailName, false);
                var expires = $"{Translator.SettingsSignatureAndEncryption_ExpiresColumn}: {certificate.NotAfter.ToString("d", CultureInfo.CurrentCulture)}";
                row.SetSubtitle(string.Join(" · ", new[] { email, expires, certificate.Thumbprint }.Where(static part => !string.IsNullOrWhiteSpace(part))));
                row.SetSubtitleLines(0);
                row.SetActivatable(true);
                List.Append(row);
                _rows.Add((row, certificate));
            }
        }

        public void MirrorSelection()
        {
            _selection.Clear();
            foreach (var (row, certificate) in _rows)
            {
                if (row.IsSelected()) _selection.Add(certificate);
            }
        }

        public X509Certificate2? ItemOf(Gtk.ListBoxRow row) => _rows.FirstOrDefault(entry => ReferenceEquals(entry.Row, row)).Certificate;

#if DEBUG
        public void Select(int[] positions)
        {
            List.UnselectAll();
            foreach (var position in positions)
            {
                if (position >= 0 && position < _rows.Count) List.SelectRow(_rows[position].Row);
            }
        }

        public string Describe() => $"{Purpose}: rows={_rows.Count} selected={_selection.Count} | "
            + string.Join(" | ", _rows.Select(static entry => $"{entry.Row.GetTitle()} / {entry.Row.GetSubtitle()}"));
#endif
    }
}
