using System.Collections.ObjectModel;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.Accounts;

/// <summary>
/// Creates or edits an account group (Windows AccountGroupDetailsPage, iOS AccountGroupDetailsPageViewController): the
/// name beside a preview of the tinted icon, Combine folders, the color and icon grids, the ordered accounts In group
/// (Move up, Move down and Remove; Android has no drag) and the Available accounts (Add, and the group they are in now),
/// Save and Cancel, and Delete for saved groups. Save stays off until the group has a name and two accounts.
/// </summary>
public sealed class AccountGroupDetailsPageFragment : SettingsPageFragment<AccountGroupDetailsPageViewModel>, ISettingsPageTitleSource
{
    private readonly List<BindingScope> _listScopes = [];
    private readonly List<WinoIconView> _iconGlyphs = [];
    private readonly List<(FrameLayout Tile, GradientDrawable Background)> _colorTiles = [];
    private readonly List<(FrameLayout Tile, GradientDrawable Background)> _iconTiles = [];
    private IPictureStorageService? _pictures;

    public string? PageTitle => ViewModel is not { } vm ? null
        : vm.IsNewGroup || string.IsNullOrWhiteSpace(vm.GroupName) ? Translator.AccountGroupDetailsPage_NewTitle : vm.GroupName;

    protected override void BuildPage()
    {
        var vm = VM;
        _pictures = MainApplication.Services?.GetService<IPictureStorageService>();
        _iconGlyphs.Clear();
        _colorTiles.Clear();
        _iconTiles.Clear();
        Bindings.Own(new ActionDisposable(() =>
        {
            foreach (var scope in _listScopes) scope.Dispose();
            _listScopes.Clear();
        }));
        Bind.Bind(vm, nameof(vm.IsNewGroup), s => s.IsNewGroup, _ => UpdateTitle());
        Bind.Bind(vm, nameof(vm.GroupName), s => s.GroupName, _ => { if (!vm.IsNewGroup) UpdateTitle(); });

        Add(new WinoSettingsGroup(Ctx, Translator.AccountGroupDetailsPage_NameHeader, Translator.AccountGroupDetailsPage_NameRequired)).AddFlush(NameRow());

        var combine = Card(Translator.AccountGroupDetailsPage_CombineFoldersHeader, Translator.AccountGroupDetailsPage_CombineFoldersDescription, WinoIconGlyph.None,
            Bind.Switch(vm, nameof(vm.CombineFolders), s => s.CombineFolders, (s, value) => s.CombineFolders = value, Translator.AccountGroupDetailsPage_CombineFoldersHeader));
        var combineGroup = Add(new WinoSettingsGroup(Ctx, null, Translator.AccountGroupDetailsPage_CombineFoldersDescription));
        combineGroup.Add(combine);

        Add(new WinoSettingsGroup(Ctx, Translator.AccountGroupDetailsPage_ColorHeader, Translator.AccountGroupDetailsPage_ColorDescription)).AddFlush(ColorGrid());
        Add(new WinoSettingsGroup(Ctx, Translator.AccountGroupDetailsPage_IconHeader, Translator.AccountGroupDetailsPage_IconDescription)).AddFlush(IconGrid());

        MembersSection();
        AvailableSection();

        AddGroup(null,
            ActionCard(Translator.Buttons_Save, vm.SaveCommand),
            ActionCard(Translator.Buttons_Cancel, vm.CancelCommand));

        var delete = ActionCard(Translator.AccountGroupDetailsPage_DeleteGroup, vm.DeleteGroupCommand, destructive: true);
        var deleteGroup = Add(new WinoSettingsGroup(Ctx, null, Translator.AccountGroupDetailsPage_DeleteGroupDescription));
        deleteGroup.Add(delete);
        deleteGroup.FollowSource = delete;
        Bind.Visible(delete, vm, nameof(vm.IsExistingGroup), s => s.IsExistingGroup);
    }

