using System.Collections.ObjectModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Creates or edits an account group (Windows AccountGroupDetailsPage): the name with the tinted icon preview, Combine
/// folders, the colour and icon grids, the ordered accounts In group (drag or Alt+Up / Alt+Down, Remove) and the Available
/// accounts (Add, and the group they are in now), Delete for a saved group, then Cancel and Save in the bottom bar.
/// </summary>
internal sealed class AccountGroupDetailsPage(AccountGroupDetailsPageViewModel viewModel, WinoViewServices services, IPictureStorageService pictures)
    : SettingsPageView<AccountGroupDetailsPageViewModel>(viewModel, services), ISettingsPageTitleSource
{
    private readonly List<WinoIcon> _iconChoices = [];
    private readonly Dictionary<AccountGroupEntryViewModel, EntryRow> _memberRows = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AccountGroupEntryViewModel, EntryRow> _availableRows = new(ReferenceEqualityComparer.Instance);
    private WinoDragList? _members;
    private WinoDragList? _available;
    private WinoChoiceGrid? _colors;
    private WinoChoiceGrid? _icons;

#if DEBUG
    internal static AccountGroupDetailsPage? DebugCurrent { get; private set; }
#endif

    public string? PageTitle => ViewModel.IsNewGroup || string.IsNullOrWhiteSpace(ViewModel.GroupName)
        ? Translator.AccountGroupDetailsPage_NewTitle
        : ViewModel.GroupName;

    public string? PageDescription => ViewModel.IsNewGroup ? Translator.AccountGroupDetailsPage_NewDescription : null;

    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Settings.Bind(vm, nameof(vm.IsNewGroup), static s => s.IsNewGroup, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Settings.Bind(vm, nameof(vm.GroupName), static s => s.GroupName, _ =>
        {
            if (!vm.IsNewGroup) PageTitleChanged?.Invoke(this, EventArgs.Empty);
        });

        var name = Settings.Invalid(Settings.Text(vm, Translator.AccountGroupDetailsPage_NameHeader, nameof(vm.GroupName),
            static s => s.GroupName, static (s, v) => s.GroupName = v), vm, nameof(vm.IsNameMissing), static s => s.IsNameMissing);
        var preview = new WinoIcon(vm.SelectedIconGlyph, 20);
        preview.Root.SetValign(Gtk.Align.Center);
        name.AddPrefix(preview.Root);
        Settings.Bind(vm, nameof(vm.SelectedIconGlyph), static s => s.SelectedIconGlyph, preview.SetIcon);
        var nameGroup = AddGroup(null, name,
            Settings.Switch(vm, Translator.AccountGroupDetailsPage_CombineFoldersHeader, Translator.AccountGroupDetailsPage_CombineFoldersDescription,
                WinoIconGlyph.PeopleLink, nameof(vm.CombineFolders), static s => s.CombineFolders, static (s, v) => s.CombineFolders = v));
        Settings.Bind(vm, nameof(vm.IsNameMissing), static s => s.IsNameMissing,
            missing => nameGroup.Description = missing ? Translator.AccountGroupDetailsPage_NameRequired : null);

        _colors = ColorGrid();
        Page.AddGroup(Translator.AccountGroupDetailsPage_ColorHeader, Translator.AccountGroupDetailsPage_ColorDescription,
            WinoSettingsRows.Custom(_colors.Root, Translator.AccountGroupDetailsPage_ColorHeader));
        _icons = IconGrid();
        Page.AddGroup(Translator.AccountGroupDetailsPage_IconHeader, Translator.AccountGroupDetailsPage_IconDescription,
            WinoSettingsRows.Custom(_icons.Root, Translator.AccountGroupDetailsPage_IconHeader));
        Settings.Bind(vm, nameof(vm.SelectedColorKey), static s => s.SelectedColorKey, key =>
        {
            var tint = AccountGroupTint.Hex(key);
            preview.TintHex = tint;
            foreach (var choice in _iconChoices) choice.TintHex = tint;
        });

        BuildMembers();

        var delete = WinoSettingsRows.Action(Translator.AccountGroupDetailsPage_DeleteGroup, Translator.AccountGroupDetailsPage_DeleteGroupDescription, WinoIconGlyph.Delete);
        var deleteButton = WinoSettingsRows.Suffix(delete, Settings.Button(Translator.AccountGroupDetailsPage_DeleteGroup, vm.DeleteGroupCommand, destructive: true));
        delete.SetActivatableWidget(deleteButton);
        Settings.Visible(AddGroup(null, delete).Root, vm, nameof(vm.IsExistingGroup), static s => s.IsExistingGroup);

        Page.SetBottomBar(BuildCommands());
        Settings.Enabled(Page.Page, vm, nameof(vm.IsBusy), static s => !s.IsBusy);
#if DEBUG
        DebugCurrent = this;
#endif
    }

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
#endif
        _colors?.Dispose();
        _icons?.Dispose();
        foreach (var row in _memberRows.Values.Concat(_availableRows.Values)) row.Scope.Dispose();
        _memberRows.Clear();
        _availableRows.Clear();
        _members?.Dispose();
        _available?.Dispose();
        return base.DeactivateAsync();
    }

    private WinoChoiceGrid ColorGrid()
    {
        var vm = ViewModel;
        var grid = new WinoChoiceGrid(Translator.AccountGroupDetailsPage_ColorHeader);
        foreach (var option in vm.ColorOptions)
        {
            var sample = Gtk.Label.New("Aa");
            sample.AddCssClass("heading");
            if (WinoTint.ClassFor(AccountGroupTint.Hex(option.Key)) is { } tint) sample.AddCssClass(tint);
            grid.Add(sample, option.DisplayName);
        }
        grid.Selected += index =>
        {
            if (index >= 0 && index < vm.ColorOptions.Length) vm.SelectedColorOption = vm.ColorOptions[index];
        };
        Settings.Bind(vm, nameof(vm.SelectedColorOption), static s => s.SelectedColorOption,
            selected => grid.SelectedIndex = selected is null ? -1 : Array.IndexOf(vm.ColorOptions, selected));
        return grid;
    }

    private WinoChoiceGrid IconGrid()
    {
        var vm = ViewModel;
        var grid = new WinoChoiceGrid(Translator.AccountGroupDetailsPage_IconHeader);
        foreach (var option in vm.IconOptions)
        {
            var glyph = new WinoIcon(option.Glyph, 20);
            _iconChoices.Add(glyph);
            grid.Add(glyph.Root, option.DisplayName);
        }
        grid.Selected += index =>
        {
            if (index >= 0 && index < vm.IconOptions.Length) vm.SelectedIconOption = vm.IconOptions[index];
        };
        Settings.Bind(vm, nameof(vm.SelectedIconOption), static s => s.SelectedIconOption,
            selected => grid.SelectedIndex = selected is null ? -1 : Array.IndexOf(vm.IconOptions, selected));
        return grid;
    }

    private void BuildMembers()
    {
        var vm = ViewModel;
        var group = new WinoDragGroup();
        var members = _members = new WinoDragList(group, Translator.AccountGroupDetailsPage_InGroupHeader) { ShowsMoveButtons = true, Tag = "members" };
        var available = _available = new WinoDragList(group, Translator.AccountGroupDetailsPage_AvailableHeader) { ShowsHandles = false, Tag = "available" };
        group.CanDrop = (source, target) => !(ReferenceEquals(source, available) && ReferenceEquals(target, available));
        group.Dropped = (source, from, target, to) =>
        {
            if (ReferenceEquals(source, members) && ReferenceEquals(target, members))
            {
                if (from < vm.MemberEntries.Count) vm.MemberEntries.Move(from, Math.Clamp(to, 0, vm.MemberEntries.Count - 1));
            }
            else if (ReferenceEquals(source, members) && from < vm.MemberEntries.Count)
                Execute(vm.RemoveMemberCommand, vm.MemberEntries[from]);
            else if (ReferenceEquals(source, available) && from < vm.AvailableEntries.Count)
            {
                var entry = vm.AvailableEntries[from];
                Execute(vm.AddMemberCommand, entry);
                var index = vm.MemberEntries.IndexOf(entry);
                if (index >= 0) vm.MemberEntries.Move(index, Math.Clamp(to, 0, vm.MemberEntries.Count - 1));
            }
        };

        members.SetEmptyRow(DimRow(Translator.AccountGroupDetailsPage_MemberRequired));
        available.SetEmptyRow(DimRow(Translator.AccountGroupDetailsPage_AvailableEmpty));

        var inGroup = Page.AddGroup(Translator.AccountGroupDetailsPage_InGroupHeader, Translator.AccountGroupDetailsPage_InGroupDescription);
        inGroup.HidesWhenEmpty = false;
        inGroup.Root.Add(members.Root);
        var required = Gtk.Label.New(Translator.AccountGroupDetailsPage_MemberRequired);
        required.AddCssClass("error");
        required.AddCssClass("caption");
        required.SetXalign(0);
        required.SetWrap(true);
        required.SetMarginTop(6);
        inGroup.Root.Add(required);
        Settings.Visible(required, vm, nameof(vm.IsMemberMissing), static s => s.IsMemberMissing);

        var availableGroup = Page.AddGroup(Translator.AccountGroupDetailsPage_AvailableHeader, Translator.AccountGroupDetailsPage_AvailableDescription);
        availableGroup.HidesWhenEmpty = false;
        availableGroup.Root.Add(available.Root);

        Settings.Collection(vm.MemberEntries, () => Rebuild(members, vm.MemberEntries, _memberRows, true));
        Settings.Collection(vm.AvailableEntries, () => Rebuild(available, vm.AvailableEntries, _availableRows, false));
    }

    private void Rebuild(WinoDragList list, ObservableCollection<AccountGroupEntryViewModel> entries, Dictionary<AccountGroupEntryViewModel, EntryRow> cache, bool member)
    {
        if (IsReleased) return;
        var rows = new List<WinoDragRow>(entries.Count);
        foreach (var entry in entries)
        {
            if (!cache.TryGetValue(entry, out var row)) cache[entry] = row = BuildEntry(entry, member);
            rows.Add(row.DragRow);
        }
        list.SetRows(rows);
        var present = new HashSet<AccountGroupEntryViewModel>(entries, ReferenceEqualityComparer.Instance);
        foreach (var stale in cache.Keys.Where(entry => !present.Contains(entry)).ToList())
        {
            cache[stale].Scope.Dispose();
            cache.Remove(stale);
        }
    }

    private EntryRow BuildEntry(AccountGroupEntryViewModel entry, bool member)
    {
        var rows = Settings.Child();
        var title = entry.Title ?? string.Empty;
        var row = WinoSettingsRows.Action(title, entry.Description);
        row.SetTitleLines(1);
        row.AddPrefix(new WinoAccountIcon(28) { Account = MailAccountIconInfoFactory.Create(entry.Account, pictures), AccessibleName = title }.Root);
        if (!member && entry.IsInOtherGroup)
        {
            var tag = Gtk.Label.New(entry.OtherGroupText);
            tag.AddCssClass("caption");
            tag.AddCssClass("wino-chip");
            WinoSettingsRows.Suffix(row, tag);
        }
        var action = member
            ? rows.Button(Translator.Buttons_Remove, ViewModel.RemoveMemberCommand, () => entry)
            : rows.Button(Translator.Buttons_Add, ViewModel.AddMemberCommand, () => entry, icon: WinoIconGlyph.Add);
        WinoA11y.Rename(action, member ? entry.RemoveAutomationName : entry.AddAutomationName);
        WinoSettingsRows.Suffix(row, action);
        WinoA11y.Rename(row, string.IsNullOrEmpty(entry.Description) ? title : $"{title}, {entry.Description}");
        return new EntryRow(rows.Scope, new WinoDragRow(row, title));
    }

    private Gtk.ActionBar BuildCommands()
    {
        var vm = ViewModel;
        var bar = Gtk.ActionBar.New();
        var spinner = Adw.Spinner.New();
        Settings.Visible(spinner, vm, nameof(vm.IsBusy), static s => s.IsBusy);
        bar.PackStart(spinner);
        bar.PackEnd(Settings.Button(Translator.Buttons_Save, vm.SaveCommand, suggested: true));
        bar.PackEnd(Settings.Button(Translator.Buttons_Cancel, vm.CancelCommand));
        return bar;
    }

    private static Adw.ActionRow DimRow(string text)
    {
        var row = Adw.ActionRow.New();
        row.SetUseMarkup(false);
        row.SetTitle(text);
        row.AddCssClass("dim-label");
        row.SetActivatable(false);
        return row;
    }

    private void Execute(System.Windows.Input.ICommand command, object parameter)
    {
        try
        {
            if (command.CanExecute(parameter)) command.Execute(parameter);
        }
        catch (Exception exception) { ReportError(exception); }
    }

#if DEBUG
    internal string DebugDescribe()
        => $"new {ViewModel.IsNewGroup}|name {ViewModel.GroupName}|color {ViewModel.SelectedColorKey}|icon {ViewModel.SelectedIconOption?.Icon}|combine {ViewModel.CombineFolders}\n"
           + $"colors: {_colors?.Describe()}\nicons: {_icons?.Describe()}\n{_members?.Describe()}\n{_available?.Describe()}\n";

    internal bool DebugPick(string grid, int index)
    {
        var target = grid == "color" ? _colors : _icons;
        if (target is null || index < 0 || index >= target.Count) return false;
        target.Activate(index);
        return true;
    }
#endif

    private sealed record EntryRow(BindingScope Scope, WinoDragRow DragRow);
}
