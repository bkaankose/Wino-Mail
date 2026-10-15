using Android.Content;
using Android.Views;
using Android.Widget;
using Java.Lang;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.Controls.Android.Common;
using Wino.Presentation.Android;
using Object = Java.Lang.Object;

namespace Wino.Mail.Android.Views.Mail.Compose;

/// <summary>
/// The recipient suggestion list under a recipient field (Mac and iOS ComposeRecipientSuggestionList, Windows
/// SuggestionTemplate): avatar, name, and the address or a list's member count. The page fills it from the shared
/// suggestion service; the filter AutoCompleteTextView requires passes the items through unchanged and turns a pick into
/// empty text, because the page adds the recipient itself.
/// </summary>
internal sealed class ComposeSuggestionAdapter(Context context) : BaseAdapter, IFilterable
{
    private readonly List<RecipientSuggestion> _items = [];
    private PassThroughFilter? _filter;

    public override int Count => _items.Count;

    public RecipientSuggestion? ItemAt(int position) => position >= 0 && position < _items.Count ? _items[position] : null;

    public void SetItems(IEnumerable<RecipientSuggestion> items)
    {
        _items.Clear();
        _items.AddRange(items);
        NotifyDataSetChanged();
    }

    // AutoCompleteTextView ignores a pick whose item is null; the filter turns it into empty text anyway.
    public override Object? GetItem(int position) => ItemAt(position) is { } item ? new Java.Lang.String(item.Address ?? string.Empty) : null;

    public override long GetItemId(int position) => position;

    public Filter Filter => _filter ??= new PassThroughFilter(this);

    public override View GetView(int position, View? convertView, ViewGroup? parent)
    {
        // compose_suggestion_row.xml
        var row = convertView ?? LayoutInflater.From(context)?.Inflate(Resource.Layout.compose_suggestion_row, parent, false)
            ?? throw new InvalidOperationException("The suggestion row layout did not inflate.");
        if (ItemAt(position) is not { } suggestion) return row;
        var picture = row.Require<WinoContactPicture>(Resource.Id.suggestion_picture);
        var name = row.Require<TextView>(Resource.Id.suggestion_name);
        var secondary = row.Require<TextView>(Resource.Id.suggestion_secondary);
        var title = string.IsNullOrWhiteSpace(suggestion.DisplayName) ? suggestion.Address : suggestion.DisplayName;
        picture.Size = ComposeStyle.Dp(context, Resource.Dimension.wino_compose_suggestion_avatar);
        picture.SetIdentity(title, suggestion.Address);
        name.Text = title;
        secondary.Text = string.IsNullOrWhiteSpace(suggestion.SourceCaption) ? suggestion.SecondaryText : $"{suggestion.SecondaryText}  ·  {suggestion.SourceCaption}";
        secondary.Visibility = string.IsNullOrWhiteSpace(secondary.Text) || secondary.Text == title ? ViewStates.Gone : ViewStates.Visible;
        row.ContentDescription = string.IsNullOrWhiteSpace(secondary.Text) ? title : $"{title}, {suggestion.SecondaryText}";
        return row;
    }

    private sealed class PassThroughFilter(ComposeSuggestionAdapter owner) : Filter
    {
        protected override FilterResults PerformFiltering(ICharSequence? constraint)
            => new() { Count = owner._items.Count };

        protected override void PublishResults(ICharSequence? constraint, FilterResults? results) => owner.NotifyDataSetChanged();

        public override ICharSequence? ConvertResultToStringFormatted(Object? resultValue) => new Java.Lang.String(string.Empty);
    }
}
