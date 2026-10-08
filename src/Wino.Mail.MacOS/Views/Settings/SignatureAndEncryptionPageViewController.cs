using System.Collections.ObjectModel;
using System.Security.Cryptography.X509Certificates;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Signature and Encryption (Windows SignatureAndEncryptionPage): "My certificates" and "Recipient
/// certificates", each a multi-select table (name, expiry, thumbprint) with Remove, Export, Import and
/// View Certificate. Remove runs the shared ViewModel command. Import and Export stay on this page so
/// the PKCS#12 password goes through a secure field and the export lands exactly where the save panel
/// granted access (the app is sandboxed). Certificates live in Wino's own encrypted store.
/// </summary>
public sealed class SignatureAndEncryptionPageViewController(SignatureAndEncryptionPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
    IMailDialogService dialogs, ISmimeCertificateService certificates)
    : SettingsPageViewController<SignatureAndEncryptionPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly List<CertificateSection> _sections = [];

    protected override void BuildPage()
    {
        var vm = ViewModel;
        AddIntro(Translator.SettingsSignatureAndEncryption_Description);

        // The catalog hides this page without the S/MIME capability; a direct navigation shows only the intro.
        if (!vm.IsSmimeAvailable) return;

        AddSection(Translator.SettingsSignatureAndEncryption_MyCertificatesHeader, Translator.SettingsSignatureAndEncryption_MyCertificatesDescription,
            SmimeCertificatePurpose.Personal, vm.PersonalCertificates, vm.SelectedPersonalCertificates, vm.RemovePersonalCertificatesCommand);
        AddSection(Translator.SettingsSignatureAndEncryption_RecipientCertificatesHeader, Translator.SettingsSignatureAndEncryption_RecipientCertificatesDescription,
            SmimeCertificatePurpose.Recipient, vm.RecipientCertificates, vm.SelectedRecipientCertificates, vm.RemoveRecipientCertificatesCommand);
    }

    private void AddSection(string header, string description, SmimeCertificatePurpose purpose, ObservableCollection<X509Certificate2> items,
        List<X509Certificate2> selection, CommunityToolkit.Mvvm.Input.IAsyncRelayCommand remove)
    {
        var section = new CertificateSection(items, selection);
        _sections.Add(section);
        Bindings.Own(new ActionDisposable(section.Dispose));

        var removeButton = Bind.Button(Translator.Buttons_Remove, () => Run(remove.ExecuteAsync(null)), icon: WinoIconGlyph.Delete);
        var exportButton = Bind.Button(Translator.Buttons_Export, () => Run(ExportAsync(section.Selected)), icon: WinoIconGlyph.ArrowUpload);
        var importButton = Bind.Button(Translator.Buttons_Import, () => Run(ImportAsync(purpose)), primary: purpose == SmimeCertificatePurpose.Personal, icon: WinoIconGlyph.ArrowDownload);
        var detailsButton = Bind.Button(Translator.IMAPSetupDialog_CertificateView, () => Run(ShowDetailsAsync(section.Selected.FirstOrDefault())), icon: WinoIconGlyph.Info);

        void UpdateButtons()
        {
            var count = section.Selected.Count;
            removeButton.Enabled = count > 0;
            exportButton.Enabled = count > 0;
            detailsButton.Enabled = count == 1;
        }

        section.SelectionChanged = UpdateButtons;
        section.Activated = certificate => Run(ShowDetailsAsync(certificate));
        WinoAccessibility.Label(section.Table, header);
        UpdateButtons();

        var buttons = Row(importButton, exportButton, removeButton, WinoLayout.Spacer(), detailsButton);
        var expander = new WinoSettingsExpander(header, description, WinoIconGlyph.Certificate, null, isExpanded: true);
        expander.Add(section.View, 10, WinoSettingsStyle.CardPadding, 6, WinoSettingsStyle.CardPadding);
        expander.Add(buttons, 8, WinoSettingsStyle.CardPadding, 10, WinoSettingsStyle.CardPadding);
        Add(expander);

        Bind.Collection(items, section.QueueReload);
    }

    #region Import, export, details

    private async Task ImportAsync(SmimeCertificatePurpose purpose)
    {
        var files = await dialogs.PickFilesAsync(".pfx", ".p12", ".cer", ".crt", ".pem", ".p7b");
        if (files is null || files.Count == 0) return;

        var failures = new List<string>();
        var imported = 0;
        foreach (var file in files)
        {
            var extension = file.FileExtension ?? string.Empty;
            string? password = null;
            if (extension is ".pfx" or ".p12")
            {
                password = await PromptPasswordAsync(file.FileName);
                if (password is null) continue; // Cancel skips this file only.
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
            ReloadViewModel();
            dialogs.InfoBarMessage(Translator.Smime_ImportCertificates_Success, Translator.GeneralTitle_Info, InfoBarMessageType.Success);
        }
        if (failures.Count > 0)
            await dialogs.ShowMessageAsync(string.Format(Translator.Smime_ImportCertificates_Error, "\n\n" + string.Join("\n", failures)),
                Translator.GeneralTitle_Warning, WinoCustomMessageDialogIcon.Warning);
    }

    /// <summary>One .cer per certificate, written to the exact path the save panel returned.</summary>
    private async Task ExportAsync(IReadOnlyList<X509Certificate2> selected)
    {
        var failures = new List<string>();
        var exported = 0;
        foreach (var certificate in selected.ToList())
        {
            var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
            var suggested = SafeFileName(string.IsNullOrWhiteSpace(name) ? certificate.Thumbprint : name) + ".cer";
            var path = await dialogs.PickFilePathAsync(suggested);
            if (string.IsNullOrEmpty(path)) continue;
            try
            {
                await File.WriteAllBytesAsync(path, certificate.RawData);
                exported++;
            }
            catch (Exception exception)
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
        var details = string.Format(Translator.Smime_CertificateDetails, certificate.Subject, certificate.Issuer, certificate.NotBefore, certificate.NotAfter, certificate.Thumbprint);
        await dialogs.ShowMessageAsync(details, Translator.GeneralTitle_Info, WinoCustomMessageDialogIcon.Information);
    }

    /// <summary>NSAlert with a secure field; null when cancelled. The password is never logged or stored.</summary>
    private Task<string?> PromptPasswordAsync(string fileName)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = View.Window;
        var field = new NSSecureTextField(new CGRect(0, 0, 300, 24));
        WinoAccessibility.Label(field, Translator.Smime_CertificatePassword_Title);
        var alert = new NSAlert
        {
            MessageText = Translator.Smime_CertificatePassword_Title,
            InformativeText = string.Format(Translator.Smime_CertificatePassword_Placeholder, fileName),
            AccessoryView = field,
        };
        alert.AddButton(Translator.Buttons_OK);
        alert.AddButton(Translator.Buttons_Cancel);
        alert.Window.InitialFirstResponder = field;

        void Complete(nint response)
        {
            completion.TrySetResult(response == (nint)(long)NSAlertButtonReturn.First ? field.StringValue ?? string.Empty : null);
            field.StringValue = string.Empty;
            alert.Dispose();
        }

        if (window is null) Complete(alert.RunModal());
        else alert.BeginSheet(window, response => Complete((nint)(long)response));
        return completion.Task;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) || character == ':' ? '_' : character).ToArray()).Trim();
        return cleaned.Length == 0 ? "certificate" : cleaned;
    }

    /// <summary>The ViewModel reloads both lists when it navigates to an empty state; drive that after an import.</summary>
    private void ReloadViewModel()
    {
        ViewModel.OnNavigatedFrom(NavigationMode.Refresh, null!);
        ViewModel.OnNavigatedTo(NavigationMode.Refresh, null!);
    }

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (Exception exception) { ReportError(exception); }
    }

    #endregion

    /// <summary>The scrolling certificate table of one section, mirroring its selection into the ViewModel list.</summary>
    private sealed class CertificateSection : NSTableViewDataSource, INSTableViewDelegate
    {
        private readonly ObservableCollection<X509Certificate2> _items;
        private readonly List<X509Certificate2> _selection;
        private readonly NSTextField _empty;
        private bool _reloadQueued;
        private bool _disposed;

        public CertificateSection(ObservableCollection<X509Certificate2> items, List<X509Certificate2> selection)
        {
            _items = items;
            _selection = selection;
            Table = new NSTableView
            {
                AllowsMultipleSelection = true,
                UsesAlternatingRowBackgroundColors = true,
                RowHeight = 24,
                Style = NSTableViewStyle.Inset,
                ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly,
            };
            Table.AddColumn(new NSTableColumn("name") { Title = Translator.SettingsSignatureAndEncryption_NameColumn, Width = 260, MinWidth = 120 });
            Table.AddColumn(new NSTableColumn("expires") { Title = Translator.SettingsSignatureAndEncryption_ExpiresColumn, Width = 110, MinWidth = 80 });
            Table.AddColumn(new NSTableColumn("thumbprint") { Title = Translator.SettingsSignatureAndEncryption_ThumbprintColumn, Width = 300, MinWidth = 120 });
            Table.DataSource = this;
            Table.Delegate = this;
            Table.DoubleClick += (_, _) =>
            {
                var row = (int)Table.ClickedRow;
                if (row >= 0 && row < _items.Count) Activated?.Invoke(_items[row]);
            };

            var scroll = new NSScrollView
            {
                DocumentView = Table,
                HasVerticalScroller = true,
                AutohidesScrollers = true,
                BorderType = NSBorderType.BezelBorder,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            WinoLayout.Size(scroll, -1, 160);

            // Windows left its empty-state text unfinished ("There are no items."); show a plain note in the table area.
            _empty = WinoStyle.Label(Translator.ToDoPage_ListEmptyTitle, WinoStyle.Description, WinoStyle.TertiaryText);
            _empty.Alignment = NSTextAlignment.Center;
            _empty.TranslatesAutoresizingMaskIntoConstraints = false;

            View = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
            WinoLayout.Fill(scroll, View);
            View.AddSubview(_empty);
            _empty.CenterXAnchor.ConstraintEqualTo(View.CenterXAnchor).Active = true;
            _empty.CenterYAnchor.ConstraintEqualTo(View.CenterYAnchor, 10).Active = true;
            UpdateEmpty();
        }

        public NSTableView Table { get; }
        public NSView View { get; }
        public Action? SelectionChanged { get; set; }
        public Action<X509Certificate2>? Activated { get; set; }
        public IReadOnlyList<X509Certificate2> Selected => _selection;

        /// <summary>The ViewModel clears and refills the collection; coalesce into one reload.</summary>
        public void QueueReload()
        {
            if (_reloadQueued || _disposed) return;
            _reloadQueued = true;
            BeginInvokeOnMainThread(() =>
            {
                _reloadQueued = false;
                if (_disposed) return;
                Table.ReloadData();
                Table.DeselectAll(null);
                SyncSelection();
                UpdateEmpty();
            });
        }

        private void UpdateEmpty() => _empty.Hidden = _items.Count > 0;

        private void SyncSelection()
        {
            _selection.Clear();
            foreach (var index in Table.SelectedRows.ToArray())
                if ((int)index < _items.Count) _selection.Add(_items[(int)index]);
            SelectionChanged?.Invoke();
        }

        public override nint GetRowCount(NSTableView tableView) => _items.Count;

        [Export("tableView:viewForTableColumn:row:")]
        public NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            var identifier = tableColumn?.Identifier ?? "name";
            var cell = tableView.MakeView(identifier, this) as NSTableCellView;
            if (cell is null)
            {
                cell = new NSTableCellView { Identifier = identifier };
                var label = WinoStyle.Label(string.Empty, identifier == "name" ? WinoStyle.Body : WinoStyle.Caption, identifier == "name" ? WinoStyle.PrimaryText : WinoStyle.SecondaryText);
                label.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
                cell.TextField = label;
                WinoLayout.Fill(label, cell, 2, 4, 2, 4);
            }

            var certificate = (int)row < _items.Count ? _items[(int)row] : null;
            cell.TextField!.StringValue = certificate is null ? string.Empty : identifier switch
            {
                "expires" => certificate.NotAfter.ToString("d"),
                "thumbprint" => certificate.Thumbprint,
                _ => DisplayName(certificate),
            };
            if (identifier == "expires" && certificate is not null)
                cell.TextField.TextColor = certificate.NotAfter < DateTime.Now ? WinoStyle.Critical : WinoStyle.SecondaryText;
            return cell;
        }

        [Export("tableViewSelectionDidChange:")]
        public void SelectionDidChange(NSNotification notification) => SyncSelection();

        private static string DisplayName(X509Certificate2 certificate)
        {
            var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
            var email = certificate.GetNameInfo(X509NameType.EmailName, false);
            if (string.IsNullOrWhiteSpace(name)) return certificate.Subject;
            return string.IsNullOrWhiteSpace(email) || email == name ? name : $"{name} <{email}>";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                Table.DataSource = null;
                Table.Delegate = null;
                SelectionChanged = null;
                Activated = null;
            }
            base.Dispose(disposing);
        }
    }

