using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>Implemented by settings pages whose window title follows their content (an account name).</summary>
public interface ISettingsPageTitleSource
{
    string? PageTitle { get; }

    /// <summary>The line under the title; null keeps the page catalog description.</summary>
    string? PageDescription => null;

    event EventHandler? PageTitleChanged;
}

/// <summary>
/// Base for Settings window pages: a vertical scroll view whose content fills the zone at the
/// Windows settings width (max 836pt inside 32pt side padding) and a <see cref="SettingsBinder"/>
/// owned by the page's binding scope. Pages describe their cards in <see cref="BuildPage"/>; cards
/// stack with the Windows 4pt spacing.
/// </summary>
public abstract class SettingsPageViewController<TViewModel>(TViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<TViewModel>(viewModel, dispatcher, logger) where TViewModel : CoreBaseViewModel
{
    /// <summary>Windows: MaxWidth 900 with 32pt padding inside the zone.</summary>
    public const double ContentMaxWidth = 836;
    public const double SidePadding = 32;
    private SettingsBinder? _binder;
    private NSStackView? _page;

    protected SettingsBinder Bind => _binder ??= new SettingsBinder(Bindings, Dispatcher, ReportError);

    /// <summary>The page's vertical content stack. Children stretch to the content width.</summary>
    protected NSStackView Page => _page ?? throw new InvalidOperationException("The page view has not been loaded.");

    public override void LoadView()
    {
        _page = WinoLayout.VStack(WinoSettingsStyle.CardSpacing);
        _page.Alignment = NSLayoutAttribute.Leading;
        _page.Distribution = NSStackViewDistribution.Fill;
        View = SettingsPageScrollView.Create(_page);
        BuildPage();
    }

    protected abstract void BuildPage();

    /// <summary>Adds a section to the page and stretches it to the content width.</summary>
    protected T Add<T>(T view) where T : NSView
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        // Page children fill the width; a hugging child (a spinner, a button row) must never pull the window narrower.
        view.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        if (view is NSStackView stack) stack.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        Page.AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(Page.WidthAnchor).Active = true;
        return view;
    }

    protected WinoSettingsGroup Group(string? title, params NSView[] rows)
    {
        var group = new WinoSettingsGroup(title);
        group.AddRange(rows);
        return group;
    }

    protected WinoSettingsGroup AddGroup(string? title, params NSView[] rows) => Add(Group(title, rows));

    protected WinoSettingsCard Card(string header, string? description, WinoIconGlyph icon = WinoIconGlyph.None, NSView? content = null)
        => new(header, string.IsNullOrWhiteSpace(description) ? null : description, icon, content);

    /// <summary>A navigation card: chevron, click, Return or Space runs <paramref name="activated"/>.</summary>
    protected WinoSettingsCard NavigationCard(string header, string? description, WinoIconGlyph icon, Action activated, NSView? content = null)
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
    protected WinoSettingsCard CommandCard(string header, string? description, WinoIconGlyph icon, System.Windows.Input.ICommand command, Func<object?>? parameter = null)
    {
        var card = Card(header, description, icon);
        card.IsClickable = true;
        var binding = Bindings.Own(new CommandBinding(command, parameter ?? (() => null), enabled => card.IsEnabled = enabled, Dispatcher, ReportError));
        EventHandler handler = (_, _) => binding.Execute();
        card.Activated += handler;
        Bindings.Own(new ActionDisposable(() => card.Activated -= handler));
        return card;
    }

    protected WinoSettingsExpander Expander(string header, string? description, WinoIconGlyph icon, NSView? content, params NSView[] items)
    {
        var expander = new WinoSettingsExpander(header, string.IsNullOrWhiteSpace(description) ? null : description, icon, content);
        foreach (var item in items) expander.Add(item);
        return expander;
    }

    protected static NSStackView Row(params NSView[] views) => WinoLayout.HStack(WinoStyle.Space2, views);

    /// <summary>Short secondary text placed as a card's trailing content.</summary>
    protected static NSTextField Caption(string? text) => WinoStyle.Label(text, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);

    protected WinoInfoBar InfoBar(WinoInfoBarSeverity severity, string? title, string? message) => new(severity, title, message);

    /// <summary>Body text at the top of a page, such as the Windows page description.</summary>
    protected NSTextField AddIntro(string text)
    {
        var label = WinoStyle.Label(text, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        return Add(label);
    }
}

/// <summary>The vertical scroll container shared by settings pages and placeholders.</summary>
public static class SettingsPageScrollView
{
    public static NSScrollView Create(NSView content)
    {
        var document = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        document.AddSubview(content);

        var scroll = new NSScrollView
        {
            HasVerticalScroller = true,
            HasHorizontalScroller = false,
            AutohidesScrollers = true,
            DrawsBackground = false,
            BorderType = NSBorderType.NoBorder,
            AutomaticallyAdjustsContentInsets = false,
            TranslatesAutoresizingMaskIntoConstraints = false,
            DocumentView = document
        };
        var clip = scroll.ContentView;
        var preferredWidth = content.WidthAnchor.ConstraintEqualTo(document.WidthAnchor, 1, (nfloat)(-2 * SettingsPageViewController<CoreBaseViewModel>.SidePadding));
        preferredWidth.Priority = 750;
        NSLayoutConstraint.ActivateConstraints(
        [
            document.LeadingAnchor.ConstraintEqualTo(clip.LeadingAnchor),
            document.TopAnchor.ConstraintEqualTo(clip.TopAnchor),
            document.WidthAnchor.ConstraintEqualTo(clip.WidthAnchor),
            content.TopAnchor.ConstraintEqualTo(document.TopAnchor, 4),
            content.BottomAnchor.ConstraintEqualTo(document.BottomAnchor, -28),
            content.CenterXAnchor.ConstraintEqualTo(document.CenterXAnchor),
            content.WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)SettingsPageViewController<CoreBaseViewModel>.ContentMaxWidth),
            content.LeadingAnchor.ConstraintGreaterThanOrEqualTo(document.LeadingAnchor, (nfloat)SettingsPageViewController<CoreBaseViewModel>.SidePadding),
            preferredWidth
        ]);
        return scroll;
    }

    private sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }
}
