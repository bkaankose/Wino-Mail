using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.WinoAccount;

/// <summary>Signed-out state: the account header, the offers as radio tiles and the selected offer's detail panel.</summary>
internal sealed partial class WinoAccountPage
{
    // Settings content is clamped to 600 px, so the art column is narrower than the Mac's 300 and gives way below 500.
    private const int ArtMinimumPanelWidth = 500;

    private readonly List<(WinoAccountBenefitItemViewModel Benefit, Gtk.ToggleButton Tile)> _tiles = [];
    private WinoAccountBenefitIllustration? _illustration;
    private bool _applyingTiles;

    private IEnumerable<WinoSettingsGroup> SignedOutGroups()
    {
        var header = Page.AddGroup();
        header.HidesWhenEmpty = false;
        header.Root.Add(SignedOutHeader());
        yield return header;

        var benefits = Page.AddGroup(Translator.WinoAccount_Management_BenefitsSectionHeader);
        benefits.HidesWhenEmpty = false;
        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        column.Append(BenefitsGrid());
        column.Append(BenefitDetail());
        benefits.Root.Add(column);
        Settings.Bind(ViewModel, nameof(ViewModel.SelectedBenefit), static s => s.SelectedBenefit, ApplySelectedBenefit);
        yield return benefits;
    }