#if DEBUG
    /// <summary>
    /// Debug bridge commands for the store: <c>smime-list personal|recipient</c>, <c>smime-import &lt;path&gt; [password]</c>
    /// (use a self-signed test certificate made with openssl; never a real one) and <c>smime-remove &lt;thumbprint&gt;</c>.
    /// The password argument is passed through only and never echoed.
    /// </summary>
    internal static class SmimeDebug
    {
        private static IServiceProvider Services => typeof(MacDebugBridge).GetField("_services", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null) as IServiceProvider
            ?? throw new InvalidOperationException("no service provider");

        private static ISmimeCertificateService Store => (ISmimeCertificateService)(Services.GetService(typeof(ISmimeCertificateService)) ?? throw new InvalidOperationException("no S/MIME service"));

        public static void Register()
        {
            MacDebugBridge.Register("smime-list", args =>
            {
                var purposes = args.Length > 0
                    ? new[] { args[0].Equals("recipient", StringComparison.OrdinalIgnoreCase) ? SmimeCertificatePurpose.Recipient : SmimeCertificatePurpose.Personal }
                    : new[] { SmimeCertificatePurpose.Personal, SmimeCertificatePurpose.Recipient };
                var lines = new List<string>();
                foreach (var purpose in purposes)
                {
                    foreach (var certificate in Store.GetCertificates(purpose))
                    {
                        using (certificate) lines.Add($"{purpose} {certificate.Thumbprint} {certificate.NotAfter:yyyy-MM-dd} {certificate.Subject}");
                    }
                }
                return Task.FromResult(lines.Count == 0 ? "empty" : string.Join("\n", lines));
            });

            MacDebugBridge.Register("smime-import", async args =>
            {
                if (args.Length == 0) return "usage: smime-import <path> [password] [personal|recipient]";
                var path = args[0];
                var password = args.Length > 1 ? args[1] : null;
                var purpose = args.Length > 2 && args[2].Equals("recipient", StringComparison.OrdinalIgnoreCase) ? SmimeCertificatePurpose.Recipient : SmimeCertificatePurpose.Personal;
                var data = await File.ReadAllBytesAsync(path);
                Store.ImportCertificate(Path.GetExtension(path).ToLowerInvariant(), data, password, purpose);
                return $"imported {Path.GetFileName(path)} as {purpose}";
            });

            MacDebugBridge.Register("smime-remove", args =>
            {
                if (args.Length == 0) return Task.FromResult("usage: smime-remove <thumbprint> [personal|recipient]");
                var purposes = args.Length > 1
                    ? new[] { args[1].Equals("recipient", StringComparison.OrdinalIgnoreCase) ? SmimeCertificatePurpose.Recipient : SmimeCertificatePurpose.Personal }
                    : new[] { SmimeCertificatePurpose.Personal, SmimeCertificatePurpose.Recipient };
                foreach (var purpose in purposes) Store.RemoveCertificate(args[0], purpose);
                return Task.FromResult("ok");
            });
        }
    }
#endif
}
