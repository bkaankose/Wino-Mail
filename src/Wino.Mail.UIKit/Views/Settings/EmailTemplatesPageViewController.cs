using System.Net;
using System.Text.RegularExpressions;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Settings › Mail › Email templates (Windows EmailTemplatesPage): New template as the navigation bar's "+", then
/// "Your templates" with one card per template (Note glyph, name, description and an italic one-line text preview
/// of its HTML) carrying Edit and Delete buttons. Tapping a card opens it; Delete confirms with the editor's strings and
/// refreshes the list. With no templates a centred empty card explains what templates are for.
/// </summary>
public sealed partial class EmailTemplatesPageViewController(
    EmailTemplatesPageViewModel viewModel,
    IMailDialogService dialogService,
    IDispatcher dispatcher,
    IWinoLogger logger)
    : SettingsPageViewController<EmailTemplatesPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly UIStackView _list = WinoLayout.VStack(WinoSettingsStyle.CardSpacing);
    private readonly UIView _listHeader = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private UIView? _empty;
    private BindingScope? _rowScope;
    private bool _rebuildQueued;
    private bool _loaded;

    protected override void BuildPage()
    {
        // iOS shows no page description: New template is the navigation bar's "+".
        var create = BarButton(WinoIconGlyph.Add, Translator.SettingsEmailTemplates_NewTemplateTitle, ViewModel.CreateTemplate);
        create.AccessibilityHint = Translator.SettingsEmailTemplates_NewTemplateDescription;

        var title = WinoStyle.Label(Translator.SettingsEmailTemplates_ListTitle, WinoSettingsStyle.SectionTitle, WinoStyle.PrimaryText);
        title.AccessibilityTraits = UIAccessibilityTrait.Header;
        WinoLayout.Fill(title, _listHeader, 8, 4, 6, 0);
        Add(_listHeader);

        _list.Alignment = UIStackViewAlignment.Fill;
        _list.Hidden = true;
        Add(_list);

        var empty = EmptyState();
        empty.Hidden = true;
        _empty = Add(empty);

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
        foreach (var template in templates) _list.AddArrangedSubview(TemplateCard(rows, template));

        var hasItems = templates.Count > 0;
        _listHeader.Hidden = !hasItems;
        _list.Hidden = !hasItems;
        if (_empty is not null) _empty.Hidden = !_loaded || hasItems;
    }

    /// <summary>One template card: Note glyph, name, description, italic preview, then Edit and Delete. A tap opens it.</summary>
    private WinoSettingsCard TemplateCard(SettingsBinder rows, EmailTemplate template)
    {
        var name = string.IsNullOrWhiteSpace(template.Name) ? Translator.SettingsEmailTemplates_EditPageTitle : template.Name;
        var preview = Preview(template.HtmlContent);
        var description = string.IsNullOrWhiteSpace(template.Description) ? null : template.Description;

        var edit = rows.Button(string.Empty, () => ViewModel.OpenTemplate(template), icon: WinoIconGlyph.Edit);
        Label(edit, Translator.Buttons_Edit, name);
        var delete = rows.Button(string.Empty, () => Run(DeleteAsync(template)), icon: WinoIconGlyph.Delete);
        Label(delete, Translator.Buttons_Delete, name);
        var buttons = WinoLayout.HStack(4, edit, delete);
        buttons.Alignment = UIStackViewAlignment.Center;

        var card = new WinoSettingsCard(name, description, WinoIconGlyph.Note, buttons) { IsClickable = true, ShowsChevron = false };
        if (!string.IsNullOrEmpty(preview))
        {
            var previewLabel = WinoStyle.Label(preview, Italic(WinoSettingsStyle.CardDescription), WinoStyle.SecondaryText);
            previewLabel.LineBreakMode = UILineBreakMode.TailTruncation;
            card.BottomContent = previewLabel;
        }

        EventHandler opened = (_, _) =>
        {
            try { ViewModel.OpenTemplate(template); }
            catch (Exception exception) { ReportError(exception); }
        };
        card.Activated += opened;
        rows.Scope.Own(new ActionDisposable(() => card.Activated -= opened));
        return card;
    }

    private static UIFont Italic(UIFont font)
        => font.FontDescriptor.CreateWithTraits(UIFontDescriptorSymbolicTraits.Italic) is { } italic ? UIFont.FromDescriptor(italic, 0) : font;

    /// <summary>Icon-only buttons carry the action and the template name as their accessible name; 44pt touch targets.</summary>
    private static void Label(UIButton button, string action, string name)
    {
        WinoLayout.Size(button, WinoStyle.MinimumHitTarget, WinoStyle.MinimumHitTarget);
        WinoAccessibility.Label(button, $"{action}: {name}");
    }

    /// <summary>Same confirmation as the Windows editor's Delete, then the list reloads.</summary>
    private async Task DeleteAsync(EmailTemplate template)
    {
        await ViewModel.DeleteTemplateAsync(template, dialogService);
    }

    /// <summary>Centred Note glyph, title and one-line hint; the bar's "+" creates the first template.</summary>
    private UIView EmptyState()
    {
        var card = new WinoSurfaceView { CornerRadius = WinoSettingsStyle.CardRadius, Fill = WinoSettingsStyle.GroupFill, Stroke = WinoSettingsStyle.CardStroke, StrokeWidth = 1, TranslatesAutoresizingMaskIntoConstraints = false };
        var glyph = new WinoIconView(WinoIconGlyph.Note, 36, WinoStyle.SecondaryText);
        WinoLayout.Size(glyph, 36, 36);
        var title = WinoStyle.Label(Translator.SettingsEmailTemplates_EmptyTitle, WinoStyle.Scaled(17, UIFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        title.TextAlignment = UITextAlignment.Center;
        var hint = WinoStyle.Label(Translator.SettingsEmailTemplates_EmptyDescription, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        hint.TextAlignment = UITextAlignment.Center;

        var stack = WinoLayout.VStack(8, glyph, title, hint);
        stack.Alignment = UIStackViewAlignment.Center;
        stack.SetCustomSpacing(12, glyph);
        WinoLayout.Fill(stack, card, 36, 24, 36, 24);
        hint.WidthAnchor.ConstraintLessThanOrEqualTo(360).Active = true;
        glyph.IsAccessibilityElement = false;
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