    /// <summary>The Windows account row: the product mark, the title with one line of context, the privacy note and the two buttons.</summary>
    private Gtk.Widget SignedOutHeader()
    {
        var vm = ViewModel;
        var mark = new WinoIcon(WinoIconGlyph.WinoIntelligence, 48, WinoIconMode.Colorful).Root;
        mark.SetValign(Gtk.Align.Start);
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        text.SetHexpand(true);
        text.Append(Text(Translator.WinoAccount_Management_SignedOutTitle, "heading"));
        text.Append(Text(Translator.WinoAccount_Management_SignedOutDescription_Device, "dim-label"));
        var privacy = Text(Translator.WinoAccount_Management_SignedOutPrivacyNote, "caption", "dim-label");
        privacy.SetMarginTop(2);
        text.Append(privacy);
        var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        buttons.SetMarginTop(10);
        buttons.Append(Named(Settings.Button(Translator.Buttons_SignIn, vm.SignInCommand, suggested: true), Translator.Buttons_SignIn));
        buttons.Append(Named(Settings.Button(Translator.Buttons_CreateAccount, vm.RegisterCommand), Translator.Buttons_CreateAccount));
        text.Append(buttons);
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 16);
        row.AddCssClass("card");
        foreach (var part in (Gtk.Widget[])[mark, text])
        {
            part.SetMarginTop(16);
            part.SetMarginBottom(16);
        }
        mark.SetMarginStart(16);
        text.SetMarginEnd(16);
        row.Append(mark);
        row.Append(text);
        return row;
    }

    /// <summary>The offers as a grid of radio tiles (Windows ItemsView, UniformGridLayout): two columns, one when narrow.</summary>
    private Gtk.Widget BenefitsGrid()
    {
        var vm = ViewModel;
        var grid = Gtk.FlowBox.New();
        grid.SetSelectionMode(Gtk.SelectionMode.None);
        grid.SetHomogeneous(true);
        grid.SetMinChildrenPerLine(1);
        grid.SetMaxChildrenPerLine(2);
        grid.SetColumnSpacing(6);
        grid.SetRowSpacing(6);
        WinoA11y.Label(grid, Translator.WinoAccount_Management_BenefitsSectionHeader);
        _tiles.Clear();
        Gtk.ToggleButton? group = null;
        foreach (var benefit in vm.Benefits)
        {
            var tile = BenefitTile(benefit);
            if (group is null) group = tile;
            else tile.SetGroup(group);
            var captured = benefit;
            Settings.Own(GtkSignals.OnToggled(tile, () =>
            {
                if (_applyingTiles || !tile.GetActive()) return;
                vm.SelectedBenefit = captured;
            }));
            _tiles.Add((benefit, tile));
            grid.Append(tile);
        }
        return grid;
    }

    private static Gtk.ToggleButton BenefitTile(WinoAccountBenefitItemViewModel benefit)
    {
        var icon = new WinoIcon(benefit.Icon, 20).Root;
        icon.SetHalign(Gtk.Align.Start);
        icon.AddCssClass("accent");
        var title = Text(benefit.Title, "heading");
        title.SetHexpand(true);
        // A wrapping label asks for its whole text as natural width; capped, two tiles fit side by side.
        title.SetMaxWidthChars(22);
        var badge = Gtk.Label.New(benefit.BadgeText);
        badge.AddCssClass("caption");
        badge.AddCssClass(benefit.IsFreeBadge ? "success" : "dim-label");
        badge.SetValign(Gtk.Align.Start);
        var top = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        icon.SetHexpand(true);
        top.Append(icon);
        top.Append(badge);
        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        content.Append(top);
        content.Append(title);
        var caption = Text(benefit.Caption, "caption", "dim-label");
        caption.SetMaxWidthChars(26);
        content.Append(caption);
        content.SetMarginTop(10);
        content.SetMarginBottom(10);
        content.SetMarginStart(6);
        content.SetMarginEnd(6);
        var tile = Gtk.ToggleButton.New();
        tile.SetChild(content);
        tile.SetValign(Gtk.Align.Fill);
        WinoA11y.Rename(tile, $"{benefit.Title}, {benefit.BadgeText}");
        WinoA11y.Description(tile, benefit.Caption);
        return tile;
    }

    private Gtk.Widget? _detailBin;
    private Gtk.Box? _detailPoints;
    private Gtk.Label? _detailTitle;
    private Gtk.Label? _detailLede;
    private Gtk.Button? _detailAction;

    /// <summary>The selected offer: illustration (hidden when the panel is narrow), title, lede, points and the call to action.</summary>
    private Gtk.Widget BenefitDetail()
    {
        var vm = ViewModel;
        var illustration = _illustration = new WinoAccountBenefitIllustration();
        var art = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        art.SetSizeRequest(200, -1);
        art.Append(illustration.Root);
        illustration.Root.SetVexpand(true);
        illustration.Root.SetMarginStart(16);
        illustration.Root.SetMarginEnd(16);
        var divider = Gtk.Separator.New(Gtk.Orientation.Vertical);

        _detailTitle = Text(string.Empty, "title-3");
        _detailLede = Text(string.Empty, "dim-label");
        _detailPoints = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        var action = _detailAction = Gtk.Button.NewWithLabel(string.Empty);
        action.AddCssClass("suggested-action");
        action.SetHalign(Gtk.Align.Start);
        Settings.Own(GtkSignals.OnClicked(action, () =>
        {
            var command = vm.SelectedBenefit?.CtaCommand;
            if (command?.CanExecute(null) == true) command.Execute(null);
        }));
        var copy = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        copy.SetHexpand(true);
        copy.SetMarginTop(20);
        copy.SetMarginBottom(20);
        copy.SetMarginStart(20);
        copy.SetMarginEnd(20);
        copy.Append(_detailTitle);
        copy.Append(_detailLede);
        copy.Append(_detailPoints);
        copy.Append(action);

        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        row.AddCssClass("card");
        row.Append(art);
        row.Append(divider);
        row.Append(copy);

        var bin = Adw.BreakpointBin.New();
        bin.SetSizeRequest(280, 200);
        bin.SetChild(row);
        var breakpoint = Adw.Breakpoint.New(Adw.BreakpointCondition.NewLength(Adw.BreakpointConditionLengthType.MaxWidth, ArtMinimumPanelWidth - 1, Adw.LengthUnit.Px));
        Settings.Own(GtkSignals.Connect<Adw.Breakpoint>(h => breakpoint.OnApply += h, h => breakpoint.OnApply -= h, (_, _) =>
        {
            art.SetVisible(false);
            divider.SetVisible(false);
        }));
        Settings.Own(GtkSignals.Connect<Adw.Breakpoint>(h => breakpoint.OnUnapply += h, h => breakpoint.OnUnapply -= h, (_, _) =>
        {
            art.SetVisible(true);
            divider.SetVisible(true);
        }));
        bin.AddBreakpoint(breakpoint);
        _detailBin = bin;
        return bin;
    }

    private void ApplySelectedBenefit(WinoAccountBenefitItemViewModel? benefit)
    {
        _applyingTiles = true;
        try
        {
            foreach (var (item, tile) in _tiles) tile.SetActive(ReferenceEquals(item, benefit));
        }
        finally { _applyingTiles = false; }

        _detailTitle?.SetText(benefit?.Title ?? string.Empty);
        _detailLede?.SetText(benefit?.Lede ?? string.Empty);
        if (_detailAction is { } action)
        {
            action.SetLabel(benefit?.CtaText ?? string.Empty);
            action.SetVisible(benefit is not null && !string.IsNullOrEmpty(benefit.CtaText));
            WinoA11y.Rename(action, benefit?.CtaText ?? string.Empty);
        }
        if (_illustration is { } illustration) illustration.Kind = benefit?.Type;
        if (_detailPoints is not { } points) return;
        while (points.GetFirstChild() is { } child) points.Remove(child);
        foreach (var point in benefit?.Points ?? [])
        {
            var check = new WinoIcon(WinoIconGlyph.Checkmark, 14).Root;
            check.AddCssClass("accent");
            check.SetValign(Gtk.Align.Start);
            var line = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            line.Append(check);
            var label = Text(point, "dim-label");
            label.SetHexpand(true);
            line.Append(label);
            points.Append(line);
        }
    }

    private void ReleaseSignedOut()
    {
        _illustration?.Dispose();
        _illustration = null;
    }

    private string DescribeTiles() => string.Join("|", _tiles.Select(static t => $"{t.Benefit.Type}{(t.Tile.GetActive() ? "*" : string.Empty)}"));

#if DEBUG
    /// <summary>Scrolls the page so the offer detail is in view (a widget outside the viewport is not drawn).</summary>
    internal void DebugRevealDetail()
    {
        if (_detailBin is not { } detail) return;
        for (var parent = detail.GetParent(); parent is not null; parent = parent.GetParent())
        {
            if (parent is not Gtk.ScrolledWindow scroller) continue;
            var adjustment = scroller.GetVadjustment();
            adjustment.SetValue(adjustment.GetUpper() - adjustment.GetPageSize());
            return;
        }
    }

    internal void DebugSelectTile(int index)
    {
        if (index >= 0 && index < _tiles.Count) _tiles[index].Tile.SetActive(true);
    }
#endif
}
