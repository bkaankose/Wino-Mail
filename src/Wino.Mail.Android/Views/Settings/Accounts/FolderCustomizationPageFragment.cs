using System.Collections.ObjectModel;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.Accounts;

/// <summary>
/// Customize folder list (iOS FolderCustomizationPageViewController, Windows FolderCustomizationPage): the account line
/// with Reset to defaults, then Pinned, Categories (Gmail only) and More as expanded sections. Each folder row has the
/// special-folder glyph, the name, System and Hidden tags, the hide toggle, Pin to top / Move to More, and a reorder
/// button whose menu moves the row up or down inside its section (iOS drags the reorder handle). The pin button moves
/// rows between Pinned and More. Every change persists at once.
/// </summary>
public sealed class FolderCustomizationPageFragment : SettingsPageFragment<FolderCustomizationPageViewModel>, ISettingsPageTitleSource
{
    private const string PinnedKey = "pinned";
    private const string CategoriesKey = "categories";
    private const string MoreKey = "more";

    private readonly Dictionary<string, (LinearLayout Rows, ObservableCollection<FolderCustomizationItemViewModel> Items, TextView Count, View Empty)> _sections = [];
    private readonly Dictionary<string, BindingScope> _scopes = [];

    public string? PageTitle => Translator.FolderCustomization_Title;

    /// <summary>The header shows the description next to the account name.</summary>
    protected override bool ShowsPageDescription => false;

    protected override void BuildPage()
    {
        var vm = VM;
        _sections.Clear();
        Bindings.Own(new ActionDisposable(() =>
        {
            foreach (var scope in _scopes.Values) scope.Dispose();
            _scopes.Clear();
        }));

        var account = Bind.Label(vm, nameof(vm.AccountName), s => s.AccountName, WinoTextStyle.BodyLargeStrong);
        var description = Text(Translator.FolderCustomization_Description, WinoTextStyle.Description);
        var reset = Bind.Button(Translator.FolderCustomization_Reset, vm.ResetCommand, icon: WinoIconGlyph.ArrowReset);
        var header = Stack(Resource.Dimension.wino_space_s, Stack(Resource.Dimension.wino_space_3xs, account, description), Row(reset));
        Add(header);
        ((LinearLayout.LayoutParams)header.LayoutParameters!).BottomMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s);

        Add(Section(PinnedKey, vm.PinnedFolders, Translator.FolderCustomization_SectionPinned, Translator.FolderCustomization_SectionPinnedDescription, WinoIconGlyph.Pin));
        var categories = Add(Section(CategoriesKey, vm.CategoryFolders, Translator.FolderCustomization_SectionCategories, Translator.FolderCustomization_SectionCategoriesDescription, WinoIconGlyph.Tag));
        Bind.Visible(categories, vm, nameof(vm.IsGmailAccount), s => s.IsGmailAccount);
        Add(Section(MoreKey, vm.MoreFolders, Translator.FolderCustomization_SectionMore, Translator.FolderCustomization_SectionMoreDescription, WinoIconGlyph.More));