    /// <summary>The group's icon in its color, then the name field.</summary>
    private View NameRow()
    {
        var vm = VM;
        var row = AccountSettingsParts.HRow(Ctx);
        WinoTokens.Pad(row, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_xs, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_s);

        var preview = AccountSettingsParts.Glyph(Ctx, vm.SelectedIconGlyph, Resource.Dimension.wino_glyph_2xl, null, colorful: false);
        preview.ImportantForAccessibility = ImportantForAccessibility.No;
        int side = WinoTokens.Px(Ctx, Resource.Dimension.wino_size_touch);
        row.AddView(preview, new LinearLayout.LayoutParams(side, side));
        Bind.Bind(vm, nameof(vm.SelectedIconGlyph), s => s.SelectedIconGlyph, glyph => preview.Icon = glyph);
        Bind.Bind(vm, nameof(vm.SelectedColorKey), s => s.SelectedColorKey, key => preview.SetTextColor(Tint(key)));

        var field = Bind.TextField(vm, nameof(vm.GroupName), s => s.GroupName, (s, value) => s.GroupName = value, Translator.AccountGroupDetailsPage_NamePlaceholder);
        field.ContentDescription = Translator.AccountGroupDetailsPage_NameHeader;
        if (field.EditText is { } text)
        {
            text.ImeOptions = ImeAction.Done;
            text.InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextFlagCapWords;
        }
        row.AddView(field, AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: Resource.Dimension.wino_space_m, width: 0));
        return row;
    }

    private Color Tint(string? key) => WinoColors.AccountGroupColor(Ctx, key) ?? WinoColors.PrimaryText(Ctx);

    /// <summary>"Aa" in each group color, Default first (Windows AccountGroupColorTemplate).</summary>
    private View ColorGrid()
    {
        var vm = VM;
        var grid = Grid(Translator.AccountGroupDetailsPage_ColorHeader);
        foreach (var option in vm.ColorOptions)
        {
            var sample = WinoTokens.Style(new TextView(Ctx) { Text = "Aa", Gravity = GravityFlags.Center }, WinoTextStyle.BodyStrong);
            sample.SetTextColor(Tint(option.Key));
            sample.ImportantForAccessibility = ImportantForAccessibility.No;
            _colorTiles.Add(AddChoice(grid, sample, option.DisplayName, () => vm.SelectedColorOption = option));
        }
        Bind.Bind(vm, nameof(vm.SelectedColorOption), s => s.SelectedColorOption,
            selected => Select(_colorTiles, selected is null ? -1 : Array.IndexOf(vm.ColorOptions, selected)));
        return grid;
    }

    /// <summary>The group icons, tinted with the selected color (Windows AccountGroupIconTemplate).</summary>
    private View IconGrid()
    {
        var vm = VM;
        var grid = Grid(Translator.AccountGroupDetailsPage_IconHeader);
        foreach (var option in vm.IconOptions)
        {
            var glyph = AccountSettingsParts.Glyph(Ctx, option.Glyph, Resource.Dimension.wino_glyph_l, null, colorful: false);
            glyph.ImportantForAccessibility = ImportantForAccessibility.No;
            _iconGlyphs.Add(glyph);
            _iconTiles.Add(AddChoice(grid, glyph, option.DisplayName, () => vm.SelectedIconOption = option));
        }
        Bind.Bind(vm, nameof(vm.SelectedIconOption), s => s.SelectedIconOption,
            selected => Select(_iconTiles, selected is null ? -1 : Array.IndexOf(vm.IconOptions, selected)));
        Bind.Bind(vm, nameof(vm.SelectedColorKey), s => s.SelectedColorKey, key =>
        {
            var tint = Tint(key);
            foreach (var glyph in _iconGlyphs) glyph.SetTextColor(tint);
        });
        return grid;
    }

    private WinoFlowView Grid(string label)
    {
        var grid = new WinoFlowView(Ctx)
        {
            Spacing = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s),
            LineSpacing = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s),
            ContentDescription = label
        };
        WinoTokens.Pad(grid, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_m);
        return grid;
    }

    /// <summary>A 48 dp choice tile with single selection (the Windows GridView items of the color and icon cards).</summary>
    private (FrameLayout Tile, GradientDrawable Background) AddChoice(WinoFlowView grid, View content, string name, Action selected)
    {
        var background = new GradientDrawable();
        background.SetCornerRadius(WinoTokens.PxF(Ctx, Resource.Dimension.wino_radius_large));
        var tile = new FrameLayout(Ctx) { Background = background, Clickable = true, Focusable = true, ContentDescription = name };
        tile.Foreground = new RippleDrawable(global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SubtleFill(Ctx)), null, null);
        tile.AddView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
        Bind.Views.Click(tile, selected);
        int side = WinoTokens.Px(Ctx, Resource.Dimension.wino_size_touch_large);
        grid.AddView(tile, new ViewGroup.MarginLayoutParams(side, side));
        Style(tile, background, false);
        return (tile, background);
    }

    private void Select(List<(FrameLayout Tile, GradientDrawable Background)> tiles, int index)
    {
        for (int position = 0; position < tiles.Count; position++) Style(tiles[position].Tile, tiles[position].Background, position == index);
    }

    private void Style(FrameLayout tile, GradientDrawable background, bool selected)
    {
        background.SetColor(selected ? WinoColors.SelectedFill(Ctx) : WinoColors.SubtleFill(Ctx));
        background.SetStroke(WinoTokens.Px(Ctx, Resource.Dimension.wino_stroke_hairline) * (selected ? 2 : 1),
            selected ? WinoColors.Accent(Ctx) : WinoColors.CardStroke(Ctx));
        tile.Selected = selected;
    }

    /// <summary>In group: the ordered members, the two-account rule under the list.</summary>
    private void MembersSection()
    {
        var vm = VM;
        var rows = AccountSettingsParts.VColumn(Ctx);
        var empty = Empty(null);
        var section = Add(new WinoSettingsGroup(Ctx, Translator.AccountGroupDetailsPage_InGroupHeader));
        section.AddFlush(Stack(0, rows, empty));

        var required = Add(AccountSettingsParts.Label(Ctx, Translator.AccountGroupDetailsPage_MemberRequired, WinoTextStyle.Description, WinoColors.Caution(Ctx)));
        required.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        Bind.Visible(required, vm, nameof(vm.IsMemberMissing), s => s.IsMemberMissing);

        BindList(vm.MemberEntries, rows, member: true, () => empty.Visibility = vm.MemberEntries.Count == 0 ? ViewStates.Visible : ViewStates.Gone);
    }

    /// <summary>Available: the other accounts, with the group they are in now.</summary>
    private void AvailableSection()
    {
        var vm = VM;
        var rows = AccountSettingsParts.VColumn(Ctx);
        var empty = Empty(Translator.AccountGroupDetailsPage_AvailableEmpty);
        var section = Add(new WinoSettingsGroup(Ctx, Translator.AccountGroupDetailsPage_AvailableHeader, Translator.AccountGroupDetailsPage_AvailableDescription));
        section.AddFlush(Stack(0, rows, empty));
        BindList(vm.AvailableEntries, rows, member: false, () => empty.Visibility = vm.AvailableEntries.Count == 0 ? ViewStates.Visible : ViewStates.Gone);
    }

    private TextView Empty(string? text)
    {
        var label = AccountSettingsParts.Label(Ctx, text, WinoTextStyle.Caption, WinoColors.TertiaryText(Ctx), maxLines: 2);
        label.Gravity = GravityFlags.Center;
        label.SetMinHeight(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_row));
        WinoTokens.Pad(label, Resource.Dimension.wino_space_l, 0, Resource.Dimension.wino_space_l, 0);
        return label;
    }

    private void BindList(ObservableCollection<AccountGroupEntryViewModel> items, LinearLayout host, bool member, Action changed)
    {
        BindingScope? scope = null;
        Bind.Collection(items, () =>
        {
            if (scope is not null)
            {
                scope.Dispose();
                _listScopes.Remove(scope);
            }
            scope = new BindingScope();
            _listScopes.Add(scope);
            var binder = Bind.Child(scope);
            host.RemoveAllViews();
            var entries = items.ToList();
            for (int index = 0; index < entries.Count; index++)
                AccountSettingsParts.AddStretched(host, EntryRow(binder, entries[index], index, entries.Count, member));
            host.Visibility = host.ChildCount == 0 ? ViewStates.Gone : ViewStates.Visible;
            changed();
        });
    }

    /// <summary>
    /// An account row: picture, name and address (and "In {group}" for an available account of another group), then
    /// Move up, Move down and Remove for members, or Add.
    /// </summary>
    private View EntryRow(SettingsBinder rows, AccountGroupEntryViewModel entry, int index, int count, bool member)
    {
        var host = AccountSettingsParts.VColumn(Ctx);
        if (index > 0)
        {
            var separator = new View(Ctx);
            separator.SetBackgroundColor(WinoColors.CardStroke(Ctx));
            host.AddView(separator, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(Ctx, Resource.Dimension.wino_space_hairline))
            {
                MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_l)
            });
        }

        var row = AccountSettingsParts.HRow(Ctx);
        row.SetMinimumHeight(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_row));
        WinoTokens.Pad(row, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_2xs, Resource.Dimension.wino_space_xs, Resource.Dimension.wino_space_2xs);

        var picture = new WinoAccountIconView(Ctx) { Size = WinoTokens.Dp(Ctx, Resource.Dimension.wino_size_swatch_large), DisplayName = entry.Title };
        if (_pictures is not null) picture.Account = MailAccountIconInfoFactory.Create(entry.Account, _pictures);
        picture.ImportantForAccessibility = ImportantForAccessibility.No;
        row.AddView(picture);

        var text = AccountSettingsParts.VColumn(Ctx);
        text.AddView(AccountSettingsParts.Label(Ctx, entry.Title, WinoTextStyle.Body, maxLines: 1));
        if (!string.IsNullOrEmpty(entry.Description)) text.AddView(AccountSettingsParts.Label(Ctx, entry.Description, WinoTextStyle.Caption, WinoColors.SecondaryText(Ctx), maxLines: 1));
        bool otherGroup = !member && entry.IsInOtherGroup;
        if (otherGroup) text.AddView(AccountSettingsParts.Label(Ctx, entry.OtherGroupText, WinoTextStyle.Caption, WinoColors.SecondaryText(Ctx), maxLines: 1));
        text.Focusable = true;
        text.ContentDescription = string.Join(", ", new[] { entry.Title, entry.Description, otherGroup ? entry.OtherGroupText : null }.Where(static part => !string.IsNullOrEmpty(part)));
        row.AddView(text, AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: Resource.Dimension.wino_space_m, width: 0));

        if (member)
        {
            var up = AccountSettingsParts.GlyphButton(Ctx, WinoIconGlyph.ChevronUp, AccountSettingsParts.ActionLabel(Translator.MailFilters_MoveUp, entry.Title));
            up.Enabled = index > 0;
            up.Alpha = up.Enabled ? 1f : 0.38f;
            rows.Views.Click(up, () => Move(entry, -1));
            row.AddView(up);
            var down = AccountSettingsParts.GlyphButton(Ctx, WinoIconGlyph.ChevronDown, AccountSettingsParts.ActionLabel(Translator.MailFilters_MoveDown, entry.Title));
            down.Enabled = index < count - 1;
            down.Alpha = down.Enabled ? 1f : 0.38f;
            rows.Views.Click(down, () => Move(entry, 1));
            row.AddView(down);
        }

        var action = AccountSettingsParts.PillButton(Ctx, member ? Translator.Buttons_Remove : Translator.Buttons_Add, member ? WinoIconGlyph.None : WinoIconGlyph.Add);
        action.ContentDescription = member ? entry.RemoveAutomationName : entry.AddAutomationName;
        rows.Views.Click(action, () => AccountSettingsParts.Execute(member ? VM.RemoveMemberCommand : VM.AddMemberCommand, entry, ReportError));
        row.AddView(action, AccountSettingsParts.RowItem(Ctx, marginStart: Resource.Dimension.wino_space_xs));

        host.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return host;
    }

    private void Move(AccountGroupEntryViewModel entry, int offset)
    {
        var members = VM.MemberEntries;
        int from = members.IndexOf(entry);
        int to = from + offset;
        if (from < 0 || to < 0 || to >= members.Count) return;
        members.Move(from, to);
    }
}
