using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Creates or edits an account group (Windows AccountGroupDetailsPage): the name with a preview of the tinted icon,
/// Combine folders, the color and icon grids, the ordered accounts In group (drag to reorder, Remove) beside the
/// Available accounts (Add, and which group they are in now), Delete for saved groups, then Save and Cancel.
/// </summary>
public sealed class AccountGroupDetailsPageViewController(AccountGroupDetailsPageViewModel viewModel, IPictureStorageService pictures, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AccountGroupDetailsPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private const string MembersKey = "members";
    private const string AvailableKey = "available";
    private const double RowHeight = 52;
    private const double RowSpacing = 4;

    private readonly SettingsDragGroup _dragGroup = new();
    private readonly List<WinoIconView> _iconChoices = new();

    public string? PageTitle => ViewModel.IsNewGroup || string.IsNullOrWhiteSpace(ViewModel.GroupName)
        ? Translator.AccountGroupDetailsPage_NewTitle
        : ViewModel.GroupName;

    public string? PageDescription => ViewModel.IsNewGroup ? Translator.AccountGroupDetailsPage_NewDescription : null;

    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Bind.Bind(vm, nameof(vm.IsNewGroup), s => s.IsNewGroup, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Bind.Bind(vm, nameof(vm.GroupName), s => s.GroupName, _ => { if (!vm.IsNewGroup) PageTitleChanged?.Invoke(this, EventArgs.Empty); });

        Add(NameCard());

        var combine = Card(Translator.AccountGroupDetailsPage_CombineFoldersHeader, Translator.AccountGroupDetailsPage_CombineFoldersDescription, WinoIconGlyph.PeopleLink,
            Bind.Switch(vm, nameof(vm.CombineFolders), s => s.CombineFolders, (s, value) => s.CombineFolders = value, Translator.AccountGroupDetailsPage_CombineFoldersHeader));
        Add(combine);

        var color = Card(Translator.AccountGroupDetailsPage_ColorHeader, Translator.AccountGroupDetailsPage_ColorDescription, WinoIconGlyph.TextColor);
        color.BottomContent = ColorGrid();
        Add(color);

        var icon = Card(Translator.AccountGroupDetailsPage_IconHeader, Translator.AccountGroupDetailsPage_IconDescription, WinoIconGlyph.Emoji);
        icon.BottomContent = IconGrid();
        Add(icon);

        Add(MembersCard());

        var delete = CommandCard(Translator.AccountGroupDetailsPage_DeleteGroup, Translator.AccountGroupDetailsPage_DeleteGroupDescription, WinoIconGlyph.Delete, vm.DeleteGroupCommand);
        delete.ShowsChevron = false;
        delete.HeaderIconTint = WinoSettingsTint.Red;
        Bind.Visible(Add(delete), vm, nameof(vm.IsExistingGroup), s => s.IsExistingGroup);

        var save = Bind.Button(Translator.Buttons_Save, vm.SaveCommand, primary: true);
        var cancel = Bind.Button(Translator.Buttons_Cancel, vm.CancelCommand);
        foreach (var button in new[] { save, cancel }) button.WidthAnchor.ConstraintGreaterThanOrEqualTo(96).Active = true;
        var buttons = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), cancel, save);
        buttons.EdgeInsets = new NSEdgeInsets(8, 0, 0, 0);
        Add(buttons);
#if DEBUG
        AccountExtrasDebug.CurrentGroupPage = new WeakReference<AccountGroupDetailsPageViewModel>(vm);
