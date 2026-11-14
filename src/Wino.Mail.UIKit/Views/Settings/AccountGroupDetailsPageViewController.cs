using System.Windows.Input;
using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Creates or edits an account group (Windows AccountGroupDetailsPage, Mac AccountGroupDetailsPageViewController): the
/// name beside a preview of the tinted icon, Combine folders, the color and icon grids, the ordered accounts In group
/// (reorder handles, Remove) and the Available accounts (Add, and the group they are in now), Delete for saved groups,
/// with Cancel and Save in the navigation bar. Save stays off until the group has a name and two accounts.
/// </summary>
public sealed class AccountGroupDetailsPageViewController(AccountGroupDetailsPageViewModel viewModel, IPictureStorageService pictures, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AccountGroupDetailsPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private const string MembersKey = "members";
    private const string AvailableKey = "available";
    private const double MemberRowHeight = 60;
    private const double AvailableRowHeight = 66;

    private readonly SettingsDragGroup _dragGroup = new();
    private readonly List<WinoIconView> _iconChoices = [];

    public string? PageTitle => ViewModel.IsNewGroup || string.IsNullOrWhiteSpace(ViewModel.GroupName)
        ? Translator.AccountGroupDetailsPage_NewTitle
        : ViewModel.GroupName;

    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Bind.Bind(vm, nameof(vm.IsNewGroup), s => s.IsNewGroup, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Bind.Bind(vm, nameof(vm.GroupName), s => s.GroupName, _ => { if (!vm.IsNewGroup) PageTitleChanged?.Invoke(this, EventArgs.Empty); });
        BuildBarItems();

        Add(new WinoSettingsGroup(Translator.AccountGroupDetailsPage_NameHeader, note: Translator.AccountGroupDetailsPage_NameRequired)).Add(NameRow());

        AddGroup(null, Card(Translator.AccountGroupDetailsPage_CombineFoldersHeader, Translator.AccountGroupDetailsPage_CombineFoldersDescription, WinoIconGlyph.None,
            Bind.Switch(vm, nameof(vm.CombineFolders), s => s.CombineFolders, (s, value) => s.CombineFolders = value, Translator.AccountGroupDetailsPage_CombineFoldersHeader)));

        Add(new WinoSettingsGroup(Translator.AccountGroupDetailsPage_ColorHeader, Translator.AccountGroupDetailsPage_ColorDescription)).Add(Surface(ColorGrid(), 12));
        Add(new WinoSettingsGroup(Translator.AccountGroupDetailsPage_IconHeader, Translator.AccountGroupDetailsPage_IconDescription)).Add(Surface(IconGrid(), 12));

        ConfigureDragGroup();
        Add(MembersSection());
        Add(AvailableSection());

        var delete = WinoSettingsRowContent.MarkDestructive(Bind.Button(Translator.AccountGroupDetailsPage_DeleteGroup, vm.DeleteGroupCommand));
        Bind.Visible(delete, vm, nameof(vm.IsExistingGroup), s => s.IsExistingGroup);
        var deleteGroup = AddGroup(null, ActionRow(delete));
        deleteGroup.Note = Translator.AccountGroupDetailsPage_DeleteGroupDescription;
        Bind.Visible(deleteGroup, vm, nameof(vm.IsExistingGroup), s => s.IsExistingGroup);
    }

    /// <summary>Cancel replaces the back button and Save is the bar's done action, enabled from the command.</summary>
    private void BuildBarItems()
    {
        var vm = ViewModel;
        CommandBinding? save = null;
        var saveItem = new UIBarButtonItem(Translator.Buttons_Save, UIBarButtonItemStyle.Done, (_, _) => save?.Execute());
        save = Bindings.Own(new CommandBinding(vm.SaveCommand, () => null, enabled => saveItem.Enabled = enabled, Dispatcher, ReportError));
        NavigationItem.RightBarButtonItem = saveItem;

        CommandBinding? cancel = null;
        var cancelItem = new UIBarButtonItem(Translator.Buttons_Cancel, UIBarButtonItemStyle.Plain, (_, _) => cancel?.Execute());
        cancel = Bindings.Own(new CommandBinding(vm.CancelCommand, () => null, _ => { }, Dispatcher, ReportError));
        NavigationItem.LeftBarButtonItem = cancelItem;
        NavigationItem.HidesBackButton = true;
    }

    private static WinoSurfaceView Surface(UIView content, double padding)
    {
        var surface = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.GroupFill,
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = WinoSettingsStyle.CardRadius,
            ClipsToBounds = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(content, surface, padding, padding, padding, padding);
        return surface;
    }

    /// <summary>The group's icon in its color, then the name field.</summary>
    private UIView NameRow()
    {
        var vm = ViewModel;
        var preview = new WinoIconView(vm.SelectedIconGlyph, 22) { Colorful = false };
        WinoLayout.Size(preview, 28, 28);
        preview.IsAccessibilityElement = false;
        Bind.Bind(vm, nameof(vm.SelectedIconGlyph), s => s.SelectedIconGlyph, glyph => preview.Icon = glyph);
        Bind.Bind(vm, nameof(vm.SelectedColorKey), s => s.SelectedColorKey, key => preview.Tint = WinoStyle.AccountGroupColor(key) ?? WinoStyle.PrimaryText);

        var field = Bind.TextField(vm, nameof(vm.GroupName), s => s.GroupName, (s, value) => s.GroupName = value, Translator.AccountGroupDetailsPage_NamePlaceholder, 0);
        field.BorderStyle = UITextBorderStyle.None;
        field.ReturnKeyType = UIReturnKeyType.Done;
        field.AutocapitalizationType = UITextAutocapitalizationType.Words;
        field.ShouldReturn = textField => { textField.ResignFirstResponder(); return true; };
        WinoAccessibility.Label(field, Translator.AccountGroupDetailsPage_NameHeader);
        field.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        WinoLayout.Priorities(field, UILayoutConstraintAxis.Horizontal, 1, 250);

        var row = WinoLayout.HStack(12, preview, field);
        row.Alignment = UIStackViewAlignment.Center;
        var surface = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.GroupFill,
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(row, surface, 4, WinoSettingsStyle.CardPadding, 4, WinoSettingsStyle.CardPadding);
        return surface;
    }

    /// <summary>"Aa" in each group color, Default first (Windows AccountGroupColorTemplate).</summary>
    private UIView ColorGrid()
    {
        var vm = ViewModel;
        var grid = new ChoiceGrid();
        WinoAccessibility.Label(grid, Translator.AccountGroupDetailsPage_ColorHeader);
        foreach (var option in vm.ColorOptions)
        {
            var sample = WinoStyle.Label("Aa", WinoStyle.Fixed(16, UIFontWeight.Semibold), WinoStyle.AccountGroupColor(option.Key) ?? WinoStyle.PrimaryText);
            sample.IsAccessibilityElement = false;
            grid.AddChoice(sample, option.DisplayName, () => vm.SelectedColorOption = option);
        }
        Bind.Bind(vm, nameof(vm.SelectedColorOption), s => s.SelectedColorOption, selected => grid.SelectedIndex = selected is null ? -1 : Array.IndexOf(vm.ColorOptions, selected));
        return grid;
    }

    /// <summary>The group icons, tinted with the selected color (Windows AccountGroupIconTemplate).</summary>
    private UIView IconGrid()
    {
        var vm = ViewModel;
        var grid = new ChoiceGrid();
        WinoAccessibility.Label(grid, Translator.AccountGroupDetailsPage_IconHeader);
        foreach (var option in vm.IconOptions)
        {
            var glyph = new WinoIconView(option.Glyph, 20) { Colorful = false };
            WinoLayout.Size(glyph, 22, 22);
            glyph.IsAccessibilityElement = false;
            _iconChoices.Add(glyph);
            grid.AddChoice(glyph, option.DisplayName, () => vm.SelectedIconOption = option);
        }
        Bind.Bind(vm, nameof(vm.SelectedIconOption), s => s.SelectedIconOption, selected => grid.SelectedIndex = selected is null ? -1 : Array.IndexOf(vm.IconOptions, selected));
        Bind.Bind(vm, nameof(vm.SelectedColorKey), s => s.SelectedColorKey, key =>
        {
            var tint = WinoStyle.AccountGroupColor(key) ?? WinoStyle.PrimaryText;
            foreach (var choice in _iconChoices) choice.Tint = tint;
        });
        return grid;
    }

    /// <summary>Members reorder inside their own list; Add and Remove move rows between the two lists.</summary>
    private void ConfigureDragGroup()
    {
        var vm = ViewModel;
        _dragGroup.CanDrop = (source, target) => source.Key == MembersKey && target.Key == MembersKey;
        _dragGroup.Dropped = (source, from, target, to) =>
        {
            if (source.Key != MembersKey || target.Key != MembersKey || from >= vm.MemberEntries.Count) return;
            vm.MemberEntries.Move(from, Math.Clamp(to, 0, vm.MemberEntries.Count - 1));
        };
    }

    private WinoSettingsGroup MembersSection()
    {
        var vm = ViewModel;
        var group = new WinoSettingsGroup(Translator.AccountGroupDetailsPage_InGroupHeader, Translator.AccountGroupDetailsPage_InGroupDescription);
        var required = WinoStyle.Label(Translator.AccountGroupDetailsPage_MemberRequired, WinoSettingsStyle.CardDescription, WinoStyle.Caution, 0);
        required.AccessibilityTraits = UIAccessibilityTrait.StaticText | UIAccessibilityTrait.UpdatesFrequently;
        Bind.Visible(required, vm, nameof(vm.IsMemberMissing), s => s.IsMemberMissing);

        var list = new SettingsDragList<AccountGroupEntryViewModel>(MembersKey, vm.MemberEntries, _dragGroup, (entry, index) => EntryRow(entry, index, member: true), MemberRowHeight);
        list.SetEmptyView(new DashedDropZoneView(null), MemberRowHeight);
        WinoAccessibility.Label(list, Translator.AccountGroupDetailsPage_InGroupHeader);
        Bindings.Own(list.Observe(vm.MemberEntries, Dispatcher));

        group.Add(Surface(list, 0));
        group.Add(Inset(required));
        return group;
    }

    private WinoSettingsGroup AvailableSection()
    {
        var vm = ViewModel;
        var group = new WinoSettingsGroup(Translator.AccountGroupDetailsPage_AvailableHeader, Translator.AccountGroupDetailsPage_AvailableDescription);
        var empty = WinoStyle.Label(Translator.AccountGroupDetailsPage_AvailableEmpty, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);

        var list = new SettingsDragList<AccountGroupEntryViewModel>(AvailableKey, vm.AvailableEntries, _dragGroup, (entry, index) => EntryRow(entry, index, member: false), AvailableRowHeight);
        WinoAccessibility.Label(list, Translator.AccountGroupDetailsPage_AvailableHeader);
        var surface = Surface(list, 0);
        var emptyHost = Inset(empty);
        Bindings.Own(list.Observe(vm.AvailableEntries, Dispatcher, () =>
        {
            surface.Hidden = vm.AvailableEntries.Count == 0;
            emptyHost.Hidden = vm.AvailableEntries.Count > 0;
        }));

        group.Add(surface);
        group.Add(emptyHost);
        return group;
    }

    /// <summary>A note at the row text inset; it hides with the label.</summary>
    private UIView Inset(UILabel label)
    {
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, host, 0, WinoSettingsStyle.CardPadding, 0, WinoSettingsStyle.CardPadding);
        Bindings.Own(label.AddObserver("hidden", Foundation.NSKeyValueObservingOptions.Initial | Foundation.NSKeyValueObservingOptions.New, _ => host.Hidden = label.Hidden));
        return host;
    }

    /// <summary>
    /// An account row: picture, name and address (and the group it is in now when it is listed as Available and belongs
    /// to another group), then Remove or Add. Member rows show the system reorder handle at the trailing edge.
    /// </summary>
    private UIView EntryRow(AccountGroupEntryViewModel entry, int index, bool member)
    {
        var row = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        if (index > 0)
        {
            var separator = new WinoSeparator { Fill = WinoSettingsStyle.RowSeparator, TranslatesAutoresizingMaskIntoConstraints = false };
            row.AddSubview(separator);
            NSLayoutConstraint.ActivateConstraints(
            [
                separator.TopAnchor.ConstraintEqualTo(row.TopAnchor),
                separator.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor, (nfloat)WinoSettingsStyle.CardPadding),
                separator.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor)
            ]);
        }

        var picture = new WinoAccountIconView(32) { Account = MailAccountIconInfoFactory.Create(entry.Account, pictures) };
        var name = WinoStyle.Label(entry.Title, WinoStyle.Body, WinoStyle.PrimaryText);
        var address = WinoStyle.Label(entry.Description, WinoStyle.Description, WinoStyle.SecondaryText);
        var text = WinoLayout.VStack(1, name, address);
        if (!member && entry.IsInOtherGroup) text.AddArrangedSubview(WinoStyle.Label(entry.OtherGroupText, WinoStyle.Description, WinoStyle.SecondaryText));
        foreach (var label in text.ArrangedSubviews.OfType<UILabel>())
        {
            label.LineBreakMode = UILineBreakMode.TailTruncation;
            WinoLayout.Priorities(label, UILayoutConstraintAxis.Horizontal, 1, 250);
        }
        text.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);

        var action = new SettingsPillButton(member ? Translator.Buttons_Remove : Translator.Buttons_Add, member ? WinoIconGlyph.None : WinoIconGlyph.Add);
        WinoAccessibility.Label(action, member ? entry.RemoveAutomationName : entry.AddAutomationName);
        action.PrimaryActionTriggered += (_, _) => Execute(member ? ViewModel.RemoveMemberCommand : ViewModel.AddMemberCommand, entry);

        var content = WinoLayout.HStack(12, picture, text, action);
        content.Alignment = UIStackViewAlignment.Center;
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        row.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(
        [
            content.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor, (nfloat)WinoSettingsStyle.CardPadding),
            content.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, member ? -4 : -(nfloat)WinoSettingsStyle.CardPadding),
            content.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor)
        ]);
        text.IsAccessibilityElement = true;
        text.AccessibilityLabel = string.Join(", ", new[] { entry.Title, entry.Description, !member && entry.IsInOtherGroup ? entry.OtherGroupText : null }
            .Where(static part => !string.IsNullOrEmpty(part)));
        return row;
    }

    private void Execute(ICommand command, object parameter)
    {
        try
        {
            if (command.CanExecute(parameter)) command.Execute(parameter);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>A wrapping grid of 44pt choice tiles with single selection (the Windows GridView of the color and icon cards).</summary>
    private sealed class ChoiceGrid : UIView
    {
        private const double TileSize = 44;
        private const double Spacing = 8;
        private readonly List<ChoiceTile> _tiles = [];
        private int _laidOutRows = -1;

        public ChoiceGrid()
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
        }

        public void AddChoice(UIView content, string name, Action selected)
        {
            var tile = new ChoiceTile(content, name, selected);
            _tiles.Add(tile);
            AddSubview(tile);
            InvalidateIntrinsicContentSize();
            SetNeedsLayout();
        }

        public int SelectedIndex
        {
            set
            {
                for (int index = 0; index < _tiles.Count; index++) _tiles[index].IsSelected = index == value;
            }
        }

        private int Columns(double width) => Math.Max(1, (int)((Math.Max(TileSize, width) + Spacing) / (TileSize + Spacing)));

        private int Rows(int columns) => Math.Max(1, (int)Math.Ceiling(_tiles.Count / (double)columns));

        public override CGSize IntrinsicContentSize
        {
            get
            {
                var rows = Rows(Columns(Bounds.Width > 0 ? Bounds.Width : 6 * (TileSize + Spacing)));
                return new CGSize(NoIntrinsicMetric, (nfloat)(rows * TileSize + (rows - 1) * Spacing));
            }
        }

        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            var columns = Columns(Bounds.Width);
            // Spread the leftover width between the columns so the grid fills the surface edge to edge.
            var gap = columns > 1 ? Math.Max(Spacing, (Bounds.Width - columns * TileSize) / (columns - 1)) : 0;
            for (int index = 0; index < _tiles.Count; index++)
            {
                int row = index / columns, column = index % columns;
                _tiles[index].Frame = new CGRect(column * (TileSize + gap), row * (TileSize + Spacing), TileSize, TileSize);
            }
            var rows = Rows(columns);
            if (rows == _laidOutRows) return;
            _laidOutRows = rows;
            InvalidateIntrinsicContentSize();
        }
    }

    private sealed class ChoiceTile : WinoPressableView
    {
        private readonly Action _selected;
        private bool _isSelected;

        public ChoiceTile(UIView content, string name, Action selected)
        {
            _selected = selected;
            // The grid places tiles by frame.
            TranslatesAutoresizingMaskIntoConstraints = true;
            CornerRadius = 8;
            AccessibilityLabel = name;
            AddSubview(content);
            content.TranslatesAutoresizingMaskIntoConstraints = false;
            NSLayoutConstraint.ActivateConstraints([content.CenterXAnchor.ConstraintEqualTo(CenterXAnchor), content.CenterYAnchor.ConstraintEqualTo(CenterYAnchor)]);
            Apply();
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                AccessibilityTraits = value ? AccessibilityTraits | UIAccessibilityTrait.Selected : AccessibilityTraits & ~UIAccessibilityTrait.Selected;
                Apply();
            }
        }

        protected override void OnActivated() => _selected();

        private void Apply()
        {
            Fill = _isSelected ? WinoSettingsStyle.SelectedFill : WinoSettingsStyle.SubtleFill;
            Stroke = _isSelected ? WinoStyle.Accent : WinoSettingsStyle.CardStroke;
            StrokeWidth = _isSelected ? 2 : 1;
        }
    }
}
