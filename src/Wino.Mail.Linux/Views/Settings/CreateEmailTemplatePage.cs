using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// The template editor (Windows/macOS CreateEmailTemplatePage), for a new template or the one whose id is the parameter:
/// name, description and subject as Windows cards, the rich-text body, and Delete (existing only) and Save in the bottom
/// bar. The ViewModel validates the name, saves or deletes, and goes back.
/// </summary>
internal sealed class CreateEmailTemplatePage(CreateEmailTemplatePageViewModel viewModel, WinoViewServices services, IExternalLauncher launcher,
    IPreferencesService preferences, ITranslationService translations)
    : SettingsPageView<CreateEmailTemplatePageViewModel>(viewModel, services), ISettingsPageTitleSource
{
    private const int MinimumEditorHeight = 270;
    private WinoRichTextEditorView? _editor;
    private Gtk.Entry? _nameEntry;
    private Gtk.Button? _save;
    private Gtk.Button? _delete;
    private bool _busy;
    private bool _editorDisposed;

#if DEBUG
    private static WeakReference<CreateEmailTemplatePage>? _debugCurrent;
    internal static CreateEmailTemplatePage? DebugCurrent => _debugCurrent is { } weak && weak.TryGetTarget(out var page) ? page : null;
    internal WinoRichTextEditorView? DebugEditor => _editor;
#endif

    public string? PageTitle => !string.IsNullOrWhiteSpace(ViewModel.TemplateName)
        ? ViewModel.TemplateName.Trim()
        : ViewModel.IsExistingTemplate ? Translator.SettingsEmailTemplates_EditPageTitle : Translator.SettingsEmailTemplates_CreatePageTitle;

    public string? PageDescription => ViewModel.IsExistingTemplate ? Translator.SettingsEmailTemplates_EditPageTitle : Translator.SettingsEmailTemplates_NewTemplateDescription;

    public event EventHandler? PageTitleChanged;

    public override bool HasPendingWork => base.HasPendingWork || _busy;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var fields = AddGroup(null);
        _nameEntry = Field(fields, Translator.SettingsEmailTemplates_NameTitle, Translator.SettingsEmailTemplates_NameDescription, WinoIconGlyph.Tag,
            Translator.SettingsEmailTemplates_NamePlaceholder, nameof(vm.TemplateName), static s => s.TemplateName, static (s, v) => s.TemplateName = v);
        Field(fields, Translator.SettingsEmailTemplates_DescriptionTitle, Translator.SettingsEmailTemplates_DescriptionDescription, WinoIconGlyph.Note,
            Translator.SettingsEmailTemplates_DescriptionPlaceholder, nameof(vm.TemplateDescription), static s => s.TemplateDescription, static (s, v) => s.TemplateDescription = v);
        Field(fields, Translator.SettingsEmailTemplates_SubjectTitle, Translator.SettingsEmailTemplates_SubjectDescription, WinoIconGlyph.Mail,
            Translator.SettingsEmailTemplates_SubjectPlaceholder, nameof(vm.TemplateSubject), static s => s.TemplateSubject, static (s, v) => s.TemplateSubject = v);

        var editor = _editor = new WinoRichTextEditorView(new WinoRichTextEditorOptions(Fused: true, Highlight: true, Table: true, Emoji: true, ClearFormatting: false),
            Translator.SettingsEmailTemplates_ContentTitle, Dispatcher, launcher, preferences, translations, ReportError);
        editor.Root.SetSizeRequest(-1, MinimumEditorHeight + 60);
        EventHandler ready = (_, _) => Refresh();
        editor.ReadyChanged += ready;
        Settings.Own(new ActionDisposable(() => editor.ReadyChanged -= ready));
        var content = Page.AddGroup(Translator.SettingsEmailTemplates_ContentTitle, Translator.SettingsEmailTemplates_ContentDescription);
        content.HidesWhenEmpty = false;
        content.Root.Add(editor.Root);

        var bar = Gtk.ActionBar.New();
        var delete = _delete = Settings.Button(Translator.Buttons_Delete, () => Observe(DeleteAsync()), destructive: true, icon: WinoIconGlyph.Delete);
        WinoA11y.Rename(delete, Translator.Buttons_Delete);
        Settings.Visible(delete, vm, nameof(vm.IsExistingTemplate), static s => s.IsExistingTemplate);
        var save = _save = Settings.Button(Translator.Buttons_Save, () => Observe(SaveAsync()), suggested: true, icon: WinoIconGlyph.Save);
        WinoA11y.Rename(save, Translator.Buttons_Save);
        bar.PackEnd(save);
        bar.PackEnd(delete);
        Page.SetBottomBar(bar);
        save.SetTooltipText($"{Translator.Buttons_Save} ({Gtk.Functions.AcceleratorGetLabel((uint)Gdk.Constants.KEY_s, Gdk.ModifierType.ControlMask)})");

        // Ctrl+S saves, as Cmd+S does on macOS; capture phase so the key is seen before the editor's web view.
        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        Settings.Own(GtkSignals.Connect<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h, (_, args) => HandleKey(args.Keyval, args.State)));
        Page.Root.AddController(keys);

        Settings.Bind(vm, nameof(vm.TemplateName), static s => s.TemplateName, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Settings.Bind(vm, nameof(vm.IsExistingTemplate), static s => s.IsExistingTemplate, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Refresh();
#if DEBUG
        _debugCurrent = new WeakReference<CreateEmailTemplatePage>(this);
#endif
    }

    protected override async Task NavigateAsync(NavigationMode mode, object? parameter)
    {
        await base.NavigateAsync(mode, parameter);
        var html = await ViewModel.LoadAsync(parameter ?? string.Empty);
        var dark = false;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased) return;
            dark = Adw.StyleManager.GetDefault().Dark;
            if (!ViewModel.IsExistingTemplate) _nameEntry?.GrabFocus();
        });
        if (IsReleased || _editor is not { } editor) return;
        await editor.LoadAsync(html, dark);
    }

    protected override async Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(DebugCurrent, this)) _debugCurrent = null;
