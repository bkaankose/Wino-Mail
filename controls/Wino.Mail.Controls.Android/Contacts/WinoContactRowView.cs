using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Android.Common;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Contacts;

/// <summary>What a <see cref="WinoContactRowView"/> shows (the UIKit WinoContactRowModel without category chips).</summary>
public sealed record WinoContactRowModel(string Name, string Secondary, string? ColorKey, Bitmap? Picture, bool IsFavorite);

/// <summary>
/// A contact in the indexed list (UIKit <c>WinoContactRowCell</c>; canvas Material pass Contacts): a Material 3
/// one-avatar list item of 64 dp with the 40 dp picture or initials, the name (body-large) and the primary address or
/// number (body-medium), and a small star for favourites. The row draws the selection fill when it is the open contact
/// (list beside the card). Sizes come from the wino_people_* tokens.
/// </summary>
public class WinoContactRowView : LinearLayout
{
    private readonly WinoContactPicture _avatar;
    private readonly TextView _name;
    private readonly TextView _secondary;
    private readonly WinoIconView _star;
    private bool _isRowSelected;

    public WinoContactRowView(Context context) : base(context)
    {
        Orientation = Orientation.Horizontal;
        SetGravity(GravityFlags.CenterVertical);
        SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_people_row_height));
        WinoTokens.Pad(this, Resource.Dimension.wino_people_row_padding, 0, Resource.Dimension.wino_people_row_padding, 0);
        _avatar = new WinoContactPicture(context) { Size = WinoTokens.Dp(context, Resource.Dimension.wino_people_avatar) };
        AddView(_avatar, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        var text = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _name = Line(context, WinoTextStyle.ItemTitle);
        _secondary = Line(context, WinoTextStyle.ItemSupporting);
        text.AddView(_name);
        text.AddView(_secondary);
        AddView(text, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_people_row_gap) });

        _star = new WinoIconView(context) { Icon = WinoIconGlyph.StarFilled, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_people_row_star), Visibility = ViewStates.Gone };
        _star.SetTextColor(WinoColors.Caution(context));
        AddView(_star, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_s) });

        Clickable = true;
        Focusable = true;
        Background = AndroidX.Core.Content.ContextCompat.GetDrawable(context, Ripple(context));
    }

    private static TextView Line(Context context, WinoTextStyle style)
    {
        var view = WinoTokens.Style(new TextView(context), style);
        view.SetMaxLines(1);
        view.Ellipsize = TextUtils.TruncateAt.End;
        return view;
    }

    private static int Ripple(Context context)
    {
        var value = new global::Android.Util.TypedValue();
        context.Theme?.ResolveAttribute(global::Android.Resource.Attribute.SelectableItemBackground, value, true);
        return value.ResourceId;
    }

    public void Apply(WinoContactRowModel model)
    {
        _avatar.SetIdentity(model.Name, model.ColorKey ?? model.Name);
        _avatar.Image = model.Picture;
        _name.Text = model.Name;
        _secondary.Text = model.Secondary;
        _secondary.Visibility = string.IsNullOrWhiteSpace(model.Secondary) ? ViewStates.Gone : ViewStates.Visible;
        _star.Visibility = model.IsFavorite ? ViewStates.Visible : ViewStates.Gone;
        ContentDescription = string.IsNullOrWhiteSpace(model.Secondary) ? model.Name : $"{model.Name}, {model.Secondary}";
    }

    /// <summary>The open contact (list beside the card): the Wino selection fill.</summary>
    public bool IsRowSelected
    {
        get => _isRowSelected;
        set
        {
            if (_isRowSelected == value) return;
            _isRowSelected = value;
            Selected = value;
            if (Context is { } context) SetBackgroundColor(value ? WinoColors.SelectedFill(context) : Color.Transparent);
            if (!value && Context is { } plain) Background = AndroidX.Core.Content.ContextCompat.GetDrawable(plain, Ripple(plain));
        }
    }
}
