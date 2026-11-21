using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Reader actions (Windows ReaderActionsPage): order and visibility of the reader command bar and the action labels
/// switch. Changes stay in the ViewModel's working copy until Apply. Rows move with up and down buttons until the
/// drag list lands.
/// </summary>
internal sealed class ReaderActionsPage(ReaderActionsPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<ReaderActionsPageViewModel>(viewModel, services)
{
    private readonly List<Gtk.Widget> _rows = [];
    private Adw.ExpanderRow? _arrange;
    private SettingsBinder? _rowBinder;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        AddGroup(null,
            Settings.Switch(vm, Translator.SettingsMailRendering_ActionLabels_Title, Translator.SettingsMailRendering_ActionLabels_Description, WinoIconGlyph.ToggleLeft,
                nameof(vm.ShowActionLabels), static s => s.ShowActionLabels, static (s, v) => s.ShowActionLabels = v));

        _arrange = WinoSettingsRows.Expander(Translator.ReaderActions_ArrangeHeader, Translator.ReaderActions_ArrangeDescription, WinoIconGlyph.ReOrderDotsVertical, true);
        AddGroup(null, _arrange);
        Settings.Collection(vm.Items, Rebuild);

        var unsaved = WinoSettingsRows.Caption(Translator.ReaderActions_UnsavedChanges);
        unsaved.SetValign(Gtk.Align.Center);
        Settings.Visible(unsaved, vm, nameof(vm.IsDirty), static s => s.IsDirty);
        var reset = Settings.Button(Translator.ReaderActions_ResetToDefault, vm.ResetToDefaultCommand);
        reset.AddCssClass("flat");
        var bar = Gtk.ActionBar.New();
        bar.PackStart(reset);
        bar.PackStart(unsaved);
        bar.PackEnd(Settings.Button(Translator.Buttons_Apply, vm.ApplyCommand, suggested: true));
        bar.PackEnd(Settings.Button(Translator.Buttons_Discard, vm.DiscardCommand));
        Page.SetBottomBar(bar);
    }

    private void Rebuild()
    {
        if (_arrange is not { } arrange) return;
        _rowBinder?.Scope.Dispose();
        var binder = _rowBinder = Settings.Child();
        foreach (var row in _rows) arrange.Remove(row);
        _rows.Clear();

        var items = ViewModel.Items;
        for (var index = 0; index < items.Count; index++)
        {
            var row = ActionRow(binder, items[index], index, items.Count);
            arrange.AddRow(row);
            _rows.Add(row);
        }
    }

    private Adw.ActionRow ActionRow(SettingsBinder binder, ReaderCommandLayoutItemViewModel item, int index, int count)
    {
        var row = WinoSettingsRows.Action(item.Title ?? string.Empty, null, item.Icon);
        if (item.HasSecondIcon)
        {
            var second = new WinoIcon(item.SecondIcon, WinoSettingsRows.GlyphSize).Root;
            second.SetValign(Gtk.Align.Center);
            row.AddPrefix(second);
        }

        var up = WinoSettingsRows.Suffix(row, WinoSettingsRows.GlyphButton(WinoIconGlyph.ChevronUp, Translator.MailFilters_MoveUp));
        up.SetSensitive(index > 0);
        binder.Own(GtkSignals.OnClicked(up, () => Move(index, index - 1)));
        var down = WinoSettingsRows.Suffix(row, WinoSettingsRows.GlyphButton(WinoIconGlyph.ChevronDown, Translator.MailFilters_MoveDown));
        down.SetSensitive(index < count - 1);
        binder.Own(GtkSignals.OnClicked(down, () => Move(index, index + 1)));
        WinoSettingsRows.Suffix(row, binder.SwitchSuffix(item, item.VisibilityAutomationName, nameof(item.IsVisible),
            static s => s.IsVisible, static (s, v) => s.IsVisible = v));
        return row;
    }

    private void Move(int from, int to)
    {
        var items = ViewModel.Items;
        if (from == to || from < 0 || from >= items.Count || to < 0 || to >= items.Count) return;
        items.Move(from, to);
    }
}
