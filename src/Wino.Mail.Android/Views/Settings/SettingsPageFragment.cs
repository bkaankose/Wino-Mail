using System.Windows.Input;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.ViewModels;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Messaging.Client.Navigation;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.Settings;

/// <summary>
/// Base for Settings pages (the iOS SettingsPageViewController and the Mac twin, same members): a scrolling column at
/// most 836 dp wide inside the settings host's zone and a <see cref="SettingsBinder"/> owned by the view's binding
/// scope. Pages describe their cards in <see cref="BuildPage"/> in the Windows order; groups are sections of title-only
/// rows (canvas Android settings boards, owner 15 Oct 2026): a switch stays inline, and everything else opens the
/// generic <see cref="WinoSettingDetailFragment"/> with the real control and its description. Cards and expanders added
/// on their own get a section of their own. The title is the host's; the Windows page description is not shown.
/// </summary>
public abstract class SettingsPageFragment<TViewModel> : WinoFragment<TViewModel>, ISettingsDetailOwner where TViewModel : CoreBaseViewModel
{
    private SettingsBinder? _binder;
    private LinearLayout? _page;
    private TViewModel? _viewModel;
    private IDispatcher? _dispatcher;

    protected override int LayoutResource => Resource.Layout.fragment_settings_page;

    /// <summary>The page's binder; valid between <see cref="OnBind(View, TViewModel, IDispatcher)"/> and the view's destruction.</summary>
    protected SettingsBinder Bind => _binder ?? throw new InvalidOperationException("The page view has not been created.");

    /// <summary>The page's vertical content column. Children stretch to its width.</summary>
    protected LinearLayout Page => _page ?? throw new InvalidOperationException("The page view has not been created.");

    /// <summary>The entry's ViewModel while the view exists.</summary>
    protected TViewModel VM => _viewModel ?? throw new InvalidOperationException("The page has no ViewModel.");

    protected IDispatcher Dispatcher => _dispatcher ?? throw new InvalidOperationException("The page has no dispatcher.");

    protected Context Ctx => Page.Context ?? throw new InvalidOperationException("The page has no context.");

    /// <summary>The scroll view around the column (pages that scroll to a card).</summary>
    protected AndroidX.Core.Widget.NestedScrollView? ScrollView { get; private set; }

    /// <summary>The Windows page this fragment shows.</summary>
    protected WinoPage SettingsPage => Entry?.Page ?? WinoPage.None;

    /// <summary>
    /// Shows the Windows page description above the cards. Off on Android: the page title says enough and every setting
    /// starts the page (owner, 15 Oct 2026).
    /// </summary>
    protected virtual bool ShowsPageDescription => false;

    protected sealed override void OnBind(View view, TViewModel viewModel, IDispatcher dispatcher)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _page = view.FindViewById<LinearLayout>(Resource.Id.settings_page_content);
        ScrollView = view as AndroidX.Core.Widget.NestedScrollView;
        view.SetBackgroundColor(WinoColors.SettingsListFill(view.RequireContext()));
        _binder = new SettingsBinder(view.RequireContext(), Bindings, dispatcher, ReportError);
        Bindings.Own(new ActionDisposable(() =>
        {
            _binder = null;
            _page = null;
        }));

