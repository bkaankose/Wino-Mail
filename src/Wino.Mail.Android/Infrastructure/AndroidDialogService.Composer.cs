using System.Globalization;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using Google.Android.Material.Button;
using Google.Android.Material.CheckBox;
using Google.Android.Material.Dialog;
using Google.Android.Material.TextField;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// The reader's and composer's dialogs of the shared contract: the question before an action card opens the sender's
/// address, the checks before sending, and the description and size of a picture (Windows ActionCardOpenDialog,
/// DraftCheckDialog, PictureDescriptionDialog and PictureResizeDialog).
/// </summary>
public sealed partial class AndroidDialogService
{
    private const int MaximumPictureSide = 4000;

    public async Task<ActionCardOpenConfirmation> ShowActionCardOpenConfirmationAsync(string actionName, string senderName, Uri address)
    {
        MaterialCheckBox? dontAsk = null;
        var confirmed = await AlertAsync(
            string.Format(CultureInfo.CurrentCulture, Translator.ActionCardOpenDialog_TitleFormat, actionName),
            string.Format(CultureInfo.CurrentCulture, Translator.ActionCardOpenDialog_MessageFormat, senderName),
            Translator.Buttons_Open, Translator.Buttons_Cancel,
            content: context =>
            {
                var body = Body(context);
                body.AddView(AddressView(context, address));
                dontAsk = new MaterialCheckBox(context) { Text = Translator.ActionCardOpenDialog_DontAskAgain };
                body.AddView(dontAsk);
                return body;
            }) == 0;

        return new ActionCardOpenConfirmation(confirmed, confirmed && dontAsk is { Checked: true });
    }

    public Task<DraftCheckDialogResult> ShowDraftCheckDialogAsync(IReadOnlyList<DraftCheckIssueItem> issues)
        => PresentAsync<DraftCheckDialogResult>((activity, completion) =>
        {
            AlertDialog? dialog = null;
            void Choose(DraftCheckDialogResult result)
            {
                completion.TrySetResult(result);
                dialog?.Dismiss();
            }

            var body = Body(activity);
            var message = new TextView(activity)
            {
                Text = issues.Count == 1
                    ? Translator.DraftCheckDialog_MessageOne
                    : string.Format(CultureInfo.CurrentCulture, Translator.DraftCheckDialog_MessageFormat, issues.Count),
            };
            message.SetTextColor(WinoColors.SecondaryText(activity));
            body.AddView(message, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(activity, 12) });
            for (var index = 0; index < issues.Count; index++)
                body.AddView(IssueRow(activity, issues[index], index, Choose), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(activity, 8) });

            var scroll = new ScrollView(activity);
            scroll.AddView(body);

