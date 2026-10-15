using System.Net;
using System.Text.RegularExpressions;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.Settings.GeneralAndMail;

/// <summary>
/// Settings › Mail › Email templates (Windows EmailTemplatesPage, iOS EmailTemplatesPageViewController): the intro with
/// the primary New template button, then "Your templates" with one card per template (Note glyph, name, description and
/// an italic one-line text preview of its HTML) carrying Edit and Delete buttons. Tapping a card opens it; Delete
/// confirms with the editor's strings and refreshes the list. With no templates a centred empty card offers "Create a
/// template". The route runs OnNavigatedTo and then the template load (iOS InitializeAsync); the entry remembers that the
/// load finished so a recreated view does not flash the empty state.
/// </summary>
public sealed partial class EmailTemplatesPageFragment : SettingsPageFragment<EmailTemplatesPageViewModel>
{
    private const string LoadedKey = "EmailTemplates.Loaded";

    private LinearLayout? _list;
    private View? _listHeader;
    private View? _empty;
    private BindingScope? _rowScope;

    /// <summary>The intro carries the action, so the catalog description is not repeated above it.</summary>
    protected override bool ShowsPageDescription => false;

    /// <summary>The route's activation step: loads the templates, then marks the entry loaded and refreshes its view.</summary>
    public static async Task LoadAsync(PageEntry entry)
    {
        if (entry.ViewModel is not EmailTemplatesPageViewModel viewModel) return;
        await viewModel.LoadAsync();
        await entry.Dispatcher.ExecuteOnUIThread(() =>
        {
            entry.State[LoadedKey] = true;
            (entry.Fragment as EmailTemplatesPageFragment)?.Rebuild();
        });
    }

    private bool IsLoaded => Entry?.State.ContainsKey(LoadedKey) == true;

