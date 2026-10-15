using Android.Content;
using Android.Views;
using Android.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.Controls.Android.Common;
using Wino.Presentation.Android;
using PopupMenu = AndroidX.AppCompat.Widget.PopupMenu;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// The filter chip (canvas boards FilterSheet and Inbox, decision A8): the shared filter and sorting options in a bottom
/// sheet on compact widths (Cancel and Apply), and in a popup menu anchored to the chip on wider windows, as on Windows.
/// The ViewModel's commands apply them.
/// </summary>
public sealed partial class MailListFragment
{
    private void ShowFilters(View anchor)
    {
        if (Context is not { } context || ViewModel is null) return;
        if (IsCompact) ShowFilterSheet(context);
        else ShowFilterMenu(context, anchor);
    }

    private void ShowFilterMenu(Context context, View anchor)
    {
        if (ViewModel is not { } viewModel) return;
        var popup = new PopupMenu(context, anchor);
        var filters = viewModel.FilterOptions.ToArray();
        var sorting = viewModel.SortingOptions.ToArray();
        for (int index = 0; index < filters.Length; index++)
        {
            var item = popup.Menu.Add(1, index, index, filters[index].Title);
            item?.SetChecked(ReferenceEquals(filters[index], viewModel.SelectedFilterOption));
        }
        popup.Menu.SetGroupCheckable(1, true, true);
        for (int index = 0; index < sorting.Length; index++)
        {
            var item = popup.Menu.Add(2, 100 + index, 100 + index, sorting[index].Title);
            item?.SetChecked(ReferenceEquals(sorting[index], viewModel.SelectedSortingOption));
        }
        popup.Menu.SetGroupCheckable(2, true, true);
        popup.MenuItemClick += (_, args) =>
        {
            if (args.Item?.ItemId is not int id) return;
            if (id < 100 && id < filters.Length) Observe(viewModel.SelectedFilterChangedCommand.ExecuteAsync(filters[id]));
            else if (id >= 100 && id - 100 < sorting.Length) Observe(viewModel.SelectedSortingChangedCommand.ExecuteAsync(sorting[id - 100]));
        };
        popup.Show();
    }

    private void ShowFilterSheet(Context context)
    {
        if (ViewModel is not { } viewModel) return;
        var sheet = new BottomSheetDialog(context);
        int pad = WinoUnits.Dp(context, 16);
        var column = new LinearLayout(context) { Orientation = Orientation.Vertical };
        column.SetPadding(pad, pad, pad, pad);
        var title = new TextView(context) { Text = Translator.Accessibility_FilterMessages };
        title.SetTextAppearance(Resource.Style.TextAppearance_Wino_RowTitle);
        title.TextSize = 18;
        column.AddView(title);

        FilterOption? filter = viewModel.SelectedFilterOption;
        SortingOption? sort = viewModel.SelectedSortingOption;
        Section(context, column, Translator.SearchBar_Filters, viewModel.FilterOptions.Select(static option => (object)option).ToArray(), filter,
            static option => ((FilterOption)option).Title, option => filter = (FilterOption)option);
        Section(context, column, Translator.ToDoPage_SortBy, viewModel.SortingOptions.Select(static option => (object)option).ToArray(), sort,
            static option => ((SortingOption)option).Title, option => sort = (SortingOption)option);

        var buttons = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        buttons.SetGravity(GravityFlags.End);
        var cancel = new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle) { Text = Translator.Buttons_Cancel };
        cancel.Click += (_, _) => sheet.Dismiss();
        var apply = new MaterialButton(context) { Text = Translator.Buttons_Apply };
        apply.Click += (_, _) =>
        {
            sheet.Dismiss();
            if (filter is not null && !ReferenceEquals(filter, viewModel.SelectedFilterOption)) Observe(viewModel.SelectedFilterChangedCommand.ExecuteAsync(filter));
            if (sort is not null && !ReferenceEquals(sort, viewModel.SelectedSortingOption)) Observe(viewModel.SelectedSortingChangedCommand.ExecuteAsync(sort));
        };
        buttons.AddView(cancel);
        buttons.AddView(apply);
        var buttonParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = pad };
        column.AddView(buttons, buttonParams);
        sheet.SetContentView(column);
        sheet.Show();
    }

    private static void Section(Context context, LinearLayout column, string header, object[] options, object? selected, Func<object, string> title, Action<object> chosen)
    {
        var label = new TextView(context) { Text = header };
        label.SetTextAppearance(Resource.Style.TextAppearance_Wino_Caption);
        label.SetPadding(0, WinoUnits.Dp(context, 16), 0, WinoUnits.Dp(context, 6));
        column.AddView(label);
        var group = new ChipGroup(context) { SingleSelection = true, SelectionRequired = true };
        foreach (var option in options)
        {
            var chip = WinoChips.Filter(context, title(option), ReferenceEquals(option, selected));
            chip.Id = View.GenerateViewId();
            var captured = option;
            chip.CheckedChange += (_, args) => { if (args.IsChecked) chosen(captured); };
            group.AddView(chip);
        }
        column.AddView(group);
    }
}
