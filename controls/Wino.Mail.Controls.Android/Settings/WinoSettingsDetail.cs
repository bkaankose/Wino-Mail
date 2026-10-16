using Android.Content;
using Android.Runtime;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.RadioButton;
using Wino.Mail.Controls.Android.Common;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Settings;

/// <summary>
/// Builds the generic setting detail page (canvas Android SettingDetail and SettingsControls "WinoSettingDetailFragment
/// anatomy") for one card or expander. The page title is the source's header. A picker becomes a radio list in one card
/// (selecting writes through the picker's own binding and stays); a stepper is a row with the stepper at its end; a time
/// opens the time picker from its row; sliders, text fields and custom content are hosted in a card; buttons become
/// action rows. An expander's header control comes first, then its items by the same rule, where switches, navigation
/// and actions stay rows and the item's header and description become the section header and footer. The source's
/// description is the supporting text under the first control.
/// </summary>
public static class WinoSettingsDetailBuilder
{
    /// <summary>The sections of the detail page of <paramref name="source"/>, top to bottom.</summary>
    public static List<View> Build(Context context, View source) => new Builder(context, WinoSettingsNavigation.Title(source)).Build(source);

    private sealed class Builder(Context context, string pageTitle)
    {
        private readonly List<View> _sections = [];
        private WinoSettingsGroup? _rows;

        public List<View> Build(View source)
        {
            string? description;
            switch (source)
            {
                case WinoSettingsExpander expander:
                    description = expander.HeaderCard.Description;
                    AddControls(expander.HeaderCard, asItem: false);
                    foreach (var item in expander.Items) AddItem(item);
                    break;
                case WinoSettingsCard card:
                    description = card.Description;
                    AddControls(card, asItem: false);
                    break;
                default:
                    return _sections;
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                var first = _sections.OfType<WinoSettingsGroup>().FirstOrDefault();
                if (first is not null && string.IsNullOrWhiteSpace(first.Footer)) first.Footer = description;
                else if (first is null) _sections.Add(FooterOnly(description));
                else _sections.Add(FooterOnly(description));
            }
            return _sections;
        }

        private TextView FooterOnly(string text)
        {
            var footer = WinoTokens.Style(new TextView(context) { Text = text }, WinoTextStyle.SettingsFooter);
            WinoSettingsRow.ApplyFooterLineHeight(footer);
            WinoTokens.Pad(footer, Resource.Dimension.wino_m3_settings_footer_start, Resource.Dimension.wino_m3_settings_section_gap, Resource.Dimension.wino_m3_settings_footer_start, 0);
            return footer;
        }

        private WinoSettingsGroup Section(string? header, View? followSource, View? followControl)
        {
            _rows = null;
            var section = new WinoSettingsGroup(context, SectionHeader(header)) { FollowSource = followSource, FollowControl = followControl };
            _sections.Add(section);
            return section;
        }

        private string? SectionHeader(string? header) => string.IsNullOrWhiteSpace(header) || header == pageTitle ? null : header;

        private WinoSettingsGroup Rows() => _rows ??= AddRowsSection();

        private WinoSettingsGroup AddRowsSection()
        {
            var section = new WinoSettingsGroup(context);
            _sections.Add(section);
            return section;
        }