        if (ShowsPageDescription && SettingsCatalog.Description(SettingsPage) is { Length: > 0 } description)
        {
            var intro = AddIntro(description);
            ((LinearLayout.LayoutParams)intro.LayoutParameters!).BottomMargin = WinoTokens.Px(view.RequireContext(), Resource.Dimension.wino_space_s);
        }
        BuildPage();
        if (this is ISettingsPageTitleSource) UpdateTitle();
    }

    protected abstract void BuildPage();

    /// <summary>The cards and expanders in the page's sections, in order (detail pages are addressed from them).</summary>
    public IReadOnlyList<View> SettingSources() => _page is null ? [] : WinoSettingsNavigation.PageSources(_page);

    /// <summary>Asks the host to read the title again (pages whose title follows their content).</summary>
    protected void UpdateTitle() => (ParentFragment as SettingsHostFragment)?.UpdateChrome();

    /// <summary>
    /// Adds a section to the page; it stretches to the content width. Cards and expanders get a section of their own;
    /// other content (intro text, buttons, info bars) keeps the section header's text inset, 16 dp under the section
    /// above it.
    /// </summary>
    protected T Add<T>(T view) where T : View => Add(view, fullWidth: false);

    /// <summary>
    /// Adds a section; <paramref name="fullWidth"/> runs other content (a stack of sections) to the section edges.
    /// </summary>
    protected T Add<T>(T view, bool fullWidth) where T : View
    {
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        View placed = view;
        if (view is WinoSettingsCard or WinoSettingsExpander)
        {
            // A card on its own is a section of one row; the card keeps layout parameters for pages that adjust them.
            var section = new WinoSettingsGroup(Ctx);
            section.Add(view);
            placed = section;
            view.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        }
        else if (!fullWidth && view is not WinoSettingsGroup)
        {
            // A vertical stack is a list host (sections built later): it runs to the section edges.
            bool listHost = view.GetType() == typeof(LinearLayout) && ((LinearLayout)(View)view).Orientation == Orientation.Vertical;
            if (!listHost)
            {
                int inset = WinoTokens.Px(Ctx, Resource.Dimension.wino_m3_settings_header_start);
                layout.MarginStart = inset;
                layout.MarginEnd = inset;
                layout.TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_m3_settings_section_gap);
            }
            else if (Page.ChildCount == 0)
                layout.TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_m3_settings_section_gap);
        }
        Page.AddView(placed, layout);
        return view;
    }

    protected WinoSettingsGroup Group(string? title, params View[] rows)
    {
        var group = new WinoSettingsGroup(Ctx, title);
        group.AddRange(rows);
        return group;
    }

    protected WinoSettingsGroup AddGroup(string? title, params View[] rows) => Add(Group(title, rows));

    protected WinoSettingsCard Card(string header, string? description, WinoIconGlyph icon = WinoIconGlyph.None, View? content = null)
        => new(Ctx, header, string.IsNullOrWhiteSpace(description) ? null : description, icon, content);

    /// <summary>
    /// A card whose control sits under the text (wide dropdowns, text fields, steppers on narrow widths). Same order as
    /// Windows; only the control's row changes on a phone.
    /// </summary>
    protected WinoSettingsCard StackedCard(string header, string? description, WinoIconGlyph icon, View content)
    {
        var card = Card(header, description, icon);
        card.BottomContent = content;
        if (content.LayoutParameters is null || content is LinearLayout or EditText or Google.Android.Material.TextField.TextInputLayout)
            content.LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        return card;
    }

    /// <summary>A navigation card: chevron, tap or TalkBack activate runs <paramref name="activated"/>.</summary>
    protected WinoSettingsCard NavigationCard(string header, string? description, WinoIconGlyph icon, Action activated, View? content = null)
    {
        var card = Card(header, description, icon, content);
        card.IsClickable = true;
        card.ShowsChevron = true;
        EventHandler handler = (_, _) =>
        {
            try { activated(); }
            catch (Exception exception) { ReportError(exception); }
        };
        card.Activated += handler;
        Bindings.Own(new ActionDisposable(() => card.Activated -= handler));
        return card;
    }

    /// <summary>An action row (a Windows button): the title in the primary colour, or the error colour when destructive.</summary>
    protected WinoSettingsCard ActionCard(string title, ICommand command, Func<object?>? parameter = null, bool destructive = false)
    {
        var card = Card(title, null, WinoIconGlyph.None, Bind.Button(title, command, parameter));
        card.IsDestructive = destructive;
        return card;
    }

    /// <summary>An action row that runs <paramref name="action"/>.</summary>
    protected WinoSettingsCard ActionCard(string title, Action action, bool destructive = false)
    {
        var card = Card(title, null, WinoIconGlyph.None, Bind.Button(title, action));
        card.IsDestructive = destructive;
        return card;
    }

    /// <summary>A clickable card that executes a command, enabled from CanExecute.</summary>
    protected WinoSettingsCard CommandCard(string header, string? description, WinoIconGlyph icon, ICommand command, Func<object?>? parameter = null)
    {
        var card = Card(header, description, icon);
        card.IsClickable = true;
        var binding = Bindings.Own(new CommandBinding(command, parameter ?? (() => null), enabled => card.IsEnabled = enabled, Dispatcher, ReportError));
        EventHandler handler = (_, _) => binding.Execute();
        card.Activated += handler;
        Bindings.Own(new ActionDisposable(() => card.Activated -= handler));
        return card;
    }

    protected WinoSettingsExpander Expander(string header, string? description, WinoIconGlyph icon, View? content, params View[] items)
    {
        var expander = new WinoSettingsExpander(Ctx, header, string.IsNullOrWhiteSpace(description) ? null : description, icon, content);
        foreach (var item in items) expander.Add(item);
        return expander;
    }

    /// <summary>Adds a nested row to an expander with margins from dimension tokens (0 = none).</summary>
    protected void AddNested(WinoSettingsExpander expander, View row, int top, int start, int bottom, int end)
    {
        double Dp(int resource) => resource == 0 ? 0 : WinoTokens.Dp(Ctx, resource);
        expander.Add(row, Dp(top), Dp(start), Dp(bottom), Dp(end));
    }

    /// <summary>A horizontal row of controls, centred vertically, 8 dp apart.</summary>
    protected LinearLayout Row(params View[] views)
    {
        var row = new LinearLayout(Ctx) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        foreach (var view in views)
        {
            var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            if (row.ChildCount > 0) layout.MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s);
            row.AddView(view, layout);
        }
        return row;
    }

    /// <summary>A vertical stack of views, the <paramref name="spacing"/> dimension token apart (0: none), stretched to its width.</summary>
    protected LinearLayout Stack(int spacing, params View[] views)
    {
        var stack = new LinearLayout(Ctx) { Orientation = Orientation.Vertical };
        foreach (var view in views) AddTo(stack, view, spacing);
        return stack;
    }

    /// <summary>
    /// Appends <paramref name="view"/> to a vertical stack with a top gap (a dimension token; -1 for the card spacing,
    /// 0 for none) after the first child.
    /// </summary>
    protected void AddTo(LinearLayout stack, View view, int spacing = -1)
    {
        if (view is WinoSettingsCard or WinoSettingsExpander)
        {
            // A card in a stack is a section of one row (sections bring their own gap).
            var section = new WinoSettingsGroup(Ctx);
            section.Add(view);
            view = section;
            spacing = 0;
        }
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        if (spacing < 0) spacing = Resource.Dimension.wino_settings_item_spacing;
        if (stack.ChildCount > 0 && spacing != 0) layout.TopMargin = WinoTokens.Px(Ctx, spacing);
        stack.AddView(view, layout);
    }

    /// <summary>Short secondary text placed as a card's trailing content.</summary>
    protected TextView Caption(string? text) => Text(text, WinoTextStyle.Description);

    /// <summary>A text view in the Wino type scale.</summary>
    protected TextView Text(string? text, WinoTextStyle style = WinoTextStyle.Body, int maxLines = 0)
    {
        var label = WinoTokens.Style(new TextView(Ctx) { Text = text ?? string.Empty }, style);
        if (maxLines > 0)
        {
            label.SetMaxLines(maxLines);
            label.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        }
        return label;
    }

    protected WinoInfoBar InfoBar(WinoInfoBarSeverity severity, string? title, string? message)
        => new(Ctx) { Severity = severity, Title = title, Message = message, IsClosable = false };

    /// <summary>Body text at the top of a page, such as the Windows page description.</summary>
    protected TextView AddIntro(string text)
    {
        var label = Text(text, WinoTextStyle.Description);
        return Add(label);
    }

    /// <summary>Pushes another Settings page through the shared router (Windows breadcrumb navigation).</summary>
    protected static void Navigate(WinoPage page, object? parameter = null)
    {
        var title = SettingsCatalog.Title(page);
        WeakReferenceMessenger.Default.Send(parameter is null ? new BreadcrumbNavigationRequested(title, page) : new BreadcrumbNavigationRequested(title, page, parameter));
    }
}
