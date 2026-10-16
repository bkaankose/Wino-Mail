using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Dialogs;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings.GeneralAndMail;

/// <summary>
/// Settings › Email templates › template (Windows CreateEmailTemplatePage, iOS CreateEmailTemplatePageViewController).
/// The title follows the template name, with "Edit template" (or the new-template description) under it. Name and
/// Description cards carry full-width fields; the Template content header precedes the rich text editor
/// (<see cref="WinoHtmlTextEditor"/> until an Android WebView editor session exists), set up with the composer font,
/// size, spell check and auto-correct preferences. The footer holds Delete (existing templates only) and the primary
/// Save, which pass the editor HTML to the ViewModel; both return to the list. Navigation parameter: the template id, or
/// none. The route runs OnNavigatedTo and then the template load (iOS InitializeAsync); the loaded HTML, and the edited
/// HTML when the view is recreated, live in the entry.
/// </summary>
public sealed class CreateEmailTemplatePageFragment : SettingsPageFragment<CreateEmailTemplatePageViewModel>, ISettingsPageTitleSource
{
    private const string HtmlKey = "CreateEmailTemplate.Html";
    private const string FocusedKey = "CreateEmailTemplate.Focused";

    private WinoHtmlTextEditor? _editor;
    private EditText? _nameField;
    private MaterialButton? _save;
    private MaterialButton? _delete;
    private bool _busy;
    private bool _editorReady;

    public string? PageTitle => ViewModel is not { } vm
        ? null
        : !string.IsNullOrWhiteSpace(vm.TemplateName)
            ? vm.TemplateName.Trim()
            : vm.IsExistingTemplate ? Translator.SettingsEmailTemplates_EditPageTitle : Translator.SettingsEmailTemplates_CreatePageTitle;

    /// <summary>The subtitle follows whether the template exists, so the catalog description is not shown.</summary>
    protected override bool ShowsPageDescription => false;

    /// <summary>The route's activation step: loads the template, keeps its HTML in the entry and fills the editor.</summary>
    public static async Task LoadAsync(PageEntry entry)
    {
        if (entry.ViewModel is not CreateEmailTemplatePageViewModel viewModel) return;
        var html = await viewModel.LoadAsync(entry.Parameter);
        await entry.Dispatcher.ExecuteOnUIThread(() =>
        {
            entry.State[HtmlKey] = html ?? string.Empty;
            (entry.Fragment as CreateEmailTemplatePageFragment)?.ApplyLoadedContent();
        });
    }

    protected override void BuildPage()
    {
        var vm = VM;
        _busy = false;
        _editorReady = false;

        var intro = AddIntro(string.Empty);
        if (intro.LayoutParameters is LinearLayout.LayoutParams introLayout) introLayout.BottomMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s);
        Bind.Bind(vm, nameof(vm.IsExistingTemplate), s => s.IsExistingTemplate, existing => intro.Text = existing
            ? Translator.SettingsEmailTemplates_EditPageTitle
            : Translator.SettingsEmailTemplates_NewTemplateDescription);

        // ---- Name and description ----
        var nameLayout = Bind.TextField(vm, nameof(vm.TemplateName), s => s.TemplateName, (s, v) => s.TemplateName = v,
            Translator.SettingsEmailTemplates_NamePlaceholder);
        _nameField = nameLayout.EditText;
        if (_nameField is { } nameField)
        {
            nameField.ContentDescription = Translator.SettingsEmailTemplates_NameTitle;
            nameField.ImeOptions = global::Android.Views.InputMethods.ImeAction.Next;
        }
        Add(StackedCard(Translator.SettingsEmailTemplates_NameTitle, Translator.SettingsEmailTemplates_NameDescription, WinoIconGlyph.Tag, nameLayout));

        var descriptionLayout = Bind.TextField(vm, nameof(vm.TemplateDescription), s => s.TemplateDescription, (s, v) => s.TemplateDescription = v,
            Translator.SettingsEmailTemplates_DescriptionPlaceholder);
        if (descriptionLayout.EditText is { } descriptionField) descriptionField.ContentDescription = Translator.SettingsEmailTemplates_DescriptionTitle;
        Add(StackedCard(Translator.SettingsEmailTemplates_DescriptionTitle, Translator.SettingsEmailTemplates_DescriptionDescription, WinoIconGlyph.Note, descriptionLayout));

        // ---- Template content ----
        var contentTitle = Text(Translator.SettingsEmailTemplates_ContentTitle, WinoTextStyle.BodyLargeStrong);
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(contentTitle, true);
        var contentDescription = Text(Translator.SettingsEmailTemplates_ContentDescription, WinoTextStyle.Description);
        var contentHeader = Stack(0, contentTitle, contentDescription);
        WinoTokens.Pad(contentHeader, Resource.Dimension.wino_space_2xs, Resource.Dimension.wino_space_ml, 0, Resource.Dimension.wino_space_xs);
        Add(contentHeader);
        Add(BuildEditor());