            dialog = new MaterialAlertDialogBuilder(activity)
                .SetTitle(Translator.DraftCheckDialog_Title)
                .SetView(scroll)
                .SetPositiveButton(Translator.DraftCheckDialog_SendAnyway, (_, _) => completion.TrySetResult(new DraftCheckDialogResult(DraftCheckDialogChoice.SendAnyway)))
                .SetNegativeButton(Translator.DraftCheckDialog_GoBack, (_, _) => completion.TrySetResult(new DraftCheckDialogResult(DraftCheckDialogChoice.GoBack)))
                .Create();
            return dialog;
        }, new DraftCheckDialogResult(DraftCheckDialogChoice.GoBack));

    public Task<string?> ShowPictureDescriptionDialogAsync(string? currentDescription)
        => PresentAsync<string?>((activity, completion) =>
        {
            var layout = new TextInputLayout(activity) { Hint = Translator.PictureDescriptionDialog_Placeholder };
            var field = new TextInputEditText(activity) { Text = currentDescription ?? string.Empty };
            field.SetMinLines(3);
            field.SetMaxLines(6);
            field.InputType = InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences;
            field.ContentDescription = Translator.PictureDescriptionDialog_Placeholder;
            layout.AddView(field);

            var body = Body(activity);
            body.AddView(layout);

            return new MaterialAlertDialogBuilder(activity)
                .SetTitle(Translator.PictureDescriptionDialog_Title)
                .SetMessage(Translator.PictureDescriptionDialog_Message)
                .SetView(body)
                .SetPositiveButton(Translator.Buttons_Save, (_, _) => completion.TrySetResult((field.Text ?? string.Empty).Trim()))
                .SetNegativeButton(Translator.Buttons_Cancel, (_, _) => completion.TrySetResult(null))
                .Create();
        }, null);

    public Task<PictureSize?> ShowPictureResizeDialogAsync(PictureSize currentSize)
        => PresentAsync<PictureSize?>((activity, completion) =>
        {
            var ratio = currentSize.Height > 0 ? (double)currentSize.Width / currentSize.Height : 1;
            var width = PictureSizeField(activity, Translator.ImageResizeDialog_Width, currentSize.Width);
            var height = PictureSizeField(activity, Translator.ImageResizeDialog_Height, currentSize.Height);
            var keepProportions = new MaterialCheckBox(activity) { Text = Translator.ImageResizeDialog_KeepProportions, Checked = true };
            var syncing = false;

            void Follow(TextInputEditText source, TextInputEditText target, Func<int, int> scale)
            {
                if (syncing || !keepProportions.Checked || !TryParseSide(source.Text, out var value)) return;
                syncing = true;
                target.Text = Math.Clamp(scale(value), 1, MaximumPictureSide).ToString(CultureInfo.InvariantCulture);
                syncing = false;
            }
            width.Field.TextChanged += (_, _) => Follow(width.Field, height.Field, value => (int)Math.Round(value / ratio));
            height.Field.TextChanged += (_, _) => Follow(height.Field, width.Field, value => (int)Math.Round(value * ratio));

            var body = Body(activity);
            body.AddView(width.Layout);
            body.AddView(height.Layout);
            body.AddView(keepProportions);

            AlertDialog? dialog = null;
            dialog = new MaterialAlertDialogBuilder(activity)
                .SetTitle(Translator.ImageResizeDialog_Title)
                .SetView(body)
                .SetPositiveButton(Translator.ImageResizeDialog_Apply, (_, _) => { })
                .SetNegativeButton(Translator.Buttons_Cancel, (_, _) => completion.TrySetResult(null))
                .Create();
            dialog.ShowEvent += (_, _) => dialog.GetButton((int)DialogButtonType.Positive)?.SetOnClickListener(new ClickListener(() =>
            {
                if (!TryParseSide(width.Field.Text, out var newWidth) || !TryParseSide(height.Field.Text, out var newHeight)) return;
                completion.TrySetResult(new PictureSize(newWidth, newHeight));
                dialog.Dismiss();
            }));
            return dialog;
        }, null);

    private static View AddressView(Context context, Uri address)
    {
        var host = address.IsAbsoluteUri ? address.IdnHost : address.OriginalString;
        var path = address.IsAbsoluteUri ? address.PathAndQuery + address.Fragment : string.Empty;
        var text = new SpannableString(host + path);
        text.SetSpan(new StyleSpan(TypefaceStyle.Bold), 0, host.Length, SpanTypes.ExclusiveExclusive);

        var view = new TextView(context) { TextFormatted = text, Typeface = Typeface.Monospace };
        view.SetTextIsSelectable(true);
        view.SetPadding(Dp(context, 12), Dp(context, 8), Dp(context, 12), Dp(context, 8));
        view.Background = Surface(context);
        view.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(context, 12) };
        return view;
    }

    private static LinearLayout IssueRow(Context context, DraftCheckIssueItem item, int index, Action<DraftCheckDialogResult> choose)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Vertical };
        row.SetPadding(Dp(context, 12), Dp(context, 10), Dp(context, 12), Dp(context, 10));
        row.Background = Surface(context);

        var title = new TextView(context) { Text = item.Title };
        title.SetTypeface(title.Typeface, TypefaceStyle.Bold);
        row.AddView(title);

        var detail = new TextView(context) { Text = item.Detail };
        detail.SetTextColor(WinoColors.SecondaryText(context));
        detail.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
        row.AddView(detail);

        var buttons = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        buttons.SetGravity(GravityFlags.End);
        buttons.AddView(IssueButton(context, Translator.DraftCheckDialog_Show, item.CanShow, false, () => choose(new DraftCheckDialogResult(DraftCheckDialogChoice.IssueAction, index, DraftCheckIssueAction.Show))));
        buttons.AddView(IssueButton(context, Translator.DraftCheckDialog_Describe, item.CanDescribe, false, () => choose(new DraftCheckDialogResult(DraftCheckDialogChoice.IssueAction, index, DraftCheckIssueAction.Describe))));
        buttons.AddView(IssueButton(context, Translator.DraftCheckDialog_NotNeeded, item.CanMarkNotNeeded, false, () => choose(new DraftCheckDialogResult(DraftCheckDialogChoice.IssueAction, index, DraftCheckIssueAction.NotNeeded))));
        buttons.AddView(IssueButton(context, Translator.DraftCheckDialog_Fix, item.CanFix, true, () => choose(new DraftCheckDialogResult(DraftCheckDialogChoice.IssueAction, index, DraftCheckIssueAction.Fix))));
        var scroll = new HorizontalScrollView(context) { HorizontalScrollBarEnabled = false };
        scroll.AddView(buttons);
        row.AddView(scroll);
        return row;
    }

    private static MaterialButton IssueButton(Context context, string text, bool visible, bool primary, Action action)
    {
        var button = SettingsBinder.CreateButton(context, text, outlined: !primary);
        button.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        button.Click += (_, _) => action();
        return button;
    }

    private static (TextInputLayout Layout, TextInputEditText Field) PictureSizeField(Context context, string hint, int value)
    {
        var layout = new TextInputLayout(context) { Hint = hint };
        var field = new TextInputEditText(context) { Text = value.ToString(CultureInfo.InvariantCulture) };
        field.InputType = InputTypes.ClassNumber;
        field.SetSingleLine(true);
        layout.AddView(field);
        layout.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(context, 8) };
        return (layout, field);
    }

    private static bool TryParseSide(string? text, out int value)
        => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value is >= 1 and <= MaximumPictureSide;

    private static GradientDrawable Surface(Context context)
    {
        var surface = new GradientDrawable();
        surface.SetColor(WinoColors.SubtleFill(context));
        surface.SetCornerRadius(Dp(context, 8));
        return surface;
    }

    private sealed class ClickListener(Action onClick) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? view) => onClick();
    }
}
