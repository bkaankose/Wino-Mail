using Android.Content;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Dialog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace Wino.Mail.Android.Views.Onboarding;

/// <summary>The Windows flyouts of <see cref="AccountSetupPageFragment"/> as Material dialogs: the account colour and the provider picker.</summary>
public sealed partial class AccountSetupPageFragment
{
    /// <summary>The account colours as round swatches; Clear color while one is chosen (the Windows colour flyout).</summary>
    private void ShowColorPicker(AccountSetupPageViewModel viewModel)
    {
        if (Context is not { } context) return;
        int Dp(double value) => WinoUnits.Dp(context, value);
        var flow = new WinoFlowView(context) { Spacing = Dp(12), LineSpacing = Dp(12) };
        flow.SetPadding(Dp(24), Dp(8), Dp(24), 0);
        AlertDialog? dialog = null;
        var size = context.Resources?.GetDimensionPixelSize(Resource.Dimension.onboarding_swatch_size) ?? Dp(36);
        foreach (var color in viewModel.AvailableColors)
        {
            var fill = WinoColors.FromHexString(color.Hex) ?? WinoColors.SubtleFill(context);
            var circle = new GradientDrawable();
            circle.SetShape(ShapeType.Oval);
            circle.SetColor(fill);
            var selected = string.Equals(viewModel.SelectedColor?.Hex, color.Hex, StringComparison.OrdinalIgnoreCase);
            circle.SetStroke(Dp(selected ? 3 : 1), selected ? WinoColors.Accent(context) : WinoColors.ZoneStroke(context));
            var swatch = new View(context)
            {
                Background = circle,
                ContentDescription = string.Format(Translator.Accessibility_ColorOption, color.Hex),
                Clickable = true,
                Focusable = true,
                Selected = selected
            };
            var chosen = color;
            swatch.Click += (_, _) =>
            {
                viewModel.SelectedColor = chosen;
                dialog?.Dismiss();
            };
            flow.AddView(swatch, new ViewGroup.MarginLayoutParams(size, size));
        }

        var builder = new MaterialAlertDialogBuilder(context)
            .SetTitle(Translator.AccountDetailsPage_ColorPicker_Title)!
            .SetView(flow)!
            .SetNegativeButton(Translator.Buttons_Cancel, (IDialogInterfaceOnClickListener?)null)!;
        if (viewModel.IsColorSelected)
            builder.SetNeutralButton(Translator.ProviderSelection_ClearColor, (_, _) => Run(viewModel.SelectColorCommand));
        dialog = builder.Show();
    }

    /// <summary>"Where is your mailbox?": every provider with its mark and description; the current one is selected.</summary>
    private void ShowProviderPicker(AccountSetupPageViewModel viewModel)
    {
        if (Context is not { } context) return;
        int Dp(double value) => WinoUnits.Dp(context, value);
        var column = new LinearLayout(context) { Orientation = Orientation.Vertical };
        column.SetPadding(0, Dp(4), 0, 0);
        var caption = new TextView(context) { Text = viewModel.ProviderPickerCaption };
        caption.SetTextAppearance(Resource.Style.TextAppearance_Wino_ListSupporting);
        caption.SetPadding(Dp(24), 0, Dp(24), Dp(8));
        column.AddView(caption);

        AlertDialog? dialog = null;
        var current = viewModel.CurrentProviderChoice;
        foreach (var choice in viewModel.ProviderChoices)
        {
            var row = new LinearLayout(context)
            {
                Orientation = Orientation.Horizontal,
                Clickable = true,
                Focusable = true,
                ContentDescription = choice.AutomationName,
                Selected = ReferenceEquals(choice, current)
            };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetMinimumHeight(Dp(56));
            row.SetPadding(Dp(24), Dp(4), Dp(24), Dp(4));
            var states = new StateListDrawable();
            states.AddState([global::Android.Resource.Attribute.StateSelected], new ColorDrawable(WinoColors.SelectedFill(context)));
            states.AddState([], new ColorDrawable(global::Android.Graphics.Color.Transparent));
            row.Background = new LayerDrawable([states, context.GetDrawable(global::Android.Resource.Drawable.ListSelectorBackground) ?? new ColorDrawable()]);

            var glyph = new WinoIconView(context) { Icon = choice.Glyph, GlyphSize = 20, Colorful = null };
            row.AddView(glyph, new LinearLayout.LayoutParams(Dp(24), Dp(24)) { MarginEnd = Dp(16) });
            var texts = new LinearLayout(context) { Orientation = Orientation.Vertical };
            row.AddView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
            var title = new TextView(context) { Text = choice.Title };
            title.SetTextAppearance(Resource.Style.TextAppearance_Wino_ListTitle);
            texts.AddView(title);
            if (choice.HasDescription)
            {
                var description = new TextView(context) { Text = choice.Description };
                description.SetTextAppearance(Resource.Style.TextAppearance_Wino_Caption);
                description.SetTextColor(WinoColors.TertiaryText(context));
                texts.AddView(description);
            }
            var picked = choice;
            row.Click += (_, _) =>
            {
                dialog?.Dismiss();
                Run(viewModel.ChangeProviderCommand, picked);
            };
            column.AddView(row);
        }

        var scroll = new ScrollView(context);
        scroll.AddView(column);
        dialog = new MaterialAlertDialogBuilder(context)
            .SetTitle(Translator.AccountSetup_ProviderPicker_Title)!
            .SetView(scroll)!
            .SetNegativeButton(Translator.Buttons_Cancel, (IDialogInterfaceOnClickListener?)null)!
            .Show();
    }
}
