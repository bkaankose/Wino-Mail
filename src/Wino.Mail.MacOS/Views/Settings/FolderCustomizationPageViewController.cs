using System.Collections.ObjectModel;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Views.Shell;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Customize folder list (Manage accounts › account › Customize folder list, Windows
/// FolderCustomizationPage): the account line with Reset to defaults, then Pinned, Categories (Gmail
/// only) and More as expanded sections. Each folder row has a drag handle, the special-folder glyph,
/// the name, System and Hidden tags, the hide toggle and Pin to top / Move to More. Rows reorder by
/// drag inside a section and move between Pinned and More by drag; every change persists at once.
/// </summary>
public sealed class FolderCustomizationPageViewController(FolderCustomizationPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<FolderCustomizationPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private const string PinnedKey = "pinned";
    private const string CategoriesKey = "categories";
    private const string MoreKey = "more";
    private const double RowHeight = 38;

    private readonly SettingsDragGroup _group = new();
    private readonly Dictionary<string, (SettingsDragList<FolderCustomizationItemViewModel> List, ObservableCollection<FolderCustomizationItemViewModel> Items, NSTextField Count)> _sections = [];

    public string? PageTitle => Translator.FolderCustomization_Title;
    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        var account = Bind.Label(vm, nameof(vm.AccountName), s => s.AccountName, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        var description = WinoStyle.Label(Translator.FolderCustomization_Description, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        description.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var text = WinoLayout.VStack(2, account, description);
        text.Alignment = NSLayoutAttribute.Leading;
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        var reset = Bind.Button(Translator.FolderCustomization_Reset, vm.ResetCommand, icon: WinoIconGlyph.ArrowReset);
        var header = WinoLayout.HStack(WinoStyle.Space3, text, reset);
        header.Alignment = NSLayoutAttribute.CenterY;
        header.EdgeInsets = new NSEdgeInsets(0, 1, 8, 0);
        Add(header);

        _group.CanDrop = (source, target) => ReferenceEquals(source, target)
            || (source.Key is PinnedKey or MoreKey && target.Key is PinnedKey or MoreKey);
        _group.Dropped = OnDropped;

        Add(Section(PinnedKey, vm.PinnedFolders, Translator.FolderCustomization_SectionPinned, Translator.FolderCustomization_SectionPinnedDescription, WinoIconGlyph.Pin));
        var categories = Add(Section(CategoriesKey, vm.CategoryFolders, Translator.FolderCustomization_SectionCategories, Translator.FolderCustomization_SectionCategoriesDescription, WinoIconGlyph.Tag));
        Bind.Visible(categories, vm, nameof(vm.IsGmailAccount), s => s.IsGmailAccount);
        Add(Section(MoreKey, vm.MoreFolders, Translator.FolderCustomization_SectionMore, Translator.FolderCustomization_SectionMoreDescription, WinoIconGlyph.More));

        var footer = WinoStyle.Label(Translator.FolderCustomization_Footer, WinoStyle.Caption, WinoStyle.TertiaryText, 0);
        var footerHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(footer, footerHost, 6, 1, 0, 0);
        Add(footerHost);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeAsync(mode, parameter!);

    private WinoSettingsExpander Section(string key, ObservableCollection<FolderCustomizationItemViewModel> items, string title, string description, WinoIconGlyph icon)
    {
        var count = Caption(string.Empty);
        var expander = new WinoSettingsExpander(title, description, icon, count, isExpanded: true);
        var list = new SettingsDragList<FolderCustomizationItemViewModel>(key, items, _group, (item, _) => FolderRow(key, item), RowHeight);
        list.SetEmptyView(EmptyZone(), 48);
        expander.Add(list, 0, 0, 0, 0);
        _sections[key] = (list, items, count);
        Bindings.Own(list.Observe(items, Dispatcher, () => UpdateCount(key)));
        return expander;
    }

    /// <summary>The empty section: a dashed drop zone inset inside the section body.</summary>
    private static NSView EmptyZone()
    {
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(new DashedDropZoneView(Translator.FolderCustomization_EmptySection), host, 8, 14, 8, 12);
        return host;
    }

    private void UpdateCount(string key)
    {
        var (_, items, count) = _sections[key];
        var hidden = items.Count(item => item.IsHidden);
        var text = string.Format(Translator.FolderCustomization_FolderCountFormat, items.Count);
        if (hidden > 0) text += " · " + string.Format(Translator.FolderCustomization_HiddenCountFormat, hidden);
        count.StringValue = text;
    }

    /// <summary>
    /// One 38pt folder row: hairline above, drag handle, glyph, name, tags, hide toggle, then the pin
    /// action (Pinned: Move to More, More: Pin to top, Categories: an empty slot). Hidden rows dim to 55%.
    /// </summary>
    private NSView FolderRow(string key, FolderCustomizationItemViewModel item)
    {
        var row = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        var separator = new WinoSeparator { Fill = WinoSettingsStyle.CardStroke };
        row.AddSubview(separator);

        var handle = new WinoIconView(WinoIconGlyph.ReOrderDotsVertical, 14, WinoStyle.TertiaryText);
        WinoLayout.Size(handle, 14, 14);
        var glyph = ShellPaneRows.FolderGlyph(item.SpecialFolderType);
        var icon = new WinoIconView(glyph == WinoIconGlyph.None ? WinoIconGlyph.Folder : glyph, 16);
        WinoLayout.Size(icon, 16, 16);
        var name = WinoStyle.Label(item.FolderName, WinoStyle.Body, WinoStyle.PrimaryText);
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        name.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        var content = WinoLayout.HStack(WinoStyle.Space3, handle, icon, name);
        content.Alignment = NSLayoutAttribute.CenterY;
        if (item.IsSystemFolder) content.AddArrangedSubview(SettingsRowParts.Tag(Translator.FolderCustomization_SystemTag));
        if (item.IsHidden) content.AddArrangedSubview(SettingsRowParts.Tag(Translator.FolderCustomization_HiddenTag));

        var hide = new SettingsGlyphButton(WinoIconGlyph.EyeOff, item.IsHidden ? Translator.FolderCustomization_Show : Translator.FolderCustomization_Hide)
        {
            IsToggled = item.IsHidden
        };
        hide.Activated += (_, _) => ToggleHidden(key, item);
        content.AddArrangedSubview(hide);

        if (key == CategoriesKey)
        {
            content.AddArrangedSubview(SettingsRowParts.Slot());
        }
        else
        {
            var pinned = key == PinnedKey;
            var pin = new SettingsGlyphButton(pinned ? WinoIconGlyph.UnPin : WinoIconGlyph.Pin,
                pinned ? Translator.FolderCustomization_Unpin : Translator.FolderCustomization_Pin);
            pin.Activated += (_, _) => Run(ViewModel.TogglePinAsync(item));
            content.AddArrangedSubview(pin);
        }

        row.AddSubview(content);
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        NSLayoutConstraint.ActivateConstraints(
        [
            separator.TopAnchor.ConstraintEqualTo(row.TopAnchor),
            separator.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor),
            separator.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor),
            content.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor, 14),
            content.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, -12),
            content.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor, 0.5f)
        ]);
        content.AlphaValue = item.IsHidden ? 0.55f : 1;
        WinoAccessibility.Label(row, item.IsHidden ? $"{item.FolderName}, {Translator.FolderCustomization_HiddenTag}" : item.FolderName);
        return row;
    }

    private void ToggleHidden(string key, FolderCustomizationItemViewModel item)
    {
        // The ViewModel flips IsHidden before it awaits the database write, so the row can redraw now.
        var operation = ViewModel.ToggleHiddenAsync(item);
        var list = _sections[key].List;
        list.ReloadRow(item);
        UpdateCount(key);
        Run(operation);
    }

    private void OnDropped(ISettingsDragList source, int from, ISettingsDragList target, int to)
    {
        var sourceItems = _sections[source.Key].Items;
        var targetItems = _sections[target.Key].Items;
        if (from < 0 || from >= sourceItems.Count) return;

        if (ReferenceEquals(sourceItems, targetItems))
        {
            sourceItems.Move(from, Math.Clamp(to, 0, sourceItems.Count - 1));
        }
        else
        {
            var item = sourceItems[from];
            sourceItems.RemoveAt(from);
            targetItems.Insert(Math.Clamp(to, 0, targetItems.Count), item);
        }

        // Same as Windows ListView.DropCompleted: the collections hold the new layout, persist it.
        Run(ViewModel.PersistLayoutAsync());
    }

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (Exception exception) { ReportError(exception); }
    }
}
