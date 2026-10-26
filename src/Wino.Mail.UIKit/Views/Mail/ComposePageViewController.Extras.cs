using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Mail.Compose;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// Insert template and signature choice (Mac Extras partial, design board ComposeTemplatesSignature), and the More menu
/// that gathers the composer commands the Mac shows in its command row. Templates replace the body like Windows
/// EmailTemplateSelectionChanged, then the chosen signature is inserted again; the signature menu swaps the
/// <c>data-wino-signature</c> block of the composing account.
/// </summary>
public sealed partial class ComposePageViewController
{
    private void BindExtras()
    {
        // The More menu reads the signatures when it opens, so it needs no list binding.
        // Coming back from Settings (templates or signatures edited there) refreshes the lists.
        var observer = Foundation.NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.DidBecomeActiveNotification, _ =>
        {
            if (!Bindings.IsDisposed) Observe(ViewModel.LoadSignaturesAsync(keepSelection: true));
        });
        Bindings.Own(new ActionDisposable(() => Foundation.NSNotificationCenter.DefaultCenter.RemoveObserver(observer)));
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        // A Settings sheet over the composer may have changed the signatures.
        if (IsActive && !Bindings.IsDisposed) Observe(ViewModel.LoadSignaturesAsync(keepSelection: true));
    }

    // ---- More menu ----

    /// <summary>
    /// Cc/Bcc, find and replace; Wino Intelligence rewrite; template and signature; importance, S/MIME and read
    /// receipt; the editor theme; Discard. Built each time it opens, so checkmarks follow the ViewModel.
    /// </summary>
    private UIMenuElement[] BuildMoreMenu()
    {
        var elements = new List<UIMenuElement>();
        var editing = new List<UIMenuElement>();
        if (!ViewModel.IsCCBCCVisible)
            editing.Add(EditorFormatToolbar.Action(Translator.Composer_CcBcc, () => { ViewModel.IsCCBCCVisible = true; CcField.Focus(); }, image: WinoIcons.Image(WinoIconGlyph.People, 16)));
        editing.Add(EditorFormatToolbar.Action(Translator.MailOperation_Find, () => ShowFind(false, SelectionSeed()), image: WinoIcons.Image(WinoIconGlyph.Find, 16)));
        editing.Add(EditorFormatToolbar.Action(Translator.Composer_FindShowReplace, () => ShowFind(true, SelectionSeed()), image: WinoIcons.Image(WinoIconGlyph.Edit, 16)));
        elements.Add(EditorFormatToolbar.Inline(editing));

        var session = ViewModel.RewriteSession;
        if (session.IsAvailable) elements.Add(EditorFormatToolbar.Inline([RewriteMenu()]));

        elements.Add(EditorFormatToolbar.Inline(
        [
            EditorFormatToolbar.Action(Translator.Composer_InsertTemplate, () => Observe(ShowTemplatesAsync()), image: WinoIcons.Image(WinoIconGlyph.Document, 16)),
            SignatureMenu()
        ]));

        var options = new List<UIMenuElement> { EditorFormatToolbar.Submenu(Translator.Composer_Importance, ImportanceMenu().Children, WinoIcons.Image(WinoIconGlyph.Important, 16)) };
        options.AddRange(SecurityMenuItems());
        options.Add(EditorFormatToolbar.Action(ThemeLabel(_darkEditor), ToggleEditorTheme, image: WinoIcons.Image(_darkEditor ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor, 16)));
        elements.Add(EditorFormatToolbar.Inline(options));

        var discard = EditorFormatToolbar.Action(Translator.Buttons_Discard, () => Observe(DiscardAsync()),
            enabled: ViewModel.DiscardCommand.CanExecute(null), image: WinoIcons.Image(WinoIconGlyph.Delete, 16), destructive: true);
        elements.Add(EditorFormatToolbar.Inline([discard]));
        return elements.ToArray();
    }

    /// <summary>Discard with the ViewModel's confirmation; a discarded draft closes the composer.</summary>
    private async Task DiscardAsync()
    {
        if (!ViewModel.DiscardCommand.CanExecute(null)) return;
        ViewModel.CancelAutosave();
        await ViewModel.DiscardCommand.ExecuteAsync(null);
    }

    // ---- Templates ----

    private async Task ShowTemplatesAsync()
    {
        await ViewModel.RefreshEmailTemplatesAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (Bindings.IsDisposed || View?.Window is null) return;
            var picker = new ComposeTemplatePicker(ViewModel.AvailableEmailTemplates.ToList(),
                template => Observe(InsertTemplateAsync(template)),
                () => Observe(NavigateAsync(WinoPage.EmailTemplatesPage)),
                () => Observe(CreateTemplateAsync()));
            PresentFromMore(picker);
        });
    }

    private async Task InsertTemplateAsync(EmailTemplate template)
    {
        if (_editor is not { } editor || _editorDisposed) return;
        // Same as Windows EmailTemplateSelectionChanged: the template replaces the body.
        await editor.RenderHtmlAsync(template.HtmlContent ?? string.Empty);
        await ViewModel.ReapplySignatureAsync();
        await editor.FocusEditorAsync(true);
        ScheduleAutosave();
    }

    private async Task CreateTemplateAsync()
    {
        // The list is opened first so the new-template page has a page to return to.
        await NavigateAsync(WinoPage.EmailTemplatesPage);
        await NavigateAsync(WinoPage.CreateEmailTemplatePage);
    }

    private Task NavigateAsync(WinoPage page, object? parameter = null)
        => _navigation is { } navigation ? navigation.NavigateAsync(page, parameter) : Task.CompletedTask;

    // ---- Signature ----

    /// <summary>None, the composing account's signatures (current checked) and Manage signatures (Mac RebuildSignatureMenu).</summary>
    private UIMenu SignatureMenu()
    {
        var account = ViewModel.ComposingAccount;
        var selected = ViewModel.SelectedSignature;
        var items = new List<UIMenuElement>
        {
            EditorFormatToolbar.Action(Translator.Composer_NoSignature, () => Observe(ViewModel.ApplySignatureCommand.ExecuteAsync(null)), selected is null, account is not null)
        };
        foreach (var signature in ViewModel.AvailableSignatures.ToArray())
        {
            if (signature is null) continue;
            var item = signature;
            items.Add(EditorFormatToolbar.Action(string.IsNullOrWhiteSpace(item.Name) ? Translator.SettingsSignature_Title : item.Name,
                () => Observe(ViewModel.ApplySignatureCommand.ExecuteAsync(item)), selected?.Id == item.Id));
        }
        items.Add(EditorFormatToolbar.Inline([EditorFormatToolbar.Action(Translator.Composer_ManageSignatures, () => Observe(ManageSignaturesAsync()), enabled: account is not null)]));
        var menu = EditorFormatToolbar.Submenu(Translator.SettingsSignature_Title, items, WinoIcons.Image(WinoIconGlyph.Signature, 16));
        if (account?.Address is { Length: > 0 } address) menu = UIMenu.Create(Translator.SettingsSignature_Title, WinoIcons.Image(WinoIconGlyph.Signature, 16), UIMenuIdentifier.None, default(UIMenuOptions),
            [EditorFormatToolbar.Inline(items, address)]);
        return menu;
    }

    /// <summary>Settings › account › Signature for the composing account.</summary>
    private async Task ManageSignaturesAsync()
    {
        if (ViewModel.ComposingAccount?.Id is not { } accountId) return;
        await NavigateAsync(WinoPage.AccountDetailsPage, accountId);
        await NavigateAsync(WinoPage.SignatureManagementPage, accountId);
    }

    private async Task ApplySignatureHtmlAsync(string html, string? previousHtml)
    {
        if (_editor is not { } editor || _editorDisposed) return;
        await editor.SetSignatureAsync(html, previousHtml);
        ScheduleAutosave();
    }
}