#endif
    }

    /// <summary>The name field, with the group's icon in its color as the card's leading preview.</summary>
    private WinoSettingsCard NameCard()
    {
        var vm = ViewModel;
        var field = Bind.TextField(vm, nameof(vm.GroupName), s => s.GroupName, (s, value) => s.GroupName = value,
            Translator.AccountGroupDetailsPage_NamePlaceholder, 240);
        WinoAccessibility.Label(field, Translator.AccountGroupDetailsPage_NameHeader);
        var card = Card(Translator.AccountGroupDetailsPage_NameHeader, Translator.AccountGroupDetailsPage_NameRequired, WinoIconGlyph.None, field);

        var preview = new WinoIconView(vm.SelectedIconGlyph, 22) { Colorful = false };
        WinoLayout.Size(preview, 32, 32);
        WinoAccessibility.Label(preview, Translator.AccountGroupDetailsPage_IconHeader);
        card.LeadingView = preview;
        Bind.Bind(vm, nameof(vm.SelectedIconGlyph), s => s.SelectedIconGlyph, glyph => preview.Icon = glyph);
        Bind.Bind(vm, nameof(vm.SelectedColorKey), s => s.SelectedColorKey, key => preview.Tint = WinoStyle.AccountGroupColor(key));
        return card;
    }

    /// <summary>"Aa" in each group color on a small tile, Default first (Windows AccountGroupColorTemplate).</summary>
    private NSView ColorGrid()
    {
        var vm = ViewModel;
        var grid = new ChoiceGrid();
        WinoAccessibility.Label(grid, Translator.AccountGroupDetailsPage_ColorHeader);
        foreach (var option in vm.ColorOptions)
        {
            var sample = WinoStyle.Label("Aa", NSFont.SystemFontOfSize(16, NSFontWeight.Semibold), WinoStyle.AccountGroupColor(option.Key) ?? WinoStyle.PrimaryText);
            sample.AccessibilityElement = false;
            grid.AddChoice(sample, option.DisplayName, () => vm.SelectedColorOption = option);
        }
        Bind.Bind(vm, nameof(vm.SelectedColorOption), s => s.SelectedColorOption, selected => grid.SelectedIndex = Array.IndexOf(vm.ColorOptions, selected));
        return grid;
    }

    /// <summary>The group icons, tinted with the selected color (Windows AccountGroupIconTemplate).</summary>
    private NSView IconGrid()
    {
        var vm = ViewModel;
        var grid = new ChoiceGrid();
        WinoAccessibility.Label(grid, Translator.AccountGroupDetailsPage_IconHeader);
        foreach (var option in vm.IconOptions)
        {
            var glyph = new WinoIconView(option.Glyph, 20) { Colorful = false };
            glyph.AccessibilityElement = false;
            _iconChoices.Add(glyph);
            grid.AddChoice(glyph, option.DisplayName, () => vm.SelectedIconOption = option);
        }
        Bind.Bind(vm, nameof(vm.SelectedIconOption), s => s.SelectedIconOption, selected => grid.SelectedIndex = Array.IndexOf(vm.IconOptions, selected));
        Bind.Bind(vm, nameof(vm.SelectedColorKey), s => s.SelectedColorKey, key =>
        {
            var tint = WinoStyle.AccountGroupColor(key);
            foreach (var choice in _iconChoices) choice.Tint = tint;
        });
        return grid;
    }

    /// <summary>In group (reorderable, Remove) beside Available (Add); rows can also be dragged between the two.</summary>
    private NSView MembersCard()
    {
        var vm = ViewModel;
        _dragGroup.CanDrop = (source, target) => !(source.Key == AvailableKey && target.Key == AvailableKey);
        _dragGroup.Dropped = (source, from, target, to) =>
        {
            if (source.Key == MembersKey && target.Key == MembersKey)
            {
                if (from < vm.MemberEntries.Count) vm.MemberEntries.Move(from, Math.Clamp(to, 0, vm.MemberEntries.Count - 1));
            }
            else if (source.Key == MembersKey && from < vm.MemberEntries.Count)
                Execute(vm.RemoveMemberCommand, vm.MemberEntries[from]);
            else if (source.Key == AvailableKey && from < vm.AvailableEntries.Count)
            {
                var entry = vm.AvailableEntries[from];
                Execute(vm.AddMemberCommand, entry);
                var index = vm.MemberEntries.IndexOf(entry);
                if (index >= 0) vm.MemberEntries.Move(index, Math.Clamp(to, 0, vm.MemberEntries.Count - 1));
            }
        };

        var required = WinoStyle.Label(Translator.AccountGroupDetailsPage_MemberRequired, WinoSettingsStyle.CardDescription, WinoStyle.Caution, 0);
        Bind.Visible(required, vm, nameof(vm.IsMemberMissing), s => s.IsMemberMissing);
        var members = new SettingsDragList<AccountGroupEntryViewModel>(MembersKey, vm.MemberEntries, _dragGroup, (entry, index) => EntryRow(entry, index, member: true), RowHeight, RowSpacing);
        members.SetEmptyView(new DashedDropZoneView(null), RowHeight);
        WinoAccessibility.Label(members, Translator.AccountGroupDetailsPage_InGroupHeader);
        Bindings.Own(members.Observe(vm.MemberEntries, Dispatcher));

        var empty = WinoStyle.Label(Translator.AccountGroupDetailsPage_AvailableEmpty, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        Bind.Visible(empty, vm, nameof(vm.HasAvailableEntries), s => !s.HasAvailableEntries);
        var available = new SettingsDragList<AccountGroupEntryViewModel>(AvailableKey, vm.AvailableEntries, _dragGroup, (entry, index) => EntryRow(entry, index, member: false), RowHeight, RowSpacing);
        WinoAccessibility.Label(available, Translator.AccountGroupDetailsPage_AvailableHeader);
        Bindings.Own(available.Observe(vm.AvailableEntries, Dispatcher));

        var left = Column(Translator.AccountGroupDetailsPage_InGroupHeader, Translator.AccountGroupDetailsPage_InGroupDescription, required, members);
        var right = Column(Translator.AccountGroupDetailsPage_AvailableHeader, Translator.AccountGroupDetailsPage_AvailableDescription, empty, available);
        var columns = WinoLayout.HStack(20, left, right);
        columns.Alignment = NSLayoutAttribute.Top;
        columns.Distribution = NSStackViewDistribution.FillEqually;

        var card = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.CardFill,
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(columns, card, WinoSettingsStyle.CardPadding);
        return card;
    }

    private static NSStackView Column(string title, string description, params NSView[] views)
    {
        var heading = WinoStyle.Label(title, WinoSettingsStyle.CardTitle, WinoStyle.PrimaryText);
        var detail = WinoStyle.Label(description, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var column = WinoLayout.VStack(2, heading, detail);
        column.Alignment = NSLayoutAttribute.Leading;
        column.SetCustomSpacing(10, detail);
        detail.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        foreach (var view in views)
        {
            column.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        }
        column.DetachesHiddenViews = true;
        return column;
    }

    /// <summary>
    /// A raised account row: drag handle (In group), account picture, name (with the group it is in now when it is
    /// listed as Available and belongs to another group) and address, then Remove or Add.
    /// </summary>
    private NSView EntryRow(AccountGroupEntryViewModel entry, int index, bool member)
    {
        var surface = new WinoSurfaceView
        {
            Fill = SettingsRowParts.RowFill,
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        var picture = new WinoAccountIconView(28) { Account = MailAccountIconInfoFactory.Create(entry.Account, pictures) };
        var name = WinoStyle.Label(entry.Title, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        var address = WinoStyle.Label(entry.Description, WinoStyle.Caption, WinoStyle.SecondaryText);
        foreach (var label in new[] { name, address })
        {
            label.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            label.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        }
        NSView title = name;
        if (!member && entry.IsInOtherGroup)
        {
            var titleRow = WinoLayout.HStack(6, name, SettingsRowParts.Tag(entry.OtherGroupText));
            titleRow.SetClippingResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            title = titleRow;
        }
        var text = WinoLayout.VStack(2, title, address);
        text.Alignment = NSLayoutAttribute.Leading;
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        text.SetClippingResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        var action = new SettingsPillButton(member ? Translator.Buttons_Remove : Translator.Buttons_Add, member ? WinoIconGlyph.None : WinoIconGlyph.Add) { Tag = index };
        WinoAccessibility.Label(action, member ? entry.RemoveAutomationName : entry.AddAutomationName);
        if (member) action.OnActivated(this, static (page, control) => page.RemoveAt((int)control.Tag));
        else action.OnActivated(this, static (page, control) => page.AddAt((int)control.Tag));

        var content = member
            ? WinoLayout.HStack(10, WinoLayout.Size(new WinoIconView(WinoIconGlyph.ReOrderDotsVertical, 14, WinoStyle.TertiaryText), 14, 14), picture, text, action)
            : WinoLayout.HStack(10, picture, text, action);
        content.Alignment = NSLayoutAttribute.CenterY;
        WinoLayout.Fill(content, surface, 0, 10, 0, 10);
        surface.ToolTip = entry.IsInOtherGroup && !member ? $"{entry.Title}, {entry.OtherGroupText}" : entry.Title;
        WinoAccessibility.Label(surface, string.IsNullOrEmpty(entry.Description) ? entry.Title : $"{entry.Title}, {entry.Description}");
        return surface;
    }

    private void RemoveAt(int index)
    {
        if (index >= 0 && index < ViewModel.MemberEntries.Count) Execute(ViewModel.RemoveMemberCommand, ViewModel.MemberEntries[index]);
    }

    private void AddAt(int index)
    {
        if (index >= 0 && index < ViewModel.AvailableEntries.Count) Execute(ViewModel.AddMemberCommand, ViewModel.AvailableEntries[index]);
    }

    private void Execute(System.Windows.Input.ICommand command, object parameter)
    {
        try
        {
            if (command.CanExecute(parameter)) command.Execute(parameter);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>A wrapping grid of 40pt choice tiles with single selection (the Windows GridView of the color and icon cards).</summary>
    private sealed class ChoiceGrid : NSView
    {
        private const double TileSize = 40;
        private const double Spacing = 6;
        private readonly List<ChoiceTile> _tiles = new();
        private int _selectedIndex = -1;
        private int _laidOutRows = -1;

        public ChoiceGrid()
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            AccessibilityElement = true;
            AccessibilityRole = NSAccessibilityRoles.GroupRole;
        }

        public override bool IsFlipped => true;

        public void AddChoice(NSView content, string name, Action selected)
        {
            var tile = new ChoiceTile(content, name, selected);
            _tiles.Add(tile);
            AddSubview(tile);
            InvalidateIntrinsicContentSize();
            NeedsLayout = true;
        }

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                _selectedIndex = value;
                for (int index = 0; index < _tiles.Count; index++) _tiles[index].IsSelected = index == value;
            }
        }

        private int Columns(double width) => Math.Max(1, (int)((Math.Max(TileSize, width) + Spacing) / (TileSize + Spacing)));

        private int Rows(int columns) => Math.Max(1, (int)Math.Ceiling(_tiles.Count / (double)columns));

        public override CGSize IntrinsicContentSize
        {
            get
            {
                var rows = Rows(Columns(Frame.Width > 0 ? Frame.Width : 10 * (TileSize + Spacing)));
                return new CGSize(NoIntrinsicMetric, (nfloat)(rows * TileSize + (rows - 1) * Spacing));
            }
        }

        public override void Layout()
        {
            base.Layout();
            var columns = Columns(Frame.Width);
            for (int index = 0; index < _tiles.Count; index++)
            {
                int row = index / columns, column = index % columns;
                _tiles[index].Frame = new CGRect(column * (TileSize + Spacing), row * (TileSize + Spacing), TileSize, TileSize);
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

        public ChoiceTile(NSView content, string name, Action selected)
        {
            _selected = selected;
            TranslatesAutoresizingMaskIntoConstraints = true;
            CornerRadius = 5;
            ToolTip = name;
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
                AccessibilitySelected = value;
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