#endif
        await base.DeactivateAsync();
        if (_editorDisposed || _editor is not { } editor) return;
        _editorDisposed = true;
        try { await editor.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    internal bool HandleKey(uint keyval, Gdk.ModifierType state)
    {
        var modifiers = state & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.ShiftMask | Gdk.ModifierType.AltMask | Gdk.ModifierType.SuperMask);
        if (modifiers != Gdk.ModifierType.ControlMask || Gdk.Functions.KeyvalToLower(keyval) != (uint)Gdk.Constants.KEY_s) return false;
        Observe(SaveAsync());
        return true;
    }

    private bool CanSave => _editor is { IsReady: true } && !_busy && !_editorDisposed;

    private void Refresh()
    {
        _save?.SetSensitive(CanSave);
        _delete?.SetSensitive(!_busy);
    }

    internal async Task SaveAsync()
    {
        if (!CanSave || _editor is not { } editor) return;
        _busy = true;
        Refresh();
        try
        {
            var html = await editor.GetHtmlBodyAsync() ?? string.Empty;
            await ViewModel.SaveAsync(html);
        }
        finally { await Dispatcher.ExecuteOnUIThread(Done); }
    }

    private async Task DeleteAsync()
    {
        if (_busy || !ViewModel.IsExistingTemplate) return;
        _busy = true;
        Refresh();
        try { await ViewModel.DeleteAsync(); }
        finally { await Dispatcher.ExecuteOnUIThread(Done); }
    }

    private void Done()
    {
        _busy = false;
        if (!IsReleased) Refresh();
    }

    private Gtk.Entry Field(WinoSettingsGroup group, string title, string description, WinoIconGlyph glyph, string placeholder, string property,
        Func<CreateEmailTemplatePageViewModel, string?> read, Action<CreateEmailTemplatePageViewModel, string> write)
    {
        var row = WinoSettingsRows.Action(title, description, glyph);
        var entry = WinoSettingsRows.Suffix(row, Settings.TextSuffix(ViewModel, title, placeholder, property, read, write, 24));
        entry.SetHexpand(false);
        row.SetActivatableWidget(entry);
        group.Add(row);
        return entry;
    }
}
