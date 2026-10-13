using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail.Compose;

/// <summary>
/// "Insert template" (Mac ComposeTemplatePopover): the stored templates (name and description), a search field once
/// there are more than eight, and "Manage templates…". With no templates it shows the empty state with "Create a
/// template". The page presents it as a popover from the More button; on iPhone UIKit adapts it to a sheet.
/// Tapping a template inserts it and closes the picker.
/// </summary>
internal sealed class ComposeTemplatePicker : UITableViewController, IUISearchResultsUpdating
{
    private const int SearchThreshold = 8;
    private const string CellId = "wino.compose.template";
    private readonly IReadOnlyList<EmailTemplate> _templates;
    private readonly Action<EmailTemplate> _picked;
    private readonly Action _manage;
    private readonly Action _create;
    private List<EmailTemplate> _visible;

    public ComposeTemplatePicker(IReadOnlyList<EmailTemplate> templates, Action<EmailTemplate> picked, Action manage, Action create)
        : base(UITableViewStyle.InsetGrouped)
    {
        _templates = templates;
        _visible = templates.ToList();
        _picked = picked;
        _manage = manage;
        _create = create;
        Title = Translator.Composer_EmailTemplatesPlaceholder;
        PreferredContentSize = new CoreGraphics.CGSize(340, templates.Count == 0 ? 300 : Math.Min(templates.Count, SearchThreshold) * 60 + 160);
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        TableView.RegisterClassForCellReuse(typeof(UITableViewCell), CellId);
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) => DismissViewController(true, null));
        if (_templates.Count > SearchThreshold)
        {
            var search = new UISearchController((UIViewController?)null) { ObscuresBackgroundDuringPresentation = false };
            search.SearchResultsUpdater = this;
            search.SearchBar.Placeholder = Translator.Composer_SearchTemplates;
            NavigationItem.SearchController = search;
            NavigationItem.HidesSearchBarWhenScrolling = false;
        }
        if (_templates.Count == 0)
        {
            var state = new WinoStateView(WinoIconGlyph.Document, Translator.SettingsEmailTemplates_EmptyTitle, Translator.SettingsEmailTemplates_EmptyDescription, WinoStyle.TertiaryText);
            var configuration = UIButtonConfiguration.FilledButtonConfiguration;
            configuration.Title = Translator.SettingsEmailTemplates_EmptyAction;
            configuration.BaseBackgroundColor = WinoStyle.Accent;
            var create = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
            create.PrimaryActionTriggered += (_, _) => Close(_create);
            state.Add(create);
            var background = new UIView();
            state.Place(background);
            TableView.BackgroundView = background;
            TableView.SeparatorStyle = UITableViewCellSeparatorStyle.None;
        }
    }

    public void UpdateSearchResultsForSearchController(UISearchController searchController)
    {
        var query = searchController.SearchBar.Text?.Trim();
        _visible = string.IsNullOrEmpty(query)
            ? _templates.ToList()
            : _templates.Where(template => (template.Name ?? string.Empty).Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || (template.Description ?? string.Empty).Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        TableView.ReloadData();
    }

    // Section 0: the templates; section 1: Manage templates.
    public override nint NumberOfSections(UITableView tableView) => _templates.Count == 0 ? 0 : 2;

    public override nint RowsInSection(UITableView tableView, nint section) => section == 0 ? _visible.Count : 1;

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        var cell = tableView.DequeueReusableCell(CellId, indexPath);
        var content = UIListContentConfiguration.SubtitleCellConfiguration;
        if (indexPath.Section == 0 && indexPath.Row < _visible.Count)
        {
            var template = _visible[indexPath.Row];
            content.Text = string.IsNullOrWhiteSpace(template.Name) ? Translator.Composer_EmailTemplatesPlaceholder : template.Name;
            content.SecondaryText = string.IsNullOrWhiteSpace(template.Description) ? Translator.SettingsEmailTemplates_DescriptionPlaceholder : template.Description;
            content.SecondaryTextProperties.Color = WinoStyle.SecondaryText;
            content.Image = WinoIcons.Image(WinoIconGlyph.Document, 18);
        }
        else
        {
            content.Text = Translator.Composer_ManageTemplates;
            content.TextProperties.Color = WinoStyle.Accent;
            content.Image = WinoIcons.Image(WinoIconGlyph.Settings, 18);
        }
        cell.ContentConfiguration = content;
        return cell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section == 0 && indexPath.Row < _visible.Count)
        {
            var template = _visible[indexPath.Row];
            Close(() => _picked(template));
        }
        else Close(_manage);
    }

    private void Close(Action then)
    {
        NavigationItem.SearchController?.DismissViewController(false, null);
        DismissViewController(true, then);
    }
}
