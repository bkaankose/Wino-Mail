using System.Collections.Specialized;
using System.Security.Cryptography.X509Certificates;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Mail.Compose;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// Options extras (Mac Options partial, Windows OptionsCustomContent): the S/MIME menu (sign, the signing certificate,
/// encrypt) and the read-receipt toggle. The S/MIME command is hidden where the platform has no S/MIME, and its items
/// are disabled without a certificate, with the reason as the menu subtitle.
/// </summary>
public sealed partial class ComposePageViewController
{
    private EditorToolbarButton? _smimeButton;
    private EditorToolbarButton? _readReceiptButton;

    private EditorToolbarItem[] BuildSecurityOptions()
    {
        _smimeButton = new EditorToolbarButton(WinoIconGlyph.Certificate, Translator.Composer_SmimeSignature, null, () => [SmimeMenu()]);
        _readReceiptButton = new EditorToolbarButton(WinoIconGlyph.MailCheckmark, Translator.Composer_RequestReadReceipt, () =>
        {
            ViewModel.IsReadReceiptRequested = !ViewModel.IsReadReceiptRequested;
            ScheduleAutosave();
        });
        return
        [
            EditorToolbarItem.Divider(),
            new EditorToolbarItem(_smimeButton, () => ViewModel.IsSmimeAvailable),
            new EditorToolbarItem(_readReceiptButton)
        ];
    }

    private void BindSecurityOptions()
    {
        Bind(nameof(ViewModel.IsSmimeSignatureEnabled), vm => vm.IsSmimeSignatureEnabled, _ => UpdateSecurityButtons());
        Bind(nameof(ViewModel.IsSmimeEncryptionEnabled), vm => vm.IsSmimeEncryptionEnabled, _ => UpdateSecurityButtons());
        Bind(nameof(ViewModel.IsReadReceiptRequested), vm => vm.IsReadReceiptRequested, on => { if (_readReceiptButton is not null) _readReceiptButton.IsActive = on; });
        Bind(nameof(ViewModel.AreCertificatesAvailable), vm => vm.AreCertificatesAvailable, _ => UpdateSecurityButtons());
        NotifyCollectionChangedEventHandler certificates = (_, _) => _ = Dispatcher.ExecuteOnUIThread(UpdateSecurityButtons);
        ViewModel.AvailableCertificates.CollectionChanged += certificates;
        Bindings.Own(new ActionDisposable(() => ViewModel.AvailableCertificates.CollectionChanged -= certificates));
    }

    private void UpdateSecurityButtons()
    {
        if (_smimeButton is not null) _smimeButton.IsActive = ViewModel.IsSmimeSignatureEnabled || ViewModel.IsSmimeEncryptionEnabled;
        _formatToolbar?.InvalidateItems();
    }

    /// <summary>The certificates that can be read, with their display names (Mac UpdateCertificates).</summary>
    private List<(X509Certificate2 Certificate, string Title)> SigningCertificates()
    {
        var result = new List<(X509Certificate2, string)>();
        foreach (var certificate in ViewModel.AvailableCertificates.ToArray())
        {
            if (certificate is null) continue;
            try
            {
                var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
                var expires = certificate.NotAfter.ToString("d", System.Globalization.CultureInfo.CurrentCulture);
                result.Add((certificate, $"{name} · {Translator.Composer_CertificateExpires.Trim()} {expires}"));
            }
            catch (System.Security.Cryptography.CryptographicException) { }
        }
        return result;
    }

    /// <summary>Sign, the signing certificate and encrypt (Mac SmimeMenuItem).</summary>
    private UIMenu SmimeMenu()
    {
        var certificates = SigningCertificates();
        var available = ViewModel.AreCertificatesAvailable && certificates.Count > 0;
        var items = new List<UIMenuElement>
        {
            EditorFormatToolbar.Action(Translator.Composer_EnableSmimeSignature, () => { ViewModel.IsSmimeSignatureEnabled = !ViewModel.IsSmimeSignatureEnabled; ScheduleAutosave(); },
                ViewModel.IsSmimeSignatureEnabled, available, WinoIcons.Image(WinoIconGlyph.Certificate, 16))
        };
        if (certificates.Count > 0)
        {
            var current = ViewModel.SelectedSigningCertificate?.Thumbprint;
            items.Add(EditorFormatToolbar.Submenu(Translator.Composer_SigningCertificate, certificates.Select(item =>
                (UIMenuElement)EditorFormatToolbar.Action(item.Title, () => ViewModel.SelectedSigningCertificate = item.Certificate, item.Certificate.Thumbprint == current, available))));
        }
        items.Add(EditorFormatToolbar.Action(Translator.Composer_EnableSmimeEncryption, () => { ViewModel.IsSmimeEncryptionEnabled = !ViewModel.IsSmimeEncryptionEnabled; ScheduleAutosave(); },
            ViewModel.IsSmimeEncryptionEnabled, available, WinoIcons.Image(WinoIconGlyph.LockClosed, 16)));
        var menu = EditorFormatToolbar.Inline(items, Translator.Composer_SmimeSignature);
        if (!available)
        {
            var address = ViewModel.SelectedAlias?.AliasAddress ?? ViewModel.ComposingAccount?.Address ?? string.Empty;
            // A disabled item still needs its reason (the Mac tooltip).
            menu = UIMenu.Create(string.Format(Translator.Composer_SmimeNoCertificateTooltip, address), null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, items.ToArray());
        }
        return menu;
    }

    /// <summary>The Options group of the More menu: S/MIME (where available) and the read receipt.</summary>
    private IEnumerable<UIMenuElement> SecurityMenuItems()
    {
        if (ViewModel.IsSmimeAvailable) yield return SmimeMenu();
        yield return EditorFormatToolbar.Action(Translator.Composer_RequestReadReceipt, () =>
        {
            ViewModel.IsReadReceiptRequested = !ViewModel.IsReadReceiptRequested;
            ScheduleAutosave();
        }, ViewModel.IsReadReceiptRequested, image: WinoIcons.Image(WinoIconGlyph.MailCheckmark, 16));
    }
}
