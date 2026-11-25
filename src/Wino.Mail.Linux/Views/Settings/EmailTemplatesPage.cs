using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Settings › Email templates (Windows/macOS EmailTemplatesPage): the intro with New template, then one row per template
/// (name, description and a one-line preview of the body, Edit, Delete with the ViewModel's confirmation), or the empty
/// state. The editor page goes back by recreating this page, so the list is current without a reload hook.
/// </summary>
internal sealed class EmailTemplatesPage(EmailTemplatesPageViewModel viewModel, WinoViewServices services, IMailDialogService dialogs)
    : SettingsPageView<EmailTemplatesPageViewModel>(viewModel, services)
{
    private WinoSettingsGroup? _list;
    private WinoSettingsGroup? _empty;
    private SettingsBinder? _rows;
    private bool _loaded;

#if DEBUG
    private static WeakReference<EmailTemplatesPage>? _debugCurrent;
    internal static EmailTemplatesPage? DebugCurrent => _debugCurrent is { } weak && weak.TryGetTarget(out var page) ? page : null;
#endif

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var intro = WinoSettingsRows.Action(Translator.SettingsEmailTemplates_NewTemplateTitle, Translator.SettingsEmailTemplates_Intro, WinoIconGlyph.Add);
        var create = WinoSettingsRows.Suffix(intro, Settings.Button(Translator.SettingsEmailTemplates_NewTemplateTitle, vm.CreateTemplate, suggested: true, icon: WinoIconGlyph.Add));
        create.SetValign(Gtk.Align.Center);
        create.SetTooltipText(Translator.SettingsEmailTemplates_NewTemplateDescription);
        AddGroup(null, intro);

        _list = Page.AddGroup(Translator.SettingsEmailTemplates_ListTitle);
        _list.Root.SetVisible(false);

        var empty = Adw.StatusPage.New();
        empty.SetTitle(Translator.SettingsEmailTemplates_EmptyTitle);
        empty.SetDescription(Translator.SettingsEmailTemplates_EmptyDescription);
        empty.AddCssClass("compact");
        var action = Settings.Button(Translator.SettingsEmailTemplates_EmptyAction, vm.CreateTemplate, suggested: true);
        action.SetHalign(Gtk.Align.Center);
        WinoA11y.Rename(action, Translator.SettingsEmailTemplates_EmptyAction);
        empty.SetChild(action);
        _empty = Page.AddGroup(null, null, WinoSettingsRows.Custom(empty, $"{Translator.SettingsEmailTemplates_EmptyTitle}. {Translator.SettingsEmailTemplates_EmptyDescription}"));
        _empty.Root.SetVisible(false);

        Settings.Collection(vm.EmailTemplates, RebuildList);
#if DEBUG
        _debugCurrent = new WeakReference<EmailTemplatesPage>(this);
#endif
    }

    protected override async Task NavigateAsync(NavigationMode mode, object? parameter)
    {
        await base.NavigateAsync(mode, parameter);
        await ViewModel.LoadAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased) return;
            _loaded = true;
            RebuildList();
        });
    }

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(DebugCurrent, this)) _debugCurrent = null;
#endif
        return base.DeactivateAsync();
    }

    internal int TemplateRowCount => ViewModel.EmailTemplates.Count;

    private void RebuildList()
    {
        if (_list is null || _empty is null || IsReleased || !_loaded) return;
        _rows?.Scope.Dispose();
        _list.Clear();
        var rows = _rows = Settings.Child();
        var templates = ViewModel.EmailTemplates.Where(static template => template is not null).ToArray();
        _list.Root.SetVisible(templates.Length > 0);
        _empty.Root.SetVisible(templates.Length == 0);
        foreach (var template in templates) _list.Add(Row(rows, template));
    }

    private Adw.ActionRow Row(SettingsBinder rows, EmailTemplate template)
    {
        var name = string.IsNullOrWhiteSpace(template.Name) ? Translator.SettingsEmailTemplates_EditPageTitle : template.Name;
        var preview = HtmlPreviewText.From(template.HtmlContent ?? string.Empty);
        var subtitle = string.IsNullOrWhiteSpace(template.Description) ? preview
            : string.IsNullOrEmpty(preview) ? template.Description : $"{template.Description}\n{preview}";
        var row = WinoSettingsRows.Action(name, subtitle, WinoIconGlyph.Edit);
        row.SetSubtitleLines(2);

        var edit = WinoSettingsRows.Suffix(row, WinoSettingsRows.GlyphButton(WinoIconGlyph.Edit, $"{Translator.Buttons_Edit}: {name}"));
        edit.SetValign(Gtk.Align.Center);
        edit.SetTooltipText(Translator.Buttons_Edit);
        rows.Own(GtkSignals.OnClicked(edit, () => ViewModel.OpenTemplate(template)));
        var delete = WinoSettingsRows.Suffix(row, WinoSettingsRows.GlyphButton(WinoIconGlyph.Delete, $"{Translator.Buttons_Delete}: {name}"));
        delete.SetValign(Gtk.Align.Center);
        delete.SetTooltipText(Translator.Buttons_Delete);
        rows.Own(GtkSignals.OnClicked(delete, () => Observe(ViewModel.DeleteTemplateAsync(template, dialogs))));
        row.SetActivatableWidget(edit);
        return row;
    }
}
