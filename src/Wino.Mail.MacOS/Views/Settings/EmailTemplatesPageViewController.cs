using System.Net;
using System.Text.RegularExpressions;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Settings › Mail › Email templates (Windows EmailTemplatesPage): the intro with the primary New
/// template button, then "Your templates" with one card per template (Note glyph, name, description and
/// an italic one-line text preview of its HTML) carrying Edit and Delete icon buttons. Windows deletes
/// only from the editor; here Delete confirms with the editor's strings and refreshes the list. With no
/// templates a centred empty card offers "Create a template".
/// </summary>
public sealed partial class EmailTemplatesPageViewController(
    EmailTemplatesPageViewModel viewModel,
    IEmailTemplateService templateService,
    IMailDialogService dialogService,
    IDispatcher dispatcher,
    IWinoLogger logger)
    : SettingsPageViewController<EmailTemplatesPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly NSStackView _list = WinoLayout.VStack(WinoSettingsStyle.CardSpacing);
    private NSView _listHeader = null!;
    private NSView _empty = null!;
    private BindingScope? _rowScope;
    private bool _rebuildQueued;
    private bool _loaded;

    protected override void BuildPage()
    {
        // Intro with the primary New template action on its trailing edge.
        var intro = WinoStyle.Label(Translator.SettingsEmailTemplates_Intro, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        intro.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        intro.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var create = Bind.Button(Translator.SettingsEmailTemplates_NewTemplateTitle, ViewModel.CreateTemplate, primary: true, icon: WinoIconGlyph.Add);
        create.ToolTip = Translator.SettingsEmailTemplates_NewTemplateDescription;
        create.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        var top = WinoLayout.HStack(WinoStyle.Space3, intro, create);
        top.Alignment = NSLayoutAttribute.CenterY;
        top.EdgeInsets = new NSEdgeInsets(0, 1, 10, 0);
        Add(top);

        var title = WinoStyle.Label(Translator.SettingsEmailTemplates_ListTitle, WinoSettingsStyle.SectionTitle, WinoStyle.PrimaryText);
        _listHeader = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        WinoLayout.Fill(title, _listHeader, 8, 1, 6, 0);
        Add(_listHeader);

        _list.Alignment = NSLayoutAttribute.Leading;
        _list.Hidden = true;
        Add(_list);

        _empty = EmptyState();
        _empty.Hidden = true;
        Add(_empty);

        Bind.Collection(ViewModel.EmailTemplates, QueueRebuild);
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        await base.InitializeAsync(mode, parameter);
        await ViewModel.LoadAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _loaded = true;
            QueueRebuild();
        });
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        BeginInvokeOnMainThread(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        foreach (var view in _list.ArrangedSubviews)
        {
            _list.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }

        var templates = ViewModel.EmailTemplates.ToList();
        foreach (var template in templates)
        {
            var row = TemplateCard(rows, template);
            _list.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(_list.WidthAnchor).Active = true;
        }

        var hasItems = templates.Count > 0;
        _listHeader.Hidden = !hasItems;
        _list.Hidden = !hasItems;
        _empty.Hidden = !_loaded || hasItems;
    }

    /// <summary>One template card: Note glyph, name, description, italic preview, then Edit and Delete.</summary>
    private NSView TemplateCard(SettingsBinder rows, EmailTemplate template)
    {
        var name = string.IsNullOrWhiteSpace(template.Name) ? Translator.SettingsEmailTemplates_EditPageTitle : template.Name;
        var icon = new WinoIconView(WinoIconGlyph.Note, WinoSettingsStyle.IconSize);
        WinoLayout.Size(icon, WinoSettingsStyle.IconSize, WinoSettingsStyle.IconSize);

        var nameLabel = OneLine(name, WinoSettingsStyle.CardTitle, WinoStyle.PrimaryText);
        var description = OneLine(template.Description, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
        var preview = OneLine(Preview(template.HtmlContent), Italic(WinoSettingsStyle.CardDescription), WinoStyle.SecondaryText);

        var text = WinoLayout.VStack(1, nameLabel, description, preview);
        text.Alignment = NSLayoutAttribute.Leading;
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        text.SetClippingResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        foreach (var label in new[] { nameLabel, description, preview })
            label.WidthAnchor.ConstraintLessThanOrEqualTo(text.WidthAnchor).Active = true;

        var edit = rows.Button(string.Empty, () => ViewModel.OpenTemplate(template), icon: WinoIconGlyph.Edit);
        Label(edit, Translator.Buttons_Edit, name);
        var delete = rows.Button(string.Empty, () => Run(DeleteAsync(template)), icon: WinoIconGlyph.Delete);
        Label(delete, Translator.Buttons_Delete, name);
        var buttons = WinoLayout.HStack(4, edit, delete);
        buttons.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);

        var row = WinoLayout.HStack(WinoSettingsStyle.IconGap, icon, text, buttons);
        row.Alignment = NSLayoutAttribute.CenterY;
        row.EdgeInsets = new NSEdgeInsets((nfloat)WinoSettingsStyle.CardVerticalPadding, (nfloat)WinoSettingsStyle.CardPadding, (nfloat)WinoSettingsStyle.CardVerticalPadding, (nfloat)WinoSettingsStyle.CardPadding);

        var card = new WinoSurfaceView { CornerRadius = WinoSettingsStyle.CardRadius, Fill = WinoSettingsStyle.CardFill, Stroke = WinoSettingsStyle.CardStroke, StrokeWidth = 1, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, card);
        card.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoSettingsStyle.CardMinHeight).Active = true;
        // Like the Windows card, a click anywhere outside the buttons opens the template.
        NSClickGestureRecognizer? click = null;
        click = new NSClickGestureRecognizer(() =>
        {
            var point = buttons.ConvertPointFromView(click!.LocationInView(null), null);
            if (buttons.Bounds.Contains(point)) return;
            ViewModel.OpenTemplate(template);
        });
        card.AddGestureRecognizer(click);
        card.AccessibilityElement = true;
        card.AccessibilityRole = NSAccessibilityRoles.GroupRole;
        card.AccessibilityLabel = string.IsNullOrWhiteSpace(template.Description) ? name : $"{name}, {template.Description}";
        return card;
    }

    private static NSTextField OneLine(string? value, NSFont font, NSColor color)
    {
        var label = WinoStyle.Label(value ?? string.Empty, font, color);
        label.LineBreakMode = NSLineBreakMode.TruncatingTail;
        label.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        label.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        label.Hidden = string.IsNullOrWhiteSpace(value);
        return label;
    }

    private static NSFont Italic(NSFont font) => NSFontManager.SharedFontManager.ConvertFont(font, NSFontTraitMask.Italic) ?? font;

    /// <summary>Icon-only buttons carry the action and the template name, as tooltip and accessible name.</summary>
    private static void Label(NSButton button, string action, string name)
    {
        var text = $"{action}: {name}";
        button.ToolTip = action;
        WinoAccessibility.Label(button, text);
    }

    /// <summary>Same confirmation as the Windows editor's Delete, then the list reloads.</summary>
    private async Task DeleteAsync(EmailTemplate template)
    {
        var confirmed = await dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.DialogMessage_DeleteEmailTemplateConfirmationMessage, template.Name),
            Translator.DialogMessage_DeleteEmailTemplateConfirmationTitle,
            Translator.Buttons_Delete);
        if (!confirmed) return;
        await templateService.DeleteEmailTemplateAsync(template);
        await ViewModel.LoadAsync();
    }

    /// <summary>Centred Note glyph, title, one-line hint and the secondary "Create a template" button.</summary>
    private NSView EmptyState()
    {
        var card = new WinoSurfaceView { CornerRadius = WinoSettingsStyle.CardRadius, Fill = WinoSettingsStyle.CardFill, Stroke = WinoSettingsStyle.CardStroke, StrokeWidth = 1, TranslatesAutoresizingMaskIntoConstraints = false };
        var glyph = new WinoIconView(WinoIconGlyph.Note, 36, WinoStyle.SecondaryText);
        WinoLayout.Size(glyph, 36, 36);
        var title = WinoStyle.Label(Translator.SettingsEmailTemplates_EmptyTitle, NSFont.SystemFontOfSize(15, NSFontWeight.Semibold), WinoStyle.PrimaryText);
        var hint = WinoStyle.Label(Translator.SettingsEmailTemplates_EmptyDescription, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        hint.Alignment = NSTextAlignment.Center;
        hint.PreferredMaxLayoutWidth = 360;
        hint.WidthAnchor.ConstraintLessThanOrEqualTo(360).Active = true;
        var action = Bind.Button(Translator.SettingsEmailTemplates_EmptyAction, ViewModel.CreateTemplate);

        var stack = WinoLayout.VStack(8, glyph, title, hint, action);
        stack.Alignment = NSLayoutAttribute.CenterX;
        stack.SetCustomSpacing(12, glyph);
        stack.SetCustomSpacing(16, hint);
        stack.EdgeInsets = new NSEdgeInsets(44, 24, 44, 24);
        WinoLayout.Fill(stack, card);
        card.AccessibilityElement = true;
        card.AccessibilityRole = NSAccessibilityRoles.GroupRole;
        card.AccessibilityLabel = $"{Translator.SettingsEmailTemplates_EmptyTitle}. {Translator.SettingsEmailTemplates_EmptyDescription}";
        return card;
    }

    /// <summary>The template's text on one line: block breaks become spaces, markup and entities are removed.</summary>
    internal static string Preview(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = HiddenBlocks().Replace(html, string.Empty);
        text = BlockBreaks().Replace(text, "\n");
        text = Tags().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        var lines = text.Split('\n')
            .Select(line => Whitespace().Replace(line, " ").Trim())
            .Where(line => line.Length > 0);
        return string.Join(" ", lines);
    }

    [GeneratedRegex(@"<(style|script|head)[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenBlocks();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|tr|h[1-6]|blockquote|table)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (Exception exception) { ReportError(exception); }
    }
}
