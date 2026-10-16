using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Messaging.Client.Navigation;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;
using Wino.Core.Domain.Models.Settings;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>Implemented by settings pages whose title follows their content (an account name).</summary>
public interface ISettingsPageTitleSource
{
    string? PageTitle { get; }

    event EventHandler? PageTitleChanged;
}

/// <summary>
/// Base for Settings sheet pages (the Mac SettingsPageViewController): a Wino zone over the theme backdrop holding a
/// vertical scroll view, whose content stack follows the readable width (max 836pt), and a <see cref="SettingsBinder"/>
/// owned by the page's binding scope. Pages describe their cards in <see cref="BuildPage"/> in Windows order; every card
/// and expander renders as a settings row on an inset-grouped surface (<see cref="WinoSettingsCard.IsListRow"/>), and
/// anything that is not a switch pushes the generic <see cref="WinoSettingDetailViewController"/>. The compact
/// navigation title comes from <see cref="SettingsCatalog"/>; the Windows page description is not shown on iOS (the
/// root list and the card texts already say what the page holds).
/// </summary>
public abstract class SettingsPageViewController<TViewModel>(TViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<TViewModel>(viewModel, dispatcher, logger) where TViewModel : CoreBaseViewModel
{
    /// <summary>Windows: MaxWidth 900 with 32pt padding inside the zone.</summary>
    public const double ContentMaxWidth = 836;

    /// <summary>Phone-width padding inside the zone; wider sheets centre the content at <see cref="ContentMaxWidth"/>.</summary>
    public const double SidePadding = 10;

    private SettingsBinder? _binder;
    private UIStackView? _page;
    private UIScrollView? _scroll;

    protected SettingsBinder Bind => _binder ??= new SettingsBinder(Bindings, Dispatcher, ReportError);

    /// <summary>The page's vertical content stack. Children stretch to the content width.</summary>
    protected UIStackView Page => _page ?? throw new InvalidOperationException("The page view has not been loaded.");

    /// <summary>The page's scroll view (for pages that scroll to a card).</summary>
    protected UIScrollView ScrollView => _scroll ?? throw new InvalidOperationException("The page view has not been loaded.");

    /// <summary>The Windows page this controller shows; it names the title and the description.</summary>
    protected virtual WinoPage? SettingsPage => SettingsPageRoutes.PageFor(GetType());

    /// <summary>Space between two cards that follow each other.</summary>
    public const double CardGap = 8;

    public override void LoadView()
    {
        _page = WinoLayout.VStack(WinoSettingsStyle.GroupSpacing);
        _page.Alignment = UIStackViewAlignment.Fill;
        _page.Distribution = UIStackViewDistribution.Fill;
        (View, _scroll) = SettingsPageScrollView.Create(_page);

        if (SettingsPage is { } known)
            Title = this is ISettingsPageTitleSource { PageTitle: { Length: > 0 } custom } ? custom : SettingsCatalog.Title(known);
        if (this is ISettingsPageTitleSource source)
        {
            EventHandler changed = (_, _) => { if (source.PageTitle is { Length: > 0 } title) Title = title; };
            source.PageTitleChanged += changed;
            Bindings.Own(new ActionDisposable(() => source.PageTitleChanged -= changed));
        }
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        WinoSettingDetailViewController.Appearing ??= static detail => SettingsSheetChrome.Install(detail.NavigationController);
        BuildPage();
        ConvertRows(Page);
    }

    /// <summary>Cards that a page placed inside its own containers render as rows too (with their own surface).</summary>
    protected static void ConvertRows(UIView root)
    {
        foreach (var child in root.Subviews)
        {
            switch (child)
            {
                case WinoSettingsCard card:
                    card.IsListRow = true;
                    break;
                case WinoSettingsExpander expander:
                    expander.IsListRow = true;
                    break;
                default:
                    ConvertRows(child);
                    break;
            }
        }
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        SettingsSheetChrome.Install(NavigationController);
    }

    protected abstract void BuildPage();

    /// <summary>
    /// Adds a section to the page; it stretches to the content width. Cards and expanders added one after another share
    /// one inset-grouped surface; everything else gets the group spacing.
    /// </summary>
    protected T Add<T>(T view) where T : UIView
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        if (view is WinoSettingsCard or WinoSettingsExpander)
        {
            if (Page.ArrangedSubviews.LastOrDefault() is not AutoGroup group)
            {
                group = new AutoGroup();
                Page.AddArrangedSubview(group);
            }
            group.Add(view);
            return view;
        }
        Page.AddArrangedSubview(view);
        return view;
    }

    /// <summary>The untitled group that collects cards added straight to the page.</summary>
    private sealed class AutoGroup : WinoSettingsGroup;

    protected WinoSettingsGroup Group(string? title, params UIView[] rows)
    {
        var group = new WinoSettingsGroup(title);
        group.AddRange(rows);
        return group;
    }

    protected WinoSettingsGroup AddGroup(string? title, params UIView[] rows) => Add(Group(title, rows));

    protected WinoSettingsCard Card(string header, string? description, WinoIconGlyph icon = WinoIconGlyph.None, UIView? content = null)
        => new(header, string.IsNullOrWhiteSpace(description) ? null : description, icon, content);

    /// <summary>
    /// A card whose control sits under the text (wide pop-ups, text fields, steppers with a value on narrow widths).
    /// Same order as Windows; only the control's row changes on a phone.
    /// </summary>
    protected WinoSettingsCard StackedCard(string header, string? description, WinoIconGlyph icon, UIView content)
    {
        var card = Card(header, description, icon);
        card.ContentPlacement = WinoSettingsContentPlacement.Below;
        card.Content = content;
        return card;
    }

    /// <summary>A navigation card: chevron, tap or VoiceOver activate runs <paramref name="activated"/>.</summary>
    protected WinoSettingsCard NavigationCard(string header, string? description, WinoIconGlyph icon, Action activated, UIView? content = null)
    {
        var card = Card(header, description, icon, content);
        card.IsClickable = true;
        EventHandler handler = (_, _) =>
        {
            try { activated(); }
            catch (Exception exception) { ReportError(exception); }
        };
        card.Activated += handler;
        Bindings.Own(new ActionDisposable(() => card.Activated -= handler));
        return card;
    }

    /// <summary>A navigation card that executes a command, enabled from CanExecute.</summary>
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

    /// <summary>
    /// A list toolbar button as an accent action row (Add, Refresh, Restore defaults); the row hides with the button.
    /// </summary>
    protected WinoSettingsCard ActionRow(UIButton button)
    {
        var card = Card(string.Empty, null, WinoIconGlyph.None, button);
        var observer = button.AddObserver("hidden", Foundation.NSKeyValueObservingOptions.Initial | Foundation.NSKeyValueObservingOptions.New, _ => card.Hidden = button.Hidden);
        Bindings.Own(observer);
        return card;
    }

    protected WinoSettingsExpander Expander(string header, string? description, WinoIconGlyph icon, UIView? content, params UIView[] items)
    {
        var expander = new WinoSettingsExpander(header, string.IsNullOrWhiteSpace(description) ? null : description, icon, content);
        foreach (var item in items) expander.Add(item);
        return expander;
    }

    protected static UIStackView Row(params UIView[] views)
    {
        var row = WinoLayout.HStack(WinoStyle.Space2, views);
        row.Alignment = UIStackViewAlignment.Center;
        return row;
    }

    /// <summary>Short secondary text placed as a card's trailing content.</summary>
    protected static UILabel Caption(string? text) => WinoStyle.Label(text, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);

    protected static WinoInfoBar InfoBar(WinoInfoBarSeverity severity, string? title, string? message) => new(severity, title, message);

    /// <summary>Secondary body text between sections (a note the Windows page shows inline, not the page description).</summary>
    protected UILabel AddIntro(string text) => Add(WinoStyle.Label(text, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0));

    /// <summary>Pushes another Settings page inside the sheet through the shared router (Windows breadcrumb navigation).</summary>
    protected void Navigate(WinoPage page, object? parameter = null)
    {
        var title = SettingsCatalog.Title(page);
        WeakReferenceMessenger.Default.Send(parameter is null ? new BreadcrumbNavigationRequested(title, page) : new BreadcrumbNavigationRequested(title, page, parameter));
    }
}

