using System.Collections.Specialized;
using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Mail.Compose;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Insert template and signature choice (design board ComposeTemplatesSignature). Templates replace the
/// body like Windows EmailTemplateSelectionChanged, then the chosen signature is inserted again.
/// The signature pull-down swaps the <c>data-wino-signature</c> block of the composing account.
/// </summary>
public sealed partial class ComposePageViewController
{
    private NSButton _templateButton = null!;
    private NSPopUpButton _signaturePopup = null!;
    private ComposeTemplatePopover? _templatePopover;
    private NSObject? _keyWindowObserver;

    private NSButton BuildTemplateButton()
    {
        _templateButton = new NSButton
        {
            Title = Translator.Composer_InsertTemplate,
            Bordered = false,
            BezelStyle = NSBezelStyle.Inline,
            Image = WinoIcons.Image(WinoIconGlyph.Document, 14, null, Translator.Composer_EmailTemplatesPlaceholder),
            ImagePosition = NSCellImagePosition.ImageLeading,
            ContentTintColor = WinoStyle.SecondaryText,
            ToolTip = Translator.Composer_EmailTemplatesPlaceholder,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _templateButton.HeightAnchor.ConstraintEqualTo(30).Active = true;
        _templateButton.Activated += (_, _) => Observe(ShowTemplatesAsync());
        WinoAccessibility.Label(_templateButton, Translator.Composer_EmailTemplatesPlaceholder);
        return _templateButton;
    }

    private NSPopUpButton BuildSignaturePicker()
    {
        _signaturePopup = new NSPopUpButton { PullsDown = true, Bordered = false, TranslatesAutoresizingMaskIntoConstraints = false, ToolTip = Translator.SettingsSignature_Title };
        _signaturePopup.HeightAnchor.ConstraintEqualTo(30).Active = true;
        WinoAccessibility.Label(_signaturePopup, Translator.SettingsSignature_Title);
        RebuildSignatureMenu();
        return _signaturePopup;
    }

    private void BindExtras()
    {
        Bind(nameof(ViewModel.SelectedSignature), vm => vm.SelectedSignature, _ => RebuildSignatureMenu());
        Bind(nameof(ViewModel.ComposingAccount), vm => vm.ComposingAccount, _ => RebuildSignatureMenu());
        NotifyCollectionChangedEventHandler signatures = (_, _) => _ = Dispatcher.ExecuteOnUIThread(RebuildSignatureMenu);
        ViewModel.AvailableSignatures.CollectionChanged += signatures;
        Bindings.Own(new ActionDisposable(() => ViewModel.AvailableSignatures.CollectionChanged -= signatures));

        // Coming back from Settings (templates or signatures edited there) refreshes the lists.
        _keyWindowObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.DidBecomeKeyNotification, notification =>
        {
            if (Bindings.IsDisposed || View.Window is null || notification.Object != View.Window) return;
            Observe(ViewModel.LoadSignaturesAsync(keepSelection: true));
        });
    }

    private void DisposeExtras()
    {
        if (_keyWindowObserver is not null)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(_keyWindowObserver);
            _keyWindowObserver = null;
        }
        _templatePopover?.Dispose();
        _templatePopover = null;
    }

    // ---- Templates ----

    private async Task ShowTemplatesAsync()
    {
        await ViewModel.RefreshEmailTemplatesAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (Bindings.IsDisposed || _templateButton.Window is null) return;
            _templatePopover?.Dispose();
            _templatePopover = new ComposeTemplatePopover(ViewModel.AvailableEmailTemplates.ToList(),
                template => Observe(InsertTemplateAsync(template)),
                () => Observe(_navigation.NavigateAsync(WinoPage.EmailTemplatesPage)),
                () => Observe(CreateTemplateAsync()));
            _templatePopover.Show(_templateButton);
        });
    }

    private async Task InsertTemplateAsync(EmailTemplate template)
    {
        if (_editor is null || _editorDisposed) return;
        // Same as Windows EmailTemplateSelectionChanged: the template replaces the body.
        await _editor.RenderHtmlAsync(template.HtmlContent ?? string.Empty);
        await ViewModel.ReapplySignatureAsync();
        await _editor.FocusEditorAsync(true);
        ScheduleAutosave();
    }

    private async Task CreateTemplateAsync()
    {
        // The list is opened first so the new-template page has a page to return to.
        await _navigation.NavigateAsync(WinoPage.EmailTemplatesPage);
        await _navigation.NavigateAsync(WinoPage.CreateEmailTemplatePage);
    }

    // ---- Signature ----

    private void RebuildSignatureMenu()
    {
        if (_signaturePopup is null) return;
        var menu = new NSMenu { AutoEnablesItems = false };
        var title = new NSMenuItem(Translator.SettingsSignature_Title)
        {
            Image = WinoIcons.Image(WinoIconGlyph.Signature, 14, null, Translator.SettingsSignature_Title)
        };
        menu.AddItem(title);

        var account = ViewModel.ComposingAccount;
        if (account is not null)
        {
            var header = new NSMenuItem(account.Address ?? string.Empty) { Enabled = false };
            header.AttributedTitle = new NSAttributedString(account.Address ?? string.Empty, new NSStringAttributes
            {
                Font = NSFont.SystemFontOfSize(11, NSFontWeight.Semibold),
                ForegroundColor = WinoStyle.SecondaryText
            });
            menu.AddItem(header);
        }

        var selected = ViewModel.SelectedSignature;
        menu.AddItem(new NSMenuItem(Translator.EditorToolbarOption_None, (_, _) => Observe(ViewModel.ApplySignatureCommand.ExecuteAsync(null)))
        {
            State = selected is null ? NSCellStateValue.On : NSCellStateValue.Off,
            Enabled = account is not null
        });
        foreach (var signature in ViewModel.AvailableSignatures.ToArray())
        {
            var item = signature;
            menu.AddItem(new NSMenuItem(string.IsNullOrWhiteSpace(item.Name) ? Translator.SettingsSignature_Title : item.Name,
                (_, _) => Observe(ViewModel.ApplySignatureCommand.ExecuteAsync(item)))
            {
                State = selected?.Id == item.Id ? NSCellStateValue.On : NSCellStateValue.Off
            });
        }
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem(Translator.Composer_ManageSignatures, (_, _) => Observe(ManageSignaturesAsync())) { Enabled = account is not null });

        _signaturePopup.Menu = menu;
        _signaturePopup.Enabled = account is not null;
        var value = selected is null ? Translator.EditorToolbarOption_None : selected.Name;
        _signaturePopup.AccessibilityValue = new NSString(value ?? string.Empty);
    }

    /// <summary>Settings › account › Signature for the composing account.</summary>
    private async Task ManageSignaturesAsync()
    {
        if (ViewModel.ComposingAccount?.Id is not { } accountId) return;
        await _navigation.NavigateAsync(WinoPage.AccountDetailsPage, accountId);
        await _navigation.NavigateAsync(WinoPage.SignatureManagementPage, accountId);
    }

    private async Task ApplySignatureHtmlAsync(string html, string? previousHtml)
    {
        if (_editor is null || _editorDisposed) return;
        await _editor.SetSignatureAsync(html, previousHtml);
        ScheduleAutosave();
    }
}
