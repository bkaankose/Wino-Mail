using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Wino.Mail.Controls.Android.Contacts;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Contacts;

/// <summary>
/// The letter-indexed contact list (Windows ContactsPage grouped list): a Material 3 list subheader per letter, then the
/// contacts of that letter as <see cref="WinoContactRowView"/> rows. The page hands it the sections (the shared
/// <see cref="ContactGroup"/>s, or the search results grouped by initial) and the row model mapping.
/// </summary>
internal sealed class ContactsListAdapter(Func<AccountContactViewModel, WinoContactRowModel> map) : RecyclerView.Adapter
{
    private const int HeaderType = 0;
    private const int RowType = 1;

    /// <summary>One list entry: a letter subheader (Contact null) or a contact.</summary>
    public readonly record struct Entry(string Letter, AccountContactViewModel? Contact);

    private readonly List<Entry> _entries = [];
    private readonly List<(string Letter, int Position)> _sections = [];
    private Guid? _selectedId;

    public event EventHandler<AccountContactViewModel>? ContactClicked;
    public event EventHandler<(AccountContactViewModel Contact, View Anchor)>? ContactLongClicked;

    public IReadOnlyList<(string Letter, int Position)> Sections => _sections;

    public IReadOnlyList<Entry> Entries => _entries;

    public void SetSections(IEnumerable<(string Letter, IReadOnlyList<AccountContactViewModel> Contacts)> sections)
    {
        _entries.Clear();
        _sections.Clear();
        foreach (var (letter, contacts) in sections)
        {
            if (contacts.Count == 0) continue;
            _sections.Add((letter, _entries.Count));
            _entries.Add(new Entry(letter, null));
            foreach (var contact in contacts) _entries.Add(new Entry(letter, contact));
        }
        NotifyDataSetChanged();
    }

    /// <summary>The open contact draws the selection fill (list beside the card).</summary>
    public void SetSelected(Guid? id)
    {
        if (_selectedId == id) return;
        _selectedId = id;
        NotifyDataSetChanged();
    }

    /// <summary>The section that holds <paramref name="position"/>.</summary>
    public int SectionOf(int position)
    {
        int found = -1;
        for (int index = 0; index < _sections.Count && _sections[index].Position <= position; index++) found = index;
        return found;
    }

    public override int ItemCount => _entries.Count;

    public override int GetItemViewType(int position) => _entries[position].Contact is null ? HeaderType : RowType;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var context = parent.RequireContext();
        if (viewType == HeaderType)
        {
            var header = WinoTokens.Style(new TextView(context), WinoTextStyle.Subheader);
            header.SetTextColor(WinoColors.AccentText(context));
            header.Gravity = GravityFlags.CenterVertical;
            header.SetMinHeight(WinoTokens.Px(context, Resource.Dimension.wino_m3_subheader_height));
            WinoTokens.Pad(header, Resource.Dimension.wino_m3_subheader_padding, 0, Resource.Dimension.wino_m3_subheader_padding, 0);
            AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(header, true);
            header.LayoutParameters = new RecyclerView.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            return new Holder(header);
        }
        var row = new WinoContactRowView(context) { LayoutParameters = new RecyclerView.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) };
        var holder = new Holder(row);
        row.Click += (_, _) => { if (holder.Contact is { } contact) ContactClicked?.Invoke(this, contact); };
        row.LongClick += (_, args) =>
        {
            args.Handled = holder.Contact is not null;
            if (holder.Contact is { } contact) ContactLongClicked?.Invoke(this, (contact, row));
        };
        return holder;
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        if (holder is not Holder bound || position >= _entries.Count) return;
        var entry = _entries[position];
        bound.Contact = entry.Contact;
        if (entry.Contact is null)
        {
            if (bound.ItemView is TextView header) header.Text = entry.Letter;
            return;
        }
        if (bound.ItemView is WinoContactRowView row)
        {
            row.Apply(map(entry.Contact));
            row.IsRowSelected = _selectedId == entry.Contact.Id;
        }
    }

    private sealed class Holder(View view) : RecyclerView.ViewHolder(view)
    {
        public AccountContactViewModel? Contact { get; set; }
    }
}
