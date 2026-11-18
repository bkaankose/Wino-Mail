using Android.Content;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.BottomSheet;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Presentation.Android;
using Color = Android.Graphics.Color;
using ColorStateList = Android.Content.Res.ColorStateList;

namespace Wino.Mail.Android.Views.Mail.Compose;

/// <summary>
/// "Insert template" (the iOS and Mac template pickers, Windows EmailTemplatesComboBox): the stored templates as Material
/// rows, name first and the description under it when there is one, in a modal bottom sheet. A tap closes the sheet and
/// reports the template.
/// </summary>
internal static class ComposeTemplateSheet
{
    public static void Show(Context context, IReadOnlyList<EmailTemplate> templates, Action<EmailTemplate> picked)
    {
        if (templates.Count == 0) return;

        var sheet = new BottomSheetDialog(context);
        var content = LayoutInflater.From(context)?.Inflate(Resource.Layout.compose_template_sheet, null, false)
            ?? throw new InvalidOperationException("compose_template_sheet.xml did not inflate.");
        content.Require<TextView>(Resource.Id.template_title).Text = Translator.Composer_EmailTemplatesPlaceholder;
        var list = content.Require<LinearLayout>(Resource.Id.template_list);
        foreach (var template in templates)
        {
            list.AddView(Row(context, template, () =>
            {
                sheet.Dismiss();
                picked(template);
            }));
        }

        sheet.SetContentView(content);
        sheet.Behavior.State = BottomSheetBehavior.StateExpanded;
        sheet.Behavior.SkipCollapsed = true;
        sheet.Show();
    }

    private static View Row(Context context, EmailTemplate template, Action activate)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Vertical, Clickable = true, Focusable = true };
        WinoTokens.Pad(row, Resource.Dimension.wino_settings_item_padding_horizontal, Resource.Dimension.wino_space_s,
            Resource.Dimension.wino_settings_item_padding_horizontal, Resource.Dimension.wino_space_s);
        row.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_sheet_item_min_height));
        row.Foreground = new RippleDrawable(ColorStateList.ValueOf(WinoColors.SubtleFill(context)), null, new ColorDrawable(Color.White));

        var name = WinoTokens.Style(new TextView(context) { Text = template.Name ?? string.Empty, Ellipsize = TextUtils.TruncateAt.End }, WinoTextStyle.ItemTitle);
        name.SetMaxLines(1);
        row.AddView(name);

        if (!string.IsNullOrWhiteSpace(template.Description))
        {
            var description = WinoTokens.Style(new TextView(context) { Text = template.Description, Ellipsize = TextUtils.TruncateAt.End }, WinoTextStyle.ItemSupporting);
            description.SetMaxLines(2);
            row.AddView(description);
        }

        row.Click += (_, _) => activate();
        row.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        return row;
    }
}
