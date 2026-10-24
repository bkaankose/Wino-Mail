using Android.Views;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Android.Views.Settings.Accounts;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.GeneralAndMail;

/// <summary>
/// Reader actions (Windows ReaderActionsPage, iOS ReaderActionsPageViewController): the order and visibility of the
/// actions above an open message and the action labels switch. A row's reorder menu moves it up or down (the folder
/// customization pattern); changes stay in the ViewModel's working copy until Apply.
/// </summary>
public sealed class ReaderActionsPageFragment : SettingsPageFragment<ReaderActionsPageViewModel>
{
    private LinearLayout? _rows;
    private BindingScope _rowScope = new();

    protected override void BuildPage()
    {
        var vm = VM;

        AddGroup(null,
            Card(Translator.SettingsMailRendering_ActionLabels_Title, Translator.SettingsMailRendering_ActionLabels_Description, WinoIconGlyph.ToggleLeft,
                Bind.Switch(vm, nameof(vm.ShowActionLabels), s => s.ShowActionLabels, (s, v) => s.ShowActionLabels = v, Translator.SettingsMailRendering_ActionLabels_Title)));

        _rows = AccountSettingsParts.VColumn(Ctx);
        Bindings.Own(new ActionDisposable(() => _rowScope.Dispose()));
        Bind.Collection(vm.Items, RebuildRows);

        var arrange = Expander(Translator.ReaderActions_ArrangeHeader, Translator.ReaderActions_ArrangeDescription, WinoIconGlyph.ReOrderDotsVertical, null);
        arrange.IsExpanded = true;
        arrange.Add(_rows, 0, 0, 0, 0);
        AddGroup(null, arrange);

        var unsaved = Caption(Translator.ReaderActions_UnsavedChanges);
        Bind.Visible(unsaved, vm, nameof(vm.IsDirty), s => s.IsDirty);
        AddGroup(null,
            ActionCard(Translator.Buttons_Apply, vm.ApplyCommand),
            ActionCard(Translator.Buttons_Discard, vm.DiscardCommand),
            ActionCard(Translator.ReaderActions_ResetToDefault, vm.ResetToDefaultCommand));
        Add(unsaved);
    }

    /// <summary>Rows bind to their items; a changed collection rebuilds them under a fresh scope.</summary>
    private void RebuildRows()
    {
        if (_rows is null) return;
        _rowScope.Dispose();
        _rowScope = new BindingScope();
        var rows = Bind.Child(_rowScope);
        _rows.RemoveAllViews();
        var items = VM.Items;
        for (int index = 0; index < items.Count; index++)
            _rows.AddView(ActionRow(rows, items[index], index, items.Count), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
    }

    /// <summary>One action row: hairline above, the glyph (both for a toggle), the title, the "show in bar" switch and the reorder menu.</summary>
    private View ActionRow(SettingsBinder rows, ReaderCommandLayoutItemViewModel item, int index, int count)
    {
        var host = AccountSettingsParts.VColumn(Ctx);
        var separator = new View(Ctx);
        separator.SetBackgroundColor(WinoColors.CardStroke(Ctx));
        host.AddView(separator, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(Ctx, Resource.Dimension.wino_space_hairline)));

        var row = AccountSettingsParts.HRow(Ctx);
        row.SetMinimumHeight(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_touch_large));

        var icons = AccountSettingsParts.HRow(Ctx);
        icons.AddView(AccountSettingsParts.Glyph(Ctx, item.Icon, Resource.Dimension.wino_glyph_m));
        if (item.HasSecondIcon)
            icons.AddView(AccountSettingsParts.Glyph(Ctx, item.SecondIcon, Resource.Dimension.wino_glyph_m), AccountSettingsParts.RowItem(Ctx, marginStart: Resource.Dimension.wino_space_xs));
        row.AddView(icons);

        var title = AccountSettingsParts.Label(Ctx, item.Title, WinoTextStyle.Body, maxLines: 1);
        row.AddView(title, AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: Resource.Dimension.wino_space_m));

        row.AddView(rows.Switch(item, nameof(item.IsVisible), s => s.IsVisible, (s, v) => s.IsVisible = v, item.VisibilityAutomationName));
        rows.Bind(item, nameof(item.IsVisible), s => s.IsVisible, visible =>
        {
            icons.Alpha = visible ? 1f : 0.55f;
            title.Alpha = visible ? 1f : 0.55f;
        });

        var reorder = AccountSettingsParts.GlyphButton(Ctx, WinoIconGlyph.ArrowSort, $"{Translator.SettingsReorderAccounts_Title}, {item.Title}");
        reorder.Enabled = count > 1;
        rows.Views.Click(reorder, () =>
        {
            var entries = new List<SettingsMenuEntry>();
            if (index > 0) entries.Add(new(Translator.MailFilters_MoveUp, () => VM.MoveItem(item, -1)));
            if (index < count - 1) entries.Add(new(Translator.MailFilters_MoveDown, () => VM.MoveItem(item, 1)));
            if (entries.Count > 0) AccountSettingsParts.ShowMenu(Ctx, reorder, entries, ReportError);
        });
        row.AddView(reorder);

        host.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return host;
    }
}
