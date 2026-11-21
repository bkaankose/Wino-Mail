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
/// switch. Changes stay in the ViewModel's working copy until Apply. Rows move by drag, Alt+Up / Alt+Down and the
/// Move up / Move down buttons of the drag list.
/// </summary>
internal sealed class ReaderActionsPage(ReaderActionsPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<ReaderActionsPageViewModel>(viewModel, services)
{
    private readonly Dictionary<ReaderCommandLayoutItemViewModel, (WinoDragRow Row, SettingsBinder Binder)> _rows = new(ReferenceEqualityComparer.Instance);
    private WinoDragList? _arrange;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        AddGroup(null,
            Settings.Switch(vm, Translator.SettingsMailRendering_ActionLabels_Title, Translator.SettingsMailRendering_ActionLabels_Description, WinoIconGlyph.ToggleLeft,
                nameof(vm.ShowActionLabels), static s => s.ShowActionLabels, static (s, v) => s.ShowActionLabels = v));

        var arrange = _arrange = new WinoDragList(null, Translator.ReaderActions_ArrangeHeader) { ShowsMoveButtons = true };
        Settings.Own(arrange);
        arrange.Group.Dropped = (_, from, _, to) => Move(from, to);
        var group = Page.AddGroup(Translator.ReaderActions_ArrangeHeader, Translator.ReaderActions_ArrangeDescription);
        group.HidesWhenEmpty = false;
        group.Root.Add(arrange.Root);
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
        if (_arrange is not { } arrange || IsReleased) return;
        var items = ViewModel.Items.ToList();
        var present = new HashSet<ReaderCommandLayoutItemViewModel>(items, ReferenceEqualityComparer.Instance);
        var rows = new List<WinoDragRow>(items.Count);
        foreach (var item in items)
        {
            if (!_rows.TryGetValue(item, out var entry))
            {
                var binder = Settings.Child();
                _rows[item] = entry = (new WinoDragRow(ActionRow(binder, item), item.Title ?? string.Empty), binder);
            }
            rows.Add(entry.Row);
        }
        arrange.SetRows(rows);
        foreach (var stale in _rows.Keys.Where(item => !present.Contains(item)).ToList())
        {
            _rows[stale].Binder.Scope.Dispose();
            _rows.Remove(stale);
        }
    }

    private static Adw.ActionRow ActionRow(SettingsBinder binder, ReaderCommandLayoutItemViewModel item)
    {
        var row = WinoSettingsRows.Action(item.Title ?? string.Empty, null, item.Icon);
        if (item.HasSecondIcon)
        {
            var second = new WinoIcon(item.SecondIcon, WinoSettingsRows.GlyphSize).Root;
            second.SetValign(Gtk.Align.Center);
            row.AddPrefix(second);
        }
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
