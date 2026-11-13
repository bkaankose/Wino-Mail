using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content.Resources;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.ViewModels.Attachments;
using Wino.Presentation.Android;
using TooltipCompat = AndroidX.AppCompat.Widget.TooltipCompat;

namespace Wino.Mail.Android.Views;

/// <summary>
/// Builds the attachment tile (attachment_tile.xml) that the reader, the composer and the calendar pages share. Android
/// has no system file icons, so the type is a rounded badge with the extension on the shared accent color.
/// </summary>
internal static class AttachmentTiles
{
    public static View Create(Context context, ViewGroup parent, AttachmentViewModelBase attachment, Action? open = null, Action? remove = null, string? removeDescription = null)
    {
        var tile = LayoutInflater.From(context)?.Inflate(Resource.Layout.attachment_tile, parent, false)
            ?? throw new InvalidOperationException("attachment_tile.xml did not inflate.");
        var fileName = attachment.FileName ?? string.Empty;

        ApplyBadge(context, tile.Require<TextView>(Resource.Id.attachment_badge), attachment);
        tile.Require<TextView>(Resource.Id.attachment_name).Text = fileName;
        tile.Require<TextView>(Resource.Id.attachment_details).Text = attachment.DetailsText;
        WinoIcons.Apply(tile.Require<TextView>(Resource.Id.attachment_warning), WinoIconGlyph.Warning, WinoTokens.Dp(context, Resource.Dimension.wino_attachment_glyph_warning));
        ApplyWarning(tile, attachment);

        var removeButton = tile.Require<ImageButton>(Resource.Id.attachment_remove);
        if (remove is not null)
        {
            removeButton.Visibility = ViewStates.Visible;
            removeButton.ContentDescription = removeDescription ?? $"{Translator.Buttons_Remove} {fileName}";
            removeButton.SetImageDrawable(WinoIcons.Drawable(context, WinoIconGlyph.Dismiss, WinoTokens.Dp(context, Resource.Dimension.wino_attachment_glyph_remove), WinoColors.SecondaryText(context)));
            TooltipCompat.SetTooltipText(removeButton, Translator.Buttons_Remove);
            removeButton.Click += (_, _) => remove();
        }
        else
        {
            tile.SetPaddingRelative(tile.PaddingStart, tile.PaddingTop, WinoTokens.Px(context, Resource.Dimension.wino_attachment_tile_padding_end_plain), tile.PaddingBottom);
        }

        if (open is not null)
        {
            tile.Focusable = true;
            tile.Click += (_, _) => open();
        }

        tile.ContentDescription = $"{fileName}, {attachment.DetailsText}";
        return tile;
    }

    /// <summary>Shows or hides the content-type warning (glyph and caution outline) after an inspection finishes.</summary>
    public static void ApplyWarning(View tile, AttachmentViewModelBase attachment)
    {
        if (tile.Context is not { } context) return;
        var warning = tile.Require<TextView>(Resource.Id.attachment_warning);
        warning.Visibility = attachment.HasContentTypeMismatch ? ViewStates.Visible : ViewStates.Gone;
        warning.ContentDescription = attachment.ContentTypeWarningText;

        var outline = new GradientDrawable();
        outline.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_attachment_tile_radius));
        outline.SetColor(WinoColors.SubtleFill(context));
        outline.SetStroke(WinoTokens.Px(context, Resource.Dimension.wino_attachment_tile_stroke), attachment.HasContentTypeMismatch
            ? WinoColors.WithAlpha(WinoColors.Caution(context), WarningStrokeAlpha(context))
            : WinoColors.CardStroke(context));
        tile.Background = outline;
        tile.ClipToOutline = true;
    }

    private static void ApplyBadge(Context context, TextView badge, AttachmentViewModelBase attachment)
    {
        var fill = new GradientDrawable();
        fill.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_attachment_badge_radius));
        fill.SetColor(WinoColors.Hex(attachment.AccentColor));
        badge.Background = fill;
        badge.SetTextColor(Color.White);

        var label = attachment.FileExtensionLabel;
        if (string.IsNullOrEmpty(label))
            WinoIcons.Apply(badge, WinoIconGlyph.Attachment, WinoTokens.Dp(context, Resource.Dimension.wino_attachment_badge_glyph));
        else
            badge.Text = label;
    }

    private static float WarningStrokeAlpha(Context context)
        => context.Resources is { } resources ? ResourcesCompat.GetFloat(resources, Resource.Dimension.wino_attachment_tile_warning_stroke_alpha) : 1f;
}
