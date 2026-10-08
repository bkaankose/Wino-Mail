using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views.Dialogs;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Settings › Email templates › template (Windows CreateEmailTemplatePage). The window breadcrumb shows
/// "Email Templates ›" and the template name, with "Edit template" (or "Create a new e-mail template")
/// under it. Name and Description cards carry 280pt fields; the Template content header precedes the
/// shared Format toolbar fused to the HTML editor, which fills the remaining height (at least 270pt).
/// The footer holds Delete (existing templates only) and the primary Save (Cmd+S), which pass the
/// editor HTML to the ViewModel; both return to the list. Navigation parameter: the template id, or none.
/// </summary>
public sealed class CreateEmailTemplatePageViewController(
    CreateEmailTemplatePageViewModel viewModel,
    IExternalLauncher launcher,
    IPreferencesService preferences,
    IDispatcher dispatcher,
    IWinoLogger logger)
    : SettingsPageViewController<CreateEmailTemplatePageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private const double MinimumEditorHeight = 270;
    private const double FieldWidth = 280;

    private WinoRichTextEditorView _editor = null!;
    private NSTextField _nameField = null!;
    private NSButton _save = null!;
    private NSButton _delete = null!;
    private bool _busy;
    private bool _editorDisposed;

    public string? PageTitle => !string.IsNullOrWhiteSpace(ViewModel.TemplateName)
        ? ViewModel.TemplateName.Trim()
        : ViewModel.IsExistingTemplate ? Translator.SettingsEmailTemplates_EditPageTitle : Translator.SettingsEmailTemplates_CreatePageTitle;

    public string? PageDescription => ViewModel.IsExistingTemplate
        ? Translator.SettingsEmailTemplates_EditPageTitle
        : Translator.SettingsEmailTemplates_NewTemplateDescription;

    public event EventHandler? PageTitleChanged;

    public override bool HasPendingWork => base.HasPendingWork || _busy;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // ---- Name and description ----
        _nameField = Bind.TextField(vm, nameof(vm.TemplateName), s => s.TemplateName, (s, v) => s.TemplateName = v,
            Translator.SettingsEmailTemplates_NamePlaceholder, FieldWidth);
        WinoAccessibility.Label(_nameField, Translator.SettingsEmailTemplates_NameTitle);
        _nameField.Changed += (_, _) => Refresh();
        var nameCard = Add(Card(Translator.SettingsEmailTemplates_NameTitle, Translator.SettingsEmailTemplates_NameDescription, WinoIconGlyph.Tag, _nameField));

        var descriptionField = Bind.TextField(vm, nameof(vm.TemplateDescription), s => s.TemplateDescription, (s, v) => s.TemplateDescription = v,
            Translator.SettingsEmailTemplates_DescriptionPlaceholder, FieldWidth);
        WinoAccessibility.Label(descriptionField, Translator.SettingsEmailTemplates_DescriptionTitle);
        var descriptionCard = Add(Card(Translator.SettingsEmailTemplates_DescriptionTitle, Translator.SettingsEmailTemplates_DescriptionDescription, WinoIconGlyph.Note, descriptionField));

        // ---- Template content ----
        var contentTitle = WinoStyle.Label(Translator.SettingsEmailTemplates_ContentTitle, WinoSettingsStyle.SectionTitle, WinoStyle.PrimaryText);
        var contentDescription = WinoStyle.Label(Translator.SettingsEmailTemplates_ContentDescription, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var contentHeader = WinoLayout.VStack(0, contentTitle, contentDescription);
        contentHeader.Alignment = NSLayoutAttribute.Leading;
        contentHeader.EdgeInsets = new NSEdgeInsets(14, 1, 6, 0);
        Add(contentHeader);

        _editor = new WinoRichTextEditorView(new WinoRichTextEditorOptions(Fused: true, Highlight: true, Table: true, Emoji: true, ClearFormatting: false),
            Translator.SettingsEmailTemplates_ContentTitle, launcher, preferences, ReportError);
        _editor.EditorSurface.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)MinimumEditorHeight).Active = true;
        _editor.ReadyChanged += (_, _) => Refresh();
        Add(_editor);

        // ---- Footer: Delete (existing only) and Save ----
        _delete = SettingsBinder.CreateButton(Translator.Buttons_Delete, icon: WinoIconGlyph.Delete);
        _delete.AttributedTitle = new Foundation.NSAttributedString(Translator.Buttons_Delete, new NSStringAttributes { ForegroundColor = WinoStyle.Critical, Font = _delete.Font });
        _delete.ContentTintColor = WinoStyle.Critical;
        WinoAccessibility.Label(_delete, Translator.Buttons_Delete);
        Bind.OnActivated(_delete, () => Run(DeleteAsync()));
        Bind.Visible(_delete, vm, nameof(vm.IsExistingTemplate), s => s.IsExistingTemplate);

        _save = SettingsBinder.CreateButton(Translator.Buttons_Save, primary: true, icon: WinoIconGlyph.Save);
        _save.KeyEquivalent = "s";
        _save.KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask;
        _save.ToolTip = $"{Translator.Buttons_Save} (⌘S)";
        Bind.OnActivated(_save, () => Run(SaveAsync()));
        EventHandler accentChanged = (_, _) => _save.BezelColor = WinoStyle.Accent;
        WinoStyle.AccentChanged += accentChanged;
        Bindings.Own(new ActionDisposable(() => WinoStyle.AccentChanged -= accentChanged));

        var footer = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), _delete, _save);
        footer.EdgeInsets = new NSEdgeInsets(8, 0, 0, 0);
        Add(footer);

        // The editor takes the height the other rows leave in the window.
        foreach (var view in new NSView[] { nameCard, descriptionCard, contentHeader, footer })
            view.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Vertical);
        // Cards have no intrinsic height (only a minimum), so hugging alone left them free to absorb
        // the spare height; keep them at their minimum unless their content needs more.
        foreach (var card in new NSView[] { nameCard, descriptionCard })
        {
            var compact = card.HeightAnchor.ConstraintEqualTo((nfloat)WinoSettingsStyle.CardMinHeight);
            compact.Priority = 740;
            compact.Active = true;
        }
        _editor.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        contentHeader.SetHuggingPriority(750, NSLayoutConstraintOrientation.Vertical);
        footer.SetHuggingPriority(750, NSLayoutConstraintOrientation.Vertical);
        Page.SetHuggingPriority(1, NSLayoutConstraintOrientation.Vertical);
        if (View is NSScrollView { DocumentView: { } document } scroll)
            document.HeightAnchor.ConstraintGreaterThanOrEqualTo(scroll.ContentView.HeightAnchor).Active = true;

        // The window title and subtitle follow the name and whether the template already exists.
        Bind.Bind(vm, nameof(vm.TemplateName), s => s.TemplateName, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Bind.Bind(vm, nameof(vm.IsExistingTemplate), s => s.IsExistingTemplate, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Refresh();
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        await base.InitializeAsync(mode, parameter);
        var html = await ViewModel.LoadAsync(parameter!);
        var dark = false;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            dark = WinoRichTextEditorView.IsDark(View.EffectiveAppearance);
            if (!ViewModel.IsExistingTemplate) View.Window?.MakeFirstResponder(_nameField);
        });
        await _editor.LoadAsync(html, dark);
    }

    protected override async Task DeactivateAsync()
    {
        await base.DeactivateAsync();
        await DisposeEditorAsync();
    }

    private bool CanSave => _editor.IsReady && !_busy;

    private void Refresh()
    {
        _save.Enabled = CanSave;
        _delete.Enabled = !_busy;
    }

    /// <summary>The ViewModel validates the name (warning info bar), saves and goes back.</summary>
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        _busy = true;
        Refresh();
        try
        {
            var html = await _editor.GetHtmlBodyAsync() ?? string.Empty;
            await ViewModel.SaveAsync(html);
        }
        finally
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _busy = false;
                if (!_editorDisposed) Refresh();
            });
        }
    }

    /// <summary>The ViewModel confirms, deletes and goes back.</summary>
    private async Task DeleteAsync()
    {
        if (_busy || !ViewModel.IsExistingTemplate) return;
        _busy = true;
        Refresh();
        try { await ViewModel.DeleteAsync(); }
        finally
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _busy = false;
                if (!_editorDisposed) Refresh();
            });
        }
    }

    private async Task DisposeEditorAsync()
    {
        if (_editorDisposed) return;
        _editorDisposed = true;
        try { await _editor.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { ReportError(exception); }
    }
}
