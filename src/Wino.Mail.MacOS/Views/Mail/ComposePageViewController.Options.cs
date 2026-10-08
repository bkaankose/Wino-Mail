using System.Collections.Specialized;
using System.Security.Cryptography.X509Certificates;
using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Options segment extras (design board ComposeExtrasToolbar, Windows OptionsCustomContent): S/MIME
/// signature and encryption toggles with the signing-certificate pop-up, and the read-receipt toggle.
/// The S/MIME group is hidden where the platform has no S/MIME and disabled without a certificate.
/// </summary>
public sealed partial class ComposePageViewController
{
    private NSStackView _smimeGroup = null!;
    private NSButton _smimeSignButton = null!;
    private NSPopUpButton _certificatePopup = null!;
    private NSButton _smimeEncryptButton = null!;
    private NSButton _readReceiptButton = null!;
    private readonly List<X509Certificate2> _certificateItems = new();

    private NSView[] BuildSecurityOptions()
    {
        _smimeSignButton = ToggleButton(WinoIconGlyph.Certificate, Translator.Composer_SmimeSignature, Translator.Composer_EnableSmimeSignature,
            on => ViewModel.IsSmimeSignatureEnabled = on);
        _certificatePopup = new NSPopUpButton { PullsDown = false, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false, ToolTip = Translator.Composer_SigningCertificate };
        _certificatePopup.WidthAnchor.ConstraintLessThanOrEqualTo(220).Active = true;
        _certificatePopup.Activated += (_, _) =>
        {
            var index = (int)_certificatePopup.IndexOfSelectedItem;
            if (index >= 0 && index < _certificateItems.Count) ViewModel.SelectedSigningCertificate = _certificateItems[index];
        };
        WinoAccessibility.Label(_certificatePopup, Translator.Composer_SigningCertificate);
        _smimeEncryptButton = ToggleButton(WinoIconGlyph.LockClosed, Translator.Composer_SmimeEncryption, Translator.Composer_EnableSmimeEncryption,
            on => ViewModel.IsSmimeEncryptionEnabled = on);
        _smimeGroup = WinoLayout.HStack(4, _smimeSignButton, _certificatePopup, _smimeEncryptButton);
        _smimeGroup.Hidden = !ViewModel.IsSmimeAvailable;

        _readReceiptButton = ToggleButton(WinoIconGlyph.MailCheckmark, Translator.Composer_ReadReceipt, Translator.Composer_RequestReadReceipt,
            on => ViewModel.IsReadReceiptRequested = on);
        return [Divider(), _smimeGroup, _readReceiptButton];
    }

    private static NSButton ToggleButton(WinoIconGlyph glyph, string title, string tooltip, Action<bool> changed)
    {
        var button = new NSButton
        {
            Title = title,
            BezelStyle = NSBezelStyle.Push,
            ControlSize = NSControlSize.Small,
            Image = WinoIcons.Image(glyph, 13, null, title),
            ImagePosition = NSCellImagePosition.ImageLeading,
            ToolTip = tooltip,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        button.SetButtonType(NSButtonType.PushOnPushOff);
        button.Activated += (_, _) => changed(button.State == NSCellStateValue.On);
        WinoAccessibility.Label(button, title);
        WinoAccessibility.Help(button, tooltip);
        return button;
    }

    private void BindSecurityOptions()
    {
        Bind(nameof(ViewModel.IsSmimeSignatureEnabled), vm => vm.IsSmimeSignatureEnabled, on => _smimeSignButton.State = on ? NSCellStateValue.On : NSCellStateValue.Off);
        Bind(nameof(ViewModel.IsSmimeEncryptionEnabled), vm => vm.IsSmimeEncryptionEnabled, on => _smimeEncryptButton.State = on ? NSCellStateValue.On : NSCellStateValue.Off);
        Bind(nameof(ViewModel.IsReadReceiptRequested), vm => vm.IsReadReceiptRequested, on => _readReceiptButton.State = on ? NSCellStateValue.On : NSCellStateValue.Off);
        Bind(nameof(ViewModel.SelectedSigningCertificate), vm => vm.SelectedSigningCertificate, _ => UpdateCertificates());
        Bind(nameof(ViewModel.AreCertificatesAvailable), vm => vm.AreCertificatesAvailable, _ => UpdateCertificates());
        Bind(nameof(ViewModel.SelectedAlias), vm => vm.SelectedAlias, _ => UpdateCertificates());
        NotifyCollectionChangedEventHandler certificates = (_, _) => _ = Dispatcher.ExecuteOnUIThread(UpdateCertificates);
        ViewModel.AvailableCertificates.CollectionChanged += certificates;
        Bindings.Own(new ActionDisposable(() => ViewModel.AvailableCertificates.CollectionChanged -= certificates));
    }

    private void UpdateCertificates()
    {
        if (_certificatePopup is null) return;
        _smimeGroup.Hidden = !ViewModel.IsSmimeAvailable;
        _certificateItems.Clear();
        _certificatePopup.RemoveAllItems();
        foreach (var certificate in ViewModel.AvailableCertificates.ToArray())
        {
            if (certificate is null) continue;
            string name;
            string expires;
            try
            {
                name = certificate.GetNameInfo(X509NameType.SimpleName, false);
                expires = certificate.NotAfter.ToString("d", System.Globalization.CultureInfo.CurrentCulture);
            }
            catch (System.Security.Cryptography.CryptographicException) { continue; }
            _certificateItems.Add(certificate);
            _certificatePopup.AddItem($"{name} · {Translator.Composer_CertificateExpires.Trim()} {expires}");
        }
        var selected = ViewModel.SelectedSigningCertificate is { } current ? _certificateItems.FindIndex(item => item.Thumbprint == current.Thumbprint) : -1;
        if (selected >= 0) _certificatePopup.SelectItem(selected);

        var available = ViewModel.AreCertificatesAvailable && _certificateItems.Count > 0;
        _smimeSignButton.Enabled = available;
        _certificatePopup.Enabled = available;
        _smimeEncryptButton.Enabled = available;
        _certificatePopup.Hidden = _certificateItems.Count == 0;
        var address = ViewModel.SelectedAlias?.AliasAddress ?? ViewModel.ComposingAccount?.Address ?? string.Empty;
        var unavailable = string.Format(Translator.Composer_SmimeNoCertificateTooltip, address);
        _smimeSignButton.ToolTip = available ? Translator.Composer_EnableSmimeSignature : unavailable;
        _smimeEncryptButton.ToolTip = available ? Translator.Composer_EnableSmimeEncryption : unavailable;
        // A disabled control is still read by VoiceOver; say why it cannot be used.
        WinoAccessibility.Help(_smimeSignButton, _smimeSignButton.ToolTip);
        WinoAccessibility.Help(_smimeEncryptButton, _smimeEncryptButton.ToolTip);
    }
}