        var footer = Add(AccountSettingsParts.Label(Ctx, Translator.FolderCustomization_Footer, WinoTextStyle.Caption, WinoColors.TertiaryText(Ctx)));
        ((LinearLayout.LayoutParams)footer.LayoutParameters!).TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_xs);
    }

    private WinoSettingsExpander Section(string key, ObservableCollection<FolderCustomizationItemViewModel> items, string title, string description, WinoIconGlyph icon)
    {
        var count = Caption(string.Empty);
        var expander = new WinoSettingsExpander(Ctx, title, description, icon, null, isExpanded: true);
        // The folder count reads under the description at phone width.
        expander.HeaderCard.BottomContent = count;

        var rows = AccountSettingsParts.VColumn(Ctx);
        var empty = AccountSettingsParts.Label(Ctx, Translator.FolderCustomization_EmptySection, WinoTextStyle.Caption, WinoColors.TertiaryText(Ctx), maxLines: 2);
        empty.Gravity = GravityFlags.Center;
        empty.SetMinHeight(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_control));
        empty.Background = AccountSettingsParts.DashedOutline(Ctx);
        var body = Stack(0, rows, empty);
        var emptyGap = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s);
        ((LinearLayout.LayoutParams)empty.LayoutParameters!).SetMargins(0, emptyGap, 0, emptyGap);
        // The expander insets its rows under the header text, so rows start at the glyph column.
        expander.Add(body, 0, 0, 0, 0);
        _sections[key] = (rows, items, count, empty);
        Bind.Collection(items, () => RebuildSection(key));
        return expander;
    }

    private void RebuildSection(string key)
    {
        if (!_sections.TryGetValue(key, out var section)) return;
        if (_scopes.Remove(key, out var previous)) previous.Dispose();
        var scope = new BindingScope();
        _scopes[key] = scope;
        var binder = Bind.Child(scope);
        section.Rows.RemoveAllViews();
        var items = section.Items.ToList();
        for (int index = 0; index < items.Count; index++)
            AccountSettingsParts.AddStretched(section.Rows, FolderRow(binder, key, items[index], index, items.Count));
        section.Empty.Visibility = items.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        UpdateCount(key);
    }

    private void UpdateCount(string key)
    {
        var (_, items, count, _) = _sections[key];
        var hidden = items.Count(item => item.IsHidden);
        var text = string.Format(Translator.FolderCustomization_FolderCountFormat, items.Count);
        if (hidden > 0) text += " · " + string.Format(Translator.FolderCustomization_HiddenCountFormat, hidden);
        count.Text = text;
    }

    /// <summary>
    /// One folder row: hairline above, glyph, name, tags, hide toggle, the pin action (Pinned: Move to More, More: Pin to
    /// top, Categories: an empty slot) and the reorder menu. Hidden rows dim to 55%.
    /// </summary>
    private View FolderRow(SettingsBinder rows, string key, FolderCustomizationItemViewModel item, int index, int count)
    {
        var host = AccountSettingsParts.VColumn(Ctx);
        var separator = new View(Ctx);
        separator.SetBackgroundColor(WinoColors.CardStroke(Ctx));
        host.AddView(separator, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(Ctx, Resource.Dimension.wino_space_hairline)));

        var row = AccountSettingsParts.HRow(Ctx);
        row.SetMinimumHeight(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_touch_large));

        row.AddView(AccountSettingsParts.Glyph(Ctx, SpecialFolderGlyphs.GetGlyphOrFolder(item.SpecialFolderType), Resource.Dimension.wino_glyph_m));

        var text = AccountSettingsParts.HRow(Ctx);
        var name = AccountSettingsParts.Label(Ctx, item.FolderName, WinoTextStyle.Body, maxLines: 1);
        name.ContentDescription = item.IsHidden ? $"{item.FolderName}, {Translator.FolderCustomization_HiddenTag}" : item.FolderName;
        text.AddView(name, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        if (item.IsSystemFolder) text.AddView(AccountSettingsParts.Tag(Ctx, Translator.FolderCustomization_SystemTag), AccountSettingsParts.RowItem(Ctx, marginStart: Resource.Dimension.wino_space_xs));
        if (item.IsHidden) text.AddView(AccountSettingsParts.Tag(Ctx, Translator.FolderCustomization_HiddenTag), AccountSettingsParts.RowItem(Ctx, marginStart: Resource.Dimension.wino_space_xs));
        row.AddView(text, AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: Resource.Dimension.wino_space_m));

        var hide = AccountSettingsParts.GlyphButton(Ctx, WinoIconGlyph.EyeOff,
            $"{(item.IsHidden ? Translator.FolderCustomization_Show : Translator.FolderCustomization_Hide)}, {item.FolderName}", item.IsHidden);
        rows.Views.Click(hide, () => ToggleHidden(key, item));
        row.AddView(hide);

        if (key == CategoriesKey)
        {
            row.AddView(new Space(Ctx), new LinearLayout.LayoutParams(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_touch_large), WinoTokens.Px(Ctx, Resource.Dimension.wino_size_touch_large)));
        }
        else
        {
            var pinned = key == PinnedKey;
            var pin = AccountSettingsParts.GlyphButton(Ctx, pinned ? WinoIconGlyph.UnPin : WinoIconGlyph.Pin,
                $"{(pinned ? Translator.FolderCustomization_Unpin : Translator.FolderCustomization_Pin)}, {item.FolderName}");
            rows.Views.Click(pin, () => AccountSettingsParts.Run(VM.TogglePinAsync(item), ReportError));
            row.AddView(pin);
        }

        var reorder = AccountSettingsParts.GlyphButton(Ctx, WinoIconGlyph.ArrowSort, $"{Translator.SettingsReorderAccounts_Title}, {item.FolderName}");
        reorder.Enabled = count > 1;
        rows.Views.Click(reorder, () =>
        {
            var entries = new List<SettingsMenuEntry>();
            if (index > 0) entries.Add(new(Translator.MailFilters_MoveUp, () => Move(key, index, index - 1)));
            if (index < count - 1) entries.Add(new(Translator.MailFilters_MoveDown, () => Move(key, index, index + 1)));
            if (entries.Count > 0) AccountSettingsParts.ShowMenu(Ctx, reorder, entries, ReportError);
        });
        row.AddView(reorder);

        row.Alpha = item.IsHidden ? 0.55f : 1f;
        host.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return host;
    }

    private void ToggleHidden(string key, FolderCustomizationItemViewModel item)
    {
        // The ViewModel flips IsHidden before it awaits the database write, so the row can redraw now.
        var operation = VM.ToggleHiddenAsync(item);
        RebuildSection(key);
        AccountSettingsParts.Run(operation, ReportError);
    }

    /// <summary>Moves a row inside its section, then persists the layout (Windows ListView.DropCompleted).</summary>
    private void Move(string key, int from, int to)
    {
        var items = _sections[key].Items;
        if (from < 0 || from >= items.Count) return;
        items.Move(from, Math.Clamp(to, 0, items.Count - 1));
        AccountSettingsParts.Run(VM.PersistLayoutAsync(), ReportError);
    }
}