/// <summary>The scroll container shared by settings pages: a Wino zone over the sheet backdrop with the content stack inside.</summary>
public static class SettingsPageScrollView
{
    public static (UIView Root, UIScrollView Scroll) Create(UIView content)
    {
        var root = new UIView { BackgroundColor = UIColor.Clear };
        var zone = new WinoZoneView { Fill = WinoSettingsStyle.PageFill };
        root.AddSubview(zone);
        var guide = root.SafeAreaLayoutGuide;
        var gutter = (nfloat)WinoStyle.ZoneGutter;
        NSLayoutConstraint.ActivateConstraints(
        [
            zone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, gutter),
            zone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, gutter),
            zone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -gutter),
            zone.BottomAnchor.ConstraintEqualTo(root.BottomAnchor)
        ]);

        var scroll = new WinoSettingsScrollView();
        WinoLayout.Fill(scroll, zone.ContentView);
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        scroll.AddSubview(content);

        var frame = scroll.FrameLayoutGuide;
        var contentGuide = scroll.ContentLayoutGuide;
        var pad = (nfloat)SettingsPageViewController<CoreBaseViewModel>.SidePadding;
        var preferredWidth = content.WidthAnchor.ConstraintEqualTo(frame.WidthAnchor, 1, -2 * pad);
        // Above the 751 hugging of trailing controls, so a card never narrows the page; below the required max width.
        preferredWidth.Priority = 990;
        NSLayoutConstraint.ActivateConstraints(
        [
            content.TopAnchor.ConstraintEqualTo(contentGuide.TopAnchor, pad),
            content.BottomAnchor.ConstraintEqualTo(contentGuide.BottomAnchor, -28),
            content.CenterXAnchor.ConstraintEqualTo(frame.CenterXAnchor),
            content.WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)SettingsPageViewController<CoreBaseViewModel>.ContentMaxWidth),
            content.LeadingAnchor.ConstraintGreaterThanOrEqualTo(frame.LeadingAnchor, pad),
            preferredWidth
        ]);
        return (root, scroll);
    }
}
