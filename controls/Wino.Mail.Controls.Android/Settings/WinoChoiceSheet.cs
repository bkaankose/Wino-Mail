using Android.Content;
using Android.Views;
using Android.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.RadioButton;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Settings;

/// <summary>
/// A single-choice picker as a Material 3 modal bottom sheet (the Settings dropdowns and form pickers): an optional
/// title, then one radio row per item; choosing a row closes the sheet. Sizes and text come from the
/// Presentation.Android tokens.
/// </summary>
public static class WinoChoiceSheet
{
    public static void Show(Context context, string? title, IReadOnlyList<string> items, int selected, Action<int> chosen)
    {
        if (items.Count == 0) return;
        var sheet = new BottomSheetDialog(context);
        var list = new LinearLayout(context) { Orientation = Orientation.Vertical };
        WinoTokens.Pad(list, 0, Resource.Dimension.wino_space_s, 0, Resource.Dimension.wino_space_2xl);
        if (!string.IsNullOrWhiteSpace(title))
        {
            var heading = WinoTokens.Style(new TextView(context) { Text = title }, WinoTextStyle.Subheader);
            heading.SetTextColor(WinoColors.AccentText(context));
            WinoTokens.Pad(heading, Resource.Dimension.wino_settings_subheader_horizontal, Resource.Dimension.wino_settings_subheader_top,
                Resource.Dimension.wino_settings_subheader_horizontal, Resource.Dimension.wino_settings_subheader_bottom);
            AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(heading, true);
            list.AddView(heading);
        }
        for (int index = 0; index < items.Count; index++)
        {
            int position = index;
            var row = WinoTokens.Style(new MaterialRadioButton(context) { Text = items[index] ?? string.Empty, Checked = index == selected }, WinoTextStyle.ItemTitle);
            WinoMaterialAccent.Tint(row);
            row.SetMinHeight(WinoTokens.Px(context, Resource.Dimension.wino_sheet_item_min_height));
            WinoTokens.Pad(row, Resource.Dimension.wino_settings_item_padding_horizontal, 0, Resource.Dimension.wino_settings_item_padding_horizontal, 0);
            row.Click += (_, _) =>
            {
                sheet.Dismiss();
                chosen(position);
            };
            list.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }
        var scroll = new ScrollView(context);
        scroll.AddView(list);
        sheet.SetContentView(scroll);
        sheet.Show();
    }
}
