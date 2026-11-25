using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.Intelligence;

/// <summary>
/// The folder tree: one flat button per folder, indented by depth (the trees are small, so no tree list model), with the
/// folder glyph, the name, the included dot and the message count. Selection and totals restyle the buttons in place; only
/// a new folder list rebuilds them.
/// </summary>
internal sealed partial class IntelligenceCoveragePage
{
    private readonly Gtk.Box _tree = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly List<TreeButton> _treeButtons = [];

    private sealed record TreeButton(IntelligenceFolderNode Node, Gtk.Button Button, Gtk.Label Name, Gtk.Widget Dot, Gtk.Label Count);

    private Gtk.Widget FolderPanel()
    {
        _tree.AddCssClass("card");
        _tree.AddCssClass("wino-coverage-tree");
        WinoA11y.Label(_tree, Wino.Core.Domain.Translator.SemanticIndex_CoverageIncludedFolders);
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetPropagateNaturalHeight(true);
        scroller.SetMinContentHeight(200);
        scroller.SetMaxContentHeight(260);
        scroller.SetChild(_tree);
        return scroller;
    }

    private void RebuildTree()
    {
        while (_tree.GetFirstChild() is { } child) _tree.Remove(child);
        _treeButtons.Clear();
        foreach (var node in ViewModel.RootFolders.ToList()) AddNode(node, 0);
        RefreshTree();
    }

    private void AddNode(IntelligenceFolderNode node, int depth)
    {
        var icon = new WinoIcon(WinoIconGlyph.Folder, 15).Root;
        var name = Gtk.Label.New(node.DisplayName);
        name.SetXalign(0);
        name.SetHexpand(true);
        name.SetEllipsize(Pango.EllipsizeMode.End);
        var dot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        dot.AddCssClass("wino-coverage-dot");
        dot.AddCssClass("wino-coverage-to-index");
        dot.SetSizeRequest(7, 7);
        dot.SetValign(Gtk.Align.Center);
        var count = Gtk.Label.New(string.Empty);
        count.AddCssClass("caption");
        count.AddCssClass("dim-label");
        count.SetXalign(1);
        count.SetWidthChars(5);
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        content.SetMarginStart(4 + depth * 14);
        foreach (var part in (Gtk.Widget[])[icon, name, dot, count]) content.Append(part);
        var button = Gtk.Button.New();
        button.AddCssClass("flat");
        button.AddCssClass("wino-coverage-folder");
        button.SetChild(content);
        var command = ViewModel.SelectFolderCommand;
        Settings.Own(GtkSignals.OnClicked(button, () =>
        {
            if (command.CanExecute(node)) command.Execute(node);
        }));
        _tree.Append(button);
        _treeButtons.Add(new TreeButton(node, button, name, dot, count));
        foreach (var child in node.ChildNodes.ToList()) AddNode(child, depth + 1);
    }

    private void RefreshTree()
    {
        var selected = ViewModel.SelectedFolder;
        foreach (var item in _treeButtons)
        {
            var node = item.Node;
            var isSelected = ReferenceEquals(node, selected);
            if (isSelected) item.Button.AddCssClass("wino-coverage-folder-selected");
            else item.Button.RemoveCssClass("wino-coverage-folder-selected");
            item.Dot.SetVisible(node.IsIncluded);
            item.Dot.SetTooltipText(node.CoverageBadgeTooltip);
            item.Count.SetText(node.AvailableMessageCountText);
            WinoA11y.Label(item.Button, node.RowAutomationName);
        }
    }
}
