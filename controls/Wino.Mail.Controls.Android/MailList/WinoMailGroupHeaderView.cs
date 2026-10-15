using Android.Content;
using Android.Views;
using Android.Widget;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.MailList;

/// <summary>
/// The mail list date group header (UIKit <c>WinoMailGroupHeaderView</c>; canvas Material pass Inbox "Today"): a Material 3
/// list subheader, label-large in the primary (accent) colour without a fill.
/// </summary>
public static class WinoMailGroupHeaderView
{
    /// <summary>A new header strip from <c>wino_mail_group_header.xml</c>.</summary>
    public static TextView Create(Context context, ViewGroup? parent)
    {
        var view = LayoutInflater.From(context)?.Inflate(Resource.Layout.wino_mail_group_header, parent, false) as TextView
            ?? throw new InvalidOperationException("wino_mail_group_header.xml did not inflate to a TextView.");
        RefreshTheme(view);
        return view;
    }

    /// <summary>Re-applies the primary colour (accent or theme change).</summary>
    public static void RefreshTheme(TextView view) => view.SetTextColor(WinoColors.AccentText(view.RequireContext()));
}