        private void AddItem(View item)
        {
            switch (item)
            {
                case WinoSettingsExpander:
                    Rows().AddRow(WinoSettingsRow.ForDetail(context, item));
                    return;
                case WinoSettingsCard card:
                    var kind = WinoSettingsRules.Classify(card);
                    if (kind is WinoSettingsRowKind.Value or WinoSettingsRowKind.Push)
                    {
                        _rows = null;
                        AddControls(card, asItem: true);
                        _rows = null;
                    }
                    else Rows().AddRow(WinoSettingsRow.ForDetail(context, card));
                    return;
                case WinoInfoBar:
                    _rows = null;
                    (item.Parent as ViewGroup)?.RemoveView(item);
                    var notice = new FrameLayout(context);
                    notice.SetPadding(0, WinoTokens.Px(context, Resource.Dimension.wino_m3_settings_section_gap), 0, 0);
                    notice.AddView(item, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
                    _sections.Add(notice);
                    return;
                default:
                    Section(null, null, item).AddRow(WinoSettingsRow.Embed(context, item));
                    _rows = null;
                    return;
            }
        }

        /// <summary>Renders a card's controls. Items use their header as the section header and their description as its footer.</summary>
        private void AddControls(WinoSettingsCard card, bool asItem)
        {
            var controls = new List<View>();
            if (card.Content is { } content) controls.Add(content);
            if (card.BottomContent is { } bottom) controls.Add(bottom);
            string title = card.Header;
            WinoSettingsGroup? last = null;
            bool first = true;
            foreach (var control in controls)
            {
                var section = AddControl(card, control, title, asItem && first);
                if (section is not null) last = section;
                first = false;
            }
            if (asItem && !string.IsNullOrWhiteSpace(card.Description))
            {
                var target = last ?? _rows;
                if (target is not null && string.IsNullOrWhiteSpace(target.Footer))
                {
                    target.Footer = card.Description;
                    _rows = null;
                }
            }
        }

        /// <summary>One control; returns the section it opened (null when it became a row).</summary>
        private WinoSettingsGroup? AddControl(WinoSettingsCard card, View wrapped, string title, bool header)
        {
            var control = WinoSettingsControls.Unwrap(wrapped) ?? wrapped;
            var kind = WinoSettingsControls.KindOf(control);
            switch (kind)
            {
                case WinoSettingsControlKind.Toggle:
                    var toggle = WinoSettingsControls.ToggleOf(control);
                    Rows().AddRow(WinoSettingsRow.Custom(context, title, control, toggle is null ? null : () => { if (toggle.Enabled) toggle.PerformClick(); }, card));
                    return null;
                case WinoSettingsControlKind.Choice when WinoSettingsControls.Choice(control) is { } choice:
                    var choices = new WinoSettingsChoiceSection(context, choice, control, header ? SectionHeader(title) : null) { FollowSource = card, FollowControl = control };
                    _rows = null;
                    _sections.Add(choices);
                    return choices;
                case WinoSettingsControlKind.Stepper:
                    Rows().AddRow(WinoSettingsRow.Custom(context, StepperTitle(card, control, title), control, null, card));
                    return null;
                case WinoSettingsControlKind.Time:
                    Rows().AddRow(WinoSettingsRow.Custom(context, string.IsNullOrWhiteSpace(control.ContentDescription) ? title : control.ContentDescription,
                        null, () => control.PerformClick(), card, () => WinoSettingsControls.ValueText(control)));
                    return null;
                case WinoSettingsControlKind.Button:
                    foreach (var button in WinoSettingsControls.ButtonsOf(control))
                        Rows().AddRow(WinoSettingsRow.Custom(context, button.Text ?? string.Empty, null, () => button.PerformClick(), button,
                            action: true, destructive: card.IsDestructive || WinoSettingsControls.IsDestructive(button)));
                    return null;
                case WinoSettingsControlKind.IconButtons:
                    Rows().AddRow(WinoSettingsRow.Custom(context, title, control, null, card));
                    return null;
                case WinoSettingsControlKind.Custom when WinoSettingsControls.ValueStack(control) is { } stack:
                    WinoSettingsGroup? last = null;
                    bool first = true;
                    foreach (var child in stack)
                    {
                        last = AddControl(card, child, title, header && first) ?? last;
                        first = false;
                    }
                    return last;
                default:
                    // Sliders, text fields, read-only text and custom content: hosted in their own card.
                    var section = Section(header ? title : null, card, control);
                    section.AddRow(WinoSettingsRow.Embed(context, control));
                    return section;
            }
        }

        /// <summary>A stepper row's title: the card's header, unless a switch row of the same card already says it.</summary>
        private string StepperTitle(WinoSettingsCard card, View control, string title)
            => card.Content is { } first && !ReferenceEquals(first, control) && WinoSettingsControls.KindOf(first) == WinoSettingsControlKind.Toggle
                ? string.Empty
                : title;
    }
}

/// <summary>
/// A picker's options as a radio list in one card (canvas SettingDetail "Mark as read"): 56 dp rows with the 20 dp radio
/// first. Tapping a row selects through the picker's binding and stays on the page; the list follows the bound value
/// and re-reads the options when they change.
/// </summary>
public sealed class WinoSettingsChoiceSection : WinoSettingsGroup
{
    private readonly WinoSettingsChoice? _choice;
    private readonly List<(View Row, MaterialRadioButton Radio)> _rows = [];
    private IReadOnlyList<string> _items = [];
    private int _selected = int.MinValue;
    private int _syncedRows = -1;

    public WinoSettingsChoiceSection(Context context, WinoSettingsChoice choice, View control, string? header) : base(context, header)
    {
        _choice = choice;
        Rebuild();
        if (control is TextView text) text.AddTextChangedListener(new Watcher(() => Post(Refresh)));
    }

    private WinoSettingsChoiceSection(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        Refresh();
    }

    private void Refresh()
    {
        if (_choice is null) return;
        var items = _choice.Items();
        if (!items.SequenceEqual(_items)) Rebuild();
        else SyncSelection();
    }

