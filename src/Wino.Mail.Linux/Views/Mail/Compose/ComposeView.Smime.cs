using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// S/MIME in the toolbar's Options page (Windows OptionsCustomContent, macOS <c>ComposePageViewController.Options</c>):
/// Sign, the signing certificate and Encrypt. Hidden without platform S/MIME, insensitive while the sender has no
/// certificate. In the row's More menu the group is one submenu.
/// </summary>
internal sealed partial class ComposeView
{
    private const int CertificateNameLength = 24;
    private readonly List<X509Certificate2> _certificateItems = [];
    private Gtk.ToggleButton? _smimeSign;
    private Gtk.ToggleButton? _smimeEncrypt;
    private Gtk.DropDown? _certificatePicker;
    private string[] _certificateTitles = [];
    private bool _updatingCertificates;

    partial void InitializeSecurity()
    {
        if (FormatToolbar is not { } toolbar || !ViewModel.IsSmimeAvailable) return;
        var sign = _smimeSign = WinoIcons.ToggleButton(WinoIconGlyph.Certificate, Translator.Composer_SmimeSignature);
        var encrypt = _smimeEncrypt = WinoIcons.ToggleButton(WinoIconGlyph.LockClosed, Translator.Composer_SmimeEncryption);
        sign.AddCssClass("wino-editor-tool");
        encrypt.AddCssClass("wino-editor-tool");
        var picker = _certificatePicker = Gtk.DropDown.New(null, null);
        picker.AddCssClass("flat");
        picker.SetValign(Gtk.Align.Center);

        var group = Gtk.Box.New(Gtk.Orientation.Horizontal, 2);
        group.Append(sign);
        group.Append(picker);
        group.Append(encrypt);
        group.SetTooltipText(Translator.Composer_SmimeSignature);
        WinoA11y.Label(group, Translator.Composer_SmimeSignature);

        Bind.BindActive(sign, nameof(ComposePageViewModel.IsSmimeSignatureEnabled), static vm => vm.IsSmimeSignatureEnabled, static (vm, on) => vm.IsSmimeSignatureEnabled = on);
        Bind.BindActive(encrypt, nameof(ComposePageViewModel.IsSmimeEncryptionEnabled), static vm => vm.IsSmimeEncryptionEnabled, static (vm, on) => vm.IsSmimeEncryptionEnabled = on);
        Bind.Bind(nameof(ComposePageViewModel.SelectedSigningCertificate), static vm => vm.SelectedSigningCertificate, _ => UpdateCertificates());
        Bind.Bind(nameof(ComposePageViewModel.AreCertificatesAvailable), static vm => vm.AreCertificatesAvailable, _ => UpdateCertificates());
        Bind.Bind(nameof(ComposePageViewModel.SelectedAlias), static vm => vm.SelectedAlias, _ => UpdateCertificates());
        Bind.BindCollection(ViewModel.AvailableCertificates, UpdateCertificates);
        Bindings.Own(GtkSignals.OnNotify(picker, "selected", () =>
        {
            if (_updatingCertificates) return;
            var index = (int)picker.GetSelected();
            if (index >= 0 && index < _certificateItems.Count) ViewModel.SelectedSigningCertificate = _certificateItems[index];
            RenameCertificatePicker();
        }));

        toolbar.AddOptionsItems(EditorToolbarItem.Divider(), new EditorToolbarItem(group, SmimeMenu, () => ViewModel.IsSmimeAvailable));
    }

    partial void DeactivateSecurity()
    {
        _certificateItems.Clear();
        _certificateTitles = [];
        _smimeSign = null;
        _smimeEncrypt = null;
        _certificatePicker = null;
    }

    private bool IsSmimeUsable => ViewModel.AreCertificatesAvailable && _certificateItems.Count > 0;

    private void SmimeMenu(WinoMenuBuilder menu)
    {
        var usable = IsSmimeUsable;
        menu.Submenu(Translator.Composer_SmimeSignature, submenu =>
        {
            submenu.Check(Translator.Composer_EnableSmimeSignature, ViewModel.IsSmimeSignatureEnabled, on => ViewModel.IsSmimeSignatureEnabled = on, usable,
                icon: WinoIconGlyph.Certificate);
            if (_certificateItems.Count > 0)
            {
                submenu.Separator(Translator.Composer_SigningCertificate);
                var selected = ViewModel.SelectedSigningCertificate?.Thumbprint;
                for (var index = 0; index < _certificateItems.Count; index++)
                {
                    var certificate = _certificateItems[index];
                    submenu.Radio("smime-certificate", _certificateTitles[index], certificate.Thumbprint == selected,
                        () => ViewModel.SelectedSigningCertificate = certificate, enabled: usable);
                }
                submenu.Separator();
            }
            submenu.Check(Translator.Composer_EnableSmimeEncryption, ViewModel.IsSmimeEncryptionEnabled, on => ViewModel.IsSmimeEncryptionEnabled = on, usable,
                icon: WinoIconGlyph.LockClosed);
        }, icon: WinoIconGlyph.Certificate);
    }

