using System.Collections.Specialized;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Reader actions (Windows ReaderActionsPage): the order and visibility of the command bar above an open message and the
/// action labels switch. Rows reorder by drag; changes stay in the ViewModel's working copy until Apply.
/// </summary>
public sealed class ReaderActionsPageViewController(ReaderActionsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<ReaderActionsPageViewModel>(viewModel, dispatcher, logger)
{
    private const double RowHeight = 44;
    private const double RowSpacing = 4;
    private const string ActionsKey = "actions";

    private readonly SettingsDragGroup _group = new();
    private BindingScope _rowScope = new();

    protected override void BuildPage()
    {
        var vm = ViewModel;

        AddGroup(null,
            Card(Translator.SettingsMailRendering_ActionLabels_Title, Translator.SettingsMailRendering_ActionLabels_Description, WinoIconGlyph.ToggleLeft,
                Bind.Switch(vm, nameof(vm.ShowActionLabels), s => s.ShowActionLabels, (s, v) => s.ShowActionLabels = v, Translator.SettingsMailRendering_ActionLabels_Title)));

        _group.Dropped = (_, from, _, target) =>
        {
            if (from == target || from < 0 || from >= vm.Items.Count || target < 0 || target >= vm.Items.Count) return;
            vm.Items.Move(from, target);
        };
        var list = new SettingsDragList<ReaderCommandLayoutItemViewModel>(ActionsKey, vm.Items, _group, (item, _) => ActionRow(item), RowHeight, RowSpacing);
        NotifyCollectionChangedEventHandler changed = (_, _) => _ = Dispatcher.ExecuteOnUIThread(() => ReloadRows(list));
        vm.Items.CollectionChanged += changed;
        Bindings.Own(new ActionDisposable(() => { vm.Items.CollectionChanged -= changed; _rowScope.Dispose(); }));
        ReloadRows(list);

        var arrange = Expander(Translator.ReaderActions_ArrangeHeader, Translator.ReaderActions_ArrangeDescription, WinoIconGlyph.ReOrderDotsVertical, null);
        arrange.IsExpanded = true;
        arrange.Add(list, 8, 12, 8, 12);
        AddGroup(null, arrange);

        var unsaved = Caption(Translator.ReaderActions_UnsavedChanges);
        Bind.Visible(unsaved, vm, nameof(vm.IsDirty), s => s.IsDirty);
        var buttons = Row(
            Bind.Button(Translator.Buttons_Apply, vm.ApplyCommand, primary: true),
            Bind.Button(Translator.Buttons_Discard, vm.DiscardCommand),
            Bind.Button(Translator.ReaderActions_ResetToDefault, vm.ResetToDefaultCommand),
            unsaved);
        buttons.Alignment = NSLayoutAttribute.CenterY;
        Add(buttons);
    }

    /// <summary>Rows bind to their items; a changed collection rebuilds them under a fresh scope.</summary>
    private void ReloadRows(SettingsDragList<ReaderCommandLayoutItemViewModel> list)
    {
        _rowScope.Dispose();
        _rowScope = new BindingScope();
        list.Reload();
    }

    /// <summary>A raised row: drag handle, the action's glyph (both for a toggle), its title and the "show in bar" switch.</summary>
    private NSView ActionRow(ReaderCommandLayoutItemViewModel item)
    {
        var rows = Bind.Child(_rowScope);
        var surface = new WinoSurfaceView
        {
            Fill = SettingsRowParts.RowFill,
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        var handle = new WinoIconView(WinoIconGlyph.ReOrderDotsVertical, 14, WinoStyle.TertiaryText);
        var icons = WinoLayout.HStack(6, new WinoIconView(item.Icon, 18, WinoStyle.PrimaryText));
        if (item.HasSecondIcon) icons.AddArrangedSubview(new WinoIconView(item.SecondIcon, 18, WinoStyle.PrimaryText));
        var title = WinoStyle.Label(item.Title, WinoStyle.Body, WinoStyle.PrimaryText);
        title.LineBreakMode = NSLineBreakMode.TruncatingTail;
        title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        title.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var toggle = rows.Switch(item, nameof(item.IsVisible), s => s.IsVisible, (s, v) => s.IsVisible = v, item.VisibilityAutomationName);
        rows.Bind(item, nameof(item.IsVisible), s => s.IsVisible, visible =>
        {
            icons.AlphaValue = visible ? 1 : 0.55f;
            title.AlphaValue = visible ? 1 : 0.55f;
        });

        var content = WinoLayout.HStack(WinoStyle.Space2, handle, icons, title, toggle);
        content.Alignment = NSLayoutAttribute.CenterY;
        WinoLayout.Fill(content, surface, 0, 12, 0, 12);
        surface.AccessibilityLabel = item.Title;
        return surface;
    }
}