    private void Rebuild()
    {
        if (_choice is null) return;
        var context = this.RequireContext();
        _items = _choice.Items().ToList();
        Clear();
        _rows.Clear();
        _syncedRows = -1;
        for (int index = 0; index < _items.Count; index++)
        {
            int position = index;
            var row = new LinearLayout(context) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_m3_settings_row_height));
            WinoTokens.Pad(row, Resource.Dimension.wino_m3_settings_row_padding_start, Resource.Dimension.wino_m3_settings_row_padding_vertical,
                Resource.Dimension.wino_m3_settings_row_padding_end, Resource.Dimension.wino_m3_settings_row_padding_vertical);
            var radio = new MaterialRadioButton(context) { Clickable = false, Focusable = false, ImportantForAccessibility = ImportantForAccessibility.No };
            radio.SetMinWidth(0);
            radio.SetMinimumWidth(0);
            radio.SetMinHeight(0);
            radio.SetMinimumHeight(0);
            WinoMaterialAccent.Tint(radio);
            row.AddView(radio, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            var title = WinoTokens.Style(new TextView(context) { Text = _items[index] }, WinoTextStyle.SettingsRowTitle);
            title.SetMaxLines(2);
            title.Ellipsize = TextUtils.TruncateAt.End;
            row.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1)
            {
                MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_m3_settings_row_gap)
            });
            row.ContentDescription = _items[index];
            row.Foreground = new global::Android.Graphics.Drawables.RippleDrawable(
                global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SubtleFill(context)), null,
                new global::Android.Graphics.Drawables.ColorDrawable(global::Android.Graphics.Color.White));
            row.Click += (_, _) =>
            {
                if (!Enabled || _choice is null) return;
                try { _choice.Select(position); }
                catch (Exception exception) { global::Android.Util.Log.Warn("Wino", $"Choosing a setting failed. {exception}"); }
                SyncSelection();
            };
            AddRow(row);
            _rows.Add((row, radio));
        }
        SyncSelection();
    }

    private void SyncSelection()
    {
        if (_choice is null) return;
        int selected = _choice.SelectedIndex();
        if (selected == _selected && _rows.Count == _syncedRows) return;
        _selected = selected;
        _syncedRows = _rows.Count;
        for (int index = 0; index < _rows.Count; index++)
        {
            var (row, radio) = _rows[index];
            bool on = index == selected;
            if (radio.Checked != on) radio.Checked = on;
            row.Selected = on;
            AndroidX.Core.View.ViewCompat.SetStateDescription(row, on ? Wino.Core.Domain.Translator.Android_Settings_Selected : null);
        }
    }

    protected override void OnFollowSync() => SyncSelection();

    private sealed class Watcher(Action changed) : Java.Lang.Object, ITextWatcher
    {
        public void AfterTextChanged(IEditable? s) => changed();
        public void BeforeTextChanged(Java.Lang.ICharSequence? s, int start, int count, int after) { }
        public void OnTextChanged(Java.Lang.ICharSequence? s, int start, int before, int count) { }
    }
}

/// <summary>
/// The detail page's list (RecyclerView + ListAdapter; canvas SettingsControls "Implementation"): each section is one
/// item. Sections hold the page's live controls, so every item keeps its own view type and holders are never rebound
/// to another section.
/// </summary>
public sealed class WinoSettingsDetailAdapter : ListAdapter
{
    private readonly Dictionary<int, Item> _byId = [];
    private int _nextId;

    public WinoSettingsDetailAdapter() : base(new Diff()) { }

    private WinoSettingsDetailAdapter(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    /// <summary>Shows <paramref name="sections"/>.</summary>
    public void Submit(IReadOnlyList<View> sections)
    {
        _byId.Clear();
        var items = new List<Java.Lang.Object>(sections.Count);
        foreach (var section in sections)
        {
            var item = new Item(_nextId++, section);
            _byId[item.Id] = item;
            items.Add(item);
        }
        SubmitList(items);
    }

    public override int GetItemViewType(int position) => GetItem(position) is Item item ? item.Id : 0;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var host = new FrameLayout(parent.Context) { LayoutParameters = new RecyclerView.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) };
        return new Holder(host);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        if (holder.ItemView is not FrameLayout host || GetItem(position) is not Item item) return;
        if (host.ChildCount == 1 && ReferenceEquals(host.GetChildAt(0), item.View)) return;
        host.RemoveAllViews();
        (item.View.Parent as ViewGroup)?.RemoveView(item.View);
        host.AddView(item.View, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
    }

    private sealed class Item(int id, View view) : Java.Lang.Object
    {
        public int Id { get; } = id;
        public View View { get; } = view;
    }

    private sealed class Holder(View view) : RecyclerView.ViewHolder(view);

    private sealed class Diff : DiffUtil.ItemCallback
    {
        public override bool AreItemsTheSame(Java.Lang.Object oldItem, Java.Lang.Object newItem)
            => oldItem is Item a && newItem is Item b && a.Id == b.Id;

        public override bool AreContentsTheSame(Java.Lang.Object oldItem, Java.Lang.Object newItem) => ReferenceEquals(oldItem, newItem);
    }
}
