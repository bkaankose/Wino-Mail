using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// "Insert template" popover (Mac <c>ComposeTemplatePopover</c>): the stored templates (name and description), a search
/// entry once there are more than eight, and "Manage templates…"; with no templates the empty state with "Create a
/// template". Picking one closes the popover first. Rows are buttons so AT-SPI can activate them.
/// </summary>
internal sealed class ComposeTemplatePopover : IDisposable
{
    private const int SearchThreshold = 8;
    private readonly EmailTemplate[] _templates;
    private readonly Action<EmailTemplate> _picked;
    private readonly Action _manage;
    private readonly Action _create;
    private readonly Gtk.Popover _popover = Gtk.Popover.New();
    private readonly Gtk.ListBox? _list;
    private readonly Gtk.SearchEntry? _search;
    private readonly List<(EmailTemplate Template, Gtk.ListBoxRow Row)> _rows = [];
    private readonly List<IDisposable> _subscriptions = [];
    private bool _disposed;

    public ComposeTemplatePopover(EmailTemplate[] templates, Action<EmailTemplate> picked, Action manage, Action create)
    {
        WinoLiveObjects.Track(this);
        _templates = templates;
        _picked = picked;
        _manage = manage;
        _create = create;
        _popover.AddCssClass("wino-compose-templates");
        WinoA11y.Label(_popover, Translator.Composer_EmailTemplatesPlaceholder);

        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        content.SetMarginTop(10);
        content.SetMarginBottom(8);
        content.SetMarginStart(8);
        content.SetMarginEnd(8);

        if (templates.Length == 0)
        {
            content.SetSizeRequest(240, -1);
            var badge = WinoIcons.Label(WinoIconGlyph.DocumentAdd, 22);
            badge.AddCssClass("accent");
            badge.SetMarginTop(8);
            var title = Gtk.Label.New(Translator.SettingsEmailTemplates_EmptyTitle);
            title.AddCssClass("heading");
            var description = Gtk.Label.New(Translator.SettingsEmailTemplates_EmptyDescription);
            description.AddCssClass("dim-label");
            description.AddCssClass("caption");
            description.SetWrap(true);
            description.SetMaxWidthChars(30);
            description.SetJustify(Gtk.Justification.Center);
            var createButton = Gtk.Button.NewWithLabel(Translator.SettingsEmailTemplates_EmptyAction);
            createButton.AddCssClass("suggested-action");
            createButton.SetHalign(Gtk.Align.Center);
            createButton.SetMarginTop(6);
            WinoA11y.Rename(createButton, Translator.SettingsEmailTemplates_EmptyAction);
            _subscriptions.Add(GtkSignals.OnClicked(createButton, () => Run(_create)));
            content.Append(badge);
            content.Append(title);
            content.Append(description);
            content.Append(createButton);
        }
        else
        {
            content.SetSizeRequest(300, -1);
            var title = Gtk.Label.New(Translator.Composer_EmailTemplatesPlaceholder);
            title.AddCssClass("heading");
            title.SetXalign(0);
            title.SetMarginStart(4);
            content.Append(title);

            if (templates.Length > SearchThreshold)
            {
                var search = _search = Gtk.SearchEntry.New();
                search.SetPlaceholderText(Translator.Composer_SearchTemplates);
                WinoA11y.Label(search, Translator.Composer_SearchTemplates);
                _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => search.OnSearchChanged += h, h => search.OnSearchChanged -= h, (_, _) => ApplyFilter(search.GetText())));
                _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => search.OnActivate += h, h => search.OnActivate -= h, (_, _) => PickFirstVisible()));
                _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => search.OnStopSearch += h, h => search.OnStopSearch -= h, (_, _) => _popover.Popdown()));
                content.Append(search);
            }

            var list = _list = Gtk.ListBox.New();
            list.SetSelectionMode(Gtk.SelectionMode.None);
            list.AddCssClass("navigation-sidebar");
            WinoA11y.Label(list, Translator.Composer_EmailTemplatesPlaceholder);
            foreach (var template in templates) list.Append(Row(template));

            var scroller = Gtk.ScrolledWindow.New();
            scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
            scroller.SetPropagateNaturalHeight(true);
            scroller.SetMaxContentHeight(Math.Min(templates.Length, SearchThreshold) * 48 + 8);
            scroller.SetChild(list);
            content.Append(scroller);
            content.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));

            var manageButton = Gtk.Button.NewWithLabel(Translator.Composer_ManageTemplates);
            manageButton.AddCssClass("flat");
            manageButton.SetHalign(Gtk.Align.Start);
            WinoA11y.Rename(manageButton, Translator.Composer_ManageTemplates);
            _subscriptions.Add(GtkSignals.OnClicked(manageButton, () => Run(_manage)));
            content.Append(manageButton);
        }

        _popover.SetChild(content);
        _popover.SetPosition(Gtk.PositionType.Bottom);
    }

    public Gtk.Popover Popover => _popover;
    public int VisibleCount => _rows.Count(static row => row.Row.GetVisible());

    public void Show(Gtk.Widget anchor)
    {
        if (_disposed) return;
        _popover.SetParent(anchor);
        _popover.Popup();
        if (_search is not null) _search.GrabFocus();
    }

    public void Pick(string name)
    {
        var match = _rows.FirstOrDefault(row => row.Row.GetVisible() && string.Equals(row.Template.Name, name, StringComparison.Ordinal));
        if (match.Template is null) throw new InvalidOperationException($"No visible template '{name}'.");
        Pick(match.Template);
    }

    public void Filter(string text)
    {
        if (_search is not null) _search.SetText(text);
        else ApplyFilter(text);
    }

    private Gtk.ListBoxRow Row(EmailTemplate template)
    {
        var name = Gtk.Label.New(template.Name ?? string.Empty);
        name.SetUseMarkup(false);
        name.SetXalign(0);
        name.SetEllipsize(Pango.EllipsizeMode.End);
        name.AddCssClass("heading");
        var hasDescription = !string.IsNullOrWhiteSpace(template.Description);
        var description = Gtk.Label.New(hasDescription ? template.Description : Translator.SettingsEmailTemplates_DescriptionPlaceholder);
        description.SetUseMarkup(false);
        description.SetXalign(0);
        description.SetEllipsize(Pango.EllipsizeMode.End);
        description.AddCssClass("caption");
        description.AddCssClass("dim-label");
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 1);
        text.SetHexpand(true);
        text.Append(name);
        text.Append(description);
        var line = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var icon = WinoIcons.Label(WinoIconGlyph.Document, 14);
        icon.AddCssClass("dim-label");
        line.Append(icon);
        line.Append(text);

        var button = Gtk.Button.New();
        button.AddCssClass("flat");
        button.SetChild(line);
        WinoA11y.Rename(button, hasDescription ? $"{template.Name}, {template.Description}" : template.Name ?? string.Empty);
        _subscriptions.Add(GtkSignals.OnClicked(button, () => Pick(template)));
        var row = Gtk.ListBoxRow.New();
        row.SetActivatable(false);
        row.SetChild(button);
        _rows.Add((template, row));
        return row;
    }

    private void ApplyFilter(string? text)
    {
        text = text?.Trim() ?? string.Empty;
        foreach (var (template, row) in _rows)
        {
            row.SetVisible(text.Length == 0
                || (template.Name ?? string.Empty).Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || (template.Description ?? string.Empty).Contains(text, StringComparison.CurrentCultureIgnoreCase));
        }
    }

    private void PickFirstVisible()
    {
        var first = _rows.FirstOrDefault(static row => row.Row.GetVisible());
        if (first.Template is not null) Pick(first.Template);
    }

    private void Pick(EmailTemplate template) => Run(() => _picked(template));

    private void Run(Action action)
    {
        if (_disposed) return;
        _popover.Popdown();
        action();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _popover.Popdown();
        // Unparenting inside the popover's own closed handler crashes GTK; the owner disposes on a later turn.
        if (_popover.GetParent() is not null) _popover.Unparent();
    }
}