        // ---- Footer: Delete (existing only) and Save ----
        var delete = SettingsBinder.CreateButton(Ctx, Translator.Buttons_Delete, outlined: true, WinoIconGlyph.Delete);
        delete.SetTextColor(WinoColors.Critical(Ctx));
        delete.Icon = WinoIcons.Drawable(Ctx, WinoIconGlyph.Delete, WinoTokens.Dp(Ctx, Resource.Dimension.wino_glyph_m), WinoColors.Critical(Ctx));
        Bind.Views.Click(delete, () => Run(DeleteAsync()));
        _delete = delete;

        var save = SettingsBinder.CreateButton(Ctx, Translator.Buttons_Save, outlined: false, WinoIconGlyph.Save);
        Bind.Views.Click(save, () => Run(SaveAsync()));
        _save = save;

        // Save and Delete are action rows (Delete in the error colour, existing templates only).
        var deleteCard = Card(Translator.Buttons_Delete, null, WinoIconGlyph.None, delete);
        deleteCard.IsDestructive = true;
        Bind.Visible(deleteCard, vm, nameof(vm.IsExistingTemplate), s => s.IsExistingTemplate);
        AddGroup(null, Card(Translator.Buttons_Save, null, WinoIconGlyph.None, save), deleteCard);

        // The title follows the name and whether the template already exists.
        Bind.Bind(vm, nameof(vm.TemplateName), s => s.TemplateName, _ => UpdateTitle());
        Bind.Bind(vm, nameof(vm.IsExistingTemplate), s => s.IsExistingTemplate, _ => UpdateTitle());

        // A recreated view keeps the edited HTML; the editor's text goes back to the entry when the view goes.
        Bindings.Own(new ActionDisposable(() =>
        {
            if (_editorReady && _editor is { } editor && Entry is { IsReleased: false } entry) entry.State[HtmlKey] = editor.Html;
            _editor = null;
            _nameField = null;
            _save = null;
            _delete = null;
            _editorReady = false;
        }));

        ApplyLoadedContent();
    }

    /// <summary>The rich text editor in a card frame, at least the template editor height tall, using the composer typography and proofing.</summary>
    private View BuildEditor()
    {
        var editor = new WinoHtmlTextEditor(Ctx, Translator.SettingsEmailTemplates_ContentTitle,
            Resource.Dimension.wino_mail_template_editor_min_height);
        var field = editor.Field;
        if (MainApplication.Services?.GetService<IPreferencesService>() is { } preferences)
        {
            if (!string.IsNullOrWhiteSpace(preferences.ComposerFont))
                field.Typeface = Typeface.Create(preferences.ComposerFont, TypefaceStyle.Normal);
            if (preferences.ComposerFontSize > 0)
                field.SetTextSize(global::Android.Util.ComplexUnitType.Sp, Math.Clamp(preferences.ComposerFontSize, 8, 48));
            var input = field.InputType;
            input = preferences.IsComposerSpellCheckEnabled ? input & ~InputTypes.TextFlagNoSuggestions : input | InputTypes.TextFlagNoSuggestions;
            input = preferences.IsComposerAutoCorrectEnabled ? input | InputTypes.TextFlagAutoCorrect : input & ~InputTypes.TextFlagAutoCorrect;
            field.InputType = input;
        }
        field.Enabled = false;
        _editor = editor;
        return editor;
    }

    /// <summary>Fills the editor once the template has loaded (now, or when the route's load finishes).</summary>
    private void ApplyLoadedContent()
    {
        if (_editor is not { } editor || Entry is not { } entry || !entry.State.TryGetValue(HtmlKey, out var stored)) return;
        if (!_editorReady)
        {
            editor.Html = stored as string ?? string.Empty;
            editor.Field.Enabled = true;
            _editorReady = true;
        }
        if (!entry.State.ContainsKey(FocusedKey))
        {
            entry.State[FocusedKey] = true;
            if (!VM.IsExistingTemplate) _nameField?.RequestFocus();
        }
        Refresh();
    }

    private bool CanSave => _editorReady && !_busy;

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
            var html = editor.Html;
            if (Entry is { } entry) entry.State[HtmlKey] = html;
            await VM.SaveAsync(html);
        }
        finally
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _busy = false;
                Refresh();
            });
        }
    }

    /// <summary>The ViewModel confirms, deletes and goes back.</summary>
    private async Task DeleteAsync()
    {
        if (_busy || !VM.IsExistingTemplate) return;
        _busy = true;
        Refresh();
        try { await VM.DeleteAsync(); }
        finally
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _busy = false;
                Refresh();
            });
        }
    }

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { ReportError(exception); }
    }
}