    private void UpdateCertificates()
    {
        if (IsReleased || _certificatePicker is not { } picker || _smimeSign is not { } sign || _smimeEncrypt is not { } encrypt) return;
        _certificateItems.Clear();
        var titles = new List<string>();
        foreach (var certificate in ViewModel.AvailableCertificates.ToArray())
        {
            if (certificate is null) continue;
            string title;
            try { title = CertificateTitle(certificate); }
            catch (CryptographicException) { continue; }
            _certificateItems.Add(certificate);
            titles.Add(title);
        }
        _certificateTitles = titles.ToArray();
        var selected = ViewModel.SelectedSigningCertificate is { } current ? _certificateItems.FindIndex(item => item.Thumbprint == current.Thumbprint) : -1;
        _updatingCertificates = true;
        try
        {
            picker.SetModel(Gtk.StringList.New(_certificateTitles));
            picker.SetSelected(selected >= 0 ? (uint)selected : Gtk.Constants.INVALID_LIST_POSITION);
        }
        finally { _updatingCertificates = false; }
        RenameCertificatePicker();

        var usable = IsSmimeUsable;
        sign.SetSensitive(usable);
        picker.SetSensitive(usable);
        encrypt.SetSensitive(usable);
        picker.SetVisible(_certificateItems.Count > 0);
        var address = ViewModel.SelectedAlias?.AliasAddress ?? ViewModel.ComposingAccount?.Address ?? string.Empty;
        var unavailable = string.Format(Translator.Composer_SmimeNoCertificateTooltip, address);
        sign.SetTooltipText(usable ? Translator.Composer_EnableSmimeSignature : unavailable);
        encrypt.SetTooltipText(usable ? Translator.Composer_EnableSmimeEncryption : unavailable);
        WinoA11y.Description(sign, usable ? Translator.Composer_EnableSmimeSignature : unavailable);
        WinoA11y.Description(encrypt, usable ? Translator.Composer_EnableSmimeEncryption : unavailable);
        FormatToolbar?.InvalidateOverflow();
    }

    // GTK names a drop-down after its selected value; keep the field's name and say the value in the description.
    private void RenameCertificatePicker()
    {
        if (_certificatePicker is not { } picker) return;
        WinoA11y.Rename(picker, Translator.Composer_SigningCertificate);
        var index = (int)picker.GetSelected();
        var title = index >= 0 && index < _certificateItems.Count
            ? $"{_certificateTitles[index]}, {Translator.Composer_CertificateExpires.Trim()} {_certificateItems[index].NotAfter.ToString("d", CultureInfo.CurrentCulture)}"
            : string.Empty;
        WinoA11y.Description(picker, title);
        picker.SetTooltipText(title.Length > 0 ? $"{Translator.Composer_SigningCertificate}: {title}" : Translator.Composer_SigningCertificate);
    }

    private static string CertificateTitle(X509Certificate2 certificate)
    {
        var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
        if (string.IsNullOrWhiteSpace(name)) name = certificate.Subject;
        if (name.Length > CertificateNameLength) name = name[..(CertificateNameLength - 1)] + "…";
        return $"{name} ({certificate.NotAfter.ToString("d", CultureInfo.CurrentCulture)})";
    }

#if DEBUG
    internal string DebugSmime()
        => $"available={ViewModel.IsSmimeAvailable} usable={IsSmimeUsable} sign={ViewModel.IsSmimeSignatureEnabled} encrypt={ViewModel.IsSmimeEncryptionEnabled} " +
           $"selected={ViewModel.SelectedSigningCertificate?.Thumbprint ?? "-"} certificates=[{string.Join("; ", _certificateTitles)}] " +
           $"widgets={(_smimeSign is null ? "none" : $"sign:{_smimeSign.GetActive()}/{_smimeSign.GetSensitive()} encrypt:{_smimeEncrypt?.GetActive()} picker:{_certificatePicker?.GetSelected()}/{_certificatePicker?.GetVisible()}")} " +
           $"alias={ViewModel.SelectedAlias?.AliasAddress} aliasCert={ViewModel.SelectedAlias?.SelectedSigningCertificateThumbprint ?? "-"} canSend={ViewModel.SendCommand.CanExecute(null)}";

    internal void DebugSelectCertificate(int index)
    {
        if (_certificatePicker is { } picker && index >= 0 && index < _certificateItems.Count) picker.SetSelected((uint)index);
    }

    internal void DebugToggle(bool encrypt, bool on)
    {
        if ((encrypt ? _smimeEncrypt : _smimeSign) is { } button) button.SetActive(on);
    }
#endif
}
