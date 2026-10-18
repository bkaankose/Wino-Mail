using System.Collections.ObjectModel;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Customize folder list (Mac FolderCustomizationPageViewController, Windows FolderCustomizationPage): the account
/// line with Reset to defaults, then Pinned, Categories (Gmail only) and More as expanded sections. Each folder row has
/// the special-folder glyph, the name, System and Hidden tags, the hide toggle and Pin to top / Move to More, with the
/// system reorder handle on its trailing edge. Rows reorder by dragging the handle inside a section; the pin button
/// moves them between Pinned and More. Every change persists at once.
/// </summary>
public sealed class FolderCustomizationPageViewController(FolderCustomizationPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<FolderCustomizationPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private const string PinnedKey = "pinned";
    private const string CategoriesKey = "categories";
    private const string MoreKey = "more";
    private const double RowHeight = 48;

    private readonly SettingsDragGroup _group = new();
    private readonly Dictionary<string, (SettingsDragList<FolderCustomizationItemViewModel> List, ObservableCollection<FolderCustomizationItemViewModel> Items, UILabel Count)> _sections = [];

    public string? PageTitle => Translator.FolderCustomization_Title;
    /// <summary>The title is fixed.</summary>
    public event EventHandler? PageTitleChanged { add { } remove { } }

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // The account the list belongs to; iOS shows no page description under it.
        var account = Bind.Label(vm, nameof(vm.AccountName), s => s.AccountName, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        Bind.Visible(account, vm, nameof(vm.AccountName), s => !string.IsNullOrWhiteSpace(s.AccountName));
        var text = WinoLayout.VStack(2, account);
        text.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);
        var reset = Bind.Button(Translator.FolderCustomization_Reset, vm.ResetCommand, icon: WinoIconGlyph.ArrowReset);
        WinoLayout.Insets(text, 0, WinoSettingsStyle.CardPadding, 0, 0);
        Add(text);
        AddGroup(null, ActionRow(reset));

        _group.CanDrop = (source, target) => ReferenceEquals(source, target)
            || (source.Key is PinnedKey or MoreKey && target.Key is PinnedKey or MoreKey);
        _group.Dropped = OnDropped;

        Add(Section(PinnedKey, vm.PinnedFolders, Translator.FolderCustomization_SectionPinned, Translator.FolderCustomization_SectionPinnedDescription, WinoIconGlyph.Pin));
        var categories = Add(Section(CategoriesKey, vm.CategoryFolders, Translator.FolderCustomization_SectionCategories, Translator.FolderCustomization_SectionCategoriesDescription, WinoIconGlyph.Tag));
        Bind.Visible(categories, vm, nameof(vm.IsGmailAccount), s => s.IsGmailAccount);
        Add(Section(MoreKey, vm.MoreFolders, Translator.FolderCustomization_SectionMore, Translator.FolderCustomization_SectionMoreDescription, WinoIconGlyph.More));

        var footer = WinoStyle.Label(Translator.FolderCustomization_Footer, WinoStyle.Caption, WinoStyle.TertiaryText, 0);
        var footerHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(footer, footerHost, 6, 1, 0, 0);
        Add(footerHost);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeAsync(mode, parameter);

    /// <summary>
    /// One section as a titled group (settings rows, 15 Oct 2026): the folder list stays on the page, not behind a push,
    /// so folders still drag between Pinned and More. The count and the description follow the list.
    /// </summary>
    private WinoSettingsGroup Section(string key, ObservableCollection<FolderCustomizationItemViewModel> items, string title, string description, WinoIconGlyph icon)
    {
        _ = icon;
        var count = Caption(string.Empty);
        var group = new WinoSettingsGroup(title, description);
        var list = new SettingsDragList<FolderCustomizationItemViewModel>(key, items, _group, (item, _) => FolderRow(key, item), RowHeight);
        list.SetEmptyView(EmptyZone(), 48);
        var surface = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.GroupFill,
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = WinoSettingsStyle.CardRadius,
            ClipsToBounds = true
        };
        WinoLayout.Fill(list, surface);
        group.Add(surface);
        var countHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(count, countHost, 0, WinoSettingsStyle.CardPadding, 0, WinoSettingsStyle.CardPadding);
        group.Add(countHost);
        _sections[key] = (list, items, count);
        Bindings.Own(list.Observe(items, Dispatcher, () => UpdateCount(key)));
        return group;
    }

    /// <summary>The empty section: a dashed placeholder inset inside the section body.</summary>
    private static UIView EmptyZone()
    {
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(new DashedDropZoneView(Translator.FolderCustomization_EmptySection), host, 8, 14, 8, 12);
        return host;
    }

    private void UpdateCount(string key)
    {
        var (_, items, count) = _sections[key];
        var hidden = items.Count(item => item.IsHidden);
        var text = string.Format(Translator.FolderCustomization_FolderCountFormat, items.Count);
        if (hidden > 0) text += " · " + string.Format(Translator.FolderCustomization_HiddenCountFormat, hidden);
        count.Text = text;
    }

    /// <summary>
    /// One folder row: hairline above, glyph, name, tags, hide toggle, then the pin action (Pinned: Move to More,
    /// More: Pin to top, Categories: an empty slot). Hidden rows dim to 55%.
    /// </summary>
    private UIView FolderRow(string key, FolderCustomizationItemViewModel item)
    {
        var row = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        var separator = new WinoSeparator { Fill = WinoSettingsStyle.CardStroke };
        separator.TranslatesAutoresizingMaskIntoConstraints = false;
        row.AddSubview(separator);

        var icon = new WinoIconView(SpecialFolderGlyphs.GetGlyphOrFolder(item.SpecialFolderType), 16);
        WinoLayout.Size(icon, 16, 16);
        var name = WinoStyle.Label(item.FolderName, WinoStyle.Body, WinoStyle.PrimaryText);
        name.LineBreakMode = UILineBreakMode.TailTruncation;
        WinoLayout.Priorities(name, UILayoutConstraintAxis.Horizontal, 1, 250);

        var content = WinoLayout.HStack(WinoStyle.Space2, icon, name);
        content.SetCustomSpacing((nfloat)WinoStyle.Space3, icon);
        // One tag at phone width: Hidden says more than System, and two tags would truncate the name.
        if (item.IsHidden) content.AddArrangedSubview(SettingsRowParts.Tag(Translator.FolderCustomization_HiddenTag));
        else if (item.IsSystemFolder) content.AddArrangedSubview(SettingsRowParts.Tag(Translator.FolderCustomization_SystemTag));

        var hide = new SettingsGlyphButton(WinoIconGlyph.EyeOff, $"{(item.IsHidden ? Translator.FolderCustomization_Show : Translator.FolderCustomization_Hide)}, {item.FolderName}")
        {
            IsToggled = item.IsHidden
        };
        hide.PrimaryActionTriggered += (_, _) => ToggleHidden(key, item);
        content.AddArrangedSubview(hide);

        if (key == CategoriesKey)
        {
            content.AddArrangedSubview(SettingsRowParts.Slot());
        }
        else
        {
            var pinned = key == PinnedKey;
            var pin = new SettingsGlyphButton(pinned ? WinoIconGlyph.UnPin : WinoIconGlyph.Pin,
                $"{(pinned ? Translator.FolderCustomization_Unpin : Translator.FolderCustomization_Pin)}, {item.FolderName}");
            pin.PrimaryActionTriggered += (_, _) => TogglePin(item);
            content.AddArrangedSubview(pin);
        }

        content.TranslatesAutoresizingMaskIntoConstraints = false;
        row.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(
        [
            separator.TopAnchor.ConstraintEqualTo(row.TopAnchor),
            separator.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor),
            separator.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor),
            content.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor, 14),
            content.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, -4),
            content.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor, 0.5f)
        ]);
        content.Alpha = item.IsHidden ? 0.55f : 1;
        name.AccessibilityLabel = item.IsHidden ? $"{item.FolderName}, {Translator.FolderCustomization_HiddenTag}" : item.FolderName;
        return row;
    }

    private void ToggleHidden(string key, FolderCustomizationItemViewModel item)
    {
        try
        {
            Serilog.Log.Information("Folder customization: toggle hidden {Folder} (was {Hidden}).", item.FolderName, item.IsHidden);
            // The ViewModel flips IsHidden before it awaits the database write, so the row can redraw now.
            var operation = ViewModel.ToggleHiddenAsync(item);
            _sections[key].List.Reload();
            UpdateCount(key);
            Run(operation);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    private void TogglePin(FolderCustomizationItemViewModel item)
    {
        Serilog.Log.Information("Folder customization: toggle pin {Folder}.", item.FolderName);
        Run(ViewModel.TogglePinAsync(item));
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
