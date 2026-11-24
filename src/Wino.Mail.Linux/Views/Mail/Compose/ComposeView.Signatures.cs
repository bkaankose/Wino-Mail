using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// Signature menu and template picker in the command row after Send (Mac order). A template replaces the body as on
/// Windows, then the chosen signature is inserted again; the Manage links open the Settings pages.
/// </summary>
internal sealed partial class ComposeView
{
    private readonly Gtk.Button _templateButton = WinoIcons.Button(WinoIconGlyph.Document, Translator.Composer_EmailTemplatesPlaceholder);
    private ComposeTemplatePopover? _templatePopover;
    private IDisposable? _windowActive;

    internal ComposeSignatureMenu? SignatureMenu { get; private set; }
    internal ComposeTemplatePopover? TemplatePopover => _templatePopover;

    partial void InitializeSignatures()
    {
        ViewModel.ApplySignatureHtmlFunc = ApplySignatureHtmlAsync;
        var menu = SignatureMenu = new ComposeSignatureMenu(ViewModel, Dispatcher, ManageSignaturesAsync, ReportError);
        CommandSlot.Append(menu.Root);

        _templateButton.AddCssClass("wino-editor-tool");
        _templateButton.SetValign(Gtk.Align.Center);
        _templateButton.SetTooltipText(Translator.Composer_EmailTemplatesPlaceholder);
        CommandSlot.Append(_templateButton);
        Bindings.Own(GtkSignals.OnClicked(_templateButton, () => Observe(ShowTemplatesAsync())));

        Bindings.Own(GtkSignals.OnNotify(Root, "root", WatchWindowActivation));
        WatchWindowActivation();
    }

    partial void DeactivateSignatures()
    {
        ViewModel.ApplySignatureHtmlFunc = null;
        _windowActive?.Dispose();
        _windowActive = null;
        DisposeTemplatePopover();
        if (SignatureMenu is { } menu)
        {
            menu.Dispose();
            CommandSlot.Remove(menu.Root);
            SignatureMenu = null;
        }
        CommandSlot.Remove(_templateButton);
    }

    // Coming back from Settings (signatures edited there) refreshes the list (Mac DidBecomeKey observer).
    private void WatchWindowActivation()
    {
        _windowActive?.Dispose();
        _windowActive = null;
        if (IsReleased || Root.GetRoot() is not Gtk.Window window) return;
        _windowActive = GtkSignals.OnNotify(window, "is-active", () =>
        {
            if (!IsReleased && window.IsActive) Observe(ViewModel.LoadSignaturesAsync(keepSelection: true));
        });
    }

    private async Task ApplySignatureHtmlAsync(string html, string? previousHtml)
    {
        if (Editor is not { } editor || _editorDisposed) return;
        await editor.SetSignatureAsync(html, previousHtml);
        ScheduleAutosave();
    }

    internal async Task ShowTemplatesAsync()
    {
        await ViewModel.RefreshEmailTemplatesAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased || _templateButton.GetRoot() is null) return;
            DisposeTemplatePopover();
            var popover = _templatePopover = new ComposeTemplatePopover([.. ViewModel.AvailableEmailTemplates],
                template => Observe(InsertTemplateAsync(template)),
                () => Observe(ShowSettingsPagesAsync(WinoPage.EmailTemplatesPage)),
                () => Observe(ShowSettingsPagesAsync(WinoPage.EmailTemplatesPage, WinoPage.CreateEmailTemplatePage)));
            popover.Show(_templateButton);
        });
    }

    internal async Task InsertTemplateAsync(EmailTemplate template)
    {
        if (Editor is not { } editor || _editorDisposed) return;
        await ViewModel.ApplyTemplateAsync(template, html => editor.RenderHtmlAsync(html));
        if (IsReleased) return;
        await editor.FocusEditorAsync(true);
        ScheduleAutosave();
    }

    private async Task ManageSignaturesAsync()
    {
        if (ViewModel.ComposingAccount?.Id is not { } accountId) return;
        await _settings.ShowAsync(WinoPage.AccountDetailsPage, accountId);
        await _settings.ShowAsync(WinoPage.SignatureManagementPage, accountId);
    }

    // The list opens first so the new-template page has a page to return to (Mac).
    private async Task ShowSettingsPagesAsync(params WinoPage[] pages)
    {
        foreach (var page in pages) await _settings.ShowAsync(page, null);
    }

    // A popover must not be unparented inside its own closed handler; the old one goes on the next turn.
    private void DisposeTemplatePopover()
    {
        if (_templatePopover is not { } popover) return;
        _templatePopover = null;
        MainLoop.Post(popover.Dispose);
    }
}