    protected override void BuildPage()
    {
        var vm = VM;
        var create = Bind.Button(Translator.SettingsEmailTemplates_NewTemplateTitle, vm.CreateTemplate, primary: true, icon: WinoIconGlyph.Add);
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) create.TooltipText = Translator.SettingsEmailTemplates_NewTemplateDescription;
        var intro = Text(Translator.SettingsEmailTemplates_Intro, WinoTextStyle.Description);
        var top = Stack(Resource.Dimension.wino_space_m, intro, Row(create));
        Add(top);
        if (top.LayoutParameters is LinearLayout.LayoutParams topLayout) topLayout.BottomMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_xs);

        var title = Text(Translator.SettingsEmailTemplates_ListTitle, WinoTextStyle.BodyLargeStrong);
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(title, true);
        WinoTokens.Pad(title, Resource.Dimension.wino_space_2xs, Resource.Dimension.wino_space_s, 0, Resource.Dimension.wino_space_3xs);
        title.Visibility = ViewStates.Gone;
        _listHeader = Add(title);

        _list = Add(new LinearLayout(Ctx) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone });

        var empty = EmptyState();
        empty.Visibility = ViewStates.Gone;
        _empty = Add(empty);

        Bindings.Own(new ActionDisposable(() =>
        {
            _rowScope?.Dispose();
            _list = null;
            _listHeader = null;
            _empty = null;
        }));
        Bind.Collection(vm.EmailTemplates, Rebuild);
    }

    private void Rebuild()
    {
        if (_list is not { } list || View is null) return;
        _rowScope?.Dispose();
        _rowScope = new BindingScope();
        var rows = Bind.Child(_rowScope);
        list.RemoveAllViews();

        var templates = VM.EmailTemplates.ToList();
        foreach (var template in templates)
        {
            var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            if (list.ChildCount > 0) layout.TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_2xs);
            list.AddView(TemplateCard(rows, template), layout);
        }

        var hasItems = templates.Count > 0;
        if (_listHeader is not null) _listHeader.Visibility = hasItems ? ViewStates.Visible : ViewStates.Gone;
        list.Visibility = hasItems ? ViewStates.Visible : ViewStates.Gone;
        if (_empty is not null) _empty.Visibility = IsLoaded && !hasItems ? ViewStates.Visible : ViewStates.Gone;
    }

    /// <summary>One template card: Note glyph, name, description, italic preview, then Edit and Delete. A tap opens it.</summary>
    private WinoSettingsCard TemplateCard(SettingsBinder rows, EmailTemplate template)
    {
        var name = string.IsNullOrWhiteSpace(template.Name) ? Translator.SettingsEmailTemplates_EditPageTitle : template.Name;
        var preview = Preview(template.HtmlContent);
        var description = string.IsNullOrWhiteSpace(template.Description) ? null : template.Description;

        var edit = rows.IconButton(WinoIconGlyph.Edit, $"{Translator.Buttons_Edit}: {name}");
        rows.Views.Click(edit, () => VM.OpenTemplate(template));
        var delete = rows.IconButton(WinoIconGlyph.Delete, $"{Translator.Buttons_Delete}: {name}");
        rows.Views.Click(delete, () => Run(DeleteAsync(template)));
        var buttons = Row(edit, delete);

        var card = Card(name, description, WinoIconGlyph.Note, buttons);
        card.IsClickable = true;
        card.ShowsChevron = false;
        if (!string.IsNullOrEmpty(preview))
        {
            var previewLabel = Text(preview, WinoTextStyle.Description, maxLines: 1);
            previewLabel.SetTypeface(previewLabel.Typeface, TypefaceStyle.Italic);
            card.BottomContent = previewLabel;
        }

        EventHandler opened = (_, _) =>
        {
            try { VM.OpenTemplate(template); }
            catch (Exception exception) { ReportError(exception); }
        };
        card.Activated += opened;
        rows.Scope.Own(new ActionDisposable(() => card.Activated -= opened));
        return card;
    }

    /// <summary>Same confirmation as the Windows editor's Delete, then the list reloads.</summary>
    private async Task DeleteAsync(EmailTemplate template)
    {
        if (MainApplication.Services?.GetService<IMailDialogService>() is not { } dialogs) return;
        await VM.DeleteTemplateAsync(template, dialogs);
    }

    /// <summary>Centred Note glyph, title, one-line hint and the secondary "Create a template" button.</summary>
    private View EmptyState()
    {
        var context = Ctx;
        var glyph = new WinoIconView(context) { Icon = WinoIconGlyph.Note, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_hero) };
        glyph.SetTextColor(WinoColors.SecondaryText(context));
        var title = Text(Translator.SettingsEmailTemplates_EmptyTitle, WinoTextStyle.Subtitle);
        title.Gravity = GravityFlags.CenterHorizontal;
        var hint = Text(Translator.SettingsEmailTemplates_EmptyDescription, WinoTextStyle.Description);
        hint.Gravity = GravityFlags.CenterHorizontal;
        hint.SetMaxWidth(WinoTokens.Px(context, Resource.Dimension.wino_size_readable_max));
        var action = Bind.Button(Translator.SettingsEmailTemplates_EmptyAction, VM.CreateTemplate);

        var stack = new LinearLayout(context) { Orientation = Orientation.Vertical };
        stack.SetGravity(GravityFlags.CenterHorizontal);
        WinoTokens.Pad(stack, Resource.Dimension.wino_space_2xl, Resource.Dimension.wino_space_5xl, Resource.Dimension.wino_space_2xl, Resource.Dimension.wino_space_5xl);
        LinearLayout.LayoutParams Centered(int top) => new(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { Gravity = GravityFlags.CenterHorizontal, TopMargin = top == 0 ? 0 : WinoTokens.Px(context, top) };
        stack.AddView(glyph, Centered(0));
        stack.AddView(title, Centered(Resource.Dimension.wino_space_m));
        stack.AddView(hint, Centered(Resource.Dimension.wino_space_s));
        stack.AddView(action, Centered(Resource.Dimension.wino_space_l));

        var frame = new GradientDrawable();
        frame.SetColor(WinoColors.CardFill(context));
        frame.SetStroke(WinoTokens.Px(context, Resource.Dimension.wino_space_hairline), WinoColors.CardStroke(context));
        frame.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_radius_small));
        stack.Background = frame;
        return stack;
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
