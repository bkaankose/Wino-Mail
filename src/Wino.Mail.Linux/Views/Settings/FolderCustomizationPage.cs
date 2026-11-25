using System.Collections.ObjectModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// An account's folder list (Windows FolderCustomizationPage): Pinned, Categories (Gmail) and More, each a drag list.
/// Rows move by drag, Alt+Up / Alt+Down and Move up / Move down; Pinned and More accept each other's rows, Categories
/// only reorders itself. Every change is written at once and the sidebar follows through the folder service's messages.
/// The navigation parameter is the account id.
/// </summary>
internal sealed class FolderCustomizationPage(FolderCustomizationPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<FolderCustomizationPageViewModel>(viewModel, services), ISettingsPageTitleSource
{
    private readonly List<Section> _sections = [];
    private readonly List<WinoSettingsGroup> _content = [];
    private WinoSettingsGroup? _missing;
    private Section? _categories;

#if DEBUG
    internal static FolderCustomizationPage? DebugCurrent { get; private set; }
#endif

    public string? PageTitle => Translator.FolderCustomization_Title;

    public string? PageDescription => string.IsNullOrWhiteSpace(ViewModel.AccountName) ? null : ViewModel.AccountName;

    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var account = WinoSettingsRows.Action(string.Empty, null, WinoIconGlyph.Folder);
        account.SetTitleLines(1);
        var reset = WinoSettingsRows.Suffix(account, Settings.Button(Translator.FolderCustomization_Reset, vm.ResetCommand));
        reset.SetValign(Gtk.Align.Center);
        Settings.Bind(vm, nameof(vm.AccountName), static s => s.AccountName, name =>
        {
            account.SetTitle(name ?? string.Empty);
            PageTitleChanged?.Invoke(this, EventArgs.Empty);
        });
        _content.Add(Page.AddGroup(null, Translator.FolderCustomization_Description, account));

        var shared = new WinoDragGroup();
        var pinned = AddSection(Translator.FolderCustomization_SectionPinned, Translator.FolderCustomization_SectionPinnedDescription, vm.PinnedFolders, shared, true);
        var categories = _categories = AddSection(Translator.FolderCustomization_SectionCategories, Translator.FolderCustomization_SectionCategoriesDescription, vm.CategoryFolders, new WinoDragGroup(), false);
        var more = AddSection(Translator.FolderCustomization_SectionMore, Translator.FolderCustomization_SectionMoreDescription, vm.MoreFolders, shared, true);
        shared.CanDrop = static (_, _) => true;
        shared.Dropped = Dropped;
        categories.List.Group.Dropped = Dropped;
        Settings.Visible(categories.Group.Root, vm, nameof(vm.IsGmailAccount), static s => s.IsGmailAccount);

        var footer = Page.AddGroup(null, null);
        footer.HidesWhenEmpty = false;
        footer.Description = Translator.FolderCustomization_Footer;
        _content.Add(footer);

        foreach (var section in (Section[])[pinned, categories, more])
        {
            var current = section;
            Settings.Collection(section.Items, () => Rebuild(current));
        }

        var missing = Adw.StatusPage.New();
        missing.SetTitle(Translator.GeneralTitle_Error);
        missing.AddCssClass("compact");
        _missing = Page.AddGroup(null, null, WinoSettingsRows.Custom(missing, Translator.GeneralTitle_Error));
        _missing.Root.SetVisible(false);
        foreach (var group in _content) group.Root.SetVisible(false);
#if DEBUG
        DebugCurrent = this;
#endif
    }

    protected override async Task NavigateAsync(NavigationMode mode, object? parameter)
    {
        await ViewModel.InitializeAsync(mode, parameter);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased) return;
            var loaded = ViewModel.HasLoadedAccount;
            foreach (var group in _content) group.Root.SetVisible(loaded);
            _categories?.Group.Root.SetVisible(loaded && ViewModel.IsGmailAccount);
            _missing?.Root.SetVisible(!loaded);
        });
    }

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
#endif
        foreach (var section in _sections)
        {
            foreach (var row in section.Rows.Values) row.Scope.Dispose();
            section.Rows.Clear();
            section.List.Dispose();
        }
        return base.DeactivateAsync();
    }

    private Section AddSection(string title, string description, ObservableCollection<FolderCustomizationItemViewModel> items, WinoDragGroup group, bool pinnable)
    {
        var settingsGroup = Page.AddGroup(title, description);
        settingsGroup.HidesWhenEmpty = false;
        var count = Gtk.Label.New(null);
        count.AddCssClass("dim-label");
        count.AddCssClass("caption");
        settingsGroup.HeaderSuffix = count;
        var list = new WinoDragList(group, title) { ShowsMoveButtons = true, Tag = title };
        var empty = Adw.ActionRow.New();
        empty.SetUseMarkup(false);
        empty.SetTitle(Translator.FolderCustomization_EmptySection);
        empty.AddCssClass("dim-label");
        empty.SetActivatable(false);
        list.SetEmptyRow(empty);
        settingsGroup.Root.Add(list.Root);
        var section = new Section(settingsGroup, list, items, count, pinnable, title == Translator.FolderCustomization_SectionPinned);
        _sections.Add(section);
        _content.Add(settingsGroup);
        return section;
    }

    private void Rebuild(Section section)
    {
        if (IsReleased) return;
        var present = new HashSet<FolderCustomizationItemViewModel>(section.Items, ReferenceEqualityComparer.Instance);
        var rows = new List<WinoDragRow>(section.Items.Count);
        foreach (var item in section.Items)
        {
            if (!section.Rows.TryGetValue(item, out var row)) section.Rows[item] = row = BuildRow(section, item);
            rows.Add(row.DragRow);
        }
        section.List.SetRows(rows);
        foreach (var stale in section.Rows.Keys.Where(item => !present.Contains(item)).ToList())
        {
            section.Rows[stale].Scope.Dispose();
            section.Rows.Remove(stale);
        }
        RefreshCount(section);
    }

    private FolderRow BuildRow(Section section, FolderCustomizationItemViewModel item)
    {
        var rows = Settings.Child();
        var name = item.FolderName ?? string.Empty;
        var row = WinoSettingsRows.Action(name, null, SpecialFolderGlyphs.GetGlyphOrFolder(item.Folder));
        row.SetTitleLines(1);

        var system = Tag(Translator.FolderCustomization_SystemTag);
        system.SetVisible(item.IsSystemFolder);
        WinoSettingsRows.Suffix(row, system);
        var hidden = Tag(Translator.FolderCustomization_HiddenTag);
        WinoSettingsRows.Suffix(row, hidden);

        // A level of a shared tree can only be hidden (macOS).
        Gtk.Button? rename = null;
        if (item.CanPinAndRename)
        {
            rename = WinoSettingsRows.Suffix(row, WinoSettingsRows.GlyphButton(WinoIconGlyph.Rename, $"{Translator.FolderOperation_Rename}: {name}"));
            rows.Own(GtkSignals.OnClicked(rename, () => Observe(ViewModel.RenameFolderAsync(item))));
        }

        var hide = WinoSettingsRows.Suffix(row, WinoIcons.ToggleButton(WinoIconGlyph.EyeOff, Translator.FolderCustomization_Hide));
        hide.SetValign(Gtk.Align.Center);
        var applying = false;
        rows.Own(GtkSignals.OnToggled(hide, () =>
        {
            if (applying || hide.GetActive() == item.IsHidden) return;
            var operation = ViewModel.ToggleHiddenAsync(item);
            Observe(operation);
        }));

        Gtk.Button? pin = null;
        if (section.Pinnable && item.CanPinAndRename)
        {
            pin = WinoSettingsRows.Suffix(row, WinoSettingsRows.GlyphButton(WinoIconGlyph.Pin,
                $"{(section.IsPinned ? Translator.FolderCustomization_Unpin : Translator.FolderCustomization_Pin)}: {name}"));
            rows.Own(GtkSignals.OnClicked(pin, () => Observe(ViewModel.TogglePinAsync(item))));
        }

        var dragRow = new WinoDragRow(row, name);
        var entry = new FolderRow(rows.Scope, dragRow);

        rows.Bind(item, nameof(item.FolderName), static s => s.FolderName, folderName =>
        {
            var text = folderName ?? string.Empty;
            row.SetTitle(text);
            if (rename is not null) WinoA11y.Rename(rename, $"{Translator.FolderOperation_Rename}: {text}");
            if (pin is not null)
                WinoA11y.Rename(pin, $"{(section.IsPinned ? Translator.FolderCustomization_Unpin : Translator.FolderCustomization_Pin)}: {text}");
            UpdateHidden(item, row, hidden, hide, ref applying);
        });
        rows.Bind(item, nameof(item.IsHidden), static s => s.IsHidden, _ =>
        {
            UpdateHidden(item, row, hidden, hide, ref applying);
            RefreshCount(section);
        });
        return entry;
    }

    private static void UpdateHidden(FolderCustomizationItemViewModel item, Adw.ActionRow row, Gtk.Label tag, Gtk.ToggleButton hide, ref bool applying)
    {
        var name = item.FolderName ?? string.Empty;
        var isHidden = item.IsHidden;
        tag.SetVisible(isHidden);
        if (isHidden) row.AddCssClass("dim-label");
        else row.RemoveCssClass("dim-label");
        applying = true;
        hide.SetActive(isHidden);
        applying = false;
        var action = isHidden ? Translator.FolderCustomization_Show : Translator.FolderCustomization_Hide;
        WinoA11y.Label(hide, $"{action}: {name}");
        hide.SetTooltipText(action);
        WinoA11y.Rename(row, isHidden ? $"{name}, {Translator.FolderCustomization_HiddenTag}" : name);
    }

    private static Gtk.Label Tag(string text)
    {
        var tag = Gtk.Label.New(text);
        tag.AddCssClass("caption");
        tag.AddCssClass("wino-chip");
        tag.SetValign(Gtk.Align.Center);
        return tag;
    }

    private static void RefreshCount(Section section)
    {
        var text = string.Format(Translator.FolderCustomization_FolderCountFormat, section.Items.Count);
        var hidden = section.Items.Count(item => item.IsHidden);
        if (hidden > 0) text += " · " + string.Format(Translator.FolderCustomization_HiddenCountFormat, hidden);
        section.Count.SetText(text);
    }

    private void Dropped(WinoDragList source, int from, WinoDragList target, int to)
    {
        if (_sections.FirstOrDefault(section => ReferenceEquals(section.List, source)) is not { } sourceSection
            || _sections.FirstOrDefault(section => ReferenceEquals(section.List, target)) is not { } targetSection)
            return;
        if (from < 0 || from >= sourceSection.Items.Count) return;
        if (ReferenceEquals(sourceSection, targetSection))
        {
            sourceSection.Items.Move(from, Math.Clamp(to, 0, sourceSection.Items.Count - 1));
        }
        else
        {
            var item = sourceSection.Items[from];
            if (!item.CanPinAndRename) return;
            sourceSection.Items.RemoveAt(from);
            targetSection.Items.Insert(Math.Clamp(to, 0, targetSection.Items.Count), item);
        }
        Observe(ViewModel.PersistLayoutAsync());
    }

#if DEBUG
    internal bool DebugMove(string folderName, int offset)
    {
        foreach (var section in _sections)
        {
            var index = section.Items.ToList().FindIndex(item => string.Equals(item.FolderName, folderName, StringComparison.Ordinal));
            if (index >= 0) return section.List.Move(index, index + offset);
        }
        return false;
    }

    internal string DebugDescribe()
        => string.Join('\n', _sections.Select(section => $"{section.Group.Root.GetTitle()}|{section.Group.Root.GetVisible()}|{section.Count.GetText()}|"
            + string.Join(", ", section.Items.Select(item => item.FolderName + (item.IsHidden ? " (hidden)" : string.Empty))))) + "\n";
#endif

    private sealed record Section(WinoSettingsGroup Group, WinoDragList List, ObservableCollection<FolderCustomizationItemViewModel> Items,
        Gtk.Label Count, bool Pinnable, bool IsPinned)
    {
        public Dictionary<FolderCustomizationItemViewModel, FolderRow> Rows { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed record FolderRow(BindingScope Scope, WinoDragRow DragRow);
}
