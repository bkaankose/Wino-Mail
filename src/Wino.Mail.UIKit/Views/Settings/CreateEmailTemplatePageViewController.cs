using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Dialogs;
using Wino.Mail.ViewModels;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Settings › Email templates › template (Windows CreateEmailTemplatePage). The title follows the template name, with
/// "Edit template" (or "Create a new e-mail template") under it. Name and Description cards carry full-width fields;
/// the Template content header precedes the rich text editor (<see cref="WinoRichTextEditorView"/>: the format
/// toolbar over the composer's WKWebView editor session, at least 270pt tall, as in the signature editor). The footer
/// holds Delete (existing templates only) and the primary Save, which pass the editor HTML to the ViewModel; both
/// return to the list. Navigation parameter: the template id, or none.
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
    private const double PreferredEditorHeight = 360;

    private WinoRichTextEditorView? _editor;
    private UITextField? _nameField;
    private UIButton? _save;
    private UIButton? _delete;
    private bool _busy;
    private bool _editorDisposed;

    public string? PageTitle => !string.IsNullOrWhiteSpace(ViewModel.TemplateName)
        ? ViewModel.TemplateName.Trim()
        : ViewModel.IsExistingTemplate ? Translator.SettingsEmailTemplates_EditPageTitle : Translator.SettingsEmailTemplates_CreatePageTitle;


    public event EventHandler? PageTitleChanged;

    public override bool HasPendingWork => base.HasPendingWork || _busy;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // ---- Name and description ----
        var nameField = Bind.TextField(vm, nameof(vm.TemplateName), s => s.TemplateName, (s, v) => s.TemplateName = v,
            Translator.SettingsEmailTemplates_NamePlaceholder, 0);
        WinoAccessibility.Label(nameField, Translator.SettingsEmailTemplates_NameTitle);
        nameField.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        nameField.ReturnKeyType = UIReturnKeyType.Next;
        _nameField = nameField;
        Add(StackedCard(Translator.SettingsEmailTemplates_NameTitle, Translator.SettingsEmailTemplates_NameDescription, WinoIconGlyph.Tag, nameField));

        var descriptionField = Bind.TextField(vm, nameof(vm.TemplateDescription), s => s.TemplateDescription, (s, v) => s.TemplateDescription = v,
            Translator.SettingsEmailTemplates_DescriptionPlaceholder, 0);
        WinoAccessibility.Label(descriptionField, Translator.SettingsEmailTemplates_DescriptionTitle);
        descriptionField.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        Add(StackedCard(Translator.SettingsEmailTemplates_DescriptionTitle, Translator.SettingsEmailTemplates_DescriptionDescription, WinoIconGlyph.Note, descriptionField));
        Bind.OnActivated(nameField, () => descriptionField.BecomeFirstResponder(), UIControlEvent.EditingDidEndOnExit);

        // ---- Template content ----
        var contentTitle = WinoStyle.Label(Translator.SettingsEmailTemplates_ContentTitle, WinoSettingsStyle.SectionTitle, WinoStyle.PrimaryText);
        contentTitle.AccessibilityTraits = UIAccessibilityTrait.Header;
        var contentDescription = WinoStyle.Label(Translator.SettingsEmailTemplates_ContentDescription, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var contentHeader = WinoLayout.VStack(0, contentTitle, contentDescription);
        contentHeader.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Insets(contentHeader, 14, 4, 6, 0);
        Add(contentHeader);
        Add(BuildEditor());

        // ---- Footer: Delete (existing only) and Save ----
        var delete = SettingsBinder.CreateButton(Translator.Buttons_Delete, icon: WinoIconGlyph.Delete);
        if (delete.Configuration is { } deleteStyle)
        {
            deleteStyle.BaseForegroundColor = WinoStyle.Critical;
            deleteStyle.Image = WinoIcons.Image(WinoIconGlyph.Delete, 14, WinoStyle.Critical, Translator.Buttons_Delete);
            delete.Configuration = deleteStyle;
        }
        WinoAccessibility.Label(delete, Translator.Buttons_Delete);
        Bind.OnActivated(delete, () => Run(DeleteAsync()));
        Bind.Visible(delete, vm, nameof(vm.IsExistingTemplate), s => s.IsExistingTemplate);
        _delete = delete;

        var save = SettingsBinder.CreateButton(Translator.Buttons_Save, primary: true, icon: WinoIconGlyph.Save);
        Bind.OnActivated(save, () => Run(SaveAsync()));
        EventHandler accentChanged = (_, _) =>
        {
            if (save.Configuration is not { } style) return;
            style.BaseBackgroundColor = WinoStyle.Accent;
            save.Configuration = style;
        };
        WinoStyle.AccentChanged += accentChanged;
        Bindings.Own(new ActionDisposable(() => WinoStyle.AccentChanged -= accentChanged));
        _save = save;

        foreach (var button in new[] { delete, save })
            button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var footer = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), delete, save);
        footer.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Insets(footer, 8, 0, 0, 0);
        Add(footer);

        // The title and subtitle follow the name and whether the template already exists.
        Bind.Bind(vm, nameof(vm.TemplateName), s => s.TemplateName, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Bind.Bind(vm, nameof(vm.IsExistingTemplate), s => s.IsExistingTemplate, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Refresh();
    }

    /// <summary>The rich text editor (format toolbar and editor surface) at the composer's preferred height.</summary>
    private UIView BuildEditor()
    {
        var editor = new WinoRichTextEditorView(Translator.SettingsEmailTemplates_ContentTitle, launcher, preferences, ReportError);
        var preferred = editor.EditorSurface.HeightAnchor.ConstraintEqualTo((nfloat)PreferredEditorHeight);
        preferred.Priority = 750;
        NSLayoutConstraint.ActivateConstraints(
        [
            editor.EditorSurface.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)MinimumEditorHeight),
            preferred
        ]);
        EventHandler ready = (_, _) => Refresh();
        editor.ReadyChanged += ready;
        Bindings.Own(new ActionDisposable(() => editor.ReadyChanged -= ready));
        _editor = editor;
        return editor;
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        await base.InitializeAsync(mode, parameter);
        var html = await ViewModel.LoadAsync(parameter);
        var dark = false;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            dark = WinoTraits.IsDark(TraitCollection);
            if (!ViewModel.IsExistingTemplate) _nameField?.BecomeFirstResponder();
        });
        await LoadEditorAsync(html, dark);
    }

    protected override async Task DeactivateAsync()
    {
        await base.DeactivateAsync();
        await DisposeEditorAsync();
    }

    /// <summary>Renders the template once the editor session is up; the view applies the composer preferences and theme.</summary>
    private async Task LoadEditorAsync(string? html, bool dark)
    {
        if (_editor is not { } editor || _editorDisposed) return;
        await editor.LoadAsync(html, dark);
    }

    private bool CanSave => _editor is { IsReady: true } && !_editorDisposed && !_busy;

    private void Refresh()
    {
        if (_save is { } save) save.Enabled = CanSave;
        if (_delete is { } delete) delete.Enabled = !_busy;
    }

    /// <summary>The ViewModel validates the name (warning info bar), saves and goes back.</summary>
    private async Task SaveAsync()
    {
        if (!CanSave || _editor is not { } editor) return;
        _busy = true;
        Refresh();
        try
        {
            View?.EndEditing(true);
            var html = await editor.GetHtmlBodyAsync() ?? string.Empty;
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
        if (_editor is not { } editor) return;
        try { await editor.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { ReportError(exception); }
    }
}
